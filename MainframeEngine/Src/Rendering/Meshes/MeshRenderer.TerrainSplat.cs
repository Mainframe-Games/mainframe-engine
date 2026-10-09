using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// <see cref="TerrainSplatMaterial3D"/> in the mesh renderer (ADR 0156): a second set-2 layout (UBO, two samplers, three
/// layer arrays, two weight maps) and a second pipeline layout sharing sets 0 and 1, so the frame and shadow sets bound
/// once per list stay valid when a terrain draw rebinds set 2. Splat materials keep a default <see cref="MaterialGpu.Set"/>
/// too, which the object-ID pass binds with the shared layout.
/// </summary>
internal sealed unsafe partial class MeshRenderer
{
    private DescriptorSetLayout _splatLayout;
    private PipelineLayout _splatPipelineLayout;
    private MaterialDescriptorAllocator _splatSets = null!;
    private GpuTexture _splatNoWeights = null!;

    /// <summary>Images in the terrain splat set: three layer arrays and two weight maps (fragment-stage budget).</summary>
    internal const int SplatSetImages = 5;

    /// <summary>Samplers in the terrain splat set: layers (repeat, anisotropic) and maps (clamp).</summary>
    internal const int SplatSetSamplers = 2;

    private void CreateSplatResources()
    {
        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 5, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 6, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 7, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        _splatLayout = PipelineBuilder.CreateSetLayout(_ctx, bindings, "terrain splat set 2");
        _splatPipelineLayout = _ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [_splatLayout], "mesh (terrain splat)");
        _splatSets = new MaterialDescriptorAllocator(_ctx, _splatLayout, SplatSetSamplers, SplatSetImages);

        // Weight maps of a material without a (Realistic) terrain: layer 0 everywhere (map 0), nothing (map 1).
        ReadOnlySpan<byte> none = [0, 0, 0, 0];
        _splatNoWeights = GpuTexture.Create2D(_ctx, 1, 1, none, TextureColorSpace.Linear, TextureSampling.LinearClamp);
    }

    /// <summary>The pipeline layout of a shader set: the terrain splat layout, the water-scene layout (ADR 0173), or the shared mesh layout.</summary>
    private PipelineLayout LayoutFor(ShaderSetId shaders) =>
        shaders == ShaderSetId.MeshTerrainSplat ? _splatPipelineLayout : UsesSceneLayout(shaders) ? _waterScenePipelineLayout : _pipelineLayout;

    /// <summary>
    /// Brings a splat material's own GPU state up to date: re-packs the layer arrays when their textures changed, follows
    /// the terrain's weight maps, re-uploads the parameters when the material changed and rewrites the set when any image
    /// moved. Allocation-free when nothing changed.
    /// </summary>
    private void PrepareSplat(MaterialGpu gpu, TerrainSplatMaterial3D material)
    {
        var splat = gpu.Splat ??= new TerrainSplatGpu();
        var rewrite = false;

        var stamp = TerrainLayerPacker.ContentStamp(material);
        if (splat.PackedStamp != stamp)
        {
            splat.PackedStamp = stamp;
            var packed = TerrainLayerPacker.Pack(material);
            splat.DisposeArrays();
            splat.Albedo = new Texture2DArrayGpu(_ctx, packed.Albedo, TextureColorSpace.Srgb);
            splat.Normal = new Texture2DArrayGpu(_ctx, packed.Normal, TextureColorSpace.Linear);
            splat.Orm = new Texture2DArrayGpu(_ctx, packed.Orm, TextureColorSpace.Linear);
            rewrite = true;
        }

        splat.Albedo!.Update();
        splat.Normal!.Update();
        splat.Orm!.Update();

        // The terrain's weight maps (the data's own textures: painting re-uploads them through the texture cache).
        var data = material.Terrain?.Data;
        var realistic = data is { Profile: TerrainProfile.Realistic };
        rewrite |= SwapTexture(realistic ? data!.GetSplatTexture(0) : null, colorUsage: false, ref splat.Weights0);
        rewrite |= SwapTexture(realistic ? data!.GetSplatTexture(1) : null, colorUsage: false, ref splat.Weights1);
        splat.Weights0?.Update();
        splat.Weights1?.Update();

        if (splat.UploadedVersion != material.Version || splat.Params is null)
        {
            splat.UploadedVersion = material.Version;
            if (splat.Params is null)
            {
                splat.Params = GpuBuffer.Create(_ctx, TerrainSplatParams.Size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.DeviceLocal);
                rewrite = true;
            }

            var parameters = TerrainSplatParams.From(material);
            splat.Params.Upload(new ReadOnlySpan<byte>(&parameters, TerrainSplatParams.Size));
        }

        if (rewrite || splat.Set.Handle == 0 || splat.SetStamp != splat.CurrentStamp())
            WriteSplatSet(splat);
    }

    private void WriteSplatSet(TerrainSplatGpu splat)
    {
        _splatSets.Free(splat.SetPool, splat.Set);
        splat.Set = _splatSets.Allocate(out var pool);
        splat.SetPool = pool;
        splat.SetStamp = splat.CurrentStamp();

        var albedo = splat.Albedo!.Gpu!;
        var weights0 = splat.Weights0?.Gpu ?? _splatNoWeights;
        var weights1 = splat.Weights1?.Gpu ?? _splatNoWeights;
        var buffer = splat.Params!.Descriptor(0, TerrainSplatParams.Size);
        var images = stackalloc DescriptorImageInfo[7];
        images[0] = new DescriptorImageInfo { Sampler = albedo.Sampler };
        images[1] = new DescriptorImageInfo { Sampler = _splatNoWeights.Sampler }; // linear clamp (the maps' own sampler is the same)
        images[2] = new DescriptorImageInfo { ImageView = albedo.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[3] = new DescriptorImageInfo { ImageView = splat.Normal!.Gpu!.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[4] = new DescriptorImageInfo { ImageView = splat.Orm!.Gpu!.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[5] = new DescriptorImageInfo { ImageView = weights0.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        images[6] = new DescriptorImageInfo { ImageView = weights1.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = splat.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &buffer,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = splat.Set,
            DstBinding = 1,
            DescriptorCount = 2,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = images,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = splat.Set,
            DstBinding = 3,
            DescriptorCount = 5,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = images + 2,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 3, writes, 0, null);
    }

    private void DestroySplat(MaterialGpu gpu)
    {
        if (gpu.Splat is not { } splat)
            return;
        splat.Params?.Dispose();
        splat.Params = null;
        _splatSets.Free(splat.SetPool, splat.Set);
        splat.Set = default;
        splat.DisposeArrays();
        ReleaseTexture(splat.Weights0);
        ReleaseTexture(splat.Weights1);
        splat.Weights0 = splat.Weights1 = null;
        gpu.Splat = null;
    }

    private void DisposeSplatResources()
    {
        _splatSets.Dispose();
        _splatNoWeights.Dispose();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_splatPipelineLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_splatLayout));
    }
}

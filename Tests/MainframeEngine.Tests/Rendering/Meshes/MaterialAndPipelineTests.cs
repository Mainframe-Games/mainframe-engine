using System.Drawing;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering.Meshes;

using Color = System.Drawing.Color;

/// <summary>Material state, pipeline keys and the state-hash pipeline cache (no GPU: a fake factory).</summary>
public sealed class MaterialAndPipelineTests
{
    private static readonly RenderPass ScenePass = new(0x1234);
    private static readonly RenderPass OtherPass = new(0x5678);

    [Fact]
    public void RenderStateFollowsTheMaterial()
    {
        var material = new StandardMaterial3D();
        Assert.Equal(new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), material.RenderState);
        Assert.True(material.RenderState.DepthWrite);
        Assert.True(material.RenderState.CastsShadows);
        Assert.False(material.RenderState.IsTransparent);

        material.Transparency = AlphaMode.Blend;
        Assert.False(material.RenderState.DepthWrite);
        Assert.False(material.RenderState.CastsShadows);
        Assert.True(material.RenderState.IsTransparent);

        material.DoubleSided = true;
        Assert.Equal(CullMode.Disabled, material.RenderState.EffectiveCull);
        material.DoubleSided = false;
        material.CullMode = CullMode.Front;
        Assert.Equal(CullMode.Front, material.RenderState.EffectiveCull);
    }

    [Fact]
    public void OutlinesAreFrontCulledBlendedAndNextPassesBumpTheChainGeneration()
    {
        var outline = new OutlineMaterial3D { Color = Color.White, Width = 7f };
        Assert.Equal(new MaterialRenderState(AlphaMode.Blend, CullMode.Front, false), outline.RenderState);
        Assert.False(outline.RenderState.CastsShadows);

        var material = new StandardMaterial3D();
        var version = material.Version;
        var generation = Material.ChainGeneration;
        material.NextPass = outline;
        Assert.Equal(version + 1, material.Version);
        Assert.NotEqual(generation, Material.ChainGeneration);
        generation = Material.ChainGeneration;
        material.NextPass = outline; // unchanged
        Assert.Equal(generation, Material.ChainGeneration);

        // Extra passes (overlays, next passes) get their own pipelines: less-or-equal depth.
        var normal = PipelineKey.ForMaterial(ShaderSetId.MeshOutline, outline.RenderState, mirrored: false, ScenePass);
        var extra = PipelineKey.ForMaterial(ShaderSetId.MeshOutline, outline.RenderState, mirrored: false, ScenePass, extraPass: true);
        Assert.NotEqual(normal, extra);
        Assert.True(extra.ExtraPass);
        Assert.Equal(CullMode.Front, extra.Cull);
        Assert.False(extra.DepthWrite);
        var indices = new HashSet<int>();
        for (var shaders = 0; shaders < MaterialGpu.ShaderSetCount; shaders++)
            for (var e = 0; e < 2; e++)
                for (var m = 0; m < 2; m++)
                    indices.Add(MaterialGpu.PipelineIndex((ShaderSetId)shaders, e == 1, m == 1));
        Assert.Equal(12, indices.Count);
        Assert.Equal(11, indices.Max());

        var parameters = MaterialParams.From(outline);
        Assert.Equal(1u, parameters.Unshaded);
        Assert.Equal(7f, BitConverter.UInt32BitsToSingle(parameters.OutlineWidth));
    }

    [Fact]
    public void EveryPropertyChangeBumpsTheVersion()
    {
        var material = new StandardMaterial3D();
        var changes = 0;
        material.Changed += () => changes++;
        var version = material.Version;

        material.AlbedoColor = Color.Red;
        material.Specular = 0.5f;
        material.AlbedoTexture = Texture2D.FromPixels(1, 1, [1, 2, 3, 4]);
        material.Transparency = AlphaMode.Cutout;
        material.UvScale = new Vector2(2);
        Assert.Equal(version + 5, material.Version);
        Assert.Equal(5, changes);

        material.Specular = 0.5f; // unchanged: no bump
        Assert.Equal(version + 5, material.Version);
    }

    [Fact]
    public void EqualStatesHashToTheSameKeyAndDifferentStatesDoNot()
    {
        var a = new StandardMaterial3D { AlbedoColor = Color.Red, Specular = 1f };
        var b = new StandardMaterial3D { AlbedoColor = Color.Blue, AlbedoTexture = Texture2D.FromPixels(1, 1, [0, 0, 0, 255]) };
        var keyA = PipelineKey.ForMaterial(ShaderSetId.MeshLit, a.RenderState, mirrored: false, ScenePass);
        var keyB = PipelineKey.ForMaterial(ShaderSetId.MeshLit, b.RenderState, mirrored: false, ScenePass);
        Assert.Equal(keyA, keyB); // colours and textures are descriptor data, not pipeline state
        Assert.Equal(keyA.GetHashCode(), keyB.GetHashCode());

        var distinct = new HashSet<PipelineKey>
        {
            keyA,
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, a.RenderState, mirrored: true, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, a.RenderState, mirrored: false, OtherPass),
            PipelineKey.ForMaterial(ShaderSetId.MeshObjectId, a.RenderState, mirrored: false, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Cutout, CullMode.Back, false), false, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Blend, CullMode.Back, false), false, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Opaque, CullMode.Front, false), false, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Opaque, CullMode.Disabled, false), false, ScenePass),
        };
        Assert.Equal(8, distinct.Count);

        // Double-sided resolves to "cull nothing": the same pipeline as CullMode.Disabled.
        Assert.Equal(
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, true), false, ScenePass),
            PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Opaque, CullMode.Disabled, false), false, ScenePass));
    }

    [Fact]
    public void ObjectIdPipelinesNeverBlendAndAlwaysWriteDepth()
    {
        var blend = new MaterialRenderState(AlphaMode.Blend, CullMode.Back, false);
        var key = PipelineKey.ForMaterial(ShaderSetId.MeshObjectId, blend, false, ScenePass);
        Assert.Equal(AlphaMode.Opaque, key.Alpha);
        Assert.True(key.DepthWrite);
        Assert.Equal(key, PipelineKey.ForMaterial(ShaderSetId.MeshObjectId, new MaterialRenderState(AlphaMode.Opaque, CullMode.Back, false), false, ScenePass));

        // Cutout keeps its discard in the ID pass (holes are not pickable).
        Assert.Equal(AlphaMode.Cutout, PipelineKey.ForMaterial(ShaderSetId.MeshObjectId,
            new MaterialRenderState(AlphaMode.Cutout, CullMode.Back, false), false, ScenePass).Alpha);
    }

    [Fact]
    public void PipelineCacheCreatesOncePerKey()
    {
        var factory = new FakeFactory();
        using var cache = new PipelineStateCache(factory);
        var opaque = PipelineKey.ForMaterial(ShaderSetId.MeshLit, new StandardMaterial3D().RenderState, false, ScenePass);
        var blend = PipelineKey.ForMaterial(ShaderSetId.MeshLit, new MaterialRenderState(AlphaMode.Blend, CullMode.Back, false), false, ScenePass);

        var first = cache.GetOrCreate(opaque);
        var again = cache.GetOrCreate(opaque);
        var other = cache.GetOrCreate(blend);

        Assert.Equal(first, again);
        Assert.NotEqual(first.Pipeline.Handle, other.Pipeline.Handle);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(2, cache.Misses);
        Assert.True(cache.Contains(blend));

        // A thousand materials with the same state share one pipeline.
        for (var i = 0; i < 1000; i++)
            cache.GetOrCreate(PipelineKey.ForMaterial(ShaderSetId.MeshLit, new StandardMaterial3D { Specular = i }.RenderState, false, ScenePass));
        Assert.Equal(2, factory.Created.Count);

        cache.Dispose();
        Assert.Equal(factory.Created.OrderBy(h => h), factory.Destroyed.OrderBy(h => h));
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrCreate(opaque));
    }

    [Fact]
    public void LookupsDoNotAllocate()
    {
        var factory = new FakeFactory();
        using var cache = new PipelineStateCache(factory);
        var key = PipelineKey.ForMaterial(ShaderSetId.MeshLit, new StandardMaterial3D().RenderState, false, ScenePass);
        static void Lookups(PipelineStateCache cache, in PipelineKey key)
        {
            for (var i = 0; i < 1000; i++)
                cache.GetOrCreate(key);
        }

        Lookups(cache, key); // warm up (JIT, tiering)
        var before = GC.GetAllocatedBytesForCurrentThread();
        Lookups(cache, key);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void MaterialParamsPackLinearColours()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = Color.FromArgb(128, 255, 128, 0),
            EmissionColor = Color.FromArgb(255, 255, 255, 255),
            EmissionEnergy = 2f,
            UvScale = new Vector2(2, 3),
            UvOffset = new Vector2(0.5f, 0.25f),
            Specular = 0.7f,
            Shininess = 64f,
            AlphaCutoff = 0.3f,
            NormalScale = 1.5f,
            ShadingMode = ShadingMode.Unshaded,
            DoubleSided = true,
        };
        var p = MaterialParams.From(material, MaterialParams.HasAlbedo | MaterialParams.HasNormal);

        Assert.Equal(MaterialParams.Size, System.Runtime.InteropServices.Marshal.SizeOf<MaterialParams>());
        Assert.Equal(1f, p.Albedo.X, 5);
        Assert.Equal(ColorSpace.SrgbToLinear(128 / 255f), p.Albedo.Y, 5);
        Assert.Equal(0f, p.Albedo.Z, 5);
        Assert.Equal(128 / 255f, p.Albedo.W, 5); // alpha is linear already
        Assert.Equal(new Vector4(2, 2, 2, 0), p.Emission);
        Assert.Equal(new Vector4(2, 3, 0.5f, 0.25f), p.UvTransform);
        Assert.Equal(new Vector4(0.7f, 64f, 0.3f, 1.5f), p.Params);
        Assert.Equal(3u, p.TextureFlags);
        Assert.Equal(1u, p.Unshaded);
        Assert.Equal(1u, p.DoubleSided);
    }

    private sealed class FakeFactory : IPipelineFactory
    {
        private ulong _next = 100;
        public List<ulong> Created { get; } = [];
        public List<ulong> Destroyed { get; } = [];

        public Pipeline Create(in PipelineKey key)
        {
            var handle = _next++;
            Created.Add(handle);
            return new Pipeline(handle);
        }

        public void Destroy(Pipeline pipeline) => Destroyed.Add(pipeline.Handle);
    }
}

/// <summary>Draw sort keys and the draw list: opaque by pipeline → material → mesh, transparent back to front.</summary>
public sealed class DrawSortTests
{
    [Fact]
    public void OpaqueKeysOrderByPipelineThenMaterialThenMeshThenSurface()
    {
        Assert.True(DrawSortKey.Opaque(1, 999, 999, 99) < DrawSortKey.Opaque(2, 0, 0, 0));
        Assert.True(DrawSortKey.Opaque(1, 1, 999, 99) < DrawSortKey.Opaque(1, 2, 0, 0));
        Assert.True(DrawSortKey.Opaque(1, 1, 1, 99) < DrawSortKey.Opaque(1, 1, 2, 0));
        Assert.True(DrawSortKey.Opaque(1, 1, 1, 0) < DrawSortKey.Opaque(1, 1, 1, 1));
    }

    [Fact]
    public void TransparentKeysOrderByPriorityThenFarthestFirst()
    {
        Assert.True(DrawSortKey.Transparent(0, 50f, 1, 1) < DrawSortKey.Transparent(0, 10f, 1, 1)); // far before near
        Assert.True(DrawSortKey.Transparent(0, 0.5f, 9, 9) < DrawSortKey.Transparent(0, 0.25f, 1, 1));
        Assert.True(DrawSortKey.Transparent(-1, 1f, 1, 1) < DrawSortKey.Transparent(0, 100f, 1, 1)); // lower priority first
        Assert.True(DrawSortKey.Transparent(5, 1f, 1, 1) > DrawSortKey.Transparent(0, 1000f, 1, 1));
        Assert.Equal(DrawSortKey.Transparent(0, -3f, 1, 1), DrawSortKey.Transparent(0, 0f, 1, 1)); // behind the camera clamps
        Assert.Equal(DrawSortKey.Transparent(0, float.NaN, 1, 1), DrawSortKey.Transparent(0, 0f, 1, 1));
    }

    [Fact]
    public void DrawListSortsItemsByKeyAndGroupsEqualState()
    {
        var list = new DrawList<(int Pipeline, int Material, int Mesh)>(capacity: 2); // grows past the initial capacity
        (int, int, int)[] items = [(2, 1, 1), (1, 2, 1), (1, 1, 2), (1, 1, 1), (2, 1, 1), (1, 1, 1)];
        foreach (var item in items)
            list.Add(DrawSortKey.Opaque(item.Item1, item.Item2, item.Item3, 0), item);
        list.Sort();

        var sorted = new List<(int, int, int)>();
        for (var i = 0; i < list.Count; i++)
            sorted.Add(list[i]);
        Assert.Equal([(1, 1, 1), (1, 1, 1), (1, 1, 2), (1, 2, 1), (2, 1, 1), (2, 1, 1)], sorted);
        for (var i = 1; i < list.Count; i++)
            Assert.True(list.KeyAt(i - 1) <= list.KeyAt(i));

        list.Clear();
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void TransparentDrawsComeOutBackToFront()
    {
        var list = new DrawList<float>();
        foreach (var depth in new[] { 3f, 12f, 0.5f, 7f })
            list.Add(DrawSortKey.Transparent(0, depth, 1, 1), depth);
        list.Sort();
        Assert.Equal([12f, 7f, 3f, 0.5f], Enumerable.Range(0, list.Count).Select(i => list[i]));
    }

    [Fact]
    public void SortingAndRefillingDoNotAllocateOnceGrown()
    {
        var list = new DrawList<int>(16);
        var random = new Random(7);
        var keys = Enumerable.Range(0, 1000).Select(_ => (ulong)random.NextInt64()).ToArray();
        void Fill()
        {
            list.Clear();
            for (var i = 0; i < keys.Length; i++)
                list.Add(keys[i], i);
            list.Sort();
        }

        Fill(); // grow
        var before = GC.GetAllocatedBytesForCurrentThread();
        Fill();
        Fill();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        for (var i = 1; i < list.Count; i++)
            Assert.True(list.KeyAt(i - 1) <= list.KeyAt(i));
    }

    [Fact]
    public void CasterKeysGroupByCullThenCutoutMaterialThenMesh()
    {
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 5, 0) < MeshRenderer.CasterKey(CullMode.Back, true, 0, 1, 0));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, true, 7, 9, 9) < MeshRenderer.CasterKey(CullMode.Disabled, false, 0, 1, 0));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 1, 3) < MeshRenderer.CasterKey(CullMode.Back, false, 0, 2, 0));
        // Opaque casters (material 0) before cutout ones; cutout runs grouped by material, then mesh.
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 999, 9) < MeshRenderer.CasterKey(CullMode.Back, false, 1, 1, 0));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 1, 999, 0) < MeshRenderer.CasterKey(CullMode.Back, false, 2, 1, 0));
    }
}

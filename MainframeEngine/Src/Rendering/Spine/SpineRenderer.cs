using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;
using Spine;
using Skeleton = Spine.Skeleton;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public class SpineRenderer : IDisposable
{
    private const int MaxVertices = 8192;
    private readonly float[] _worldVerticesPositions = new float[MaxVertices];
    private readonly Vertex[] _vertices = new Vertex[MaxVertices];

    public BlendingFactor SrcFactor = BlendingFactor.One;
    public BlendingFactor DestFactor = BlendingFactor.OneMinusSrcAlpha;

    // OpenGL
    private readonly GL? _gl;
    private readonly BufferObject<Vertex>? _vertexBuffer;
    private readonly VertexArrayObject<Vertex, uint>? _vbo;
    private readonly Shader? _shader;
    private readonly List<Texture>? _textures;

    // Vulkan
    private IVulkanContext? _vkCtx;
    private Image[] _vkImages = null!;
    private DeviceMemory[] _vkImageMemory = null!;
    private ImageView[] _vkImageViews = null!;
    private Silk.NET.Vulkan.Sampler _vkSampler;
    private VkBuffer[] _vkUboBuffers = null!;
    private DeviceMemory[] _vkUboMemory = null!;
    private nint[] _vkUboMapped = null!;
    private VkBuffer[] _vkVertexBuffers = null!;
    private DeviceMemory[] _vkVertexMemory = null!;
    private nint[] _vkVertexMapped = null!;
    private ulong[] _vkVertexCapacity = null!;
    private DescriptorSetLayout _vkUboLayout;
    private DescriptorSetLayout _vkTexLayout;
    private DescriptorPool _vkDescPool;
    private DescriptorSet[] _vkUboDescSets = null!;
    private DescriptorSet[][] _vkTexDescSets = null!; // [imageIdx][texIdx]
    private PipelineLayout _vkPipelineLayout;
    private Pipeline _vkPipeline;

    // Per-frame draw batches for Vulkan: (texIdx, vertexStart)
    private readonly List<(int texIdx, int start)> _vkBatches = new();

    // MVP
    private Matrix4x4 _modelMatrix;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    private readonly bool _pma;
    private readonly Skeleton _skeleton;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position; // offset  0
        public Vector2 Uv;       // offset 12
        public Vector4 Color;    // offset 20
        public float TextureIndex; // offset 36 — used by OpenGL shader, ignored by Vulkan
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkVpUbo { public Matrix4x4 View; public Matrix4x4 Projection; }

    public SpineRenderer(IRenderer renderer, Skeleton skeleton, bool pma, SpineTextureLoader textureLoader)
    {
        _skeleton = skeleton;
        _pma = pma;

        if (renderer.Backend == RenderingBackend.OpenGL)
        {
            _gl = renderer.GetGL();
            _textures = textureLoader.GlTextures;

            _vertexBuffer = new BufferObject<Vertex>(_gl, null, BufferTargetARB.ArrayBuffer);
            var indexBuffer = new BufferObject<uint>(_gl, [0u], BufferTargetARB.ElementArrayBuffer);
            _vbo = new VertexArrayObject<Vertex, uint>(_gl, _vertexBuffer, indexBuffer);

            var stride = (uint)Marshal.SizeOf<Vertex>();
            _vbo.VertexAttributePointer2(0, 3, VertexAttribPointerType.Float, stride, (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)));
            _vbo.VertexAttributePointer2(1, 2, VertexAttribPointerType.Float, stride, (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv)));
            _vbo.VertexAttributePointer2(2, 4, VertexAttribPointerType.Float, stride, (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color)));
            _vbo.VertexAttributePointer2(3, 1, VertexAttribPointerType.Float, stride, (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.TextureIndex)));

            _shader = new Shader(_gl,
                "Content/Shaders/Spine/Spine.vert",
                "Content/Shaders/Spine/Spine.frag");
        }
        else if (renderer is IVulkanContext vkCtx)
        {
            _vkCtx = vkCtx;
            CreateVkResources(vkCtx, textureLoader.VkImageData);
        }
    }

    public void Draw(float zSpacing, Matrix4x4 model, Matrix4x4 view, Matrix4x4 projection)
    {
        _modelMatrix = model;
        _viewMatrix = view;
        _projectionMatrix = projection;

        var vertexIndex = 0;
        var z = 0f;

        if (_vkCtx is not null) _vkBatches.Clear();

        for (int i = 0; i < _skeleton.DrawOrder.Count; i++)
        {
            var slot = _skeleton.DrawOrder.Items[i];
            var attachment = slot.Attachment;
            if (attachment is null) continue;

            var tintA = _skeleton.A * slot.A;
            var alpha = _pma ? tintA : 1;
            var tintR = _skeleton.R * slot.R * alpha;
            var tintG = _skeleton.G * slot.G * alpha;
            var tintB = _skeleton.B * slot.B * alpha;

            switch (attachment)
            {
                case RegionAttachment region:
                {
                    int texIdx = ResolveTexIdx(region.Region);
                    BeginBatch(texIdx, vertexIndex);
                    region.ComputeWorldVertices(slot, _worldVerticesPositions, 0);

                    AddVertex(_worldVerticesPositions[0], _worldVerticesPositions[1], z, region.UVs[0], region.UVs[1], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    AddVertex(_worldVerticesPositions[2], _worldVerticesPositions[3], z, region.UVs[2], region.UVs[3], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    AddVertex(_worldVerticesPositions[4], _worldVerticesPositions[5], z, region.UVs[4], region.UVs[5], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    AddVertex(_worldVerticesPositions[4], _worldVerticesPositions[5], z, region.UVs[4], region.UVs[5], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    AddVertex(_worldVerticesPositions[6], _worldVerticesPositions[7], z, region.UVs[6], region.UVs[7], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    AddVertex(_worldVerticesPositions[0], _worldVerticesPositions[1], z, region.UVs[0], region.UVs[1], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    break;
                }

                case MeshAttachment mesh:
                {
                    if (mesh.WorldVerticesLength > _worldVerticesPositions.Length) continue;

                    int texIdx = ResolveTexIdx(mesh.Region);
                    BeginBatch(texIdx, vertexIndex);
                    mesh.ComputeWorldVertices(slot, _worldVerticesPositions);

                    for (int j = 0; j < mesh.Triangles.Length; j++)
                    {
                        var idx = mesh.Triangles[j] << 1;
                        AddVertex(_worldVerticesPositions[idx], _worldVerticesPositions[idx + 1], z, mesh.UVs[idx], mesh.UVs[idx + 1], tintR, tintG, tintB, tintA, texIdx, ref vertexIndex);
                    }
                    break;
                }
            }

            z += zSpacing;
        }

        if (_vkCtx is not null)
            DrawVulkan(vertexIndex);
        else
        {
            BeginBlendMode();
            DrawCallGl(_vertices, (uint)vertexIndex);
            EndBlendMode();
        }
    }

    private int ResolveTexIdx(object region)
    {
        var atlasRegion = (AtlasRegion)region;
        if (_vkCtx is not null)
            return (int)atlasRegion.page.rendererObject;
        var texture = (Texture)atlasRegion.page.rendererObject;
        return _textures!.IndexOf(texture);
    }

    private void BeginBatch(int texIdx, int vertexStart)
    {
        if (_vkCtx is null) return;
        if (_vkBatches.Count > 0 && _vkBatches[^1].texIdx == texIdx) return;
        _vkBatches.Add((texIdx, vertexStart));
    }

    private void AddVertex(float x, float y, float z, float u, float v,
        float r, float g, float b, float a, float textureIndex, ref int vertexIndex)
    {
        _vertices[vertexIndex++] = new Vertex
        {
            Position = new Vector3(x, y, z),
            Uv = new Vector2(u, v),
            Color = new Vector4(r, g, b, a),
            TextureIndex = textureIndex,
        };
    }

    #region OpenGL draw

    private void DrawCallGl(ReadOnlySpan<Vertex> vertices, uint count)
    {
        for (int i = 0; i < _textures!.Count; i++)
            _textures[i].Bind((TextureUnit)((uint)TextureUnit.Texture0 + i));

        _shader!.Use();
        _shader.SetUniform("uTextures", _textures.Count == 1 ? [0] : [0, 1]);
        _shader.SetUniform("uModel", _modelMatrix);
        _shader.SetUniform("uView", _viewMatrix);
        _shader.SetUniform("uProjection", _projectionMatrix);

        _vbo!.Bind();
        _vertexBuffer!.Update(vertices, count);
        _gl!.CullFace(TriangleFace.Back);
        _gl.PolygonMode(TriangleFace.FrontAndBack, Silk.NET.OpenGL.PolygonMode.Fill);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, count);
    }

    private void BeginBlendMode()
    {
        _gl!.Enable(EnableCap.Blend);
        _gl.BlendFunc(SrcFactor, DestFactor);
        _gl.BlendEquation(GLEnum.FuncAdd);
    }

    private void EndBlendMode() => _gl!.Disable(EnableCap.Blend);

    #endregion

    #region Vulkan draw

    private unsafe void DrawVulkan(int vertexCount)
    {
        if (_vkBatches.Count == 0 || vertexCount == 0) return;

        var vk = _vkCtx!.Vk;
        var cb = _vkCtx.CurrentCommandBuffer;
        var extent = _vkCtx.SwapchainExtent;
        var imageIdx = (int)_vkCtx.CurrentImageIndex;

        // Update VP UBO
        *(VkVpUbo*)(void*)_vkUboMapped[imageIdx] = new VkVpUbo { View = _viewMatrix, Projection = _projectionMatrix };

        // Ensure vertex buffer capacity
        var requiredBytes = (ulong)(vertexCount * sizeof(Vertex));
        EnsureVertexBuffer(imageIdx, requiredBytes);

        // Upload vertices
        fixed (Vertex* src = _vertices)
            Unsafe.CopyBlock((void*)_vkVertexMapped[imageIdx], src, (uint)requiredBytes);

        // Set up commands
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _vkPipeline);

        var vb = _vkVertexBuffers[imageIdx];
        var vbOffset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &vbOffset);

        // Right-handed viewport (negative height flips Y)
        var viewport = new Viewport
        {
            X = 0, Y = (float)extent.Height,
            Width = (float)extent.Width, Height = -(float)extent.Height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        // Bind UBO descriptor set (set 0)
        var uboDs = _vkUboDescSets[imageIdx];
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _vkPipelineLayout, 0, 1, &uboDs, 0, null);

        // Push model matrix
        var model = _modelMatrix;
        vk.CmdPushConstants(cb, _vkPipelineLayout, ShaderStageFlags.VertexBit, 0, 64, &model);

        // Draw each texture batch
        for (int b = 0; b < _vkBatches.Count; b++)
        {
            var (texIdx, start) = _vkBatches[b];
            int count = b + 1 < _vkBatches.Count ? _vkBatches[b + 1].start - start : vertexCount - start;
            if (count <= 0) continue;

            // Bind texture descriptor set (set 1)
            var texDs = _vkTexDescSets[imageIdx][texIdx];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _vkPipelineLayout, 1, 1, &texDs, 0, null);

            vk.CmdDraw(cb, (uint)count, 1, (uint)start, 0);
        }
    }

    private unsafe void EnsureVertexBuffer(int imageIdx, ulong required)
    {
        if (required <= _vkVertexCapacity[imageIdx]) return;

        var vk = _vkCtx!.Vk;
        if (_vkVertexCapacity[imageIdx] > 0)
        {
            vk.UnmapMemory(_ctx.Device, _vkVertexMemory[imageIdx]);
            vk.DestroyBuffer(_ctx.Device, _vkVertexBuffers[imageIdx], null);
            vk.FreeMemory(_ctx.Device, _vkVertexMemory[imageIdx], null);
        }

        _vkVertexCapacity[imageIdx] = Math.Max(required, _vkVertexCapacity[imageIdx] == 0 ? (ulong)(MaxVertices * sizeof(Vertex)) : _vkVertexCapacity[imageIdx] * 2);
        CreateBuffer(_vkVertexCapacity[imageIdx],
            BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _vkVertexBuffers[imageIdx], out _vkVertexMemory[imageIdx]);

        void* ptr;
        vk.MapMemory(_ctx.Device, _vkVertexMemory[imageIdx], 0, _vkVertexCapacity[imageIdx], 0, &ptr);
        _vkVertexMapped[imageIdx] = (nint)ptr;
    }

    // Convenience alias so EnsureVertexBuffer can call _ctx
    private IVulkanContext _ctx => _vkCtx!;

    #endregion

    #region Vulkan setup

    private unsafe void CreateVkResources(IVulkanContext ctx, List<(byte[] Pixels, int Width, int Height)> imageData)
    {
        var imageCount = (int)ctx.SwapchainImageCount;
        var texCount = imageData.Count;

        // --- Textures ---
        _vkImages = new Image[texCount];
        _vkImageMemory = new DeviceMemory[texCount];
        _vkImageViews = new ImageView[texCount];

        for (int t = 0; t < texCount; t++)
            UploadTexture(ctx, imageData[t], t);

        // Shared sampler
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, samplerInfo, null, out _vkSampler);

        // --- VP UBOs (one per swapchain image) ---
        _vkUboBuffers = new VkBuffer[imageCount];
        _vkUboMemory = new DeviceMemory[imageCount];
        _vkUboMapped = new nint[imageCount];
        for (int i = 0; i < imageCount; i++)
        {
            CreateBuffer((ulong)sizeof(VkVpUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vkUboBuffers[i], out _vkUboMemory[i]);
            void* ptr;
            ctx.Vk.MapMemory(ctx.Device, _vkUboMemory[i], 0, (ulong)sizeof(VkVpUbo), 0, &ptr);
            _vkUboMapped[i] = (nint)ptr;
        }

        // --- Per-frame vertex buffers ---
        _vkVertexBuffers = new VkBuffer[imageCount];
        _vkVertexMemory = new DeviceMemory[imageCount];
        _vkVertexMapped = new nint[imageCount];
        _vkVertexCapacity = new ulong[imageCount];

        // --- Descriptor set layouts ---
        var uboBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit,
        };
        var uboLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1, PBindings = &uboBinding,
        };
        ctx.Vk.CreateDescriptorSetLayout(ctx.Device, uboLayoutInfo, null, out _vkUboLayout);

        var texBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
        };
        var texLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1, PBindings = &texBinding,
        };
        ctx.Vk.CreateDescriptorSetLayout(ctx.Device, texLayoutInfo, null, out _vkTexLayout);

        // --- Descriptor pool ---
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer,         DescriptorCount = (uint)imageCount },
            new() { Type = DescriptorType.CombinedImageSampler,  DescriptorCount = (uint)(imageCount * texCount) },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2, PPoolSizes = poolSizes,
            MaxSets = (uint)(imageCount + imageCount * texCount),
        };
        ctx.Vk.CreateDescriptorPool(ctx.Device, poolInfo, null, out _vkDescPool);

        // --- UBO descriptor sets ---
        var uboLayouts = stackalloc DescriptorSetLayout[imageCount];
        for (int i = 0; i < imageCount; i++) uboLayouts[i] = _vkUboLayout;
        var uboAllocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _vkDescPool,
            DescriptorSetCount = (uint)imageCount,
            PSetLayouts = uboLayouts,
        };
        _vkUboDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* ptr = _vkUboDescSets)
            ctx.Vk.AllocateDescriptorSets(ctx.Device, uboAllocInfo, ptr);

        for (int i = 0; i < imageCount; i++)
        {
            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = _vkUboBuffers[i], Offset = 0, Range = (ulong)sizeof(VkVpUbo),
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _vkUboDescSets[i], DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1,
                PBufferInfo = &bufInfo,
            };
            ctx.Vk.UpdateDescriptorSets(ctx.Device, 1, &write, 0, null);
        }

        // --- Texture descriptor sets [imageIdx][texIdx] ---
        int totalTexSets = imageCount * texCount;
        var texLayouts = stackalloc DescriptorSetLayout[totalTexSets];
        for (int i = 0; i < totalTexSets; i++) texLayouts[i] = _vkTexLayout;
        var texAllocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _vkDescPool,
            DescriptorSetCount = (uint)totalTexSets,
            PSetLayouts = texLayouts,
        };
        var flatTexSets = new DescriptorSet[totalTexSets];
        fixed (DescriptorSet* ptr = flatTexSets)
            ctx.Vk.AllocateDescriptorSets(ctx.Device, texAllocInfo, ptr);

        _vkTexDescSets = new DescriptorSet[imageCount][];
        for (int i = 0; i < imageCount; i++)
        {
            _vkTexDescSets[i] = new DescriptorSet[texCount];
            for (int t = 0; t < texCount; t++)
            {
                _vkTexDescSets[i][t] = flatTexSets[i * texCount + t];
                var imgInfo = new DescriptorImageInfo
                {
                    Sampler = _vkSampler,
                    ImageView = _vkImageViews[t],
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _vkTexDescSets[i][t], DstBinding = 0,
                    DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1,
                    PImageInfo = &imgInfo,
                };
                ctx.Vk.UpdateDescriptorSets(ctx.Device, 1, &write, 0, null);
            }
        }

        CreateVkPipeline(ctx);
    }

    private unsafe void UploadTexture(IVulkanContext ctx, (byte[] Pixels, int Width, int Height) data, int texIndex)
    {
        var vk = ctx.Vk;
        var imageSize = (ulong)(data.Width * data.Height * 4);

        // Staging buffer
        CreateBuffer(imageSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuf, out var stagingMem);

        void* mapped;
        vk.MapMemory(ctx.Device, stagingMem, 0, imageSize, 0, &mapped);
        fixed (byte* src = data.Pixels)
            Unsafe.CopyBlock(mapped, src, (uint)imageSize);
        vk.UnmapMemory(ctx.Device, stagingMem);

        // Device-local image
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            Extent = new Extent3D { Width = (uint)data.Width, Height = (uint)data.Height, Depth = 1 },
            MipLevels = 1, ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        vk.CreateImage(ctx.Device, imageInfo, null, out _vkImages[texIndex]);

        vk.GetImageMemoryRequirements(ctx.Device, _vkImages[texIndex], out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        vk.AllocateMemory(ctx.Device, allocInfo, null, out _vkImageMemory[texIndex]);
        vk.BindImageMemory(ctx.Device, _vkImages[texIndex], _vkImageMemory[texIndex], 0);

        var cb = BeginOneTimeCommands(ctx);
        TransitionImageLayout(ctx, cb, _vkImages[texIndex], ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new Extent3D { Width = (uint)data.Width, Height = (uint)data.Height, Depth = 1 },
        };
        vk.CmdCopyBufferToImage(cb, stagingBuf, _vkImages[texIndex], ImageLayout.TransferDstOptimal, 1, &region);

        TransitionImageLayout(ctx, cb, _vkImages[texIndex], ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndOneTimeCommands(ctx, cb);

        vk.DestroyBuffer(ctx.Device, stagingBuf, null);
        vk.FreeMemory(ctx.Device, stagingMem, null);

        // Image view
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _vkImages[texIndex],
            ViewType = ImageViewType.Type2D,
            Format = Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        };
        vk.CreateImageView(ctx.Device, viewInfo, null, out _vkImageViews[texIndex]);
    }

    private unsafe void CreateVkPipeline(IVulkanContext ctx)
    {
        var vk = ctx.Vk;

        var vertCode = File.ReadAllBytes("Content/Shaders/Spine/Spine.vk.vert.spv");
        var fragCode = File.ReadAllBytes("Content/Shaders/Spine/Spine.vk.frag.spv");
        var vertModule = CreateShaderModule(ctx, vertCode);
        var fragModule = CreateShaderModule(ctx, fragCode);
        var entry = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,   Module = vertModule, PName = entry },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entry },
        };

        // Vertex layout: Position(vec3, 0) + UV(vec2, 12) + Color(vec4, 20) + unused TextureIndex float = stride 40
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0, Stride = (uint)sizeof(Vertex), InputRate = VertexInputRate.Vertex,
        };
        var attribs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat,    Offset = 0  },
            new() { Location = 1, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32Sfloat,        Offset = 12 },
            new() { Location = 2, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32A32Sfloat,  Offset = 20 },
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,   PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attribs,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill, // disambiguate from Silk.NET.OpenGL.PolygonMode
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates,
        };

        // Pipeline layout: set 0 = UBO, set 1 = texture; push constant = model matrix
        var layouts = stackalloc DescriptorSetLayout[] { _vkUboLayout, _vkTexLayout };
        var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 64 };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2, PSetLayouts = layouts,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
        };
        vk.CreatePipelineLayout(ctx.Device, layoutInfo, null, out _vkPipelineLayout);

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2, PStages = stages,
            PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState, PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling, PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState, Layout = _vkPipelineLayout,
            RenderPass = ctx.RenderPass,
        };
        vk.CreateGraphicsPipelines(ctx.Device, default, 1, pipelineInfo, null, out _vkPipeline);

        SilkMarshal.Free((nint)entry);
        vk.DestroyShaderModule(ctx.Device, vertModule, null);
        vk.DestroyShaderModule(ctx.Device, fragModule, null);
    }

    #endregion

    #region Vulkan helpers

    private unsafe void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var ctx = _vkCtx!;
        var bufInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive,
        };
        ctx.Vk.CreateBuffer(ctx.Device, bufInfo, null, out buffer);
        ctx.Vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };
        ctx.Vk.AllocateMemory(ctx.Device, allocInfo, null, out memory);
        ctx.Vk.BindBufferMemory(ctx.Device, buffer, memory, 0);
    }

    private static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags props)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        throw new Exception("[Vulkan] No suitable memory type!");
    }

    private static unsafe CommandBuffer BeginOneTimeCommands(IVulkanContext ctx)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary, CommandPool = ctx.CommandPool, CommandBufferCount = 1,
        };
        CommandBuffer cb;
        ctx.Vk.AllocateCommandBuffers(ctx.Device, allocInfo, &cb);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ctx.Vk.BeginCommandBuffer(cb, beginInfo);
        return cb;
    }

    private static unsafe void EndOneTimeCommands(IVulkanContext ctx, CommandBuffer cb)
    {
        ctx.Vk.EndCommandBuffer(cb);
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb,
        };
        ctx.Vk.QueueSubmit(ctx.GraphicsQueue, 1, submitInfo, default);
        ctx.Vk.QueueWaitIdle(ctx.GraphicsQueue);
        ctx.Vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, &cb);
    }

    private static unsafe void TransitionImageLayout(IVulkanContext ctx, CommandBuffer cb, Image image,
        ImageLayout oldLayout, ImageLayout newLayout)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout, NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        };
        PipelineStageFlags srcStage, dstStage;
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        ctx.Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private static unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)ptr,
            };
            ctx.Vk.CreateShaderModule(ctx.Device, info, null, out var m);
            return m;
        }
    }

    #endregion

    public unsafe void Dispose()
    {
        if (_vkCtx is not null)
        {
            var vk = _vkCtx.Vk;
            vk.DeviceWaitIdle(_vkCtx.Device);

            for (int i = 0; i < _vkVertexCapacity.Length; i++)
            {
                if (_vkVertexCapacity[i] > 0)
                {
                    vk.UnmapMemory(_vkCtx.Device, _vkVertexMemory[i]);
                    vk.DestroyBuffer(_vkCtx.Device, _vkVertexBuffers[i], null);
                    vk.FreeMemory(_vkCtx.Device, _vkVertexMemory[i], null);
                }
            }
            for (int i = 0; i < _vkUboBuffers.Length; i++)
            {
                vk.UnmapMemory(_vkCtx.Device, _vkUboMemory[i]);
                vk.DestroyBuffer(_vkCtx.Device, _vkUboBuffers[i], null);
                vk.FreeMemory(_vkCtx.Device, _vkUboMemory[i], null);
            }

            vk.DestroyPipeline(_vkCtx.Device, _vkPipeline, null);
            vk.DestroyPipelineLayout(_vkCtx.Device, _vkPipelineLayout, null);
            vk.DestroyDescriptorPool(_vkCtx.Device, _vkDescPool, null);
            vk.DestroyDescriptorSetLayout(_vkCtx.Device, _vkUboLayout, null);
            vk.DestroyDescriptorSetLayout(_vkCtx.Device, _vkTexLayout, null);
            vk.DestroySampler(_vkCtx.Device, _vkSampler, null);

            for (int t = 0; t < _vkImageViews.Length; t++)
            {
                vk.DestroyImageView(_vkCtx.Device, _vkImageViews[t], null);
                vk.DestroyImage(_vkCtx.Device, _vkImages[t], null);
                vk.FreeMemory(_vkCtx.Device, _vkImageMemory[t], null);
            }
        }

        if (_textures is not null)
            foreach (var tex in _textures)
                tex.Dispose();
    }
}

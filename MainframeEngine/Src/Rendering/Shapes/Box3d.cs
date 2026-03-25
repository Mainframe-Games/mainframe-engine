using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

public class Box3d : ShapeBase, IDisposable
{
    #region Vertices

    // csharpier-ignore
    private static readonly float[] Vertices =
    [
        //  X      Y      Z      U     V     NX    NY    NZ
        // Back face  (Z = -0.5)  normal = (0, 0, -1)
         0.5f,  0.5f, -0.5f,  1.0f, 1.0f,  0.0f,  0.0f, -1.0f,
         0.5f, -0.5f, -0.5f,  1.0f, 0.0f,  0.0f,  0.0f, -1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,  0.0f,  0.0f, -1.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 0.0f,  0.0f,  0.0f, -1.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,  0.0f,  0.0f, -1.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 1.0f,  0.0f,  0.0f, -1.0f,
        // Front face (Z = +0.5)  normal = (0, 0, 1)
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,  0.0f,  0.0f,  1.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 0.0f,  0.0f,  0.0f,  1.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,  0.0f,  0.0f,  1.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 1.0f,  0.0f,  0.0f,  1.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 1.0f,  0.0f,  0.0f,  1.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,  0.0f,  0.0f,  1.0f,
        // Left face  (X = -0.5)  normal = (-1, 0, 0)
        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  1.0f, 1.0f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f, -1.0f,  0.0f,  0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f, -1.0f,  0.0f,  0.0f,
        -0.5f,  0.5f,  0.5f,  1.0f, 0.0f, -1.0f,  0.0f,  0.0f,
        // Right face (X = +0.5)  normal = (1, 0, 0)
         0.5f, -0.5f, -0.5f,  0.0f, 1.0f,  1.0f,  0.0f,  0.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 1.0f,  1.0f,  0.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,  1.0f,  0.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,  1.0f,  0.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  0.0f, 0.0f,  1.0f,  0.0f,  0.0f,
         0.5f, -0.5f, -0.5f,  0.0f, 1.0f,  1.0f,  0.0f,  0.0f,
        // Bottom face (Y = -0.5) normal = (0, -1, 0)
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f, -0.5f,  1.0f, 1.0f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 0.0f,  0.0f, -1.0f,  0.0f,
         0.5f, -0.5f,  0.5f,  1.0f, 0.0f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f,  0.5f,  0.0f, 0.0f,  0.0f, -1.0f,  0.0f,
        -0.5f, -0.5f, -0.5f,  0.0f, 1.0f,  0.0f, -1.0f,  0.0f,
        // Top face   (Y = +0.5)  normal = (0, 1, 0)
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,  0.0f,  1.0f,  0.0f,
         0.5f,  0.5f, -0.5f,  1.0f, 1.0f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f, -0.5f,  0.0f, 1.0f,  0.0f,  1.0f,  0.0f,
        -0.5f,  0.5f,  0.5f,  0.0f, 0.0f,  0.0f,  1.0f,  0.0f,
         0.5f,  0.5f,  0.5f,  1.0f, 0.0f,  0.0f,  1.0f,  0.0f,
    ];

    #endregion

    // --- UBO layout constants (must match GLSL LightsUBO std140 layout) ---
    // Header: ambientColor(vec4) + cameraPosition(vec4) + counts(ivec4) = 48 bytes
    // DirLight:   2 × vec4 = 32 bytes each
    // PointLight: 2 × vec4 = 32 bytes each
    // SpotLight:  4 × vec4 = 64 bytes each
    private const int LightsUboSize =
        48 +
        LightEnvironment.MaxDirectional * 32 +
        LightEnvironment.MaxPoint       * 32 +
        LightEnvironment.MaxSpot        * 64;

    // OpenGL
    private readonly GL? _gl;
    private readonly VertexArrayObject<float, uint>? _vertexArray;
    private readonly Shader? _shader;
    private uint _glLightsUbo;
    private readonly byte[] _glLightsUboData = new byte[LightsUboSize];

    // Vulkan
    private IVulkanContext? _vkCtx;
    private VkBuffer _vkVertexBuffer;
    private DeviceMemory _vkVertexBufferMemory;
    private PipelineLayout _vkPipelineLayout;
    private Pipeline _vkPipeline;

    // set=0  VP UBO
    private DescriptorSetLayout _vkVpDescSetLayout;
    private DescriptorPool _vkDescPool;
    private DescriptorSet[] _vkVpDescSets = null!;
    private VkBuffer[] _vkVpUboBuffers = null!;
    private DeviceMemory[] _vkVpUboMemory = null!;
    private nint[] _vkVpUboMapped = null!;

    // set=1  Lights UBO
    private DescriptorSetLayout _vkLightsDescSetLayout;
    private DescriptorSet[] _vkLightsDescSets = null!;
    private VkBuffer[] _vkLightsUboBuffers = null!;
    private DeviceMemory[] _vkLightsUboMemory = null!;
    private nint[] _vkLightsUboMapped = null!;

    [StructLayout(LayoutKind.Sequential)]
    private struct VpUbo
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstant
    {
        public Matrix4x4 Model; // 64 bytes
        public Vector4 Color;   // 16 bytes  → total 80 bytes
    }

    public Box3d(IRenderer renderer)
    {
        if (renderer.Backend == RenderingBackend.OpenGL)
        {
            _gl = renderer.GetGL();

            var vertexBuffer = new BufferObject<float>(_gl, Vertices, BufferTargetARB.ArrayBuffer);
            var indexBuffer  = new BufferObject<uint>(_gl, [], BufferTargetARB.ElementArrayBuffer);
            _vertexArray = new VertexArrayObject<float, uint>(_gl, vertexBuffer, indexBuffer);
            // stride = 8 floats; offsets: pos=0, uv=3, normal=5
            _vertexArray.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 8, 0);
            _vertexArray.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, 8, 3);
            _vertexArray.VertexAttributePointer(2, 3, VertexAttribPointerType.Float, 8, 5);

            _shader = new Shader(_gl,
                "Content/Shaders/Shapes/Shapes.vert",
                "Content/Shaders/Shapes/Shapes.frag");

            // Bind the shader's LightsUBO block to GL binding point 0
            _shader.BindUniformBlock("LightsUBO", 0);

            // Create and pre-allocate the lights UBO
            _glLightsUbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _glLightsUbo);
            unsafe
            {
                _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)LightsUboSize, (void*)0,
                    BufferUsageARB.DynamicDraw);
            }
        }
        else if (renderer is IVulkanContext vkCtx)
        {
            _vkCtx = vkCtx;
            CreateVkVertexBuffer(vkCtx);
            CreateVkPipeline(vkCtx);
        }
    }

    public void Draw(ICamera camera, LightEnvironment lights)
    {
        if (_gl is not null)
            DrawOpenGL(camera, lights);
        else if (_vkCtx is not null)
            DrawVulkan(camera, lights);
    }

    public unsafe void Dispose()
    {
        if (_gl is not null)
        {
            _shader?.Dispose();
            _vertexArray?.Dispose();
            _gl.DeleteBuffer(_glLightsUbo);
            return;
        }

        if (_vkCtx is null) return;
        var vk     = _vkCtx.Vk;
        var device = _vkCtx.Device;
        vk.DeviceWaitIdle(device);

        for (int i = 0; i < _vkVpUboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _vkVpUboMemory[i]);
            vk.DestroyBuffer(device, _vkVpUboBuffers[i], null);
            vk.FreeMemory(device, _vkVpUboMemory[i], null);
        }
        for (int i = 0; i < _vkLightsUboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _vkLightsUboMemory[i]);
            vk.DestroyBuffer(device, _vkLightsUboBuffers[i], null);
            vk.FreeMemory(device, _vkLightsUboMemory[i], null);
        }

        vk.DestroyDescriptorPool(device, _vkDescPool, null);
        vk.DestroyDescriptorSetLayout(device, _vkLightsDescSetLayout, null);
        vk.DestroyDescriptorSetLayout(device, _vkVpDescSetLayout, null);
        vk.DestroyPipeline(device, _vkPipeline, null);
        vk.DestroyPipelineLayout(device, _vkPipelineLayout, null);
        vk.DestroyBuffer(device, _vkVertexBuffer, null);
        vk.FreeMemory(device, _vkVertexBufferMemory, null);
    }

    // -------------------------------------------------------------------------
    // OpenGL draw
    // -------------------------------------------------------------------------

    private unsafe void DrawOpenGL(ICamera camera, LightEnvironment lights)
    {
        _shader!.Use();
        _shader.SetUniform("uModel",      ModelMatrix);
        _shader.SetUniform("uView",       camera.ViewMatrix);
        _shader.SetUniform("uProjection", camera.ProjectionMatrix);
        _shader.SetUniform("uColor",      Color);

        // Upload light data
        fixed (byte* ptr = _glLightsUboData)
        {
            WriteLightsUbo((nint)ptr, lights, camera.Position);
            _gl!.BindBuffer(BufferTargetARB.UniformBuffer, _glLightsUbo);
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)LightsUboSize, ptr);
        }
        _gl!.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _glLightsUbo);

        _vertexArray!.Bind();
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
    }

    // -------------------------------------------------------------------------
    // Vulkan draw
    // -------------------------------------------------------------------------

    private unsafe void DrawVulkan(ICamera camera, LightEnvironment lights)
    {
        var vk       = _vkCtx!.Vk;
        var cb       = _vkCtx.CurrentCommandBuffer;
        var extent   = _vkCtx.SwapchainExtent;
        var imageIdx = _vkCtx.CurrentImageIndex;

        // Update VP UBO
        *(VpUbo*)(void*)_vkVpUboMapped[imageIdx] = new VpUbo
        {
            View       = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix,
        };

        // Update Lights UBO
        WriteLightsUbo(_vkLightsUboMapped[imageIdx], lights, camera.Position);

        var viewport = new Viewport
        {
            X = 0, Y = (float)extent.Height,
            Width = (float)extent.Width, Height = -(float)extent.Height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _vkPipeline);

        var vb     = _vkVertexBuffer;
        var offset = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);

        var sets = stackalloc[] { _vkVpDescSets[imageIdx], _vkLightsDescSets[imageIdx] };
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _vkPipelineLayout,
            0, 2, sets, 0, null);

        var push = new PushConstant { Model = ModelMatrix, Color = new Vector4(Color, 1f) };
        vk.CmdPushConstants(cb, _vkPipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(PushConstant), &push);

        vk.CmdDraw(cb, 36, 1, 0, 0);
    }

    // -------------------------------------------------------------------------
    // Shared light UBO writer
    // -------------------------------------------------------------------------

    private static unsafe void WriteLightsUbo(nint ptr, LightEnvironment env, Vector3 camPos)
    {
        Unsafe.InitBlock((void*)ptr, 0, (uint)LightsUboSize);
        float* f = (float*)ptr;
        int    fi = 0;

        // ambientColor (vec4)
        f[fi++] = env.AmbientColor.X; f[fi++] = env.AmbientColor.Y;
        f[fi++] = env.AmbientColor.Z; fi++;

        // cameraPosition (vec4)
        f[fi++] = camPos.X; f[fi++] = camPos.Y; f[fi++] = camPos.Z; fi++;

        // counts (ivec4)
        int numDir   = Math.Min(env.DirectionalLights.Count, LightEnvironment.MaxDirectional);
        int numPoint = Math.Min(env.PointLights.Count,       LightEnvironment.MaxPoint);
        int numSpot  = Math.Min(env.SpotLights.Count,        LightEnvironment.MaxSpot);
        var ci = (int*)(f + fi);
        ci[0] = numDir; ci[1] = numPoint; ci[2] = numSpot; ci[3] = 0;
        fi += 4;

        // Directional lights — 2 × vec4 = 8 floats each
        for (int i = 0; i < numDir; i++)
        {
            var l = env.DirectionalLights[i];
            f[fi++] = l.Direction.X; f[fi++] = l.Direction.Y; f[fi++] = l.Direction.Z;
            f[fi++] = l.Intensity;
            f[fi++] = l.Color.X; f[fi++] = l.Color.Y; f[fi++] = l.Color.Z;
            fi++;  // padding w
        }
        fi += (LightEnvironment.MaxDirectional - numDir) * 8;

        // Point lights — 2 × vec4 = 8 floats each
        for (int i = 0; i < numPoint; i++)
        {
            var l = env.PointLights[i];
            f[fi++] = l.Position.X; f[fi++] = l.Position.Y; f[fi++] = l.Position.Z;
            f[fi++] = l.Range;
            f[fi++] = l.Color.X; f[fi++] = l.Color.Y; f[fi++] = l.Color.Z;
            f[fi++] = l.Intensity;
        }
        fi += (LightEnvironment.MaxPoint - numPoint) * 8;

        // Spot lights — 4 × vec4 = 16 floats each
        for (int i = 0; i < numSpot; i++)
        {
            var l = env.SpotLights[i];
            // positionRange
            f[fi++] = l.Position.X; f[fi++] = l.Position.Y; f[fi++] = l.Position.Z;
            f[fi++] = l.Range;
            // directionIntensity
            f[fi++] = l.Direction.X; f[fi++] = l.Direction.Y; f[fi++] = l.Direction.Z;
            f[fi++] = l.Intensity;
            // colorInner
            f[fi++] = l.Color.X; f[fi++] = l.Color.Y; f[fi++] = l.Color.Z;
            f[fi++] = float.Cos(float.DegreesToRadians(l.InnerConeAngle));
            // outerPad
            f[fi++] = float.Cos(float.DegreesToRadians(l.OuterConeAngle));
            fi += 3; // padding
        }
    }

    // -------------------------------------------------------------------------
    // Vulkan resource creation
    // -------------------------------------------------------------------------

    private unsafe void CreateVkVertexBuffer(IVulkanContext ctx)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;
        var size   = (ulong)(Vertices.Length * sizeof(float));

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        vk.MapMemory(device, stagingMemory, 0, size, 0, &mapped);
        fixed (float* src = Vertices)
            Unsafe.CopyBlock(mapped, src, (uint)size);
        vk.UnmapMemory(device, stagingMemory);

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vkVertexBuffer, out _vkVertexBufferMemory);

        CopyBuffer(ctx, stagingBuffer, _vkVertexBuffer, size);
        vk.DestroyBuffer(device, stagingBuffer, null);
        vk.FreeMemory(device, stagingMemory, null);
    }

    private unsafe void CreateVkPipeline(IVulkanContext ctx)
    {
        var vk         = ctx.Vk;
        var device     = ctx.Device;
        var imageCount = ctx.SwapchainImageCount;

        // --- Descriptor set layouts ---
        var vpBinding = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.VertexBit,
        };
        var vpLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &vpBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, vpLayoutInfo, null, out _vkVpDescSetLayout) != Result.Success)
            throw new Exception("[Vulkan] Failed to create VP descriptor set layout!");

        var lightsBinding = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.FragmentBit,
        };
        var lightsLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &lightsBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, lightsLayoutInfo, null, out _vkLightsDescSetLayout) != Result.Success)
            throw new Exception("[Vulkan] Failed to create Lights descriptor set layout!");

        // --- UBO buffers ---
        _vkVpUboBuffers    = new VkBuffer[imageCount];
        _vkVpUboMemory     = new DeviceMemory[imageCount];
        _vkVpUboMapped     = new nint[imageCount];
        _vkLightsUboBuffers = new VkBuffer[imageCount];
        _vkLightsUboMemory  = new DeviceMemory[imageCount];
        _vkLightsUboMapped  = new nint[imageCount];

        for (int i = 0; i < imageCount; i++)
        {
            CreateBuffer(ctx, (ulong)sizeof(VpUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vkVpUboBuffers[i], out _vkVpUboMemory[i]);
            void* ptr;
            vk.MapMemory(device, _vkVpUboMemory[i], 0, (ulong)sizeof(VpUbo), 0, &ptr);
            _vkVpUboMapped[i] = (nint)ptr;

            CreateBuffer(ctx, (ulong)LightsUboSize,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vkLightsUboBuffers[i], out _vkLightsUboMemory[i]);
            vk.MapMemory(device, _vkLightsUboMemory[i], 0, (ulong)LightsUboSize, 0, &ptr);
            _vkLightsUboMapped[i] = (nint)ptr;
        }

        // --- One pool for all descriptor sets (VP + Lights, imageCount each) ---
        var poolSize = new DescriptorPoolSize
        {
            Type            = DescriptorType.UniformBuffer,
            DescriptorCount = imageCount * 2,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType         = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes    = &poolSize,
            MaxSets       = imageCount * 2,
        };
        if (vk.CreateDescriptorPool(device, poolInfo, null, out _vkDescPool) != Result.Success)
            throw new Exception("[Vulkan] Failed to create descriptor pool!");

        // --- Allocate VP descriptor sets ---
        var vpLayouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) vpLayouts[i] = _vkVpDescSetLayout;
        var vpAlloc = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _vkDescPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = vpLayouts,
        };
        _vkVpDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _vkVpDescSets)
            if (vk.AllocateDescriptorSets(device, vpAlloc, p) != Result.Success)
                throw new Exception("[Vulkan] Failed to allocate VP descriptor sets!");

        // --- Allocate Lights descriptor sets ---
        var lightsLayouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) lightsLayouts[i] = _vkLightsDescSetLayout;
        var lightsAlloc = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _vkDescPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = lightsLayouts,
        };
        _vkLightsDescSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _vkLightsDescSets)
            if (vk.AllocateDescriptorSets(device, lightsAlloc, p) != Result.Success)
                throw new Exception("[Vulkan] Failed to allocate Lights descriptor sets!");

        // --- Write descriptor sets ---
        for (int i = 0; i < imageCount; i++)
        {
            var vpBuf = new DescriptorBufferInfo
                { Buffer = _vkVpUboBuffers[i], Offset = 0, Range = (ulong)sizeof(VpUbo) };
            var vpWrite = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _vkVpDescSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &vpBuf,
            };

            var lightsBuf = new DescriptorBufferInfo
                { Buffer = _vkLightsUboBuffers[i], Offset = 0, Range = (ulong)LightsUboSize };
            var lightsWrite = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _vkLightsDescSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &lightsBuf,
            };

            var writes = stackalloc[] { vpWrite, lightsWrite };
            vk.UpdateDescriptorSets(device, 2, writes, 0, null);
        }

        // --- Shaders ---
        var vertCode   = File.ReadAllBytes("Content/Shaders/Shapes/Shapes.vk.vert.spv");
        var fragCode   = File.ReadAllBytes("Content/Shaders/Shapes/Shapes.vk.frag.spv");
        var vertModule = CreateShaderModule(ctx, vertCode);
        var fragModule = CreateShaderModule(ctx, fragCode);
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc[]
        {
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName  = entryPoint,
            },
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName  = entryPoint,
            },
        };

        // Vertex layout: X Y Z  U V  NX NY NZ  → stride 32 bytes
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding   = 0,
            Stride    = 8 * sizeof(float),
            InputRate = VertexInputRate.Vertex,
        };
        var attribs = stackalloc VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat, Offset = 0  }, // pos
            new() { Location = 1, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32Sfloat,    Offset = 12 }, // uv
            new() { Location = 2, Binding = 0, Format = Silk.NET.Vulkan.Format.R32G32B32Sfloat, Offset = 20 }, // normal
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType                           = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount   = 1,
            PVertexBindingDescriptions      = &bindingDesc,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions    = attribs,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType                  = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology               = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType         = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount  = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType                   = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable        = false,
            RasterizerDiscardEnable = false,
            PolygonMode             = Silk.NET.Vulkan.PolygonMode.Fill,
            LineWidth               = 1f,
            CullMode                = CullModeFlags.BackBit,
            FrontFace               = FrontFace.Clockwise,
            DepthBiasEnable         = false,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType                = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable  = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType           = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable   = false,
            AttachmentCount = 1,
            PAttachments    = &colorBlendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState  = new PipelineDynamicStateCreateInfo
        {
            SType             = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates    = dynamicStates,
        };

        // Pipeline layout: set=0 (VP), set=1 (Lights), push constants (model + color)
        var setLayouts = stackalloc[] { _vkVpDescSetLayout, _vkLightsDescSetLayout };
        var pushRange  = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset     = 0,
            Size       = (uint)sizeof(PushConstant),
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = 2,
            PSetLayouts            = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges    = &pushRange,
        };
        if (vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out _vkPipelineLayout) != Result.Success)
            throw new Exception("[Vulkan] Failed to create pipeline layout!");

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType               = StructureType.GraphicsPipelineCreateInfo,
            StageCount          = 2,
            PStages             = stages,
            PVertexInputState   = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState      = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState   = &multisampling,
            PColorBlendState    = &colorBlend,
            PDynamicState       = &dynamicState,
            Layout              = _vkPipelineLayout,
            RenderPass          = ctx.RenderPass,
            Subpass             = 0,
        };
        if (vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out _vkPipeline) != Result.Success)
            throw new Exception("[Vulkan] Failed to create graphics pipeline!");

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    // -------------------------------------------------------------------------
    // Vulkan helpers (unchanged)
    // -------------------------------------------------------------------------

    private unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
        BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;
        var bi     = new BufferCreateInfo
        {
            SType       = StructureType.BufferCreateInfo,
            Size        = size,
            Usage       = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (vk.CreateBuffer(device, bi, null, out buffer) != Result.Success)
            throw new Exception("[Vulkan] Failed to create buffer!");
        vk.GetBufferMemoryRequirements(device, buffer, out var memReq);
        var ai = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, properties),
        };
        if (vk.AllocateMemory(device, ai, null, out memory) != Result.Success)
            throw new Exception("[Vulkan] Failed to allocate buffer memory!");
        vk.BindBufferMemory(device, buffer, memory, 0);
    }

    private unsafe void CopyBuffer(IVulkanContext ctx, VkBuffer src, VkBuffer dst, ulong size)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType              = StructureType.CommandBufferAllocateInfo,
            Level              = CommandBufferLevel.Primary,
            CommandPool        = ctx.CommandPool,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        vk.AllocateCommandBuffers(device, allocInfo, &cb);
        vk.BeginCommandBuffer(cb, new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        });
        var region = new BufferCopy { Size = size };
        vk.CmdCopyBuffer(cb, src, dst, 1, &region);
        vk.EndCommandBuffer(cb);
        var submitInfo = new SubmitInfo
        {
            SType              = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers    = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, submitInfo, default);
        vk.QueueWaitIdle(ctx.GraphicsQueue);
        vk.FreeCommandBuffers(device, ctx.CommandPool, 1, &cb);
    }

    private static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags properties)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        throw new Exception("[Vulkan] No suitable memory type found!");
    }

    private unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode    = (uint*)ptr,
            };
            if (ctx.Vk.CreateShaderModule(ctx.Device, ci, null, out var module) != Result.Success)
                throw new Exception("[Vulkan] Failed to create shader module!");
            return module;
        }
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>
/// Full-screen sky backdrop rendered before all other geometry.
/// Supports procedural gradient, equirectangular panoramic, and cubemap sky types.
/// </summary>
/// <remarks>
/// Camera data comes from the per-frame shared set 0 (<see cref="FrameContext"/>); the sky parameters are push
/// constants, and textured skies bind their image as set 1. Colours are authored in sRGB like every other
/// colour in the engine; the sun (<see cref="SunColor"/> × <see cref="SunIntensity"/>) is an HDR value.
/// </remarks>
public class SkyEnvironment : IDisposable
{
    // ─── Procedural parameters ────────────────────────────────────────────────

    /// <summary>Zenith / upper sky color (procedural only).</summary>
    public Vector3 SkyColor { get; set; } = new(0.18f, 0.48f, 0.87f);
    /// <summary>Color at the horizon band (procedural only).</summary>
    public Vector3 HorizonColor { get; set; } = new(0.70f, 0.85f, 1.00f);
    /// <summary>
    /// Default <see cref="GroundColor"/> (sRGB): a muted earth tone that displays as about (95, 86, 74) at the default
    /// exposure. Calibrated for the ACES tonemap, whose toe crushes darker values: the pre-M3 default
    /// (0.15, 0.14, 0.13) displays as (15, 13, 11), an almost black void below the horizon.
    /// </summary>
    public static readonly Vector3 DefaultGroundColor = new(0.42f, 0.39f, 0.35f);

    /// <summary>Color of the ground / nadir hemisphere (procedural only).</summary>
    public Vector3 GroundColor { get; set; } = DefaultGroundColor;
    /// <summary>World-space direction toward the sun (procedural only).</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new(0.3f, 1f, 0.5f));
    /// <summary>Sun disk color (procedural only).</summary>
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    /// <summary>Sun brightness multiplier (procedural only).</summary>
    public float SunIntensity { get; set; } = 20f;
    /// <summary>Angular radius of the sun disk in degrees. Default ≈ real sun (0.53°).</summary>
    public float SunAngularRadius { get; set; } = 0.53f;
    /// <summary>How sharply sky blends into horizon / ground. Higher = tighter band.</summary>
    public float HorizonSharpness { get; set; } = 6f;

    public SkyEnvironmentType Type { get; }

    // ─── Push constants (must match SkyParams in the sky fragment shaders, 96 bytes) ───

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyParams
    {
        public Vector4 SkyColor;
        public Vector4 HorizonColor;
        public Vector4 GroundColor;
        public Vector4 SunDirection;      // xyz = direction toward the sun
        public Vector4 SunColorIntensity; // rgb = colour, a = intensity
        public Vector4 Sun;               // x = cos(angular radius), y = horizon sharpness
    }

    // ─── Vulkan state ─────────────────────────────────────────────────────────

    private readonly IVulkanContext? _ctx;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _texSetLayout; // Panoramic / Cubemap only
    private DescriptorPool _descPool;
    private DescriptorSet _texSet;             // static: shared by every frame slot
    private GpuTexture? _texture;
    private bool _disposed;

    private bool HasTexture => Type is SkyEnvironmentType.Panoramic or SkyEnvironmentType.Cubemap;

    // ─── Constructor ──────────────────────────────────────────────────────────

    protected SkyEnvironment(
        IRenderer renderer,
        SkyEnvironmentType type,
        string? panoramicPath = null,
        string[]? cubeFacePaths = null)
    {
        Type = type;

        if (renderer is not IVulkanContext ctx) return;
        _ctx = ctx;

        switch (type)
        {
            case SkyEnvironmentType.Panoramic when panoramicPath is not null:
                _texture = GpuTexture.Load(ctx, panoramicPath, TextureColorSpace.Srgb, TextureSampling.LinearClamp);
                break;
            case SkyEnvironmentType.Cubemap when cubeFacePaths is not null:
                _texture = LoadCubemap(ctx, cubeFacePaths);
                break;
            case SkyEnvironmentType.Panoramic or SkyEnvironmentType.Cubemap:
                throw new ArgumentException($"A {type} sky needs its image path(s).");
        }

        CreatePipeline(ctx);
    }

    // ─── Public draw method ───────────────────────────────────────────────────

    /// <summary>
    /// Records sky draw commands into the current command buffer.
    /// Call this <em>first</em> in OnRender, before any other geometry.
    /// </summary>
    public unsafe void Draw(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_ctx is null || !_ctx.FrameStarted) return;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        var frame = _ctx.Frame;
        frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);

        PipelineBuilder.SetViewport(vk, cb, _ctx.SwapchainExtent, flipY: true); // same Y-flip as the scene
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        frame.Bind(cb, _pipelineLayout);
        if (HasTexture)
        {
            var set = _texSet;
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 1, 1, &set, 0, null);
        }

        var push = new SkyParams
        {
            SkyColor = new Vector4(SkyColor, 1f),
            HorizonColor = new Vector4(HorizonColor, 1f),
            GroundColor = new Vector4(GroundColor, 1f),
            SunDirection = new Vector4(Vector3.Normalize(SunDirection), 0f),
            SunColorIntensity = new Vector4(SunColor, SunIntensity),
            Sun = new Vector4(MathF.Cos(float.DegreesToRadians(SunAngularRadius)), HorizonSharpness, 0f, 0f),
        };
        vk.CmdPushConstants(cb, _pipelineLayout, FrameContext.PushConstantStages, 0, (uint)sizeof(SkyParams), &push);

        // Three vertices expand into the fullscreen triangle in the vertex shader.
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    // ─── Dispose ──────────────────────────────────────────────────────────────

    /// <summary>Releases the GPU objects through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_ctx is null || _disposed) return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_pipeline));
        deletions.Enqueue(GpuDeletion.Of(_pipelineLayout));
        deletions.Enqueue(GpuDeletion.Of(_descPool));
        deletions.Enqueue(GpuDeletion.Of(_texSetLayout));
        _texture?.Dispose();
    }

    // ─── Image loading ────────────────────────────────────────────────────────

    private static GpuTexture LoadCubemap(IVulkanContext ctx, string[] facePaths)
    {
        if (facePaths.Length != 6)
            throw new ArgumentException("Cubemap requires exactly 6 face paths (+X, -X, +Y, -Y, +Z, -Z).", nameof(facePaths));

        byte[]? pixels = null;
        var size = 0;
        for (var i = 0; i < 6; i++)
        {
            var face = ImageResult.FromMemory(File.ReadAllBytes(ContentPaths.Resolve(facePaths[i])), ColorComponents.RedGreenBlueAlpha)
                       ?? throw new InvalidDataException($"Could not decode cubemap face '{facePaths[i]}'.");
            if (face.Width != face.Height || (pixels is not null && face.Width != size))
                throw new InvalidDataException($"Cubemap faces must be square and the same size ('{facePaths[i]}' is {face.Width}x{face.Height}).");
            size = face.Width;
            pixels ??= new byte[size * size * 4 * 6];
            face.Data.CopyTo(pixels, i * size * size * 4);
        }

        return GpuTexture.CreateCube(ctx, (uint)size, pixels, TextureColorSpace.Srgb, TextureSampling.LinearClamp);
    }

    // ─── Pipeline creation ────────────────────────────────────────────────────

    private unsafe void CreatePipeline(IVulkanContext ctx)
    {
        if (HasTexture)
        {
            _texSetLayout = PipelineBuilder.CreateSetLayout(ctx,
                [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
                "sky texture");
            _descPool = PipelineBuilder.CreatePool(ctx, 1,
                [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "sky texture");
            _texSet = PipelineBuilder.AllocateSet(ctx, _descPool, _texSetLayout, "sky texture");
            PipelineBuilder.WriteImage(ctx, _texSet, 0, _texture!.Descriptor);
            _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [_texSetLayout], "sky");
        }
        else
        {
            _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [], "sky");
        }

        var fragment = Type switch
        {
            SkyEnvironmentType.Procedural => "Shaders/Sky/Sky.Procedural.vk.frag.spv",
            SkyEnvironmentType.Panoramic => "Shaders/Sky/Sky.Panoramic.vk.frag.spv",
            SkyEnvironmentType.Cubemap => "Shaders/Sky/Sky.Cubemap.vk.frag.spv",
            _ => throw new InvalidOperationException($"Unknown sky type {Type}."),
        };

        // No vertex input (fullscreen triangle), no culling, no depth test or write: drawn first, behind everything.
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _pipelineLayout, ctx.RenderPass,
            "Shaders/Sky/Sky.vk.vert.spv", fragment, [], [], "sky");
    }
}

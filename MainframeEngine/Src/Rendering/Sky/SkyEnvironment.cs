using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>
/// Full-screen sky backdrop rendered before all other geometry.
/// Supports procedural gradient, equirectangular panoramic, cubemap and physical (atmosphere) sky types.
/// </summary>
/// <remarks>
/// Camera data comes from the per-frame shared set 0 (<see cref="FrameContext"/>); the sky parameters are push
/// constants, and textured skies bind their image as set 1 (the physical sky its sky-view LUT). Colours are authored
/// in sRGB like every other colour in the engine; the sun (<see cref="SunColor"/> × <see cref="SunIntensity"/>) is an
/// HDR value.
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
    /// <summary>World-space direction toward the sun (procedural and physical).</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new(0.3f, 1f, 0.5f));
    /// <summary>Sun disk color (procedural); the sun's colour (physical), sRGB.</summary>
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    /// <summary>Sun brightness multiplier (procedural); the sun's illuminance, a light's <c>Energy</c> (physical).</summary>
    public float SunIntensity { get; set; } = 20f;
    /// <summary>Angular radius of the sun disk in degrees. Default ≈ real sun (0.53°).</summary>
    public float SunAngularRadius { get; set; } = 0.53f;
    /// <summary>How sharply sky blends into horizon / ground. Higher = tighter band.</summary>
    public float HorizonSharpness { get; set; } = 6f;

    // ─── Physical parameters (ADR 0154) ──────────────────────────────────────

    /// <summary>
    /// The atmosphere of a <see cref="SkyEnvironmentType.Physical"/> sky. Its sun is <see cref="SunDirection"/>, with
    /// <see cref="SunColor"/> (sRGB) × <see cref="SunIntensity"/> as the sun's illuminance (a
    /// <see cref="DirectionalLight3D"/>'s colour and energy; <see cref="WorldEnvironment"/> copies them from the world's
    /// first one).
    /// </summary>
    public PhysicalSkySettings Physical { get; set; } = new();

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
    private DescriptorSetLayout _texSetLayout; // Panoramic / Cubemap / Physical only
    private DescriptorPool _descPool;
    private DescriptorSet _texSet;             // static: shared by every frame slot
    private GpuTexture? _texture;
    private PhysicalSkyLuts? _luts;            // Physical only
    private bool _disposed;

    // Set 1 binding 0: the image (Panoramic, Cubemap) or the sky-view LUT (Physical).
    private bool HasTexture => Type is SkyEnvironmentType.Panoramic or SkyEnvironmentType.Cubemap or SkyEnvironmentType.Physical;

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
            case SkyEnvironmentType.Physical:
                _luts = new PhysicalSkyLuts(ctx);
                break;
        }

        CreatePipeline(ctx);
    }

    // ─── Public draw methods ──────────────────────────────────────────────────

    /// <summary>
    /// Records the sky's offscreen work: for a <see cref="SkyEnvironmentType.Physical"/> sky, the atmosphere LUTs that
    /// its parameters or the sun's elevation invalidated (nothing when unchanged). Call once per frame with no render
    /// pass active, before <see cref="Draw"/> (the render server does, at the start of its offscreen work); the other
    /// sky types have nothing to do.
    /// </summary>
    public void Prepare(CommandBuffer cb)
    {
        if (_luts is null || _ctx is null || !_ctx.FrameStarted) return;
        var physical = Physical;
        var atmosphere = physical.ToAtmosphere();
        _luts.Update(cb, atmosphere, ViewRadius(atmosphere, physical), SafeNormalize(SunDirection).Y);
    }

    /// <summary>
    /// Records sky draw commands into the current command buffer.
    /// Call this <em>first</em> in OnRender, before any other geometry. A physical sky draws nothing until
    /// <see cref="Prepare"/> has rendered its LUTs.
    /// </summary>
    public void Draw(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_ctx is null || !_ctx.FrameStarted || !CanDraw) return;

        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;
        var frame = _ctx.Frame;
        frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);

        PipelineBuilder.SetViewport(vk, cb, frame.Extent, flipY: true); // same Y-flip as the scene; the current view's size
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        frame.Bind(cb, _pipelineLayout);
        BindResources(cb, _pipelineLayout);

        // Three vertices expand into the fullscreen triangle in the vertex shader.
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    // ─── Drawing the sky into another pass (sky captures) ─────────────────────

    /// <summary>This sky type's fragment shader (its vertex shader is <c>Shaders/Sky/Sky.vk.vert.spv</c>).</summary>
    internal string FragmentShaderPath => FragmentShader(Type);

    /// <summary>
    /// Set 1 of the sky's pipeline layout (after the frame's set 0, with the frame's push range), or a null handle when
    /// the type binds nothing. A pipeline that draws this sky into another pass builds its layout with it.
    /// </summary>
    internal DescriptorSetLayout ResourceSetLayout => _texSetLayout;

    /// <summary>False while a physical sky's LUTs have not been rendered yet (<see cref="Prepare"/>).</summary>
    internal bool CanDraw => _luts is null || _luts.Ready;

    /// <summary>The physical sky's LUTs (tests); null for the other types.</summary>
    internal PhysicalSkyLuts? Luts => _luts;

    /// <summary>
    /// Binds set 1 (when the type has one) and pushes <c>SkyParams</c> for a pipeline whose layout is set 0 (frame) +
    /// <see cref="ResourceSetLayout"/> with the frame's push range. The caller binds the pipeline and set 0 (per view or
    /// cube face), sets the viewport and draws three vertices; nothing here depends on the target's size.
    /// </summary>
    internal unsafe void BindResources(CommandBuffer cb, PipelineLayout layout)
    {
        var vk = _ctx!.Vk;
        if (HasTexture)
        {
            var set = _texSet;
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 1, 1, &set, 0, null);
        }

        var push = Type == SkyEnvironmentType.Physical ? PhysicalParams() : new SkyParams
        {
            SkyColor = new Vector4(SkyColor, 1f),
            HorizonColor = new Vector4(HorizonColor, 1f),
            GroundColor = new Vector4(GroundColor, 1f),
            SunDirection = new Vector4(Vector3.Normalize(SunDirection), 0f),
            SunColorIntensity = new Vector4(SunColor, SunIntensity),
            Sun = new Vector4(MathF.Cos(float.DegreesToRadians(SunAngularRadius)), HorizonSharpness, 0f, 0f),
        };
        vk.CmdPushConstants(cb, layout, FrameContext.PushConstantStages, 0, (uint)sizeof(SkyParams), &push);
    }

    /// <summary>
    /// The physical sun's illuminance in the engine's units: its linear colour × <see cref="SunIntensity"/> ×
    /// <see cref="PhysicalSkySettings.EnergyMultiplier"/>. The sky is the sky-view LUT (per unit illuminance) × π × this.
    /// </summary>
    internal Vector3 PhysicalSunIlluminance => ColorSpace.SrgbToLinear(SunColor) * (SunIntensity * Physical.EnergyMultiplier);

    // SkyParams for Sky.Physical.vk.frag (the field meanings are listed there).
    private SkyParams PhysicalParams()
    {
        var physical = Physical;
        var atmosphere = physical.ToAtmosphere();
        var sun = SafeNormalize(SunDirection);
        var illuminance = PhysicalSunIlluminance;
        var transmittance = AtmosphereModel.SunTransmittance(atmosphere, physical.AltitudeMeters, sun);
        var radius = float.DegreesToRadians(SunAngularRadius * MathF.Max(physical.SunDiskScale, 0f));
        return new SkyParams
        {
            SkyColor = new Vector4(ViewRadius(atmosphere, physical), atmosphere.BottomRadius, atmosphere.TopRadius, 0f),
            HorizonColor = new Vector4(illuminance * AtmosphereModel.RadianceScale, 0f),
            SunDirection = new Vector4(sun, 0f),
            SunColorIntensity = new Vector4(illuminance * transmittance * AtmosphereModel.SunDiscRadiance, 1f),
            Sun = new Vector4(MathF.Cos(radius), 0f, 0f, 0f),
        };
    }

    private static float ViewRadius(in AtmosphereParameters atmosphere, in PhysicalSkySettings physical) =>
        AtmosphereModel.ViewRadius(atmosphere, physical.AltitudeMeters);

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;

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
        _luts?.Dispose();
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
            PipelineBuilder.WriteImage(ctx, _texSet, 0, _luts?.SkyViewDescriptor ?? _texture!.Descriptor);
            _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [_texSetLayout], "sky");
        }
        else
        {
            _pipelineLayout = ctx.Frame.CreatePipelineLayout(null, [], "sky");
        }

        // No vertex input (fullscreen triangle), no culling, no depth test or write: drawn first, behind everything.
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _pipelineLayout, ctx.RenderPass,
            "Shaders/Sky/Sky.vk.vert.spv", FragmentShader(Type), [], [], "sky");
    }

    private static string FragmentShader(SkyEnvironmentType type) => type switch
    {
        SkyEnvironmentType.Procedural => "Shaders/Sky/Sky.Procedural.vk.frag.spv",
        SkyEnvironmentType.Panoramic => "Shaders/Sky/Sky.Panoramic.vk.frag.spv",
        SkyEnvironmentType.Cubemap => "Shaders/Sky/Sky.Cubemap.vk.frag.spv",
        SkyEnvironmentType.Physical => "Shaders/Sky/Sky.Physical.vk.frag.spv",
        _ => throw new InvalidOperationException($"Unknown sky type {type}."),
    };
}

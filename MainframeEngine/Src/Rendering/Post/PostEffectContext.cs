using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The images of the view being post-processed (ADR 0163), all the scene's size (<see cref="Extent"/>). Views are
/// <c>default</c> when the image does not exist this frame (no prepass: no <see cref="Velocity"/>). Image views change on
/// resize: <see cref="Generation"/> moves, and effects rewrite the descriptor sets that sample them. There are no
/// view-space normals: GTAO rebuilds them from depth; a normals attachment would be a second prepass output.
/// </summary>
internal sealed class SceneTextures
{
    /// <summary>The HDR colour format (<see cref="VulkanRenderer.SceneColorFormat"/>).</summary>
    public const Format ColorFormat = Format.R16G16B16A16Sfloat;

    /// <summary>The motion-vector format: screen-UV motion, <c>previousUv = uv − velocity</c>.</summary>
    public const Format VelocityFormat = Format.R16G16Sfloat;

    /// <summary>The LDR (tonemapped, display-encoded) format of the <see cref="PostStage.AfterTonemap"/> stage.</summary>
    public const Format LdrFormat = Format.R8G8B8A8Unorm;

    public Extent2D Extent { get; internal set; }

    /// <summary>Bumped when the views below were recreated (resize).</summary>
    public int Generation { get; internal set; }

    /// <summary>
    /// The linear HDR scene colour (<see cref="ColorFormat"/>, <c>SHADER_READ_ONLY_OPTIMAL</c>) in
    /// <see cref="PostStage.BeforeTonemap"/>: what the scene pass and the effects before this one produced. Not rendered
    /// yet in <see cref="PostStage.AfterPrepass"/>.
    /// </summary>
    public ImageView Color { get; internal set; }

    /// <summary>
    /// The scene depth (<see cref="DepthFormat"/>, depth aspect, <c>DEPTH_STENCIL_READ_ONLY_OPTIMAL</c>; 1 = far, the
    /// sky): the prepass's opaque and cutout depth in <see cref="PostStage.AfterPrepass"/>, the scene pass's (which adds
    /// what draws outside the prepass: the grid, Spine) in <see cref="PostStage.BeforeTonemap"/>. Read it with a
    /// non-filtering sampler (<see cref="PointSampler"/>) or <c>Load</c>.
    /// </summary>
    public ImageView Depth { get; internal set; }

    public Format DepthFormat { get; internal set; }

    /// <summary>
    /// Screen-space motion (<see cref="VelocityFormat"/>, <c>SHADER_READ_ONLY_OPTIMAL</c>) written by the prepass: this
    /// pixel's UV minus its UV last frame, both unjittered (0 where nothing moved). Camera motion everywhere, object motion
    /// for moving nodes, the wind for foliage; the sky gets the camera rotation. <c>default</c> without a prepass this frame.
    /// </summary>
    public ImageView Velocity { get; internal set; }

    /// <summary>True when the depth prepass ran this frame (<see cref="Depth"/> before lighting, <see cref="Velocity"/>).</summary>
    public bool HasPrepass { get; internal set; }

    /// <summary>
    /// The tonemapped, display-encoded image (<see cref="LdrFormat"/>, <c>SHADER_READ_ONLY_OPTIMAL</c>) an
    /// <see cref="PostStage.AfterTonemap"/> effect reads: the tonemap's output, or the previous effect's.
    /// </summary>
    public ImageView Ldr { get; internal set; }

    /// <summary>Nearest, clamp-to-edge: depth, velocity and texel reads.</summary>
    public Sampler PointSampler { get; internal set; }

    /// <summary>Bilinear, clamp-to-edge.</summary>
    public Sampler LinearSampler { get; internal set; }
}

/// <summary>
/// The camera of the view being post-processed (ADR 0163). <see cref="Projection"/> and <see cref="ViewProjection"/> are
/// unjittered; <see cref="JitteredProjection"/> is what the view rasterised with this frame.
/// </summary>
internal readonly record struct PostCamera(
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 JitteredProjection,
    Matrix4x4 ViewProjection,
    Matrix4x4 PreviousViewProjection,
    Vector2 Jitter,
    Vector2 PreviousJitter,
    bool HistoryValid,
    Vector3 Position,
    float Near,
    float Far)
{
    /// <summary>The camera's inverse unjittered view-projection (world positions from depth), or identity when singular.</summary>
    public Matrix4x4 InverseViewProjection => Matrix4x4.Invert(ViewProjection, out var inverse) ? inverse : Matrix4x4.Identity;
}

/// <summary>What the renderer provides for an <see cref="PostStage.AfterTonemap"/> effect's output pass and the HDR write-back.</summary>
internal interface IPostOutput
{
    /// <summary>The render pass of the swapchain image the last <see cref="PostStage.AfterTonemap"/> effect draws into.</summary>
    RenderPass PresentPass { get; }

    /// <summary>True when the swapchain view encodes sRGB itself: an LDR effect drawing into it must decode first.</summary>
    bool PresentEncodesSrgb { get; }

    /// <summary>Begins the swapchain pass (the overlay renderers draw after the effect, in the same pass).</summary>
    void BeginPresentPass(CommandBuffer cb);

    /// <summary>The render pass of an intermediate <see cref="SceneTextures.LdrFormat"/> target.</summary>
    RenderPass LdrPass { get; }

    /// <summary>Begins the next intermediate LDR target (the stage's ping-pong).</summary>
    void BeginLdrPass(CommandBuffer cb);

    /// <summary>Ends it (with the barrier that makes it readable) and makes it <see cref="SceneTextures.Ldr"/>.</summary>
    void EndLdrPass(CommandBuffer cb);

    /// <summary>Records a fullscreen copy of <paramref name="source"/> (scene-sized, HDR) into the scene colour.</summary>
    void CopyToSceneColor(CommandBuffer cb, ImageView source);
}

/// <summary>
/// Everything a <see cref="PostEffect"/> records with (ADR 0163): the command buffer, the stage, the frame's settings, the
/// scene's images (<see cref="Scene"/>), the target pool with ping-pong histories (<see cref="Targets"/>), the camera with
/// jittered and unjittered matrices (<see cref="Camera"/>) and the frame's timing. One instance per stack, refilled for
/// each stage; never kept by effects past the call.
/// </summary>
internal sealed class PostEffectContext
{
    private readonly IPostOutput? _output;
    private bool _outputOpen;

    internal PostEffectContext(IVulkanContext vulkan, PostTargetPool<RenderTarget> targets, IPostOutput? output)
    {
        Vulkan = vulkan;
        Targets = targets;
        _output = output;
    }

    /// <summary>For tests: no GPU, no targets.</summary>
    internal PostEffectContext()
    {
        Vulkan = null!;
        Targets = null!;
    }

    public IVulkanContext Vulkan { get; }
    public CommandBuffer CommandBuffer { get; internal set; }
    public PostStage Stage { get; internal set; }
    public PostEffectSettings Settings { get; internal set; }
    public SceneTextures Scene { get; } = new();

    /// <summary>Named targets and ping-pong histories that follow the scene's size.</summary>
    public PostTargetPool<RenderTarget> Targets { get; }

    public PostCamera Camera { get; internal set; }

    /// <summary>True when the view has a camera this frame (no camera: nothing was drawn; the effects still run).</summary>
    public bool HasCamera { get; internal set; }

    public ulong FrameNumber { get; internal set; }

    /// <summary>Seconds since the last frame (<see cref="IVulkanContext.FrameDeltaTime"/>).</summary>
    public float DeltaTime { get; internal set; }

    /// <summary>Shader time in seconds (<see cref="FrameContext.Time"/>).</summary>
    public float Time { get; internal set; }

    /// <summary>The frame's exposure (<see cref="IVulkanContext.Exposure"/>, or the Godot tonemap's).</summary>
    public float Exposure { get; internal set; }

    /// <summary>The view's jitter sample index this frame (<see cref="FrameContext.JitterIndex"/> for the main view; 0 without jitter).</summary>
    public int JitterIndex { get; internal set; }

    /// <summary>The sun's screen position and fade for the light shafts (ADR 0160), from the view's camera and world.</summary>
    public LightShaftsSun LightShaftsSun { get; internal set; }

    /// <summary>True for the last enabled effect of its stage (an <see cref="PostStage.AfterTonemap"/> one draws into the swapchain).</summary>
    public bool IsLastInStage { get; internal set; }

    /// <summary>
    /// The render pass an <see cref="PostStage.AfterTonemap"/> effect draws into this frame: the swapchain's when it is the
    /// last one, else an intermediate LDR target's. Build (and cache) a pipeline per render pass it is handed.
    /// </summary>
    public RenderPass OutputRenderPass => _output is null ? default : IsLastInStage ? _output.PresentPass : _output.LdrPass;

    /// <summary>True when the output encodes sRGB itself (the swapchain's sRGB view): decode the LDR values before writing.</summary>
    public bool OutputEncodesSrgb => IsLastInStage && _output is { PresentEncodesSrgb: true };

    /// <summary>
    /// Begins the <see cref="PostStage.AfterTonemap"/> output pass (<see cref="OutputRenderPass"/>, the scene's size);
    /// draw one fullscreen triangle from <see cref="SceneTextures.Ldr"/>, then call <see cref="EndOutput"/>.
    /// </summary>
    public void BeginOutput()
    {
        if (Stage != PostStage.AfterTonemap || _output is null)
            throw new InvalidOperationException("Only AfterTonemap effects draw into an output pass.");
        if (_outputOpen)
            throw new InvalidOperationException("The output pass is already open.");
        _outputOpen = true;
        if (IsLastInStage)
            _output.BeginPresentPass(CommandBuffer);
        else
            _output.BeginLdrPass(CommandBuffer);
    }

    /// <summary>Ends the output pass (the swapchain pass stays open for the overlay renderers).</summary>
    public void EndOutput()
    {
        if (!_outputOpen)
            throw new InvalidOperationException("No output pass is open.");
        _outputOpen = false;
        if (!IsLastInStage)
            _output!.EndLdrPass(CommandBuffer);
    }

    /// <summary>True when an effect began an output pass it has not ended (the stack checks after each effect).</summary>
    internal bool OutputOpen => _outputOpen;

    /// <summary>
    /// <see cref="PostStage.BeforeTonemap"/>: replaces the HDR scene colour with <paramref name="source"/> (a scene-sized
    /// image in <c>SHADER_READ_ONLY_OPTIMAL</c>, e.g. TAA's resolved history), so every later effect and the tonemap read
    /// it. One fullscreen pass; no render pass may be active.
    /// </summary>
    public void CopyToSceneColor(ImageView source)
    {
        if (Stage != PostStage.BeforeTonemap || _output is null)
            throw new InvalidOperationException("Only BeforeTonemap effects write the scene colour.");
        _output.CopyToSceneColor(CommandBuffer, source);
    }
}

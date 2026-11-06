using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The texture a <see cref="UiPanel3D"/>'s context renders into (G11.5, ADR 0252): its size and how it is resolved, and
/// — with the Vulkan UI renderer — the panel's RGBA8 image with mips that its material borrows
/// (<see cref="Texture2D.FromGpuSource"/>). Headless servers keep only the counters.
/// </summary>
internal sealed class UiPanelTarget
{
    /// <summary>Context and image size in pixels.</summary>
    public int Width { get; set; } = 1;

    public int Height { get; set; } = 1;

    /// <summary>What an opaque panel is composited over: sRGB-encoded RGB, alpha 1.</summary>
    public Vector4 Background { get; set; } = new(0, 0, 0, 1);

    /// <summary>Straight alpha instead of compositing over <see cref="Background"/>.</summary>
    public bool Transparent { get; set; }

    /// <summary>Times the server rendered the panel's context into the command list.</summary>
    public int Renders { get; internal set; }

    /// <summary>Times the GPU resolved it into <see cref="Image"/> (0 headless).</summary>
    public int Resolves { get; internal set; }

    /// <summary>A recorded render never reached the GPU (a skipped frame): the server renders the panel again.</summary>
    public bool NeedsRender { get; internal set; }

    /// <summary>A render of this frame will resolve into <see cref="Image"/> before the scene samples it.</summary>
    internal bool Scheduled { get; set; }

    /// <summary>The panel image (Vulkan renderer only; null before the first render).</summary>
    internal GpuImage? Image { get; set; }

    /// <summary>The image a material may sample now: resolved before, or resolving before this frame's scene pass.</summary>
    internal GpuImage? SampledImage => Resolves > 0 || Scheduled ? Image : null;

    internal ImageView Mip0;
    internal Framebuffer Framebuffer;
}

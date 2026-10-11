using Silk.NET.SDL;

namespace MainframeEngine.Tests.Core;

/// <summary>How <see cref="WindowMode"/> maps to SDL's window flags (the real window needs a display: render tests and QA).</summary>
public sealed class WindowModeTests
{
    [Theory]
    [InlineData(WindowMode.Windowed, 0u)]
    [InlineData(WindowMode.Maximized, 0u)] // maximized is a windowed state, not a fullscreen flag
    [InlineData(WindowMode.Fullscreen, 0x1001u)] // SDL_WINDOW_FULLSCREEN_DESKTOP: borderless, no mode change
    [InlineData(WindowMode.ExclusiveFullscreen, 0x1u)] // SDL_WINDOW_FULLSCREEN: a display mode change
    public void ModesSetTheirSdlFullscreenFlags(WindowMode mode, uint flags) => Assert.Equal(flags, WindowModeFlags.ToSdlFullscreen(mode));

    [Fact]
    public void SdlFlagsReadBackAsTheMode()
    {
        const uint shown = (uint)WindowFlags.Shown, vulkan = (uint)WindowFlags.Vulkan, resizable = (uint)WindowFlags.Resizable;
        Assert.Equal(WindowMode.Windowed, WindowModeFlags.FromSdl(shown | vulkan | resizable));
        Assert.Equal(WindowMode.Maximized, WindowModeFlags.FromSdl(shown | (uint)WindowFlags.Maximized));
        Assert.Equal(WindowMode.Fullscreen, WindowModeFlags.FromSdl(shown | (uint)WindowFlags.FullscreenDesktop));
        Assert.Equal(WindowMode.ExclusiveFullscreen, WindowModeFlags.FromSdl(shown | (uint)WindowFlags.Fullscreen));
        Assert.Equal(WindowMode.Fullscreen, WindowModeFlags.FromSdl((uint)WindowFlags.FullscreenDesktop | (uint)WindowFlags.Maximized));
        foreach (var mode in Enum.GetValues<WindowMode>())
        {
            if (mode != WindowMode.Maximized)
                Assert.Equal(mode, WindowModeFlags.FromSdl(WindowModeFlags.ToSdlFullscreen(mode)));
        }
    }
}

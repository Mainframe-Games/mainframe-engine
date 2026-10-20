using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace MainframeEngine;

/// <summary><see cref="IWindowControl"/> over the engine's Silk.NET (SDL) window.</summary>
/// <remarks>
/// The window mode goes to SDL directly (<see cref="WindowModeFlags"/>): Silk.NET 2.23's <c>WindowState.Fullscreen</c> is
/// <c>SDL_SetWindowFullscreen(window, SDL_WINDOW_FULLSCREEN)</c>, exclusive fullscreen, and its <c>WindowState.Normal</c> only
/// calls <c>SDL_RestoreWindow</c>, which does not leave fullscreen.
/// </remarks>
internal sealed unsafe class SilkWindowControl(IWindow window, Engine? engine = null) : IWindowControl
{
    public string Title
    {
        get => window.Title;
        set => window.Title = value;
    }

    public bool Fullscreen
    {
        get => Mode is WindowMode.Fullscreen or WindowMode.ExclusiveFullscreen;
        set => Mode = value ? WindowMode.Fullscreen : WindowMode.Windowed;
    }

    public WindowMode Mode
    {
        get
        {
            if (!TryGetSdl(out var sdl, out var handle))
                return window.WindowState switch
                {
                    WindowState.Maximized => WindowMode.Maximized,
                    WindowState.Fullscreen => WindowMode.ExclusiveFullscreen,
                    _ => WindowMode.Windowed,
                };
            return WindowModeFlags.FromSdl(sdl.GetWindowFlags(handle));
        }
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown window mode.");
            if (!TryGetSdl(out var sdl, out var handle))
            {
                window.WindowState = value switch
                {
                    WindowMode.Maximized => WindowState.Maximized,
                    WindowMode.Fullscreen or WindowMode.ExclusiveFullscreen => WindowState.Fullscreen,
                    _ => WindowState.Normal,
                };
                return;
            }

            if (Mode == value)
                return;
            if (value == WindowMode.ExclusiveFullscreen)
                sdl.SetWindowDisplayMode(handle, (DisplayMode*)null); // the mode closest to the window's size, desktop refresh
            if (sdl.SetWindowFullscreen(handle, WindowModeFlags.ToSdlFullscreen(value)) != 0)
            {
                Log.Warning($"[Window] Could not switch to {value}: {sdl.GetErrorS()}");
                return;
            }

            if (value == WindowMode.Maximized)
                sdl.MaximizeWindow(handle);
            else if (value == WindowMode.Windowed)
                sdl.RestoreWindow(handle);
        }
    }

    public Vector2 Size
    {
        get => new(window.Size.X, window.Size.Y);
        set => window.Size = new Vector2D<int>((int)value.X, (int)value.Y);
    }

    public Vector2 Position
    {
        get => new(window.Position.X, window.Position.Y);
        set => window.Position = new Vector2D<int>((int)value.X, (int)value.Y);
    }

    public Rect2 ScreenBounds => window.Monitor is { } monitor
        ? new Rect2(monitor.Bounds.Origin.X, monitor.Bounds.Origin.Y, monitor.Bounds.Size.X, monitor.Bounds.Size.Y)
        : default;

    public bool VSync
    {
        get => engine?.Renderer is { } renderer ? renderer.VSync : engine?.StartVSync ?? true;
        set
        {
            if (engine?.Renderer is { } renderer)
                renderer.VSync = value;
            else
                Log.Warning("[Window] VSync cannot change before the renderer exists; set EngineOptions.VSync.");
        }
    }

    public int MaxFps
    {
        get => engine?.MaxFPS ?? (int)window.FramesPerSecond;
        set
        {
            if (engine is not null)
                engine.MaxFPS = value;
            else
                window.FramesPerSecond = window.UpdatesPerSecond = Math.Max(0, value);
        }
    }

    /// <summary>
    /// Keeps the OS pointer inside the window (SDL's window mouse grab) for <see cref="MouseMode.Confined"/> and
    /// <see cref="MouseMode.ConfinedHidden"/>. SDL applies the grab only while the window has input focus: it lets go
    /// when focus leaves and grabs again when it returns.
    /// </summary>
    internal void SetMouseConfined(bool confined)
    {
        if (TryGetSdl(out var sdl, out var handle))
            sdl.SetWindowMouseGrab(handle, confined ? SdlBool.True : SdlBool.False);
    }

    private bool TryGetSdl(out Sdl sdl, out Silk.NET.SDL.Window* handle)
    {
        sdl = null!;
        handle = null;
        if (!SdlWindowing.IsViewSdl(window) || SdlWindowing.GetExistingApi(window) is not { } api)
            return false;
        handle = SdlWindowing.GetHandle(window);
        sdl = api;
        return handle is not null;
    }
}

/// <summary>The SDL window flags of each <see cref="WindowMode"/> (pure arithmetic, testable without a window).</summary>
internal static class WindowModeFlags
{
    /// <summary>The flags for <c>SDL_SetWindowFullscreen</c>: 0 (windowed or maximized), <c>FULLSCREEN_DESKTOP</c> or <c>FULLSCREEN</c>.</summary>
    public static uint ToSdlFullscreen(WindowMode mode) => mode switch
    {
        WindowMode.Fullscreen => (uint)WindowFlags.FullscreenDesktop,
        WindowMode.ExclusiveFullscreen => (uint)WindowFlags.Fullscreen,
        _ => 0u,
    };

    /// <summary>The mode <c>SDL_GetWindowFlags</c> describes (<c>FULLSCREEN_DESKTOP</c> includes the <c>FULLSCREEN</c> bit).</summary>
    public static WindowMode FromSdl(uint flags)
    {
        const uint desktop = (uint)WindowFlags.FullscreenDesktop;
        if ((flags & desktop) == desktop)
            return WindowMode.Fullscreen;
        if ((flags & (uint)WindowFlags.Fullscreen) != 0)
            return WindowMode.ExclusiveFullscreen;
        return (flags & (uint)WindowFlags.Maximized) != 0 ? WindowMode.Maximized : WindowMode.Windowed;
    }
}

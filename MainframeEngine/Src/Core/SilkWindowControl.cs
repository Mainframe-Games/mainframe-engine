using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace MainframeEngine;

/// <summary><see cref="IWindowControl"/> over the engine's Silk.NET (SDL) window.</summary>
internal sealed class SilkWindowControl(IWindow window) : IWindowControl
{
    public string Title
    {
        get => window.Title;
        set => window.Title = value;
    }

    public bool Fullscreen
    {
        get => window.WindowState == WindowState.Fullscreen;
        set => window.WindowState = value ? WindowState.Fullscreen : WindowState.Normal;
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
}

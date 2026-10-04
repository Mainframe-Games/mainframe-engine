namespace MainframeEngine;

/// <summary>
/// A rendered frame copied back to the CPU: tightly packed RGBA8 rows, top row first, alpha forced to
/// 255 (the swapchain is composited opaque). Produced by <see cref="Engine.CaptureFrame"/>.
/// </summary>
public sealed class FrameCapture
{
    public FrameCapture(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8, got {pixels.Length}.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA8 pixels, <c>Width * 4</c> bytes per row, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>Writes the capture as an 8-bit RGBA PNG, creating the directory if needed.</summary>
    public void SavePng(string path) => Png.WriteRgba8(path, Width, Height, Pixels);
}

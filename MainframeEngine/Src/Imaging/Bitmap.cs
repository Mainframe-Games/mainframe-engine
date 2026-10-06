namespace MainframeEngine;

/// <summary>
/// Pixels in memory, RGBA8 and tightly packed: build or edit them on the CPU (<see cref="SetPixel"/>, <see cref="Fill"/>,
/// whole rows), then upload with <see cref="Texture2D.FromBitmap"/> and refresh with <see cref="Texture2D.SetPixels(Bitmap)"/>.
/// One-channel data (a mask, a fog map) is expanded to RGBA so shaders sample the values they would from an L8 texture
/// (<c>l, l, l, 1</c>) or an R8 one (<c>r, 0, 0, 1</c>).
/// </summary>
public sealed class Bitmap
{
    /// <summary>A blank (transparent black) bitmap.</summary>
    public Bitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Data = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The pixels, RGBA8, row by row from the top.</summary>
    public byte[] Data { get; }

    /// <summary>An bitmap from RGBA8 pixels (copied).</summary>
    public static Bitmap FromRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        var bitmap = new Bitmap(width, height);
        bitmap.SetRgba(rgba);
        return bitmap;
    }

    /// <summary>An bitmap from one luminance byte per pixel, expanded to (l, l, l, 255).</summary>
    public static Bitmap FromLuminance(int width, int height, ReadOnlySpan<byte> luminance)
    {
        var bitmap = new Bitmap(width, height);
        bitmap.SetLuminance(luminance);
        return bitmap;
    }

    /// <summary>An bitmap from one red byte per pixel, expanded to (r, 0, 0, 255).</summary>
    public static Bitmap FromRed(int width, int height, ReadOnlySpan<byte> red)
    {
        var bitmap = new Bitmap(width, height);
        bitmap.SetRed(red);
        return bitmap;
    }

    /// <summary>Replaces every pixel with <paramref name="rgba"/> (RGBA8, same size).</summary>
    public void SetRgba(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != Data.Length)
            throw new ArgumentException($"Expected {Data.Length} bytes of RGBA8 pixels ({Width}×{Height}), got {rgba.Length}.", nameof(rgba));
        rgba.CopyTo(Data);
    }

    /// <summary>Replaces every pixel with one luminance byte each: (l, l, l, 255).</summary>
    public void SetLuminance(ReadOnlySpan<byte> luminance) => Expand(luminance, grey: true);

    /// <summary>Replaces every pixel with one red byte each: (r, 0, 0, 255).</summary>
    public void SetRed(ReadOnlySpan<byte> red) => Expand(red, grey: false);

    /// <summary>Sets one pixel (each channel rounded to the nearest byte).</summary>
    public void SetPixel(int x, int y, Color color)
    {
        var i = Index(x, y);
        Data[i] = ToByte(color.R);
        Data[i + 1] = ToByte(color.G);
        Data[i + 2] = ToByte(color.B);
        Data[i + 3] = ToByte(color.A);
    }

    public Color GetPixel(int x, int y)
    {
        var i = Index(x, y);
        return new Color(Data[i] / 255f, Data[i + 1] / 255f, Data[i + 2] / 255f, Data[i + 3] / 255f);
    }

    /// <summary>Sets every pixel to <paramref name="color"/>.</summary>
    public void Fill(Color color)
    {
        Span<byte> pixel = [ToByte(color.R), ToByte(color.G), ToByte(color.B), ToByte(color.A)];
        for (var i = 0; i < Data.Length; i += 4)
            pixel.CopyTo(Data.AsSpan(i, 4));
    }

    private void Expand(ReadOnlySpan<byte> values, bool grey)
    {
        if (values.Length != Width * Height)
            throw new ArgumentException($"Expected {Width * Height} bytes ({Width}×{Height}), got {values.Length}.", nameof(values));
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            Data[i * 4] = v;
            Data[i * 4 + 1] = grey ? v : (byte)0;
            Data[i * 4 + 2] = grey ? v : (byte)0;
            Data[i * 4 + 3] = 255;
        }
    }

    private int Index(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {Width}×{Height} bitmap.");
        return (y * Width + x) * 4;
    }

    private static byte ToByte(float channel) => (byte)Math.Clamp(Math.Round(channel * 255f), 0, 255);
}

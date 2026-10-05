using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// SVG rasterisation through the <c>mfsvg</c> native library: ThorVG 1.0.3, the copy Godot 4.7.2 ships, called exactly as
/// Godot's <c>ImageLoaderSVG</c> does (ADR 0112), so an SVG rasterises to the same pixels as in Godot: straight-alpha RGBA8,
/// <c>round(size × scale)</c> pixels (at least 1, at most 16384 per side).
/// </summary>
public static unsafe partial class Svg
{
    private const string Library = "mfsvg";
    private const uint AbiVersion = (1 << 16) | 0;

    /// <summary>Rasterises an SVG document (UTF-8 bytes) at <paramref name="scale"/>.</summary>
    /// <exception cref="InvalidDataException">The document could not be parsed or drawn.</exception>
    public static (byte[] Rgba, int Width, int Height) Rasterize(ReadOnlySpan<byte> svg, float scale = 1f)
    {
        EnsureAbi();
        if (svg.IsEmpty)
            throw new InvalidDataException("The SVG document is empty.");
        byte* rgba;
        uint width, height;
        int result;
        fixed (byte* data = svg)
            result = NativeRasterize(data, (nuint)svg.Length, scale, &rgba, &width, &height);
        if (result != 0)
            throw new InvalidDataException($"SVG rasterisation failed ({Describe(result)}).");
        try
        {
            return (new ReadOnlySpan<byte>(rgba, checked((int)(width * height * 4))).ToArray(), (int)width, (int)height);
        }
        finally
        {
            NativeFree(rgba);
        }
    }

    /// <summary>The document's natural size (its width/height or view box) without rasterising.</summary>
    public static (float Width, float Height) Size(ReadOnlySpan<byte> svg)
    {
        EnsureAbi();
        float w, h;
        int result;
        fixed (byte* data = svg)
            result = NativeSize(data, (nuint)svg.Length, &w, &h);
        if (result != 0)
            throw new InvalidDataException($"SVG parsing failed ({Describe(result)}).");
        return (w, h);
    }

    /// <summary>The pixel size <see cref="Rasterize"/> produces at <paramref name="scale"/> (Godot's rounding and clamps).</summary>
    public static (int Width, int Height) PixelSize(ReadOnlySpan<byte> svg, float scale = 1f)
    {
        var (w, h) = Size(svg);
        return (Math.Min(16384, Math.Max(1, (int)MathF.Round(w * scale))), Math.Min(16384, Math.Max(1, (int)MathF.Round(h * scale))));
    }

    private static string Describe(int result) => result switch
    {
        1 => "initialisation",
        2 => "parse error",
        3 => "render error",
        4 => "invalid argument",
        _ => $"code {result}",
    };

    private static bool _abiChecked;

    private static void EnsureAbi()
    {
        if (_abiChecked)
            return;
        var version = NativeAbiVersion();
        if (version >> 16 != AbiVersion >> 16)
            throw new InvalidOperationException($"mfsvg ABI {version >> 16}.{version & 0xFFFF} does not match the engine's {AbiVersion >> 16}.{AbiVersion & 0xFFFF}.");
        _abiChecked = true;
    }

    [LibraryImport(Library, EntryPoint = "mfsvg_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint NativeAbiVersion();

    [LibraryImport(Library, EntryPoint = "mfsvg_rasterize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int NativeRasterize(byte* svg, nuint length, float scale, byte** rgba, uint* width, uint* height);

    [LibraryImport(Library, EntryPoint = "mfsvg_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int NativeSize(byte* svg, nuint length, float* width, float* height);

    [LibraryImport(Library, EntryPoint = "mfsvg_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void NativeFree(byte* rgba);
}

/// <summary>CPU image operations ported from Godot's <c>Image</c>.</summary>
public static class ImageOps
{
    /// <summary>
    /// Godot's <c>Image::fix_alpha_edges</c> (the texture importer's <c>process/fix_alpha_border</c>): every pixel with alpha
    /// below 20 takes the RGB of the nearest pixel (within 4) whose alpha is at least 20, so linear filtering does not bleed
    /// dark edges. Alpha is untouched.
    /// </summary>
    public static void FixAlphaEdges(Span<byte> rgba, int width, int height)
    {
        const int maxRadius = 4;
        const int alphaThreshold = 20;
        const int maxDist = int.MaxValue;
        var source = rgba.ToArray();
        for (var i = 0; i < height; i++)
        {
            for (var j = 0; j < width; j++)
            {
                var w = (i * width + j) * 4;
                if (source[w + 3] >= alphaThreshold)
                    continue;
                var closest = maxDist;
                byte r = 0, g = 0, b = 0;
                var fromX = Math.Max(0, j - maxRadius);
                var toX = Math.Min(width - 1, j + maxRadius);
                var fromY = Math.Max(0, i - maxRadius);
                var toY = Math.Min(height - 1, i + maxRadius);
                for (var k = fromY; k <= toY; k++)
                {
                    for (var l = fromX; l <= toX; l++)
                    {
                        var dy = i - k;
                        var dx = j - l;
                        var dist = dy * dy + dx * dx;
                        if (dist >= closest)
                            continue;
                        var p = (k * width + l) << 2;
                        if (source[p + 3] < alphaThreshold)
                            continue;
                        closest = dist;
                        r = source[p];
                        g = source[p + 1];
                        b = source[p + 2];
                    }
                }

                if (closest != maxDist)
                {
                    rgba[w] = r;
                    rgba[w + 1] = g;
                    rgba[w + 2] = b;
                }
            }
        }
    }
}

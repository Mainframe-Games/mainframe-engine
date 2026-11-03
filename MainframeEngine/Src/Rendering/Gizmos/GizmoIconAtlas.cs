using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The RGBA8 texture <see cref="GizmoDrawList.Icon"/> draws from: images packed in rows (2-pixel gutters, alpha edges
/// fixed so linear filtering shows no dark fringes) after a white block that the lines and solids sample. Straight
/// alpha, sRGB. The renderer uploads it the first time it is drawn and again whenever <see cref="Version"/> changes
/// (load time: an editor rebuilds it when the display scale changes).
/// </summary>
public sealed class GizmoIconAtlas : IDisposable
{
    private const int Gutter = 2;
    private const int WhiteSize = 4;

    private readonly List<(Vector2 Min, Vector2 Max)> _rects = [];
    private byte[] _pixels;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

    public GizmoIconAtlas(int width = 512, int height = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, WhiteSize + Gutter * 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, WhiteSize + Gutter * 2);
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 4];
        Clear();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bumped by every change; the renderer re-uploads the texture when it differs from the uploaded one.</summary>
    public int Version { get; private set; }

    /// <summary>Icons in the atlas.</summary>
    public int Count => _rects.Count;

    internal ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>The UV of the centre of the white block (what lines and solid shapes sample).</summary>
    public Vector2 WhiteUv => new((Gutter + WhiteSize * 0.5f) / Width, (Gutter + WhiteSize * 0.5f) / Height);

    /// <summary>The GPU copy (owned by the gizmo renderer; released with the atlas).</summary>
    internal IDisposable? Gpu { get; set; }

    internal int GpuVersion { get; set; } = -1;

    /// <summary>Removes every icon (keeps the white block).</summary>
    public void Clear()
    {
        Array.Clear(_pixels);
        _rects.Clear();
        for (var y = Gutter - 1; y < Gutter + WhiteSize + 1; y++)
            for (var x = Gutter - 1; x < Gutter + WhiteSize + 1; x++)
                _pixels.AsSpan((y * Width + x) * 4, 4).Fill(255);
        _cursorX = Gutter * 2 + WhiteSize + Gutter;
        _cursorY = Gutter;
        _rowHeight = WhiteSize + Gutter;
        Version++;
    }

    /// <summary>Adds an RGBA8 straight-alpha image; returns its icon index for <see cref="GizmoDrawList.Icon"/>.</summary>
    /// <exception cref="InvalidOperationException">The atlas is full.</exception>
    public int Add(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("The pixel span is smaller than width × height × 4.", nameof(rgba));
        if (_cursorX + width + Gutter > Width)
        {
            _cursorX = Gutter;
            _cursorY += _rowHeight + Gutter;
            _rowHeight = 0;
        }

        if (_cursorX + width + Gutter > Width || _cursorY + height + Gutter > Height)
            throw new InvalidOperationException($"The {Width}×{Height} gizmo icon atlas is full.");

        var copy = rgba[..(width * height * 4)].ToArray();
        ImageOps.FixAlphaEdges(copy, width, height);
        for (var y = 0; y < height; y++)
            copy.AsSpan(y * width * 4, width * 4).CopyTo(_pixels.AsSpan(((_cursorY + y) * Width + _cursorX) * 4));
        _rects.Add((new Vector2((float)_cursorX / Width, (float)_cursorY / Height),
            new Vector2((float)(_cursorX + width) / Width, (float)(_cursorY + height) / Height)));
        _cursorX += width + Gutter;
        _rowHeight = Math.Max(_rowHeight, height);
        Version++;
        return _rects.Count - 1;
    }

    /// <summary>The UV rectangle of <paramref name="icon"/> (the white block for an unknown index).</summary>
    public (Vector2 Min, Vector2 Max) UvRect(int icon) =>
        (uint)icon < (uint)_rects.Count ? _rects[icon] : (WhiteUv, WhiteUv);

    /// <summary>Releases the GPU copy through the deletion queue.</summary>
    public void Dispose()
    {
        Gpu?.Dispose();
        Gpu = null;
        GpuVersion = -1;
    }
}

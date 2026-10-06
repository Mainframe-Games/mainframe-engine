using System.Numerics;

namespace MainframeEngine;

/// <summary>How a <see cref="Gradient"/> blends between its points (Godot's <c>Gradient.InterpolationMode</c>).</summary>
public enum GradientInterpolationMode : byte
{
    Linear,
    Constant,
}

/// <summary>
/// Colours at offsets (Godot's <c>Gradient</c>; default black at 0 to white at 1). <see cref="Sample"/> is Godot's
/// <c>get_color_at_offset</c>: the nearest points' colours lerped in sRGB (straight alpha), clamped outside the range.
/// </summary>
[EditorIcon("palette")]
public sealed class Gradient : Resource
{
    [Export] public float[] Offsets { get; set; } = [0f, 1f];

    /// <summary>Straight sRGB colours, one per offset.</summary>
    [Export] public Vector4[] Colors { get; set; } = [new(0, 0, 0, 1), Vector4.One];

    [Export] public GradientInterpolationMode InterpolationMode { get; set; }

    /// <summary>The colour at <paramref name="offset"/>.</summary>
    public Vector4 Sample(float offset)
    {
        var count = Math.Min(Offsets.Length, Colors.Length);
        if (count == 0)
            return new Vector4(0, 0, 0, 1);
        Span<int> order = count <= 64 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
            order[i] = i;
        // Godot keeps its points sorted by offset; sort indices (stable for equal offsets).
        for (var i = 1; i < count; i++)
        {
            var key = order[i];
            var j = i - 1;
            while (j >= 0 && Offsets[order[j]] > Offsets[key])
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = key;
        }

        // Godot's binary search: an exact offset returns its point; otherwise the pair around the offset.
        int low = 0, high = count - 1, middle = 0;
        while (low <= high)
        {
            middle = (low + high) / 2;
            var o = Offsets[order[middle]];
            if (o > offset)
                high = middle - 1;
            else if (o < offset)
                low = middle + 1;
            else
                return Colors[order[middle]];
        }

        if (Offsets[order[middle]] > offset)
            middle--;
        var first = middle;
        var second = middle + 1;
        if (second >= count)
            return Colors[order[count - 1]];
        if (first < 0)
            return Colors[order[0]];
        var a = order[first];
        var b = order[second];
        if (InterpolationMode == GradientInterpolationMode.Constant)
            return Colors[a];
        var weight = (offset - Offsets[a]) / (Offsets[b] - Offsets[a]);
        return Vector4.Lerp(Colors[a], Colors[b], weight);
    }
}

/// <summary>
/// A texture filled from a <see cref="Gradient"/> (Godot's <c>GradientTexture2D</c>, RGBA8, no HDR): each pixel's offset is
/// its position (0..1 over <c>size − 1</c>) measured from <see cref="FillFrom"/> towards <see cref="FillTo"/> — along the
/// line, as a distance (radial), as the larger axis distance (square) or as an angle (conic) — then repeated or clamped.
/// Pixels are rebuilt on the next use after any change.
/// </summary>
[EditorIcon("palette")]
public sealed class GradientTexture2D : Texture2D
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Godot's name (GradientTexture2D.FillEnum), kept for ported games.")]
    public enum FillEnum : byte
    {
        Linear,
        Radial,
        Square,
        Conic,
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Godot's name (GradientTexture2D.RepeatEnum), kept for ported games.")]
    public enum RepeatEnum : byte
    {
        None,
        Repeat,
        Mirror,
    }

    private Gradient? _gradient;
    private int _width = 64, _height = 64;
    private FillEnum _fill;
    private Vector2 _fillFrom, _fillTo = new(1, 0);
    private RepeatEnum _repeat;
    private bool _dirty = true;

    [Export]
    public Gradient? Gradient
    {
        get => _gradient;
        set => Set(ref _gradient, value);
    }

    [Export(Range = "1,16384,1")]
    public new int Width
    {
        get => _width;
        set => Set(ref _width, Math.Clamp(value, 1, 16384));
    }

    [Export(Range = "1,16384,1")]
    public new int Height
    {
        get => _height;
        set => Set(ref _height, Math.Clamp(value, 1, 16384));
    }

    [Export]
    public FillEnum Fill
    {
        get => _fill;
        set => Set(ref _fill, value);
    }

    [Export]
    public Vector2 FillFrom
    {
        get => _fillFrom;
        set => Set(ref _fillFrom, value);
    }

    [Export]
    public Vector2 FillTo
    {
        get => _fillTo;
        set => Set(ref _fillTo, value);
    }

    [Export]
    public RepeatEnum Repeat
    {
        get => _repeat;
        set => Set(ref _repeat, value);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        _dirty = true;
        MarkChanged();
    }

    private protected override void Generate()
    {
        if (!_dirty)
            return;
        _dirty = false;
        var pixels = new byte[_width * _height * 4];
        var gradient = _gradient;
        for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                // Godot: no gradient = a transparent image; one point = its colour; none = opaque black.
                var c = gradient is null ? Vector4.Zero : gradient.Sample(OffsetAt(x, y));
                var i = (y * _width + x) * 4;
                pixels[i] = To8(c.X);
                pixels[i + 1] = To8(c.Y);
                pixels[i + 2] = To8(c.Z);
                pixels[i + 3] = To8(c.W);
            }

        SetGenerated(pixels, _width, _height);
    }

    private static byte To8(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    // GradientTexture2D::_get_gradient_offset_at.
    private float OffsetAt(int x, int y)
    {
        if (_fillTo == _fillFrom)
            return 0;
        var pos = new Vector2(_width > 1 ? (float)x / (_width - 1) : 0, _height > 1 ? (float)y / (_height - 1) : 0);
        var axis = _fillTo - _fillFrom;
        float ofs = 0;
        switch (_fill)
        {
            case FillEnum.Linear:
                {
                    var closest = _fillFrom + axis * (Vector2.Dot(pos - _fillFrom, axis) / axis.LengthSquared());
                    ofs = (closest - _fillFrom).Length() / axis.Length();
                    if (Vector2.Dot(closest - _fillFrom, axis) < 0)
                        ofs = -ofs;
                    break;
                }

            case FillEnum.Radial:
                ofs = (pos - _fillFrom).Length() / axis.Length();
                break;
            case FillEnum.Square:
                ofs = Math.Max(Math.Abs(pos.X - _fillFrom.X), Math.Abs(pos.Y - _fillFrom.Y)) / Math.Max(Math.Abs(axis.X), Math.Abs(axis.Y));
                break;
            case FillEnum.Conic:
                {
                    var rel = pos - _fillFrom;
                    var angle = MathF.Atan2(axis.X * rel.Y - axis.Y * rel.X, Vector2.Dot(axis, rel));
                    ofs = (float)(((angle % Math.Tau) + Math.Tau) % Math.Tau / Math.Tau);
                    break;
                }
        }

        switch (_repeat)
        {
            case RepeatEnum.None:
                ofs = Math.Clamp(ofs, 0f, 1f);
                break;
            case RepeatEnum.Repeat:
                ofs %= 1f;
                if (ofs < 0)
                    ofs += 1;
                break;
            case RepeatEnum.Mirror:
                ofs = Math.Abs(ofs) % 2f;
                if (ofs > 1f)
                    ofs = 2f - ofs;
                break;
        }

        return ofs;
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A grid of booleans (Godot's <c>BitMap</c>, ported from Godot 4's <c>scene/resources/bit_map.cpp</c>, MIT): build one
/// from a texture's alpha (<see cref="CreateFromImageAlpha(Texture2D, float)"/>), grow or shrink it
/// (<see cref="GrowMask"/>) and trace its opaque regions into polygons (<see cref="OpaqueToPolygons"/>) for collision
/// shapes, <see cref="Polygon2D"/>s or soft bodies. Named <c>BitMask</c> because <see cref="Bitmap"/> (RGBA pixels) differs
/// from Godot's name only by case (ADR 0010). Built at run time: its bits are not saved in scenes.
/// </summary>
[EditorIcon("grid-3x3")]
public sealed class BitMask : Resource
{
    private byte[] _bits = [];

    /// <summary>An empty (0×0) mask; <see cref="Create"/> sizes it.</summary>
    public BitMask()
    {
    }

    /// <summary>A mask of <paramref name="size"/>, every bit false.</summary>
    public BitMask(Vector2I size) => Create(size);

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The mask's size (Godot's <c>get_size</c>).</summary>
    public Vector2I GetSize() => new(Width, Height);

    /// <summary>Resizes the mask to <paramref name="size"/> and clears every bit (Godot's <c>create</c>).</summary>
    public void Create(Vector2I size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Y);
        if ((long)size.X * size.Y > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size), size, "BitMask is limited to int.MaxValue bits.");
        _bits = new byte[(size.X * size.Y + 7) / 8];
        Width = size.X;
        Height = size.Y;
    }

    /// <summary>
    /// A bit per pixel of <paramref name="texture"/>, true where its alpha is above <paramref name="threshold"/> (Godot's
    /// <c>create_from_image_alpha</c>).
    /// </summary>
    public void CreateFromImageAlpha(Texture2D texture, float threshold = 0.1f)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var (rgba, width, height) = texture.DecodePixels();
        CreateFromImageAlpha(rgba, width, height, threshold);
    }

    /// <summary><see cref="CreateFromImageAlpha(Texture2D, float)"/> for a CPU <see cref="Bitmap"/>.</summary>
    public void CreateFromImageAlpha(Bitmap image, float threshold = 0.1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        CreateFromImageAlpha(image.Data, image.Width, image.Height, threshold);
    }

    /// <summary><see cref="CreateFromImageAlpha(Texture2D, float)"/> for RGBA8 pixels, row by row from the top.</summary>
    public void CreateFromImageAlpha(ReadOnlySpan<byte> rgba, int width, int height, float threshold = 0.1f)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"{width}×{height} RGBA8 needs {width * height * 4} bytes, not {rgba.Length}.", nameof(rgba));
        Create(new Vector2I(width, height));
        for (var i = 0; i < width * height; i++)
            if (rgba[i * 4 + 3] / 255.0 > threshold)
                _bits[i / 8] |= (byte)(1 << (i % 8));
    }

    /// <summary>The bit at (<paramref name="x"/>, <paramref name="y"/>) (Godot's <c>get_bit</c>).</summary>
    public bool GetBit(int x, int y)
    {
        CheckIndex(x, y);
        return Bit(x, y);
    }

    public bool GetBit(Vector2I position) => GetBit(position.X, position.Y);

    /// <summary>Sets the bit at (<paramref name="x"/>, <paramref name="y"/>) (Godot's <c>set_bit</c>).</summary>
    public void SetBit(int x, int y, bool value)
    {
        CheckIndex(x, y);
        Write(x, y, value);
    }

    public void SetBit(Vector2I position, bool value) => SetBit(position.X, position.Y, value);

    /// <summary>Sets every bit of <paramref name="rect"/> inside the mask (Godot's <c>set_bit_rect</c>).</summary>
    public void SetBitRect(Rect2I rect, bool value)
    {
        var r = Bounds.Intersection(rect);
        for (var x = r.Position.X; x < r.Position.X + r.Size.X; x++)
            for (var y = r.Position.Y; y < r.Position.Y + r.Size.Y; y++)
                Write(x, y, value);
    }

    /// <summary>How many bits are true (Godot's <c>get_true_bit_count</c>).</summary>
    public int GetTrueBitCount()
    {
        var count = 0;
        foreach (var b in _bits)
            count += BitOperations.PopCount(b);
        return count;
    }

    /// <summary>
    /// Scales the mask to <paramref name="size"/>, nearest bit (Godot's <c>resize</c>).
    /// </summary>
    public void Resize(Vector2I size)
    {
        if (size == GetSize())
            return;
        var resized = new BitMask(size);
        var scaleX = (float)Width / size.X;
        var scaleY = (float)Height / size.Y;
        var lw = Width == 0 ? 0 : size.X;
        var lh = Height == 0 ? 0 : size.Y;
        for (var x = 0; x < lw; x++)
            for (var y = 0; y < lh; y++)
                resized.Write(x, y, Bit((int)(x * scaleX), (int)(y * scaleY)));
        (_bits, Width, Height) = (resized._bits, resized.Width, resized.Height);
    }

    /// <summary>
    /// Sets every bit within <paramref name="pixels"/> (a disc) of a true bit, inside <paramref name="rect"/> (Godot's
    /// <c>grow_mask</c>); a negative count shrinks the true area instead, the area outside the rect counting as false.
    /// </summary>
    public void GrowMask(int pixels, Rect2I rect)
    {
        if (pixels == 0)
            return;
        var value = pixels > 0;
        pixels = Math.Abs(pixels);
        var pixels2 = pixels * pixels;
        var r = Bounds.Intersection(rect);
        var copy = (byte[])_bits.Clone();
        const float cmpEpsilon2 = 0.00001f * 0.00001f; // Godot's CMP_EPSILON2

        for (var i = r.Position.Y; i < r.Position.Y + r.Size.Y; i++)
        {
            for (var j = r.Position.X; j < r.Position.X + r.Size.X; j++)
            {
                if (value == Bit(j, i))
                    continue;
                var found = false;
                for (var y = i - pixels; y <= i + pixels && !found; y++)
                {
                    for (var x = j - pixels; x <= j + pixels; x++)
                    {
                        var outside = false;
                        if (x < rect.Position.X || x >= rect.Position.X + rect.Size.X || y < rect.Position.Y || y >= rect.Position.Y + rect.Size.Y)
                        {
                            if (value)
                                continue;
                            outside = true; // outside the rect counts as a false bit
                        }

                        if (Vector2.DistanceSquared(new Vector2(j, i), new Vector2(x, y)) - cmpEpsilon2 > pixels2)
                            continue;
                        if (outside || value == Bit(copy, x, y))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                    Write(j, i, value);
            }
        }
    }

    /// <summary><see cref="GrowMask"/> with <c>-pixels</c> (Godot's <c>shrink_mask</c>).</summary>
    public void ShrinkMask(int pixels, Rect2I rect) => GrowMask(-pixels, rect);

    /// <summary>
    /// Outlines of the true regions inside <paramref name="rect"/>, relative to its position, simplified with
    /// Ramer–Douglas–Peucker at <paramref name="epsilon"/> pixels (Godot's <c>opaque_to_polygons</c>): one polygon per
    /// connected region (8-connected), more where an outline touches itself. Holes are not traced.
    /// </summary>
    public List<Vector2[]> OpaqueToPolygons(Rect2I rect, float epsilon = 2f)
    {
        var r = Bounds.Intersection(rect);
        var fill = new BitMask { _bits = new byte[_bits.Length], Width = Width, Height = Height };
        var polygons = new List<Vector2[]>();
        for (var i = r.Position.Y; i < r.Position.Y + r.Size.Y; i++)
        {
            for (var j = r.Position.X; j < r.Position.X + r.Size.X; j++)
            {
                if (fill.Bit(j, i) || !Bit(j, i))
                    continue;
                FillBits(fill, new Vector2I(j, i), r);
                foreach (var outline in MarchSquare(r, new Vector2I(j, i)))
                {
                    var polygon = Reduce(outline, r, epsilon);
                    if (polygon.Count >= 3)
                        polygons.Add([.. polygon]);
                }
            }
        }

        return polygons;
    }

    /// <summary>The mask as an image: white where true, black where false, opaque (Godot's <c>convert_to_image</c>).</summary>
    public Bitmap ConvertToImage()
    {
        var luminance = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                luminance[y * Width + x] = Bit(x, y) ? (byte)255 : (byte)0;
        return Bitmap.FromLuminance(Width, Height, luminance);
    }

    /// <summary>ORs <paramref name="source"/>'s true bits into this mask at <paramref name="position"/> (Godot's <c>blit</c>).</summary>
    public void Blit(Vector2I position, BitMask source)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (var i = 0; i < source.Width; i++)
            for (var j = 0; j < source.Height; j++)
            {
                var (px, py) = (position.X + i, position.Y + j);
                if (px >= 0 && px < Width && py >= 0 && py < Height && source.Bit(i, j))
                    Write(px, py, true);
            }
    }

    private Rect2I Bounds => new(0, 0, Width, Height);

    private void CheckIndex(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(x < 0 || x >= Width ? nameof(x) : nameof(y), $"({x}, {y}) is outside the {Width}×{Height} mask.");
    }

    // Out of range reads are false, as Godot's get_bit returns after its error.
    private bool Bit(int x, int y) => Bit(_bits, x, y);

    private bool Bit(byte[] bits, int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return false;
        var offset = Width * y + x;
        return (bits[offset / 8] & (1 << (offset % 8))) != 0;
    }

    private void Write(int x, int y, bool value)
    {
        var offset = Width * y + x;
        if (value)
            _bits[offset / 8] |= (byte)(1 << (offset % 8));
        else
            _bits[offset / 8] &= (byte)~(1 << (offset % 8));
    }

    // Godot's fill_bits: marks the 8-connected region of p_pos in fill, iteratively (a custom stack, no recursion).
    private void FillBits(BitMask fill, Vector2I start, Rect2I rect)
    {
        var stack = new List<(Vector2I Pos, int I, int J)>();
        var pos = start;
        int nextI = 0, nextJ = 0;
        var reenter = true;
        var popped = false;
        do
        {
            if (reenter)
            {
                nextI = pos.X - 1;
                nextJ = pos.Y - 1;
                reenter = false;
            }

            for (var i = nextI; i <= pos.X + 1; i++)
            {
                for (var j = nextJ; j <= pos.Y + 1; j++)
                {
                    if (popped)
                    {
                        nextJ = pos.Y - 1; // the next loop over j starts normally
                        popped = false;
                        continue; // this iteration already ran before the push
                    }

                    if (i < rect.Position.X || i >= rect.Position.X + rect.Size.X || j < rect.Position.Y || j >= rect.Position.Y + rect.Size.Y)
                        continue;
                    if (fill.Bit(i, j))
                        continue;
                    if (Bit(i, j))
                    {
                        fill.Write(i, j, true);
                        stack.Add((pos, i, j));
                        pos = new Vector2I(i, j);
                        reenter = true;
                        break;
                    }
                }

                if (reenter)
                    break;
            }

            if (!reenter && stack.Count > 0)
            {
                var entry = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                pos = entry.Pos;
                nextI = entry.I;
                nextJ = entry.J;
                popped = true;
            }
        }
        while (reenter || popped);
    }

    // Godot's _march_square: walks the outline clockwise from start over 2×2 squares (1 = top-left, 2 = top-right,
    // 4 = bottom-left, 8 = the current pixel), splitting off a loop each time the walk crosses itself (cases 6 and 9).
    private List<List<Vector2>> MarchSquare(Rect2I rect, Vector2I start)
    {
        int stepX = 0, stepY = 0, prevX = 0, prevY = 0;
        var (curX, curY) = (start.X, start.Y);
        var count = 0;
        var crossings = new List<(Vector2I Pos, int Index)>(); // Godot's ordered HashMap
        var points = new List<Vector2>();
        var pointsSize = 0;
        var result = new List<List<Vector2>> { new() };
        var origin = new Vector2(rect.Position.X, rect.Position.Y);

        do
        {
            var sv = 0;
            sv += Inside(rect, curX - 1, curY - 1) ? 1 : 0;
            sv += Inside(rect, curX, curY - 1) ? 2 : 0;
            sv += Inside(rect, curX - 1, curY) ? 4 : 0;
            sv += Inside(rect, curX, curY) ? 8 : 0;
            if (sv is 0 or 15)
                return [];

            switch (sv)
            {
                case 1 or 5 or 13:
                    (stepX, stepY) = (0, -1);
                    break;
                case 8 or 10 or 11:
                    (stepX, stepY) = (0, 1);
                    break;
                case 4 or 12 or 14:
                    (stepX, stepY) = (-1, 0);
                    break;
                case 2 or 3 or 7:
                    (stepX, stepY) = (1, 0);
                    break;
                case 9: // down when coming from the left, else up
                    (stepX, stepY) = prevX == 1 ? (0, 1) : (0, -1);
                    break;
                case 6: // right when coming from below, else left
                    (stepX, stepY) = prevY == -1 ? (1, 0) : (-1, 0);
                    break;
            }

            if (sv is 6 or 9)
            {
                var here = new Vector2I(curX, curY);
                var found = crossings.FindIndex(c => c.Pos == here);
                if (found >= 0)
                {
                    var index = crossings[found].Index;
                    result.Add(points.GetRange(index + 1, pointsSize - (index + 1)));
                    pointsSize = index + 1;
                    crossings.RemoveRange(found, crossings.Count - found);
                }
                else
                {
                    crossings.Add((here, pointsSize - 1));
                }
            }

            curX += stepX;
            curY += stepY;
            var point = new Vector2(curX, curY) - origin;
            if (stepX == prevX && stepY == prevY)
            {
                points[pointsSize - 1] = point; // same direction: move the last vertex
            }
            else
            {
                if (pointsSize < points.Count)
                    points[pointsSize] = point;
                else
                    points.Add(point);
                pointsSize++;
            }

            count++;
            prevX = stepX;
            prevY = stepY;
            if (count > 2 * (Width * Height + 1))
                return [];
        }
        while (curX != start.X || curY != start.Y);

        result[0] = points.GetRange(0, pointsSize);
        return result;
    }

    private bool Inside(Rect2I rect, int x, int y) => rect.HasPoint(new Vector2I(x, y)) && Bit(x, y);

    private static float PerpendicularDistance(Vector2 p, Vector2 start, Vector2 end)
    {
        if (start.X == end.X)
            return MathF.Abs(p.X - end.X);
        if (start.Y == end.Y)
            return MathF.Abs(p.Y - end.Y);
        var slope = (end.Y - start.Y) / (end.X - start.X);
        var intercept = start.Y - slope * start.X;
        return (float)(MathF.Abs(slope * p.X - p.Y + intercept) / Math.Sqrt(MathF.Pow(slope, 2f) + 1.0));
    }

    private static List<Vector2> Rdp(List<Vector2> v, float optimization)
    {
        if (v.Count < 3)
            return v;
        var index = -1;
        var dist = 0f;
        for (var i = 1; i < v.Count - 1; i++)
        {
            var d = PerpendicularDistance(v[i], v[0], v[^1]);
            if (d > dist)
            {
                dist = d;
                index = i;
            }
        }

        if (dist <= optimization)
            return [v[0], v[^1]];
        var left = Rdp(v.GetRange(0, index), optimization);
        var right = Rdp(v.GetRange(index, v.Count - index), optimization);
        var joined = new List<Vector2>(left.Count + right.Count);
        joined.AddRange(left);
        joined.AddRange(right);
        return joined;
    }

    private static List<Vector2> Reduce(List<Vector2> points, Rect2I rect, float epsilon)
    {
        if (points.Count < 3)
            return [];
        if (points.Count < 9)
            return points; // nothing worth reducing
        var maxEp = (float)Math.Min(rect.Size.X, rect.Size.Y);
        var ep = Math.Clamp(epsilon, 0f, maxEp / 2);
        var result = Rdp(points, ep);
        var last = result[^1];
        if (last.Y > result[0].Y && Vector2.Distance(last, result[0]) < ep * 0.5f)
        {
            result[0] = result[0] with { Y = last.Y };
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}

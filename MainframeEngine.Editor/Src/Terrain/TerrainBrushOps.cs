using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>The Sculpt tools.</summary>
public enum TerrainSculptTool : byte
{
    Raise,
    Lower,
    Smooth,
    Flatten,
    SetHeight,
    Ramp,
}

/// <summary>How a user channel is painted.</summary>
public enum TerrainChannelMode : byte
{
    /// <summary>The channel moves toward the value by the brush weight.</summary>
    Value,

    /// <summary>Cells under at least half the brush take the value exactly (ids, masks).</summary>
    Id,
}

/// <summary>
/// One brush stamp applied to a terrain (docs/design/terrain.md#editing). Every stamp reads its rectangle once, computes
/// from that copy (no order bias) and writes once through the terrain's edit API, so a recording
/// <see cref="TerrainEdit"/> captures it for undo. Positions are terrain-local metres (XZ from the terrain's origin);
/// heights are terrain-local too. Buffers are reused between stamps.
/// </summary>
public sealed class TerrainBrushOps
{
    /// <summary>Metres a full-strength Raise or Lower stamp moves the centre.</summary>
    public const float RaiseStep = 0.5f;

    private float[] _read = [];
    private float[] _write = [];
    private uint[] _cells = [];
    private float[] _weights = [];

    // ── Sculpt ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="tool"/> at <paramref name="center"/>. <paramref name="target"/> is the height Flatten and
    /// Set height move toward (Ramp is <see cref="Ramp"/>).
    /// </summary>
    public void Sculpt(TerrainData data, TerrainBrush brush, TerrainSculptTool tool, Vector2 center, float target = 0f)
    {
        if (tool == TerrainSculptTool.Ramp)
            return;
        var inner = VertexRect(data, center, brush.Radius, 0);
        if (inner.Size.X <= 0 || inner.Size.Y <= 0)
            return;
        var outer = Grow(inner, 1, data.VerticesPerSide);
        var read = Read(data, outer);
        var write = Buffer(ref _write, inner.Area);
        var spacing = data.VertexSpacing;
        var ow = outer.Size.X;
        for (var y = 0; y < inner.Size.Y; y++)
            for (var x = 0; x < inner.Size.X; x++)
            {
                var i = inner.Position.X + x;
                var j = inner.Position.Y + y;
                var lx = i - outer.Position.X;
                var ly = j - outer.Position.Y;
                var h = read[ly * ow + lx];
                var w = brush.Weight(i * spacing - center.X, j * spacing - center.Y, SlopeDegrees(read, outer, lx, ly, spacing));
                if (w > 0f)
                {
                    h = tool switch
                    {
                        TerrainSculptTool.Raise => h + w * RaiseStep,
                        TerrainSculptTool.Lower => h - w * RaiseStep,
                        TerrainSculptTool.Smooth => float.Lerp(h, Mean5(read, outer, lx, ly), w),
                        _ => float.Lerp(h, target, w),
                    };
                }

                write[y * inner.Size.X + x] = h;
            }

        data.SetHeights(inner, write.AsSpan(0, inner.Area));
    }

    /// <summary>The brush-weighted mean height under a stamp at <paramref name="center"/> (Flatten's target).</summary>
    public float WeightedMeanHeight(TerrainData data, TerrainBrush brush, Vector2 center)
    {
        var rect = VertexRect(data, center, brush.Radius, 0);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return 0f;
        var read = Read(data, rect);
        double sum = 0, weight = 0;
        var spacing = data.VertexSpacing;
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var w = brush.Shape01((rect.Position.X + x) * spacing - center.X, (rect.Position.Y + y) * spacing - center.Y);
                sum += read[y * rect.Size.X + x] * w;
                weight += w;
            }

        return weight > 0 ? (float)(sum / weight) : read[rect.Area / 2];
    }

    /// <summary>
    /// A ramp from (<paramref name="from"/>, <paramref name="fromHeight"/>) to (<paramref name="to"/>,
    /// <paramref name="toHeight"/>): vertices within a brush radius of the segment move toward the height along it by the
    /// brush's fade and strength (the band's edges blend into the ground).
    /// </summary>
    public void Ramp(TerrainData data, TerrainBrush brush, Vector2 from, float fromHeight, Vector2 to, float toHeight)
    {
        var r = brush.Radius;
        var min = Vector2.Min(from, to) - new Vector2(r);
        var max = Vector2.Max(from, to) + new Vector2(r);
        var rect = VertexRect(data, (min + max) * 0.5f, 0f, 0, (max - min) * 0.5f);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        var read = Read(data, rect);
        var write = Buffer(ref _write, rect.Area);
        var spacing = data.VertexSpacing;
        var axis = to - from;
        var length2 = axis.LengthSquared();
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var p = new Vector2((rect.Position.X + x) * spacing, (rect.Position.Y + y) * spacing);
                var t = length2 > 0f ? Math.Clamp(Vector2.Dot(p - from, axis) / length2, 0f, 1f) : 0f;
                var closest = from + axis * t;
                var d = Vector2.Distance(p, closest);
                var h = read[y * rect.Size.X + x];
                // The band's cross-section is the brush's fade (a circle profile across the ramp).
                var w = brush.Shape == TerrainBrushShape.Square ? (d <= r ? 1f : 0f) : FadeAcross(brush, d);
                w *= brush.Strength;
                write[y * rect.Size.X + x] = w > 0f ? float.Lerp(h, float.Lerp(fromHeight, toHeight, t), w) : h;
            }

        data.SetHeights(rect, write.AsSpan(0, rect.Area));
    }

    private static float FadeAcross(TerrainBrush brush, float distance) => brush.Shape01(distance, 0f);

    // ── Paint ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Faceted surfaces are crisp: a cell takes <paramref name="surface"/> where the brush shape is at least
    /// <c>max(1 − strength, 0.05)</c> (full strength paints the whole footprint, low strength only its core).
    /// <paramref name="cells"/> (a rectangle fill) paints every cell of the rectangle instead.
    /// </summary>
    public void PaintSurface(Terrain3D terrain, TerrainData data, TerrainBrush brush, Vector2 center, int surface, Rect2I? cells = null)
    {
        var rect = cells ?? CellRect(data, center, brush.Radius);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        var texels = Buffer(ref _cells, rect.Area);
        data.GetCells(TerrainLayers.Surface, 0, rect, texels.AsSpan(0, rect.Area));
        var threshold = MathF.Max(1f - brush.Strength, 0.05f);
        var id = (uint)Math.Clamp(surface, 0, 254);
        var changed = false;
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var k = y * rect.Size.X + x;
                if (cells is null && CellShape(terrain, data, brush, center, rect, x, y) < threshold)
                    continue;
                var texel = (texels[k] & 0xFFFFFF00u) | id;
                changed |= texel != texels[k];
                texels[k] = texel;
            }

        if (changed)
            data.SetCells(TerrainLayers.Surface, 0, rect, texels.AsSpan(0, rect.Area));
    }

    /// <summary>
    /// Realistic layers blend: <paramref name="layer"/>'s weight moves toward 1 by the brush weight and the others scale
    /// down, then every cell is renormalised to 255. A rectangle fill moves by the brush strength alone.
    /// </summary>
    public void PaintLayer(Terrain3D terrain, TerrainData data, TerrainBrush brush, Vector2 center, int layer, Rect2I? cells = null)
    {
        if ((uint)layer >= TerrainData.MaxLayers)
            return;
        var rect = cells ?? CellRect(data, center, brush.Radius);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        var layers = Math.Max(data.SplatImageCount * 4, layer + 1 > 8 ? TerrainData.MaxLayers : 8);
        var weights = Buffer(ref _weights, rect.Area * layers);
        var changed = false;
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var u = rect.Position.X + x;
                var v = rect.Position.Y + y;
                var o = (y * rect.Size.X + x) * layers;
                for (var l = 0; l < layers; l++)
                    weights[o + l] = data.GetLayerWeight(l, u, v);
                var w = cells is null ? CellShape(terrain, data, brush, center, rect, x, y) * brush.Strength : brush.Strength;
                if (w <= 0f)
                    continue;
                changed = true;
                for (var l = 0; l < layers; l++)
                    weights[o + l] = l == layer ? weights[o + l] + (1f - weights[o + l]) * w : weights[o + l] * (1f - w);
            }

        if (changed)
            data.SetWeights(rect, weights.AsSpan(0, rect.Area * layers));
    }

    /// <summary>
    /// User channel <paramref name="channel"/> (0–3): <see cref="TerrainChannelMode.Value"/> lerps toward
    /// <paramref name="value"/> by the brush weight; <see cref="TerrainChannelMode.Id"/> writes it where the shape is at
    /// least 0.5. A rectangle fill writes the value into every cell.
    /// </summary>
    public void PaintChannel(Terrain3D terrain, TerrainData data, TerrainBrush brush, Vector2 center, int channel,
        TerrainChannelMode mode, byte value, Rect2I? cells = null)
    {
        channel &= 3;
        var rect = cells ?? CellRect(data, center, brush.Radius);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        var texels = Buffer(ref _cells, rect.Area);
        data.GetCells(TerrainLayers.User, 0, rect, texels.AsSpan(0, rect.Area));
        var shift = channel * 8;
        var changed = false;
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var k = y * rect.Size.X + x;
                var current = (byte)(texels[k] >> shift);
                byte next;
                if (cells is not null)
                    next = value;
                else
                {
                    var shape = CellShape(terrain, data, brush, center, rect, x, y);
                    if (mode == TerrainChannelMode.Id)
                        next = shape >= 0.5f ? value : current;
                    else
                        next = (byte)Math.Clamp(MathF.Round(float.Lerp(current, value, shape * brush.Strength)), 0f, 255f);
                }

                if (next == current)
                    continue;
                texels[k] = (texels[k] & ~(0xFFu << shift)) | ((uint)next << shift);
                changed = true;
            }

        if (changed)
            data.SetCells(TerrainLayers.User, 0, rect, texels.AsSpan(0, rect.Area));
    }

    /// <summary>Water depth (metres above the bed) moves toward <paramref name="level"/> by the brush weight.</summary>
    public void PaintWater(TerrainData data, TerrainBrush brush, Vector2 center, float level, Rect2I? vertices = null)
    {
        var rect = vertices ?? VertexRect(data, center, brush.Radius, 0);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        level = Math.Clamp(level, 0f, data.MaxWaterDepth);
        var write = Buffer(ref _write, rect.Area);
        var spacing = data.VertexSpacing;
        var changed = false;
        for (var y = 0; y < rect.Size.Y; y++)
            for (var x = 0; x < rect.Size.X; x++)
            {
                var i = rect.Position.X + x;
                var j = rect.Position.Y + y;
                var depth = data.GetWaterFraction(i, j) * data.MaxWaterDepth;
                var w = vertices is not null ? 1f : brush.Shape01(i * spacing - center.X, j * spacing - center.Y) * brush.Strength;
                var next = w > 0f ? float.Lerp(depth, level, w) : depth;
                changed |= MathF.Abs(next - depth) > 1e-4f;
                write[y * rect.Size.X + x] = next;
            }

        if (changed)
            data.SetWaterDepth(rect, write.AsSpan(0, rect.Area));
    }

    // ── Rectangles ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The vertices within <paramref name="radius"/> of <paramref name="center"/> (square bounds), plus <paramref name="pad"/>, clamped.</summary>
    public static Rect2I VertexRect(TerrainData data, Vector2 center, float radius, int pad, Vector2? halfExtents = null)
    {
        var e = halfExtents ?? new Vector2(radius);
        var s = data.VertexSpacing;
        var last = data.VerticesPerSide - 1;
        var i0 = Math.Clamp((int)MathF.Ceiling((center.X - e.X) / s) - pad, 0, last);
        var j0 = Math.Clamp((int)MathF.Ceiling((center.Y - e.Y) / s) - pad, 0, last);
        var i1 = Math.Clamp((int)MathF.Floor((center.X + e.X) / s) + pad, 0, last);
        var j1 = Math.Clamp((int)MathF.Floor((center.Y + e.Y) / s) + pad, 0, last);
        if (center.X + e.X < 0f || center.Y + e.Y < 0f || center.X - e.X > last * s || center.Y - e.Y > last * s)
            return default;
        return new Rect2I(i0, j0, Math.Max(0, i1 - i0 + 1), Math.Max(0, j1 - j0 + 1));
    }

    /// <summary>The cells whose centres fall within <paramref name="radius"/> of <paramref name="center"/> (square bounds), clamped.</summary>
    public static Rect2I CellRect(TerrainData data, Vector2 center, float radius)
    {
        var c = data.CellSize;
        var last = data.CellsPerSide - 1;
        var size = data.CellsPerSide * c;
        if (center.X + radius < 0f || center.Y + radius < 0f || center.X - radius > size || center.Y - radius > size)
            return default;
        var u0 = Math.Clamp((int)MathF.Ceiling((center.X - radius) / c - 0.5f), 0, last);
        var v0 = Math.Clamp((int)MathF.Ceiling((center.Y - radius) / c - 0.5f), 0, last);
        var u1 = Math.Clamp((int)MathF.Floor((center.X + radius) / c - 0.5f), 0, last);
        var v1 = Math.Clamp((int)MathF.Floor((center.Y + radius) / c - 0.5f), 0, last);
        return new Rect2I(u0, v0, Math.Max(0, u1 - u0 + 1), Math.Max(0, v1 - v0 + 1));
    }

    /// <summary>The cells of the rectangle with corners <paramref name="a"/> and <paramref name="b"/> (terrain-local metres), snapped to cells.</summary>
    public static Rect2I CellRectBetween(TerrainData data, Vector2 a, Vector2 b)
    {
        var c = data.CellSize;
        var last = data.CellsPerSide - 1;
        var min = Vector2.Min(a, b) / c;
        var max = Vector2.Max(a, b) / c;
        var u0 = Math.Clamp((int)MathF.Floor(min.X), 0, last);
        var v0 = Math.Clamp((int)MathF.Floor(min.Y), 0, last);
        var u1 = Math.Clamp((int)MathF.Floor(max.X), 0, last);
        var v1 = Math.Clamp((int)MathF.Floor(max.Y), 0, last);
        return new Rect2I(u0, v0, u1 - u0 + 1, v1 - v0 + 1);
    }

    /// <summary>The vertices of the rectangle with corners <paramref name="a"/> and <paramref name="b"/>, snapped to vertices.</summary>
    public static Rect2I VertexRectBetween(TerrainData data, Vector2 a, Vector2 b)
    {
        var s = data.VertexSpacing;
        var last = data.VerticesPerSide - 1;
        var min = Vector2.Min(a, b) / s;
        var max = Vector2.Max(a, b) / s;
        var i0 = Math.Clamp((int)MathF.Round(min.X), 0, last);
        var j0 = Math.Clamp((int)MathF.Round(min.Y), 0, last);
        var i1 = Math.Clamp((int)MathF.Round(max.X), 0, last);
        var j1 = Math.Clamp((int)MathF.Round(max.Y), 0, last);
        return new Rect2I(i0, j0, i1 - i0 + 1, j1 - j0 + 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    // The brush shape at a cell centre, with the slope limit (the face normal under the centre).
    private static float CellShape(Terrain3D terrain, TerrainData data, TerrainBrush brush, Vector2 center, Rect2I rect, int x, int y)
    {
        var c = data.CellSize;
        var px = (rect.Position.X + x + 0.5f) * c;
        var pz = (rect.Position.Y + y + 0.5f) * c;
        var shape = brush.Shape01(px - center.X, pz - center.Y);
        if (shape <= 0f || !brush.SlopeLimit)
            return shape;
        var o = terrain.GlobalPosition;
        var n = terrain.NormalAt(o.X + px, o.Z + pz);
        var slope = MathF.Acos(Math.Clamp(MathF.Abs(n.Y), 0f, 1f)) * (180f / MathF.PI);
        return brush.SlopePasses(slope) ? shape : 0f;
    }

    private float[] Read(TerrainData data, Rect2I rect)
    {
        var read = Buffer(ref _read, rect.Area);
        data.GetHeights(rect, read.AsSpan(0, rect.Area));
        return read;
    }

    private static Rect2I Grow(Rect2I rect, int by, int verticesPerSide)
    {
        var x0 = Math.Max(0, rect.Position.X - by);
        var y0 = Math.Max(0, rect.Position.Y - by);
        var x1 = Math.Min(verticesPerSide - 1, rect.End.X - 1 + by);
        var y1 = Math.Min(verticesPerSide - 1, rect.End.Y - 1 + by);
        return new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static float Mean5(float[] read, Rect2I rect, int x, int y)
    {
        var w = rect.Size.X;
        var h = read[y * w + x];
        var l = x > 0 ? read[y * w + x - 1] : h;
        var r = x < w - 1 ? read[y * w + x + 1] : h;
        var d = y > 0 ? read[(y - 1) * w + x] : h;
        var u = y < rect.Size.Y - 1 ? read[(y + 1) * w + x] : h;
        return (h + l + r + d + u) * 0.2f;
    }

    // The slope (degrees) at a vertex from central differences of the read copy.
    private static float SlopeDegrees(float[] read, Rect2I rect, int x, int y, float spacing)
    {
        var w = rect.Size.X;
        var h = read[y * w + x];
        var l = x > 0 ? read[y * w + x - 1] : h;
        var r = x < w - 1 ? read[y * w + x + 1] : h;
        var d = y > 0 ? read[(y - 1) * w + x] : h;
        var u = y < rect.Size.Y - 1 ? read[(y + 1) * w + x] : h;
        var gx = (r - l) / (2f * spacing);
        var gz = (u - d) / (2f * spacing);
        return MathF.Atan(MathF.Sqrt(gx * gx + gz * gz)) * (180f / MathF.PI);
    }

    private static T[] Buffer<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new T[Math.Max(length, buffer.Length * 2)];
        return buffer;
    }
}

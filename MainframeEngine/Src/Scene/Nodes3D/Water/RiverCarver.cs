using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The channel a <see cref="River3D"/> carves into a <see cref="TerrainData"/> height layer (plain C#, no tree): for
/// every height vertex near the river, the nearest centreline segment gives the lateral distance <c>r</c>, the half
/// width <c>hw</c>, the depth <c>D</c> and the surface height <c>y</c>, then
/// <list type="bullet">
/// <item><b>channel</b> (<c>r ≤ hw</c>, <c>x = r / hw</c>): <c>y + ShoreLift − (D + ShoreLift)(1 − x²)</c>, a parabola whose
/// bed meets the surface just inside the ribbon's edge;</item>
/// <item><b>bank</b> (<c>hw &lt; r ≤ hw + BankWidth</c>): <c>lerp(y + ShoreLift, original, smoothstep((r − hw) / BankWidth))</c>,
/// raising low ground and cutting through high ground;</item>
/// <item>past either end of the river the channel closes: the bank blend runs from the end section's lip outwards, so a
/// river that stops leaves no dry pit.</item>
/// </list>
/// </summary>
/// <remarks>
/// The result is a pure function of the sections, the settings and the original heights: scalar float maths in a fixed
/// loop order (segments in river order, vertices row by row, the first nearest segment wins ties), no parallelism.
/// </remarks>
internal static class RiverCarver
{
    /// <summary>
    /// The vertex rectangle a carve touches: every segment's box (terrain-local XZ) grown by its widest half width plus
    /// <paramref name="bankWidth"/>, clamped to the map. False when the river lies off the map.
    /// </summary>
    public static bool ComputeRect(TerrainData data, ReadOnlySpan<Vector3> centres, ReadOnlySpan<float> halfWidths, float bankWidth,
        out Rect2I rect)
    {
        rect = default;
        if (centres.Length < 2)
            return false;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (var k = 0; k < centres.Length; k++)
        {
            var reach = halfWidths[k] + bankWidth;
            minX = MathF.Min(minX, centres[k].X - reach);
            maxX = MathF.Max(maxX, centres[k].X + reach);
            minZ = MathF.Min(minZ, centres[k].Z - reach);
            maxZ = MathF.Max(maxZ, centres[k].Z + reach);
        }

        var spacing = data.VertexSpacing;
        var last = data.VerticesPerSide - 1;
        var i0 = Math.Max(0, (int)MathF.Floor(minX / spacing));
        var j0 = Math.Max(0, (int)MathF.Floor(minZ / spacing));
        var i1 = Math.Min(last, (int)MathF.Ceiling(maxX / spacing));
        var j1 = Math.Min(last, (int)MathF.Ceiling(maxZ / spacing));
        if (i1 < i0 || j1 < j0 || !float.IsFinite(minX) || !float.IsFinite(minZ))
            return false;
        rect = new Rect2I(i0, j0, i1 - i0 + 1, j1 - j0 + 1);
        return true;
    }

    /// <summary>
    /// Writes the carved heights of <paramref name="rect"/> (row by row) into <paramref name="result"/> from
    /// <paramref name="original"/>. <paramref name="centres"/> are terrain-local (Y = the clamped water surface).
    /// </summary>
    public static void Carve(float vertexSpacing, Rect2I rect, ReadOnlySpan<float> original, Span<float> result,
        ReadOnlySpan<Vector3> centres, ReadOnlySpan<float> halfWidths, ReadOnlySpan<float> depths, float bankWidth, float shoreLift)
    {
        var area = rect.Area;
        if (original.Length != area || result.Length != area)
            throw new ArgumentException($"Expected {area} heights for the {rect.Size.X}×{rect.Size.Y} rectangle.");
        original.CopyTo(result);
        var segments = centres.Length - 1;
        if (segments < 1)
            return;

        bankWidth = MathF.Max(bankWidth, 0f);
        shoreLift = MathF.Max(shoreLift, 0f);
        var best = new float[area];
        Array.Fill(best, float.PositiveInfinity);
        var width = rect.Size.X;
        var x0 = rect.Position.X;
        var z0 = rect.Position.Y;

        for (var k = 0; k < segments; k++)
        {
            var a = centres[k];
            var b = centres[k + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var length2 = dx * dx + dz * dz;
            var length = MathF.Sqrt(length2);
            var reach = MathF.Max(halfWidths[k], halfWidths[k + 1]) + bankWidth;
            var first = k == 0;
            var lastSegment = k == segments - 1;

            // The segment's vertices: its box grown by the reach, inside the rectangle.
            var iMin = Math.Max(x0, (int)MathF.Floor((MathF.Min(a.X, b.X) - reach) / vertexSpacing));
            var iMax = Math.Min(x0 + width - 1, (int)MathF.Ceiling((MathF.Max(a.X, b.X) + reach) / vertexSpacing));
            var jMin = Math.Max(z0, (int)MathF.Floor((MathF.Min(a.Z, b.Z) - reach) / vertexSpacing));
            var jMax = Math.Min(z0 + rect.Size.Y - 1, (int)MathF.Ceiling((MathF.Max(a.Z, b.Z) + reach) / vertexSpacing));

            for (var j = jMin; j <= jMax; j++)
            {
                var pz = j * vertexSpacing;
                for (var i = iMin; i <= iMax; i++)
                {
                    var px = i * vertexSpacing;
                    var ax = px - a.X;
                    var az = pz - a.Z;
                    var t = length2 > 0f ? (ax * dx + az * dz) / length2 : 0f;
                    var tc = t < 0f ? 0f : t > 1f ? 1f : t;
                    var cx = px - (a.X + dx * tc);
                    var cz = pz - (a.Z + dz * tc);
                    var distance = MathF.Sqrt(cx * cx + cz * cz);
                    var v = (j - z0) * width + (i - x0);
                    if (!(distance < best[v]) || distance > reach)
                        continue;
                    best[v] = distance;

                    var hw = halfWidths[k] + (halfWidths[k + 1] - halfWidths[k]) * tc;
                    var depth = depths[k] + (depths[k + 1] - depths[k]) * tc;
                    var y = a.Y + (b.Y - a.Y) * tc;
                    var lip = y + shoreLift;
                    var ground = original[v];

                    float h;
                    var pastStart = first && t < 0f;
                    var pastEnd = lastSegment && t > 1f;
                    if ((pastStart || pastEnd) && length > 0f)
                    {
                        // Past an end: distance along the river beyond the end section and across beyond the half width.
                        var along = (pastStart ? -t : t - 1f) * length;
                        var lateral = MathF.Abs(ax * dz - az * dx) / length;
                        var across = MathF.Max(lateral - hw, 0f);
                        h = Bank(lip, ground, MathF.Sqrt(across * across + along * along), bankWidth);
                    }
                    else if (distance <= hw && hw > 0f)
                    {
                        var x = distance / hw;
                        h = lip - (depth + shoreLift) * (1f - x * x);
                    }
                    else
                    {
                        h = Bank(lip, ground, distance - hw, bankWidth);
                    }

                    result[v] = h;
                }
            }
        }
    }

    // The bank: the lip at the channel's edge, easing (smoothstep) to the original ground over bankWidth.
    private static float Bank(float lip, float ground, float distance, float bankWidth)
    {
        if (!(bankWidth > 0f))
            return distance <= 0f ? lip : ground;
        var t = distance / bankWidth;
        if (t >= 1f)
            return ground;
        if (t <= 0f)
            return lip;
        var s = t * t * (3f - 2f * t);
        return lip + (ground - lip) * s;
    }
}

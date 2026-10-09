using System.Numerics;

namespace MainframeEngine;

/// <summary>One view of an impostor's frame blend: the frame's grid cell and its weight.</summary>
public readonly record struct ImpostorFrame(int Column, int Row, float Weight);

/// <summary>
/// The octahedral mapping of <see cref="TreeImpostor"/> (ADR 0172), mirrored by <c>include/impostor.slang</c>: view
/// directions (object space, y up, pointing from the impostor's centre towards the viewer) on a <c>frames × frames</c>
/// grid whose cell (i, j) holds the view from <see cref="FrameDirection"/>(i, j). Hemi-octahedral (default) covers the
/// upper hemisphere: the grid's centre looks straight down, its border along the horizon. Full octahedral covers the
/// sphere. Frames sit on the grid points <c>i / (frames − 1)</c>, so the horizon's views are baked exactly.
/// </summary>
public static class ImpostorOctahedron
{
    /// <summary>Grid coordinates (0..1)² of a unit direction (hemi: its lower half is clamped to the horizon).</summary>
    public static Vector2 Encode(Vector3 direction, bool hemi)
    {
        if (hemi)
        {
            direction.Y = MathF.Max(direction.Y, 0f);
            var l1 = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
            if (l1 < 1e-20f)
                return new Vector2(0.5f);
            direction /= l1;
            return new Vector2(direction.X + direction.Z, direction.Z - direction.X) * 0.5f + new Vector2(0.5f);
        }

        var n = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        if (n < 1e-20f)
            return new Vector2(0.5f);
        direction /= n;
        var p = new Vector2(direction.X, direction.Z);
        if (direction.Y < 0f)
            p = new Vector2((1f - MathF.Abs(p.Y)) * SignNotZero(p.X), (1f - MathF.Abs(p.X)) * SignNotZero(p.Y));
        return p * 0.5f + new Vector2(0.5f);
    }

    /// <summary>The unit direction of grid coordinates (0..1)².</summary>
    public static Vector3 Decode(Vector2 grid, bool hemi)
    {
        var p = grid * 2f - Vector2.One;
        if (hemi)
        {
            var x = (p.X - p.Y) * 0.5f;
            var z = (p.X + p.Y) * 0.5f;
            var y = 1f - MathF.Abs(x) - MathF.Abs(z);
            return Vector3.Normalize(new Vector3(x, MathF.Max(y, 0f), z));
        }

        var d = new Vector3(p.X, 1f - MathF.Abs(p.X) - MathF.Abs(p.Y), p.Y);
        if (d.Y < 0f)
            (d.X, d.Z) = ((1f - MathF.Abs(p.Y)) * SignNotZero(p.X), (1f - MathF.Abs(p.X)) * SignNotZero(p.Y));
        return Vector3.Normalize(d);
    }

    /// <summary>The view direction baked into cell (<paramref name="column"/>, <paramref name="row"/>) of a <paramref name="frames"/>² grid.</summary>
    public static Vector3 FrameDirection(int column, int row, int frames, bool hemi) =>
        Decode(new Vector2(column, row) / Math.Max(1, frames - 1), hemi);

    /// <summary>
    /// The image axes of a view from <paramref name="direction"/> (towards the viewer): right = Y × direction (X when
    /// looking straight down or up), up = direction × right.
    /// </summary>
    public static (Vector3 Right, Vector3 Up) Basis(Vector3 direction)
    {
        var right = Vector3.Cross(Vector3.UnitY, direction);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        return (right, Vector3.Cross(direction, right));
    }

    /// <summary>
    /// The three frames nearest <paramref name="direction"/> and their barycentric weights (summing to 1): the grid
    /// triangle around its coordinates. A direction on a frame's own gives that frame alone.
    /// </summary>
    public static (ImpostorFrame A, ImpostorFrame B, ImpostorFrame C) Blend(Vector3 direction, int frames, bool hemi)
    {
        var f = Encode(direction, hemi) * (frames - 1);
        var bx = Math.Clamp((int)MathF.Floor(f.X), 0, frames - 2);
        var by = Math.Clamp((int)MathF.Floor(f.Y), 0, frames - 2);
        var tx = Math.Clamp(f.X - bx, 0f, 1f);
        var ty = Math.Clamp(f.Y - by, 0f, 1f);
        return tx + ty < 1f
            ? (new ImpostorFrame(bx, by, 1f - tx - ty), new ImpostorFrame(bx + 1, by, tx), new ImpostorFrame(bx, by + 1, ty))
            : (new ImpostorFrame(bx + 1, by, 1f - ty), new ImpostorFrame(bx, by + 1, 1f - tx), new ImpostorFrame(bx + 1, by + 1, tx + ty - 1f));
    }

    private static float SignNotZero(float v) => v >= 0f ? 1f : -1f;
}

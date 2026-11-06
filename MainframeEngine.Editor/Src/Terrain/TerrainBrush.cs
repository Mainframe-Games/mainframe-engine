using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>The brush's footprint.</summary>
public enum TerrainBrushShape : byte
{
    Circle,
    Square,
}

/// <summary>
/// The Terrain dock's brush (docs/design/terrain.md#editing): a circle or square of <see cref="Size"/> metres whose weight
/// at a point is <c>mask · fade · strength · slopePass</c>. The fade is a smoothstep from <c>(1 − Falloff) · radius</c>
/// to the edge (Falloff 0: a hard edge, 1: soft from the centre). Stamps are laid along the drag every
/// <see cref="Spacing"/> × size (<see cref="TerrainStroke"/>), so results do not depend on the event rate.
/// </summary>
public sealed class TerrainBrush
{
    public const float MinSize = 1f;
    public const float MaxSize = 256f;

    public TerrainBrushShape Shape { get; set; }

    /// <summary>Diameter (or side) in metres, 1–256.</summary>
    public float Size
    {
        get;
        set => field = Math.Clamp(float.IsFinite(value) ? value : 16f, MinSize, MaxSize);
    } = 16f;

    /// <summary>0.01–1.</summary>
    public float Strength
    {
        get;
        set => field = Math.Clamp(float.IsFinite(value) ? value : 0.25f, 0.01f, 1f);
    } = 0.25f;

    /// <summary>The fraction of the radius the edge fade covers, 0–1.</summary>
    public float Falloff
    {
        get;
        set => field = Math.Clamp(float.IsFinite(value) ? value : 0.5f, 0f, 1f);
    } = 0.5f;

    /// <summary>Distance between stamps as a fraction of <see cref="Size"/>, 0.05–1.</summary>
    public float Spacing
    {
        get;
        set => field = Math.Clamp(float.IsFinite(value) ? value : 0.25f, 0.05f, 1f);
    } = 0.25f;

    /// <summary>Whether samples outside <see cref="SlopeMin"/>..<see cref="SlopeMax"/> (degrees) are skipped.</summary>
    public bool SlopeLimit { get; set; }

    public float SlopeMin { get; set; }

    public float SlopeMax { get; set; } = 90f;

    public float Radius => Size * 0.5f;

    /// <summary>The stamp distance in metres.</summary>
    public float StampDistance => MathF.Max(0.05f, Size * Spacing);

    /// <summary>
    /// The footprint and fade at offset (<paramref name="dx"/>, <paramref name="dz"/>) metres from the centre, without
    /// strength or slope: 1 inside the inner part, smoothstep to 0 at the edge, 0 outside.
    /// </summary>
    public float Shape01(float dx, float dz)
    {
        var r = Radius;
        var d = Shape == TerrainBrushShape.Circle ? MathF.Sqrt(dx * dx + dz * dz) / r : MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) / r;
        if (d >= 1f)
            return d > 1f ? 0f : Falloff > 0f ? 0f : 1f;
        var start = 1f - Falloff;
        if (d <= start)
            return 1f;
        var t = (d - start) / (1f - start);
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>Whether a sample of slope <paramref name="slopeDegrees"/> is painted.</summary>
    public bool SlopePasses(float slopeDegrees) => !SlopeLimit || (slopeDegrees >= SlopeMin && slopeDegrees <= SlopeMax);

    /// <summary>The full weight at an offset: shape × strength × slope pass.</summary>
    public float Weight(float dx, float dz, float slopeDegrees) =>
        SlopePasses(slopeDegrees) ? Shape01(dx, dz) * Strength : 0f;
}

/// <summary>
/// Lays stamps along a drag: one at the press, then one every <see cref="TerrainBrush.StampDistance"/> along the path,
/// with the leftover distance carried to the next move, at most <see cref="MaxStampsPerMove"/> per move. The same path
/// gives the same stamps however many events it arrives in.
/// </summary>
public sealed class TerrainStroke
{
    public const int MaxStampsPerMove = 32;

    private Vector2 _last;
    private float _carried;

    public bool IsActive { get; private set; }

    /// <summary>Starts a stroke at <paramref name="point"/> (terrain-local XZ) and returns its first stamp.</summary>
    public Vector2 Begin(Vector2 point)
    {
        IsActive = true;
        _last = point;
        _carried = 0f;
        return point;
    }

    /// <summary>Moves to <paramref name="point"/>; adds the stamps passed on the way to <paramref name="stamps"/>.</summary>
    public void MoveTo(Vector2 point, float spacing, List<Vector2> stamps)
    {
        if (!IsActive)
            return;
        var delta = point - _last;
        var length = delta.Length();
        if (length <= 0f)
            return;
        var dir = delta / length;
        var travelled = spacing - _carried; // distance to the next stamp
        var count = 0;
        while (travelled <= length && count < MaxStampsPerMove)
        {
            stamps.Add(_last + dir * travelled);
            travelled += spacing;
            count++;
        }

        _carried = count == MaxStampsPerMove ? 0f : length - (travelled - spacing);
        _last = point;
    }

    public void End() => IsActive = false;
}

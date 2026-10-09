namespace MainframeEngine;

/// <summary>How a directional light's cascades are re-rendered (<see cref="DirectionalLight.CacheMode"/>, ADR 0167).</summary>
public enum ShadowCacheMode
{
    /// <summary>Every cascade renders every frame (the default).</summary>
    Off,

    /// <summary>
    /// Cascade 0 renders every frame, the others on a fixed schedule (<see cref="ShadowCacheSchedule"/>: 60 / 30 / 15 / 15 Hz
    /// at 60 fps with four cascades), at most two cascade passes per frame. A cascade between its renders keeps the map and
    /// the matrix it rendered with; its sphere is grown by the camera's travel over its interval, and it re-renders early
    /// when the camera leaves it anyway, when the light turns, or after <see cref="ShadowSystem.InvalidateShadowCache"/>.
    /// </summary>
    Staggered,
}

/// <summary>
/// Which shadow passes an instance casts into (<see cref="GeometryInstance3D.ShadowCasterLod"/>, ADR 0167): the coarse
/// passes are the primary light's last <see cref="DirectionalLight.CoarseCascades"/> cascades and its far shadow.
/// </summary>
public enum ShadowCasterLod : byte
{
    /// <summary>Every pass, while the instance is inside its visibility range (the default).</summary>
    All,

    /// <summary>Only the fine passes (the nearer cascades, atlas tiles, cubes), while inside its visibility range: a tree's near levels.</summary>
    Fine,

    /// <summary>
    /// The coarse passes from any distance (its visibility range is ignored for them), the fine passes while inside its
    /// visibility range: a tree's coarsest level, an impostor caster (G8e.5). Far cascades then draw every tree at that
    /// level, and near cascades still get the far trees' long shadows without drawing a tree twice.
    /// </summary>
    Coarse,
}

/// <summary>The staggered cascade schedule (<see cref="ShadowCacheMode.Staggered"/>): pure functions, unit-tested.</summary>
public static class ShadowCacheSchedule
{
    /// <summary>The frame rate the travel margins assume (<see cref="Margin"/>); faster cameras re-render early instead.</summary>
    public const float AssumedFrameRate = 60f;

    /// <summary>
    /// Frames between two renders of cascade <paramref name="cascade"/> of <paramref name="count"/>: 1 for cascade 0; 2 for
    /// the others with two or three cascades; with four, 2 for cascade 1 and 4 for cascades 2 and 3.
    /// </summary>
    public static int Interval(int cascade, int count)
    {
        if (cascade <= 0)
            return 1;
        return count >= 4 && cascade >= 2 ? 4 : 2;
    }

    /// <summary>
    /// Whether cascade <paramref name="cascade"/> of <paramref name="count"/> is due on frame <paramref name="frame"/>:
    /// cascade 0 always; cascade 1 on even frames; with three cascades cascade 2 on odd frames, with four cascades 2 and 3
    /// on alternate odd frames. At most two cascades are due on any frame.
    /// </summary>
    public static bool IsDue(int cascade, int count, ulong frame)
    {
        if (cascade <= 0)
            return true;
        if (cascade == 1)
            return frame % 2 == 0;
        if (count <= 3)
            return frame % 2 == 1;
        return cascade == 2 ? frame % 4 == 1 : frame % 4 == 3;
    }

    /// <summary>
    /// How far a cached cascade's sphere is grown (world units) so that it still covers its slice over its interval at
    /// <see cref="AssumedFrameRate"/> while the camera moves at <paramref name="maxSpeed"/> units per second and turns at
    /// <paramref name="maxTurnDegrees"/> per second (the slice's sphere is centred <paramref name="centerDistance"/> ahead of
    /// the camera, so turning swings it by that distance × the angle); 0 for cascade 0.
    /// </summary>
    public static float Margin(int cascade, int count, float maxSpeed, float maxTurnDegrees = 0f, float centerDistance = 0f)
    {
        if (cascade <= 0)
            return 0f;
        var seconds = Interval(cascade, count) / AssumedFrameRate;
        var turn = float.DegreesToRadians(MathF.Max(0f, maxTurnDegrees)) * seconds;
        return MathF.Max(0f, maxSpeed) * seconds + MathF.Max(0f, centerDistance) * 2f * MathF.Sin(MathF.Min(turn, MathF.PI) * 0.5f);
    }
}

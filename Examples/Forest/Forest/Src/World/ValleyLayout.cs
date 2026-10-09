using System.Numerics;

namespace Forest;

/// <summary>A reference shot: a camera pose (eye and target in world XZ, heights above the ground) and its vertical FOV.</summary>
/// <param name="Name">File name stem (<c>r1-glade</c>).</param>
/// <param name="Eye">World X, Z of the camera.</param>
/// <param name="EyeHeight">Camera height above the ground at <paramref name="Eye"/>.</param>
/// <param name="Target">World X, Z the camera looks at.</param>
/// <param name="TargetHeight">Height of the look-at point above the ground at <paramref name="Target"/> (or above 0 when <paramref name="AbsoluteTarget"/>).</param>
/// <param name="Fov">Vertical field of view in degrees.</param>
/// <param name="AbsoluteTarget">The target height is a world Y, not a height above the ground.</param>
/// <param name="BlurFarFrom">A photo shot's far depth of field (ADR 0168): blur starts this many metres away (0: none).</param>
/// <param name="BlurNearUntil">A photo shot's near depth of field: what is nearer than this many metres blurs (0: none).</param>
public readonly record struct ReferenceShot(string Name, Vector2 Eye, float EyeHeight, Vector2 Target, float TargetHeight, float Fov = 55f,
    bool AbsoluteTarget = false, float BlurFarFrom = 0f, float BlurNearUntil = 0f)
{
    /// <summary>True for the photo shots with depth of field (R4, R5): never used while walking.</summary>
    public bool HasDepthOfField => BlurFarFrom > 0f || BlurNearUntil > 0f;
}

/// <summary>
/// Where things are in the Forest's valley (docs/design/future/forest-showcase.md → The scene): world = terrain-local
/// metres (the terrain's corner is the origin), north is −Z, east is +X. A 256 m valley runs north to south: the stream
/// starts in the rocky outcrop (north-west), drops over a small fall, steps down through three pools, passes under a
/// fallen-log bridge and ends in the pond (south). The glade is west of the stream, the dense pine slope east. A walking
/// loop (<see cref="PathPoints"/>) circles it all.
/// </summary>
public static class ValleyLayout
{
    public const int SizeMeters = 256;
    public const float VertexSpacing = 0.5f;
    public const int ChunkMeters = 32;

    /// <summary>
    /// The stream from source to mouth: centreline position (y = water surface), full width and centre depth. The fall is
    /// the 3.5 m drop between the second and third points; pools are the wide, deep points.
    /// </summary>
    public static readonly (Vector3 Position, float Width, float Depth)[] StreamPoints =
    [
        (new Vector3(70f, 16.6f, 18f), 1.6f, 0.25f),    // source among the outcrop's rocks
        (new Vector3(77f, 16.2f, 30f), 2.2f, 0.3f),
        (new Vector3(83f, 15.8f, 39.5f), 2.6f, 0.3f),   // lip of the fall
        (new Vector3(85.5f, 12.2f, 43.5f), 3.2f, 0.45f), // foot of the fall
        (new Vector3(89f, 12.0f, 51f), 6.5f, 0.95f),    // pool 1
        (new Vector3(94f, 11.4f, 63f), 2.6f, 0.3f),     // riffle
        (new Vector3(99f, 10.6f, 76f), 5.8f, 0.85f),    // pool 2
        (new Vector3(103f, 9.4f, 91f), 2.8f, 0.3f),
        (new Vector3(108f, 8.2f, 106f), 3.0f, 0.35f),
        (new Vector3(114f, 7.4f, 121f), 6.8f, 0.95f),   // pool 3
        (new Vector3(119f, 7.0f, 134f), 3.2f, 0.4f),    // the bridge
        (new Vector3(124f, 5.8f, 150f), 3.4f, 0.4f),
        (new Vector3(129f, 3.6f, 168f), 3.6f, 0.45f),
        (new Vector3(132f, 1.7f, 183f), 4.2f, 0.5f),
        (new Vector3(134f, 0.62f, 192.5f), 5.2f, 0.6f), // mouth
    ];

    /// <summary>The pond (terrain water layer): centre, radii (x, z) of its shoreline ellipse, water surface height and deepest bed.</summary>
    public static readonly Vector2 PondCentre = new(134f, 212f);

    public static readonly Vector2 PondRadii = new(27f, 18f);
    public const float PondLevel = 0.6f;
    public const float PondDepth = 1.7f;

    /// <summary>The rocky outcrop (north-west): centre and radius of its plateau, and how high it rises above the valley.</summary>
    public static readonly Vector2 OutcropCentre = new(50f, 30f);

    public const float OutcropRadius = 30f;
    public const float OutcropHeight = 9f;

    /// <summary>The open oak and ash glade (west of the stream).</summary>
    public static readonly Vector2 GladeCentre = new(62f, 140f);

    public const float GladeRadius = 40f;

    /// <summary>The walking loop, clockwise from the trailhead (closed: the last point joins the first).</summary>
    public static readonly Vector2[] PathPoints =
    [
        new(64f, 198f),   // trailhead (spawn)
        new(58f, 172f),
        new(57f, 146f),   // through the glade
        new(63f, 118f),
        new(72f, 92f),
        new(80f, 72f),    // the falls viewpoint
        new(84f, 60f),
        new(89f, 70f),
        new(91f, 86f),    // west bank, past pool 2
        new(99f, 104f),
        new(107f, 124f),  // west bank of pool 3
        new(111f, 137.5f),
        new(128f, 131.8f), // over the log bridge
        new(146f, 140f),
        new(163f, 158f),  // into the pine slope's edge
        new(170f, 182f),
        new(171f, 206f),  // the pond's east shore
        new(163f, 232f),
        new(140f, 239f),  // south shore
        new(112f, 235f),
        new(88f, 222f),
    ];

    /// <summary>The path's half width (dirt), metres.</summary>
    public const float PathHalfWidth = 0.9f;

    /// <summary>Where the player starts: the trailhead, facing the glade (R1's direction).</summary>
    public static Vector2 Spawn => PathPoints[0];

    /// <summary>Spawn yaw in degrees (0 looks north along −Z, positive turns left/west): up the path, north into the glade.</summary>
    public const float SpawnYawDegrees = 8f;

    /// <summary>
    /// The morning sun: azimuth from north towards east, and elevation, in degrees. ADR 0178: north-north-east and low, so
    /// the walk's first leg (north from the trailhead, up through the glade to the fall) and most shots look into it:
    /// back-lit leaves and grass, shadows towards the camera, the haze glowing (105° and 21° before, side-lit).
    /// </summary>
    public const float SunAzimuthDegrees = 25f;

    public const float SunElevationDegrees = 25f;

    /// <summary>World direction towards the sun.</summary>
    public static Vector3 TowardsSun
    {
        get
        {
            var a = float.DegreesToRadians(SunAzimuthDegrees);
            var e = float.DegreesToRadians(SunElevationDegrees);
            return Vector3.Normalize(new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), -MathF.Cos(a) * MathF.Cos(e)));
        }
    }

    /// <summary>Places kept clear of trees (view corridors of the reference shots and the vista), centre and radius.</summary>
    public static readonly (Vector2 Centre, float Radius)[] Clearings =
    [
        (new Vector2(210f, 120f), 10f),  // R4's lookout
        (new Vector2(64f, 198f), 10f),   // the trailhead
        (new Vector2(48f, 146f), 6f),    // R1 (G8e.5's pose)
        (new Vector2(58f, 150f), 4f),    // R1
        (new Vector2(60f, 25f), 2.5f),   // R6, among the outcrop's pines
        (new Vector2(92.5f, 51f), 3f),   // R7, the pool's east bank
        (new Vector2(84f, 63f), 6f),     // R2
        (new Vector2(112.5f, 128.5f), 4f), // R3
        (new Vector2(120f, 160f), 3f),   // R5
    ];

    /// <summary>View corridors kept clear of trees: from a point towards another, within a half angle, out to a distance (R4's vista over the pond, R2's view of the fall).</summary>
    public static readonly (Vector2 From, Vector2 Towards, float HalfAngleDegrees, float Distance)[] ViewCorridors =
    [
        (new Vector2(210f, 120f), new Vector2(134f, 212f), 21f, 112f),
        (new Vector2(84f, 63f), new Vector2(86f, 44f), 16f, 21f),
    ];

    /// <summary>
    /// The seven reference shots (forest-showcase.md → Reference shots; G8e.7 recomposed R1 and added R6 and R7, ADR 0175):
    /// pose, vertical FOV and, for the photo shots, depth of field.
    /// </summary>
    public static readonly ReferenceShot[] Shots =
    [
        new("r1-glade", new Vector2(58f, 150f), 1.5f, new Vector2(100f, 160f), 14f, 55f),
        new("r2-fall", new Vector2(84f, 63f), 1.65f, new Vector2(86f, 44f), 1.6f, 55f),
        new("r3-bridge", new Vector2(112.5f, 128.5f), 1.6f, new Vector2(120f, 135.5f), 0.2f, 58f),
        new("r4-vista", new Vector2(210f, 120f), 2.2f, new Vector2(134f, 212f), PondLevel - 4f, 50f, AbsoluteTarget: true, BlurNearUntil: 6f),
        new("r5-floor", new Vector2(120f, 160f), 0.5f, new Vector2(108f, 150f), 0.2f, 60f, BlurFarFrom: 9f),
        new("r6-pines", new Vector2(60f, 25f), 1.7f, new Vector2(80f, 50f), 2f, 60f),
        new("r7-fall-close", new Vector2(92.5f, 51f), 1.2f, new Vector2(85.5f, 42.5f), 14f, 58f, AbsoluteTarget: true),
    ];

    /// <summary>A Catmull-Rom point on the closed path loop at parameter <paramref name="t"/> (0 … <see cref="PathPoints"/>.Length).</summary>
    public static Vector2 PathAt(float t)
    {
        var n = PathPoints.Length;
        var i = (int)MathF.Floor(t);
        var f = t - i;
        i = ((i % n) + n) % n;
        var p0 = PathPoints[(i + n - 1) % n];
        var p1 = PathPoints[i];
        var p2 = PathPoints[(i + 1) % n];
        var p3 = PathPoints[(i + 2) % n];
        return CatmullRom(p0, p1, p2, p3, f);
    }

    /// <summary>The path loop as a polyline of about <paramref name="spacing"/> metres per segment (closed: the last point equals the first).</summary>
    public static Vector2[] SamplePath(float spacing = 1f)
    {
        var points = new List<Vector2>();
        for (var i = 0; i < PathPoints.Length; i++)
        {
            var a = PathPoints[i];
            var b = PathPoints[(i + 1) % PathPoints.Length];
            var steps = Math.Max(2, (int)MathF.Ceiling(Vector2.Distance(a, b) / spacing));
            for (var s = 0; s < steps; s++)
                points.Add(PathAt(i + s / (float)steps));
        }

        points.Add(points[0]);
        return [.. points];
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }
}

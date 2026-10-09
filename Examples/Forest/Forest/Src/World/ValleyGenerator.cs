using System.Numerics;
using MainframeEngine;
using static Forest.ValleyNoise;

namespace Forest;

/// <summary>
/// Generates the Forest's valley from <see cref="ValleyLayout"/> and a seed, deterministically: the height field (the
/// valley floor following the stream's surface profile, the west and east ridges, the outcrop, the glade's gentler
/// ground, the pond basin, a smoothed path bed and the raised banks at the log bridge), the stream's
/// <see cref="Curve3D"/>, the pond's water and the splat weights (painted by slope, height, curvature, zone and the
/// distance to water and path). Heights are terrain-local metres; the terrain sits at the origin, so they are also world
/// coordinates. Pure C# apart from the <see cref="TerrainData"/> it fills.
/// </summary>
public sealed class ValleyGenerator
{
    // Splat channels (ForestAssets.TerrainLayers order).
    public const int Grass = 0, Leaves = 1, Moss = 2, Rock = 3, Dirt = 4, Gravel = 5, Mud = 6, Needles = 7;
    public const int LayerCount = 8;

    private const float TableStep = 0.5f;
    private readonly float[] _streamX;      // centreline x by z
    private readonly float[] _streamY;      // water surface by z (downhill-clamped)
    private readonly float[] _streamYFar;   // the surface smoothed over ±24 m: the floor away from the stream
    private readonly Vector2[] _streamPolyline;
    private readonly float[] _streamHalfWidths; // per polyline point

    private ValleyGenerator(int seed)
    {
        Seed = seed;
        StreamCurve = CreateStreamCurve();
        var baked = StreamCurve.GetBakedPoints();
        var distances = StreamCurve.GetBakedDistances();

        // The centreline polyline (XZ) with its half widths, and z-indexed tables of x and the clamped surface.
        _streamPolyline = new Vector2[baked.Length];
        _streamHalfWidths = new float[baked.Length];
        var surface = new float[baked.Length];
        for (var i = 0; i < baked.Length; i++)
        {
            _streamPolyline[i] = new Vector2(baked[i].X, baked[i].Z);
            _streamHalfWidths[i] = 0.5f * StreamCurve.SampleBakedWidth(distances[i]);
            surface[i] = i == 0 ? baked[i].Y : MathF.Min(baked[i].Y, surface[i - 1]);
        }

        var rows = (int)(ValleyLayout.SizeMeters / TableStep) + 1;
        _streamX = new float[rows];
        _streamY = new float[rows];
        _streamYFar = new float[rows];
        var k = 0;
        for (var r = 0; r < rows; r++)
        {
            var z = r * TableStep;
            while (k < baked.Length - 2 && baked[k + 1].Z < z)
                k++;
            var a = baked[k];
            var b = baked[k + 1];
            var t = Math.Clamp((z - a.Z) / MathF.Max(b.Z - a.Z, 1e-4f), 0f, 1f);
            _streamX[r] = a.X + (b.X - a.X) * t;
            _streamY[r] = surface[k] + (surface[k + 1] - surface[k]) * t;
        }

        const int window = 48; // ±24 m
        for (var r = 0; r < rows; r++)
        {
            float sum = 0f, weight = 0f;
            for (var o = -window; o <= window; o++)
            {
                var w = 1f - MathF.Abs(o) / (window + 1f);
                sum += _streamY[Math.Clamp(r + o, 0, rows - 1)] * w;
                weight += w;
            }

            _streamYFar[r] = sum / weight;
        }

        StreamField = PolylineField.Build(_streamPolyline, ValleyLayout.SizeMeters);
        PathPolyline = ValleyLayout.SamplePath();
        PathField = PolylineField.Build(PathPolyline, ValleyLayout.SizeMeters);
        (BridgeCentre, BridgeDirection) = FindBridge(PathPolyline, _streamPolyline);
        BridgeWaterLevel = SurfaceAtZ(BridgeCentre.Y);
        Heights = BuildHeights();
    }

    public int Seed { get; }

    /// <summary>The stream's centreline (y = water surface) with per-point width and depth.</summary>
    public Curve3D StreamCurve { get; }

    /// <summary>Distance to the stream's centreline.</summary>
    public PolylineField StreamField { get; }

    /// <summary>Distance to the walking path's centreline.</summary>
    public PolylineField PathField { get; }

    /// <summary>The path loop as a 1 m polyline (closed).</summary>
    public Vector2[] PathPolyline { get; }

    /// <summary>Where the path crosses the stream (the log bridge), and the path's direction there.</summary>
    public Vector2 BridgeCentre { get; }

    public Vector2 BridgeDirection { get; }

    public float BridgeWaterLevel { get; }

    /// <summary>Height of the bank top at the bridge (the log rests on it).</summary>
    public float BridgeBankHeight => BridgeWaterLevel + 1.25f;

    /// <summary>The generated heights before carving, <see cref="VerticesPerSide"/>² row by row.</summary>
    public float[] Heights { get; }

    public static int VerticesPerSide => (int)(ValleyLayout.SizeMeters / ValleyLayout.VertexSpacing) + 1;

    public static ValleyGenerator Generate(int seed = 1) => new(seed);

    /// <summary>A new terrain data resource holding <see cref="Heights"/> (Realistic, 256 m at 0.5 m, 32 m chunks).</summary>
    public TerrainData CreateTerrainData()
    {
        // TerrainData.Create, with a deeper water layer and a tighter height range (2 mm steps).
        var data = new TerrainData
        {
            ResourceName = "Valley",
            Profile = TerrainProfile.Realistic,
            SizeMeters = ValleyLayout.SizeMeters,
            VertexSpacing = ValleyLayout.VertexSpacing,
            ChunkMeters = ValleyLayout.ChunkMeters,
            HeightMin = -8f,
            HeightMax = 120f,
            MaxWaterDepth = 2f,
        };
        data.SetHeights(new Rect2I(0, 0, VerticesPerSide, VerticesPerSide), Heights);
        return data;
    }

    /// <summary>Stream centreline x at <paramref name="z"/> (clamped at the ends).</summary>
    public float StreamXAtZ(float z) => Table(_streamX, z);

    /// <summary>Stream water surface at <paramref name="z"/> (clamped at the ends).</summary>
    public float SurfaceAtZ(float z) => Table(_streamY, z);

    /// <summary>Half the stream's width at the nearest centreline point of (x, z).</summary>
    public float StreamHalfWidthAt(float x, float z)
    {
        var offset = StreamField.Offset(x, z);
        return 0.5f * StreamCurve.SampleBakedWidth(offset);
    }

    /// <summary>Distance from (x, z) to the stream's water edge (negative inside the water).</summary>
    public float StreamEdgeDistance(float x, float z) => StreamField.Distance(x, z) - StreamHalfWidthAt(x, z);

    /// <summary>
    /// The pond's normalised radius at (x, z): 1 on the shoreline, an ellipse whose radius wanders by ±18 % around it
    /// (smooth noise over the angle), so the shore has bays and points.
    /// </summary>
    public static float PondRadius(float x, float z)
    {
        var dx = (x - ValleyLayout.PondCentre.X) / ValleyLayout.PondRadii.X;
        var dz = (z - ValleyLayout.PondCentre.Y) / ValleyLayout.PondRadii.Y;
        var r = MathF.Sqrt(dx * dx + dz * dz);
        if (r < 1e-4f)
            return r;
        var cx = dx / r;
        var cz = dz / r;
        var wobble = 0.36f * (Fbm(3f + 2.2f * cx, 3f + 2.2f * cz, 1f, 3, 71) - 0.5f);
        return r / (1f + wobble);
    }

    /// <summary>Approximate distance in metres from (x, z) to the pond's shoreline (negative inside).</summary>
    public static float PondEdgeDistance(float x, float z) => (PondRadius(x, z) - 1f) * MathF.Min(ValleyLayout.PondRadii.X, ValleyLayout.PondRadii.Y);

    /// <summary>The outcrop's influence at (x, z), 0..1.</summary>
    public float OutcropMask(float x, float z)
    {
        var d = Vector2.Distance(new Vector2(x, z), ValleyLayout.OutcropCentre);
        var wobble = 7f * (Fbm(x, z, 22f, 2, Seed + 41) - 0.5f);
        return SmoothStep(ValleyLayout.OutcropRadius + 3f, ValleyLayout.OutcropRadius - 5f, d + wobble);
    }

    /// <summary>The glade's influence at (x, z), 0..1 (an open meadow inside, its edge at about the radius).</summary>
    public float GladeMask(float x, float z)
    {
        var d = Vector2.Distance(new Vector2(x, z), ValleyLayout.GladeCentre);
        var wobble = 12f * (Fbm(x, z, 30f, 2, Seed + 43) - 0.5f);
        return SmoothStep(ValleyLayout.GladeRadius, ValleyLayout.GladeRadius * 0.55f, d + wobble);
    }

    /// <summary>The dense pine forest's influence at (x, z), 0..1: the east slope, the outcrop and the high west ridge.</summary>
    public float PineMask(float x, float z)
    {
        var u = x - StreamXAtZ(z);
        var east = SmoothStep(11f, 30f, u);
        var west = SmoothStep(-78f, -100f, u) * (1f - GladeMask(x, z));
        var north = SmoothStep(34f, 10f, z);
        var mask = MathF.Max(MathF.Max(east, west), MathF.Max(OutcropMask(x, z), north));
        // Ragged edges.
        return Math.Clamp(mask + 0.35f * (Fbm(x, z, 26f, 2, Seed + 47) - 0.5f), 0f, 1f);
    }

    /// <summary>The aspen stream bank's influence at (x, z), 0..1.</summary>
    public float BankMask(float x, float z)
    {
        var e = StreamEdgeDistance(x, z);
        var along = SmoothStep(40f, 52f, z) * SmoothStep(PondTop + 4f, PondTop - 8f, z);
        return SmoothStep(-1f, 3f, e) * SmoothStep(24f, 12f, e) * along;
    }

    private static float PondTop => ValleyLayout.PondCentre.Y - ValleyLayout.PondRadii.Y;

    // ── Heights ─────────────────────────────────────────────────────────────

    /// <summary>The analytic ground height at (x, z) before the path, bridge and carve passes.</summary>
    public float GroundHeight(float x, float z)
    {
        var u = x - StreamXAtZ(z);
        var d = MathF.Abs(u);
        var near = Table(_streamY, z);
        var far = Table(_streamYFar, z);
        var floor = near + (far - near) * SmoothStep(3f, 26f, d);

        var glade = GladeMask(x, z);
        float rise;
        if (u < 0f)
        {
            var t = SmoothStep(16f, 125f, d);
            rise = 30f * MathF.Pow(t, 1.35f) * (1f - 0.45f * glade);
        }
        else
        {
            rise = 46f * SmoothStep(9f, 118f, d);
        }

        var h = floor + 0.4f + 0.04f * d + rise;

        // The valley closes in the north and rises again past the pond in the south.
        h += 11f * SmoothStep(30f, 0f, z) * SmoothStep(0f, 20f, d);
        h += 9f * SmoothStep(236f, 256f, z);

        // Rolling ground, quieter near the stream and in the glade.
        var calm = SmoothStep(4f, 24f, d) * (1f - 0.6f * glade);
        h += calm * (5f * (Fbm(x, z, 70f, 3, Seed + 1) - 0.5f) + 1.4f * (Fbm(x, z, 19f, 3, Seed + 2) - 0.5f));
        h += (0.35f + 0.65f * calm) * 0.22f * (Fbm(x, z, 4.5f, 2, Seed + 3) - 0.5f);

        // The outcrop: a plateau with ridged, cliffy faces.
        var outcrop = OutcropMask(x, z);
        if (outcrop > 0f)
        {
            var ridges = Ridged(x, z, 14f, 3, Seed + 5);
            h += outcrop * (ValleyLayout.OutcropHeight + 3.5f * ridges) * SmoothStep(1.5f, 7f, d);
        }

        // The pond basin.
        var r = PondRadius(x, z);
        if (r < 1f)
        {
            h = ValleyLayout.PondLevel - ValleyLayout.PondDepth * MathF.Pow(1f - r * r, 0.6f) + 0.15f * (Fbm(x, z, 6f, 2, Seed + 7) - 0.5f) * r;
        }
        else if (r < 1.6f)
        {
            var shore = ValleyLayout.PondLevel + 1.1f * (r - 1f);
            h = MathF.Max(shore + (h - shore) * SmoothStep(1.0f, 1.55f, r), ValleyLayout.PondLevel + 0.06f);
        }

        return h;
    }

    private float[] BuildHeights()
    {
        var n = VerticesPerSide;
        var s = ValleyLayout.VertexSpacing;
        var raw = new float[n * n];
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
                raw[j * n + i] = GroundHeight(i * s, j * s);

        // The path bed: level across, following the ground along the path (smoothed), cut into slopes and filled over
        // hollows, sunk a few centimetres.
        var path = PathPolyline;
        var along = new float[path.Length];
        var level = new float[path.Length];
        for (var k = 1; k < path.Length; k++)
            along[k] = along[k - 1] + Vector2.Distance(path[k - 1], path[k]);
        for (var k = 0; k < path.Length; k++)
            level[k] = Sample(raw, n, s, path[k].X, path[k].Y);
        var smoothed = new float[path.Length];
        const int window = 7;
        for (var k = 0; k < path.Length; k++)
        {
            float sum = 0f, weight = 0f;
            for (var o = -window; o <= window; o++)
            {
                var index = ((k + o) % (path.Length - 1) + (path.Length - 1)) % (path.Length - 1);
                var w = window + 1f - MathF.Abs(o);
                sum += level[index] * w;
                weight += w;
            }

            smoothed[k] = sum / weight;
        }

        var heights = (float[])raw.Clone();
        const float band = 5f; // cut and fill slopes, wide enough to stay gentle on the steep east side
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                var x = i * s;
                var z = j * s;
                var dp = PathField.Distance(x, z);
                if (dp > ValleyLayout.PathHalfWidth + band)
                    continue;
                // Bilinear offsets, except across the loop's seam (where the offset wraps from the length to 0).
                var offset = PathField.Offset(x, z);
                var nearest = PathField.NearestOffset(x, z);
                if (MathF.Abs(offset - nearest) > 5f)
                    offset = nearest;
                var found = Array.BinarySearch(along, offset);
                var k = Math.Clamp(found < 0 ? ~found - 1 : found, 0, path.Length - 2);
                var t = Math.Clamp((offset - along[k]) / MathF.Max(along[k + 1] - along[k], 1e-3f), 0f, 1f);
                var target = smoothed[k] + (smoothed[k + 1] - smoothed[k]) * t - 0.06f * SmoothStep(ValleyLayout.PathHalfWidth + 0.4f, 0f, dp);
                var w = SmoothStep(ValleyLayout.PathHalfWidth + band, ValleyLayout.PathHalfWidth + 0.3f, dp);
                heights[j * n + i] += (target - heights[j * n + i]) * w;
            }

        // Raised banks at the bridge, so the log spans a small gorge above the water.
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                var p = new Vector2(i * s, j * s);
                var db = Vector2.Distance(p, BridgeCentre);
                if (db > 9f)
                    continue;
                var w = SmoothStep(9f, 4.5f, db);
                var bank = BridgeBankHeight - 0.08f * MathF.Max(MathF.Abs(Vector2.Dot(p - BridgeCentre, BridgeDirection)) - 3f, 0f);
                var index = j * n + i;
                heights[index] = MathF.Max(heights[index], heights[index] + (bank - heights[index]) * w);
            }

        return heights;
    }

    // Bilinear sample of a vertex grid.
    private static float Sample(float[] grid, int n, float spacing, float x, float z)
    {
        var u = Math.Clamp(x / spacing, 0f, n - 1.001f);
        var v = Math.Clamp(z / spacing, 0f, n - 1.001f);
        var i = (int)u;
        var j = (int)v;
        var fu = u - i;
        var fv = v - j;
        var a = grid[j * n + i] + (grid[j * n + i + 1] - grid[j * n + i]) * fu;
        var b = grid[(j + 1) * n + i] + (grid[(j + 1) * n + i + 1] - grid[(j + 1) * n + i]) * fu;
        return a + (b - a) * fv;
    }

    // ── Stream, pond ────────────────────────────────────────────────────────

    private Curve3D CreateStreamCurve()
    {
        // The authored points plus two in-between points per reach with jittered width and a small sideways offset, so
        // the banks wander (not across the fall, whose drop must stay a step).
        var authored = ValleyLayout.StreamPoints;
        var points = new List<(Vector3 Position, float Width, float Depth)>();
        for (var i = 0; i < authored.Length; i++)
        {
            points.Add(authored[i]);
            if (i == authored.Length - 1 || i == 2)
                continue;
            var a = authored[i];
            var b = authored[i + 1];
            var across = Vector3.Normalize(Vector3.Cross(b.Position - a.Position, Vector3.UnitY));
            for (var k = 1; k <= 2; k++)
            {
                var t = k / 3f;
                var side = (Hash01(i, k, Seed + 61) - 0.5f) * 1.4f;
                var widthScale = 0.75f + 0.55f * Hash01(i, k, Seed + 62);
                var position = Vector3.Lerp(a.Position, b.Position, t) + across * side;
                points.Add((position, float.Lerp(a.Width, b.Width, t) * widthScale, float.Lerp(a.Depth, b.Depth, t)));
            }
        }

        var curve = new Curve3D { ResourceName = "Stream", BakeInterval = 0.5f };
        for (var i = 0; i < points.Count; i++)
        {
            var prev = points[Math.Max(i - 1, 0)].Position;
            var next = points[Math.Min(i + 1, points.Count - 1)].Position;
            var tangent = (next - prev) / (i == 0 || i == points.Count - 1 ? 3f : 6f);
            tangent.Y = 0f; // keep heights from overshooting between points (the fall stays a step)
            curve.AddPoint(points[i].Position, -tangent, tangent);
            curve.SetPointWidth(i, points[i].Width);
            curve.SetPointDepth(i, points[i].Depth);
        }

        return curve;
    }

    /// <summary>
    /// Floods the pond after carving: every vertex of the basin whose ground lies below <see cref="ValleyLayout.PondLevel"/>
    /// gets its stored height at the water surface and the difference as water depth.
    /// </summary>
    public static void ApplyPond(TerrainData data)
    {
        var n = data.VerticesPerSide;
        var s = data.VertexSpacing;
        var x0 = (int)MathF.Floor((ValleyLayout.PondCentre.X - ValleyLayout.PondRadii.X * 1.3f) / s);
        var x1 = (int)MathF.Ceiling((ValleyLayout.PondCentre.X + ValleyLayout.PondRadii.X * 1.3f) / s);
        var z0 = (int)MathF.Floor((ValleyLayout.PondCentre.Y - ValleyLayout.PondRadii.Y * 1.3f) / s);
        var z1 = (int)MathF.Ceiling((ValleyLayout.PondCentre.Y + ValleyLayout.PondRadii.Y * 1.3f) / s);
        x0 = Math.Max(x0, 0);
        z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, n - 1);
        z1 = Math.Min(z1, n - 1);
        var rect = new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        var heights = new float[rect.Area];
        data.GetHeights(rect, heights);
        var depths = new float[rect.Area];
        for (var j = 0; j < rect.Size.Y; j++)
            for (var i = 0; i < rect.Size.X; i++)
            {
                var k = j * rect.Size.X + i;
                var x = (x0 + i) * s;
                var z = (z0 + j) * s;
                if (PondRadius(x, z) > 1.2f || heights[k] >= ValleyLayout.PondLevel - 0.005f)
                    continue;
                depths[k] = MathF.Min(ValleyLayout.PondLevel - heights[k], data.MaxWaterDepth);
                heights[k] = ValleyLayout.PondLevel;
            }

        data.SetHeights(rect, heights);
        data.SetWaterDepth(rect, depths);
    }

    // ── Splat weights ───────────────────────────────────────────────────────

    /// <summary>
    /// Paints the eight layers from the final (carved, flooded) ground of <paramref name="data"/>: rock on steep slopes and
    /// the outcrop, needles under the pines, leaf litter on the aspen bank and the forest edges, grass in the glade, moss
    /// near water and around rock, gravel in the stream bed and along the path, mud on the banks and the pond's shore,
    /// dirt on the path.
    /// </summary>
    public void PaintWeights(TerrainData data)
    {
        var grid = data.Grid;
        var bed = data.BedHeights.ToArray();
        var cells = data.CellsPerSide;
        var cell = data.CellSize;
        var weights = new float[cells * cells * LayerCount];
        Span<float> w = stackalloc float[LayerCount];
        for (var v = 0; v < cells; v++)
            for (var u = 0; u < cells; u++)
            {
                var x = (u + 0.5f) * cell;
                var z = (v + 0.5f) * cell;
                w.Clear();
                Weights(grid, bed, x, z, w);
                w.CopyTo(weights.AsSpan((v * cells + u) * LayerCount, LayerCount));
            }

        data.SetWeights(new Rect2I(0, 0, cells, cells), weights);
    }

    private void Weights(in TerrainGrid grid, float[] bed, float x, float z, Span<float> w)
    {
        var normal = grid.SmoothNormalAt(bed, x, z);
        var slope = float.RadiansToDegrees(MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)));
        var h = grid.HeightAt(bed, x, z);
        // Curvature: the height against the mean of a 3 m ring (positive on crests, negative in hollows).
        var ring = 0.25f * (grid.HeightAt(bed, x + 3f, z) + grid.HeightAt(bed, x - 3f, z) + grid.HeightAt(bed, x, z + 3f) + grid.HeightAt(bed, x, z - 3f));
        var convex = h - ring;

        var pine = PineMask(x, z);
        var glade = GladeMask(x, z);
        var bank = BankMask(x, z);
        var outcrop = OutcropMask(x, z);
        var n1 = Fbm(x, z, 9f, 3, Seed + 11);
        var n2 = Fbm(x, z, 23f, 2, Seed + 12);
        var n3 = Fbm(x, z, 5f, 2, Seed + 13);

        // The ground of each zone.
        w[Needles] = pine * (0.75f + 0.5f * n1);
        w[Leaves] = bank * (0.7f + 0.6f * n2) + (1f - pine) * (1f - glade) * 0.55f + glade * 0.18f * SmoothStep(0.55f, 0.8f, n2);
        w[Grass] = glade * (1.1f - 0.3f * n1) * (1f - pine) + (1f - pine) * (1f - bank) * 0.25f * SmoothStep(0.4f, 0.7f, n2);
        w[Moss] = (pine * 0.35f + bank * 0.3f) * SmoothStep(0.5f, 0.75f, n3 * 0.6f + n2 * 0.4f) + 0.25f * SmoothStep(0.6f, 0.85f, n1) * (1f - glade);
        w[Dirt] = 0.12f * SmoothStep(0.6f, 0.9f, n2) * (1f - glade);
        if (w[Needles] + w[Leaves] + w[Grass] + w[Moss] + w[Dirt] < 0.05f)
            w[Leaves] = 0.1f;

        // Overrides, from the bottom up.
        var rockBase = SmoothStep(20f, 30f, slope) * SmoothStep(-0.2f, 0.4f, convex) * 0.6f;
        Paint(w, Moss, rockBase + SmoothStep(0.3f, 0.6f, outcrop) * 0.5f * (1f - SmoothStep(30f, 38f, slope)));
        var rock = MathF.Max(SmoothStep(33f, 43f, slope + 6f * (n3 - 0.5f)), outcrop * SmoothStep(18f, 30f, slope + 8f * convex));
        Paint(w, Rock, rock);

        var edge = StreamEdgeDistance(x, z);
        var pondEdge = PondEdgeDistance(x, z);
        var water = MathF.Min(edge, pondEdge);
        Paint(w, Moss, SmoothStep(5.5f, 2.2f, water) * SmoothStep(0.4f, 1.6f, water) * 0.65f * (1f - rock));
        Paint(w, Mud, SmoothStep(2.4f, 0.6f, water + 0.8f * (n3 - 0.5f)) * SmoothStep(-0.6f, 0.2f, water) * (1f - 0.7f * rock));
        Paint(w, Gravel, SmoothStep(0.2f, -0.5f, edge));                       // stream bed
        Paint(w, Mud, SmoothStep(0.1f, -1.5f, pondEdge) * SmoothStep(-6f, -2f, pondEdge));
        Paint(w, Gravel, SmoothStep(-4f, -7f, pondEdge) * 0.5f);              // the pond's deep bed

        var path = PathField.Distance(x, z);
        var pathEdge = path - ValleyLayout.PathHalfWidth - 0.25f * (n3 - 0.5f);
        Paint(w, Gravel, SmoothStep(0.9f, 0.25f, pathEdge) * SmoothStep(-0.5f, 0.1f, pathEdge) * 0.55f);
        Paint(w, Dirt, SmoothStep(0.2f, -0.3f, pathEdge));
    }

    private static void Paint(Span<float> w, int layer, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        if (amount <= 0f)
            return;
        float sum = 0f;
        for (var i = 0; i < w.Length; i++)
            sum += w[i];
        if (sum <= 0f)
            sum = 1f;
        for (var i = 0; i < w.Length; i++)
            w[i] = w[i] / sum * (1f - amount);
        w[layer] += amount;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static float Table(float[] table, float z)
    {
        var f = Math.Clamp(z / TableStep, 0f, table.Length - 1.001f);
        var i = (int)f;
        var t = f - i;
        return table[i] + (table[i + 1] - table[i]) * t;
    }

    private static (Vector2 Centre, Vector2 Direction) FindBridge(Vector2[] path, Vector2[] stream)
    {
        for (var i = 0; i < path.Length - 1; i++)
            for (var k = 0; k < stream.Length - 1; k++)
                if (Intersect(path[i], path[i + 1], stream[k], stream[k + 1], out var point))
                    return (point, Vector2.Normalize(path[i + 1] - path[i]));
        throw new InvalidOperationException("The path never crosses the stream.");
    }

    /// <summary>The number of times the path crosses the stream (the walking loop must cross once: the bridge).</summary>
    public int PathStreamCrossings()
    {
        var count = 0;
        var path = PathPolyline;
        for (var i = 0; i < path.Length - 1; i++)
            for (var k = 0; k < _streamPolyline.Length - 1; k++)
                if (Intersect(path[i], path[i + 1], _streamPolyline[k], _streamPolyline[k + 1], out _))
                    count++;
        return count;
    }

    private static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 point)
    {
        point = default;
        var r = b - a;
        var s = d - c;
        var denom = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(denom) < 1e-8f)
            return false;
        var q = c - a;
        var t = (q.X * s.Y - q.Y * s.X) / denom;
        var u = (q.X * r.Y - q.Y * r.X) / denom;
        if (t < 0f || t >= 1f || u < 0f || u >= 1f)
            return false;
        point = a + r * t;
        return true;
    }
}

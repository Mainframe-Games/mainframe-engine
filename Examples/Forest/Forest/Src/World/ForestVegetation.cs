using System.Numerics;
using MainframeEngine;
using MainframeEngine.Trees;
using static Forest.ValleyNoise;

namespace Forest;

/// <summary>Where a prop goes: the model, world XZ, yaw in degrees, scale, how far it sinks into the ground, and its footstep surface.</summary>
public readonly record struct PropPlacement(ForestPropAsset Prop, Vector2 Position, float YawDegrees, Vector3 Scale, float Sink, string Surface, bool Collision = true);

/// <summary>
/// The Forest's trees, bushes and props, placed deterministically from the <see cref="ValleyGenerator"/>'s zones: pines
/// with spruces and firs on the east slope, the outcrop and the high ground, oaks, ashes and beeches in and around the
/// glade, aspens and birches along the stream, bushes at the edges. Everything keeps clear of the path, the water, the bridge, steep rock and the view clearings.
/// </summary>
public static class ForestVegetation
{
    // Tree species (TreeScatter.Species indices).
    public const int PineLarge = 0, PineMedium = 1, PineSmall = 2, OakLarge = 3, OakMedium = 4, AshLarge = 5, AshMedium = 6,
        AspenLarge = 7, AspenMedium = 8, AspenSmall = 9, BirchLarge = 10, BirchMedium = 11, BeechLarge = 12, BeechMedium = 13,
        SpruceLarge = 14, SpruceMedium = 15, FirLarge = 16, FirMedium = 17;

    /// <summary>
    /// The share of each zone's trees that are G8e.5's species (ADR 0172): spruces and firs among the pines, birches among
    /// the aspens, beeches among the oaks and ashes. The same cells keep their trees; only the species changes.
    /// </summary>
    public const float NewSpeciesShare = 0.35f;

    /// <summary>
    /// The tree species: Ez Tree presets in the Realistic style, with their seeds (one variant per seed); with
    /// <paramref name="clusters"/>, their leaves as leaf-cluster cards (ADR 0172) casting <paramref name="clusterShadowDensity"/>
    /// of their cutout into the sun's shadow maps (<see cref="TreeOptions.ClusterShadowDensity"/>).
    /// </summary>
    public static TreeSpecies[] CreateTreeSpecies(bool clusters = false, float clusterShadowDensity = 1f) =>
    [
        Species("Pine Large", clusters, clusterShadowDensity, 13977, 4410),
        Species("Pine Medium", clusters, clusterShadowDensity, 52, 8812),
        Species("Pine Small", clusters, clusterShadowDensity, 3071),
        Species("Oak Large", clusters, clusterShadowDensity, 35729),
        Species("Oak Medium", clusters, clusterShadowDensity, 1201),
        Species("Ash Large", clusters, clusterShadowDensity, 2290),
        Species("Ash Medium", clusters, clusterShadowDensity, 6113),
        Species("Aspen Large", clusters, clusterShadowDensity, 18020, 777),
        Species("Aspen Medium", clusters, clusterShadowDensity, 9123, 4417),
        Species("Aspen Small", clusters, clusterShadowDensity, 271),
        Species("Birch Large", clusters, clusterShadowDensity, 6421),
        Species("Birch Medium", clusters, clusterShadowDensity, 3307),
        Species("Beech Large", clusters, clusterShadowDensity, 7741),
        Species("Beech Medium", clusters, clusterShadowDensity, 2203),
        Species("Spruce Large", clusters, clusterShadowDensity, 4099),
        Species("Spruce Medium", clusters, clusterShadowDensity, 6007),
        Species("Fir Large", clusters, clusterShadowDensity, 5153),
        Species("Fir Medium", clusters, clusterShadowDensity, 8191),
    ];

    private static bool IsEzTreePreset(string preset) =>
        !(preset.StartsWith("Birch", StringComparison.Ordinal) || preset.StartsWith("Beech", StringComparison.Ordinal) ||
          preset.StartsWith("Spruce", StringComparison.Ordinal) || preset.StartsWith("Fir", StringComparison.Ordinal));

    private static bool IsConifer(string preset) =>
        preset.StartsWith("Pine", StringComparison.Ordinal) || preset.StartsWith("Spruce", StringComparison.Ordinal) ||
        preset.StartsWith("Fir", StringComparison.Ordinal);

    private static TreeSpecies Species(string preset, bool clusters, float clusterShadowDensity, params int[] seeds)
    {
        var options = TreePresets.Load(preset);
        Mute(options, preset);
        if (!clusters)
            return new TreeSpecies { Options = options, Seeds = seeds };
        // Level 0 keeps Ez Tree's sprigs (sharp up close), levels 1 and 2 use cluster cards (fewer, fuller), every leaf image
        // keeps its coverage in the distance (ADR 0172).
        options.LeafMode = TreeLeafMode.Cluster;
        // G8e.5's species carry 2–4× the leaves of Ez Tree's (spruce: 5 700 needle sprigs, pine: 1 800): they are clusters
        // from level 0, which keeps their cost near the others'.
        options.ClusterFromLod = IsEzTreePreset(preset) ? 1 : 0;
        options.ClusterShadowDensity = clusterShadowDensity;
        // ADR 0209's look pass: back-lit crowns glowed a flat yellow-white (0.8 through a 235/245/190 tint): less light
        // through the leaves, and green, so a crown against the low sun keeps its form.
        options.ClusterTranslucency = 0.45f;
        options.ClusterTranslucencyColor = System.Drawing.Color.FromArgb(255, 170, 205, 110);
        options.LeafCoverageMips = true;
        options.HierarchicalWind = true;
        options.RootFlare = 1.6;
        options.CollarScale = 1.25;
        options.BarkMoss = IsConifer(preset) ? 0.25f : 0.4f;
        options.BarkDetailScale = 6f;
        return new TreeSpecies { Options = options, Seeds = seeds };
    }

    /// <summary>
    /// ADR 0178: the look-dev palette. Every leaf tint is multiplied towards a muted olive (warmer, less blue; the grade takes
    /// the rest of the saturation), the conifers a little less (their needles are already dark blue-green), and the aspens
    /// lose their autumn yellow: the birch leaf in a soft green, as in the references' summer morning.
    /// </summary>
    public static void Mute(TreeOptions options, string preset)
    {
        var conifer = IsConifer(preset) || preset == "Bush 3";
        // ADR 0209's look pass: deeper and less yellow (0.96/0.9/0.78 and 0.97/0.86/0.68 before): the crowns read as a pale
        // yellow-green wash against the sun.
        var (r, g, b) = conifer ? (0.8f, 0.86f, 0.76f) : (0.78f, 0.84f, 0.62f);
        if (preset.StartsWith("Aspen", StringComparison.Ordinal) || options.LeafTexture == "aspen")
        {
            options.LeafTexture = "birch";
            options.LeafTint = System.Drawing.Color.White;
            (r, g, b) = (0.8f, 0.86f, 0.62f);
        }

        var tint = options.LeafTint;
        options.LeafTint = System.Drawing.Color.FromArgb(255, (int)MathF.Round(tint.R * r), (int)MathF.Round(tint.G * g), (int)MathF.Round(tint.B * b));
    }

    /// <summary>The bush species (muted like the trees, <see cref="Mute"/>).</summary>
    public static TreeSpecies[] CreateBushSpecies() =>
    [
        Bush("Bush 1", 101, 202),
        Bush("Bush 2", 303),
        Bush("Bush 3", 404),
    ];

    private static TreeSpecies Bush(string preset, params int[] seeds)
    {
        var options = TreePresets.Load(preset);
        Mute(options, preset);
        return new TreeSpecies { Options = options, Seeds = seeds };
    }

    /// <summary>Grid cell of the tree placement (one candidate per cell; a second one on the pine slope).</summary>
    public const float TreeCell = 4f;

    /// <summary>
    /// The pine slope's density (ADR 0175): the share of its cells that take a tree (0.6 before G8e.7), and of those that
    /// take a second, smaller one, so the slope reads as a dense stand rather than spaced trees.
    /// </summary>
    public const float PineCellShare = 0.7f, PineSecondShare = 0f;

    /// <summary>The share of forest cells with an understorey sapling (a young tree of the zone's species, 2–5 m tall).</summary>
    public const float SaplingShare = 0.08f;

    public const float BushCell = 3f;

    /// <summary>Trees keep this far from the path's centreline.</summary>
    public const float PathClearance = 3.2f;

    /// <summary>
    /// Places the trees on the ground <paramref name="height"/>(x, z) with slope <paramref name="slopeDegrees"/>(x, z):
    /// one jittered candidate per <see cref="TreeCell"/> cell, accepted by the zones' densities.
    /// </summary>
    public static TreePlacement[] PlaceTrees(ValleyGenerator valley, Func<float, float, float> height, Func<float, float, float> slopeDegrees,
        float newSpeciesShare = NewSpeciesShare, bool understorey = true)
    {
        var seed = valley.Seed * 7919;
        var cells = (int)(ValleyLayout.SizeMeters / TreeCell);
        var placements = new List<TreePlacement>(2400);
        for (var cj = 0; cj < cells; cj++)
            for (var ci = 0; ci < cells; ci++)
            {
                var x = (ci + 0.1f + 0.8f * Hash01(ci, cj, seed + 1)) * TreeCell;
                var z = (cj + 0.1f + 0.8f * Hash01(ci, cj, seed + 2)) * TreeCell;
                if (!IsClear(valley, x, z, PathClearance, 1.6f, slopeDegrees))
                    continue;

                var pine = valley.PineMask(x, z);
                var glade = valley.GladeMask(x, z);
                var bank = valley.BankMask(x, z);
                var gladeEdge = SmoothStep(0.1f, 0.4f, glade) * SmoothStep(0.85f, 0.55f, glade);
                var mixed = (1f - pine) * (1f - glade) * (1f - bank);

                var pPine = pine * (understorey ? PineCellShare : 0.6f) + mixed * 0.1f;
                var shore = SmoothStep(16f, 4f, ValleyGenerator.PondEdgeDistance(x, z)) * (1f - pine);
                var dune = DuneBackMask(valley, x, z);
                var pAspen = (bank * 0.62f + shore * 0.35f + dune * 0.3f) * (1f - 0.6f * pine);
                var pAsh = gladeEdge * 0.09f + mixed * 0.15f;
                var pOak = glade * (1f - gladeEdge) * 0.012f + gladeEdge * 0.04f + mixed * 0.07f;
                var roll = Hash01(ci, cj, seed + 3);
                var pick = Hash01(ci, cj, seed + 4);
                int species;
                if ((roll -= pPine) < 0f)
                    species = pick < 0.34f ? PineLarge : pick < 0.8f ? PineMedium : PineSmall;
                else if ((roll -= pAspen) < 0f)
                    species = pick < 0.35f ? AspenLarge : pick < 0.8f ? AspenMedium : AspenSmall;
                else if ((roll -= pAsh) < 0f)
                    species = pick < 0.45f ? AshLarge : AshMedium;
                else if ((roll -= pOak) < 0f)
                    species = pick < 0.5f ? OakLarge : OakMedium;
                else
                    continue;

                // G8e.5's species replace a share of each zone's trees in the same cells (ADR 0172).
                var swap = Hash01(ci, cj, seed + 7);
                if (swap < newSpeciesShare)
                {
                    var large = pick < 0.4f;
                    species = species switch
                    {
                        PineLarge or PineMedium or PineSmall => swap < newSpeciesShare * 0.55f ? large ? SpruceLarge : SpruceMedium
                            : large ? FirLarge : FirMedium,
                        AspenLarge or AspenMedium or AspenSmall => large ? BirchLarge : BirchMedium,
                        _ => large ? BeechLarge : BeechMedium,
                    };
                }

                var scale = 0.82f + 0.36f * Hash01(ci, cj, seed + 5);
                var yaw = Hash01(ci, cj, seed + 6) * MathF.Tau;
                placements.Add(new TreePlacement(new Vector3(x, height(x, z) - 0.15f, z), yaw, scale, species));
            }

        if (understorey)
            AddUnderstorey(valley, height, slopeDegrees, placements, seed);
        return [.. placements];
    }

    /// <summary>
    /// ADR 0175: a second, smaller pine in some of the pine slope's cells (a denser stand), and understorey saplings: young
    /// trees of the zone's species at a quarter to under half their size, in the gaps between the big ones (2–5 m tall),
    /// so the forest has layers instead of trunks standing in an empty floor.
    /// </summary>
    private static void AddUnderstorey(ValleyGenerator valley, Func<float, float, float> height, Func<float, float, float> slopeDegrees,
        List<TreePlacement> placements, int seed)
    {
        var cells = (int)(ValleyLayout.SizeMeters / TreeCell);
        for (var cj = 0; cj < cells; cj++)
            for (var ci = 0; ci < cells; ci++)
            {
                // The point opposite the cell's first candidate (in the cell's other half), so the two never stand together.
                var first = new Vector2(0.1f + 0.8f * Hash01(ci, cj, seed + 1), 0.1f + 0.8f * Hash01(ci, cj, seed + 2));
                var x = (ci + Wrap(first.X + 0.35f + 0.3f * Hash01(ci, cj, seed + 11))) * TreeCell;
                var z = (cj + Wrap(first.Y + 0.35f + 0.3f * Hash01(ci, cj, seed + 12))) * TreeCell;
                if (!IsClear(valley, x, z, PathClearance, 1.6f, slopeDegrees))
                    continue;
                var pine = valley.PineMask(x, z);
                var glade = valley.GladeMask(x, z);
                var bank = valley.BankMask(x, z);
                var roll = Hash01(ci, cj, seed + 13);
                var pick = Hash01(ci, cj, seed + 14);
                var yaw = Hash01(ci, cj, seed + 15) * MathF.Tau;
                var ground = new Vector3(x, height(x, z) - 0.1f, z);
                if (roll < pine * PineSecondShare)
                {
                    var species = pick < 0.5f ? PineSmall : pick < 0.75f ? SpruceMedium : FirMedium;
                    placements.Add(new TreePlacement(ground, yaw, 0.6f + 0.25f * Hash01(ci, cj, seed + 16), species));
                    continue;
                }

                var forest = (1f - glade) * (1f - 0.5f * bank);
                if (Hash01(ci, cj, seed + 17) >= SaplingShare * forest)
                    continue;
                var sapling = pine > 0.4f
                    ? pick < 0.4f ? PineSmall : pick < 0.7f ? SpruceMedium : FirMedium
                    : bank > 0.4f ? pick < 0.5f ? AspenSmall : BirchMedium
                    : pick < 0.4f ? BeechMedium : pick < 0.7f ? AshMedium : OakMedium;
                placements.Add(new TreePlacement(ground, yaw, 0.22f + 0.2f * Hash01(ci, cj, seed + 18), sapling));
            }

        static float Wrap(float v) => v >= 0.9f ? v - 0.8f : v;
    }

    /// <summary>Places the bushes: glade edges, path sides, the stream bank and forest edges.</summary>
    public static TreePlacement[] PlaceBushes(ValleyGenerator valley, Func<float, float, float> height, Func<float, float, float> slopeDegrees)
    {
        var seed = valley.Seed * 104729;
        var cells = (int)(ValleyLayout.SizeMeters / BushCell);
        var placements = new List<TreePlacement>(1200);
        for (var cj = 0; cj < cells; cj++)
            for (var ci = 0; ci < cells; ci++)
            {
                var x = (ci + 0.1f + 0.8f * Hash01(ci, cj, seed + 1)) * BushCell;
                var z = (cj + 0.1f + 0.8f * Hash01(ci, cj, seed + 2)) * BushCell;
                if (!IsClear(valley, x, z, 2.2f, 1.2f, slopeDegrees))
                    continue;
                var glade = valley.GladeMask(x, z);
                var pine = valley.PineMask(x, z);
                var gladeEdge = SmoothStep(0.05f, 0.35f, glade) * SmoothStep(0.8f, 0.5f, glade);
                var path = valley.PathField.Distance(x, z);
                var pathSide = SmoothStep(2.2f, 3.2f, path) * SmoothStep(8f, 5f, path);
                var shore = SmoothStep(10f, 3f, ValleyGenerator.PondEdgeDistance(x, z));
                var p = 0.1f * gladeEdge + 0.25f * shore + 0.12f * pathSide * (1f - glade) + 0.12f * valley.BankMask(x, z) + 0.05f * (1f - pine) * (1f - glade);
                if (Hash01(ci, cj, seed + 3) >= p)
                    continue;
                var species = (int)(Hash01(ci, cj, seed + 4) * 3f);
                var scale = 0.75f + 0.5f * Hash01(ci, cj, seed + 5);
                var yaw = Hash01(ci, cj, seed + 6) * MathF.Tau;
                placements.Add(new TreePlacement(new Vector3(x, height(x, z) - 0.1f, z), yaw, scale, Math.Min(species, 2)));
            }

        return [.. placements];
    }

    /// <summary>The back of the beach (the dune bank above the sand), where a few aspens and birches stand, 0..1.</summary>
    public static float DuneBackMask(ValleyGenerator valley, float x, float z)
    {
        var s = z - valley.LakeShoreAt(x);
        return SmoothStep(-32f, -24f, s) * SmoothStep(-15f, -20f, s);
    }

    /// <summary>
    /// Whether a tree or bush may stand at (x, z): inside the map, away from the path, the stream, the pond, the lake's
    /// beach, the bridge and the clearings, and not on steep rock.
    /// </summary>
    public static bool IsClear(ValleyGenerator valley, float x, float z, float pathClearance, float waterClearance, Func<float, float, float> slopeDegrees)
    {
        const float margin = 1.5f;
        if (x < margin || z < margin || x > ValleyLayout.SizeMeters - margin || z > ValleyLayout.SizeMeters - margin)
            return false;
        if (valley.TrailDistance(x, z) < pathClearance)
            return false;
        if (valley.StreamEdgeDistance(x, z) < waterClearance)
            return false;
        if (ValleyGenerator.PondEdgeDistance(x, z) < waterClearance + 1f)
            return false;
        if (z > 240f && valley.BeachMask(x, z, valley.GroundHeight(x, z)) > 0.1f)
            return false;
        // Where the north cliff raised the ground its rim stays bare (trees on it would shade the fall under the low
        // north-north-east sun); where the sun's line held it at the old rim, the old trees stand.
        // Nor do trees grow in the ravines (their flat floors would crowd the old slopes' views, R6's above all).
        var edge = MathF.Min(MathF.Min(x, ValleyLayout.SizeMeters - x), z);
        if (edge < ValleyLayout.CliffPlateau + ValleyLayout.CliffFace + 30f)
        {
            var change = valley.GroundHeight(x, z) - valley.GroundHeight(x, z, cliffs: false);
            if (change < -0.5f || change > 1f && z < ValleyLayout.CliffPlateau + ValleyLayout.CliffFace + 2f && z < x && z < ValleyLayout.SizeMeters - x)
                return false;
        }
        if (Vector2.Distance(new Vector2(x, z), valley.BridgeCentre) < 7f)
            return false;
        foreach (var (centre, radius) in ValleyLayout.Clearings)
            if (Vector2.Distance(new Vector2(x, z), centre) < radius)
                return false;
        foreach (var (from, towards, halfAngle, reach) in ValleyLayout.ViewCorridors)
        {
            var offset = new Vector2(x, z) - from;
            if (offset.Length() < reach && offset.Length() > 0.1f &&
                Vector2.Dot(Vector2.Normalize(offset), Vector2.Normalize(towards - from)) > MathF.Cos(float.DegreesToRadians(halfAngle)))
                return false;
        }

        var slope = slopeDegrees(x, z);
        if (slope > 36f)
            return false;
        return !(valley.OutcropMask(x, z) > 0.5f && slope > 26f);
    }

    /// <summary>
    /// The props: the log bridge, mossy rock sets at the source and the fall, boulders along the pools, fallen logs,
    /// stumps and dry branches near the path. <paramref name="slopeDegrees"/> keeps loose props off steep ground.
    /// </summary>
    public static List<PropPlacement> PlaceProps(ValleyGenerator valley, Func<float, float, float> slopeDegrees)
    {
        var props = new List<PropPlacement>();
        var bridgeYaw = float.RadiansToDegrees(MathF.Atan2(-valley.BridgeDirection.Y, valley.BridgeDirection.X));
        props.Add(new PropPlacement(ForestAssets.DeadTrunk02, valley.BridgeCentre, bridgeYaw, new Vector3(1.6f, 1f, 1f), 0f, "wood"));

        // Rock sets framing the source and the fall, and the outcrop's edge.
        props.Add(new PropPlacement(ForestAssets.MossyRocks01, new Vector2(78.5f, 37f), 205f, new Vector3(1.1f), 0.35f, "rock"));
        props.Add(new PropPlacement(ForestAssets.MossyRocks02, new Vector2(90f, 41f), 120f, new Vector3(1f), 0.3f, "rock"));
        props.Add(new PropPlacement(ForestAssets.MossyRocks01, new Vector2(66f, 15f), 20f, new Vector3(1.2f), 0.4f, "rock"));
        props.Add(new PropPlacement(ForestAssets.MossyRocks02, new Vector2(60f, 44f), 75f, new Vector3(1.3f), 0.4f, "rock"));
        props.Add(new PropPlacement(ForestAssets.MossyRocks02, new Vector2(98f, 64f), 160f, new Vector3(0.9f), 0.3f, "rock"));

        // Boulders along the pools and the bank.
        (Vector2 At, float Yaw, float Scale)[] boulders =
        [
            (new Vector2(82.3f, 42.6f), 75f, 1.9f), (new Vector2(88.9f, 43.8f), 200f, 1.7f), // the fall's foot
            (new Vector2(81.0f, 39.0f), 140f, 1.5f), (new Vector2(85.6f, 37.9f), 320f, 1.4f), // its lip
            (new Vector2(84.5f, 53f), 30f, 1.3f), (new Vector2(94.5f, 49f), 160f, 1.1f), (new Vector2(104.5f, 78f), 80f, 1.4f),
            (new Vector2(119.5f, 118f), 200f, 1.2f), (new Vector2(109.5f, 127.5f), 300f, 0.9f), (new Vector2(211.5f, 116f), 10f, 1.6f),
            (new Vector2(129f, 156f), 120f, 1f),
        ];
        foreach (var (at, yaw, scale) in boulders)
            props.Add(new PropPlacement(ForestAssets.Boulder, at, yaw, new Vector3(scale), 0.18f * scale, "rock"));

        // ADR 0178: R5's fern dell: a big mossy log lying towards the sun, a smaller one across its far end.
        var dell = ValleyLayout.FernDell;
        var along = new Vector2(MathF.Cos(float.DegreesToRadians(dell.YawDegrees)), -MathF.Sin(float.DegreesToRadians(dell.YawDegrees)));
        // The scan's origin is inside the trunk (its underside 0.33 m below): raised so it lies on the ferns' floor.
        // Turned end for end: its root plate lies at the far end, the broken top towards the camera.
        props.Add(new PropPlacement(ForestAssets.DeadTrunk02, dell.Centre, dell.YawDegrees + 180f, new Vector3(1.4f), -0.3f, "wood"));
        props.Add(new PropPlacement(ForestAssets.DeadTrunk, dell.Centre + along * 3.4f + new Vector2(along.Y, -along.X) * 1.2f,
            dell.YawDegrees + 75f, new Vector3(1.2f), 0.1f, "wood"));

        // Fallen logs, stumps and branches near the path (seen on the walk), placed by hashed search.
        var seed = valley.Seed * 31337;
        Scatter(props, valley, slopeDegrees, ForestAssets.DeadTrunk, 7, 3.5f, 14f, seed + 1, "wood", 0.08f, new Vector2(1.0f, 1.4f));
        Scatter(props, valley, slopeDegrees, ForestAssets.DeadTrunk02, 3, 5f, 20f, seed + 2, "wood", 0.25f, new Vector2(0.9f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.Stump01, 4, 3f, 12f, seed + 3, "wood", 0.08f, new Vector2(0.8f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.Stump02, 4, 3f, 14f, seed + 4, "wood", 0.06f, new Vector2(0.8f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.DryBranches, 16, 1.6f, 9f, seed + 5, "leaves", 0.02f, new Vector2(0.9f, 1.4f), collision: false);

        PlaceCliffRocks(props, valley, seed + 6);
        PlaceBeachProps(props, valley, seed + 7);
        return props;
    }

    /// <summary>
    /// Rock faces and the tall mountainside scan along the cliffs' feet (every ≈ 22 m, jittered), turned to face into the
    /// valley and sunk into the talus: the edge reads as broken rock instead of a smooth terrain wall.
    /// </summary>
    private static void PlaceCliffRocks(List<PropPlacement> props, ValleyGenerator valley, int seed)
    {
        var size = ValleyLayout.SizeMeters;
        const float spacing = 22f, foot = ValleyLayout.CliffPlateau + ValleyLayout.CliffFace - 2.5f;
        // (start, along, inward normal, length): the west edge (south to north), the north edge, the east edge.
        (Vector2 Start, Vector2 Along, Vector2 In, float Length)[] edges =
        [
            (new Vector2(0f, size - 30f), new Vector2(0f, -1f), new Vector2(1f, 0f), size - 30f),
            (new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), size),
            (new Vector2(size, 0f), new Vector2(0f, 1f), new Vector2(-1f, 0f), size - 30f),
        ];
        for (var e = 0; e < edges.Length; e++)
        {
            var (start, along, inward, length) = edges[e];
            for (var k = 0; k * spacing < length; k++)
            {
                var t = (k + 0.2f + 0.6f * Hash01(e, k, seed)) * spacing;
                if (t < 12f || t > length - 6f)
                    continue;
                var p = start + along * t + inward * (foot + 2f * Hash01(e, k, seed + 1));
                if (valley.PathField.Distance(p.X, p.Y) < 5f || valley.StreamEdgeDistance(p.X, p.Y) < 3f || InAShot(p))
                    continue;
                var pick = Hash01(e, k, seed + 2);
                var prop = pick < 0.35f ? ForestAssets.Mountainside : pick < 0.7f ? ForestAssets.RockFace01 : ForestAssets.RockFace02;
                var scale = (prop == ForestAssets.Mountainside ? 0.9f : 1.2f) + 0.5f * Hash01(e, k, seed + 3);
                // The scans face +Z: turn that towards the valley, ±20°.
                var yaw = float.RadiansToDegrees(MathF.Atan2(inward.X, inward.Y)) + 40f * (Hash01(e, k, seed + 4) - 0.5f);
                props.Add(new PropPlacement(prop, p, yaw, new Vector3(scale), 0.6f * scale, "rock"));
            }
        }
    }

    /// <summary>Whether <paramref name="p"/> is in the frame of a reference shot (within 60 m and 40° of its view): kept as composed.</summary>
    private static bool InAShot(Vector2 p)
    {
        foreach (var shot in ValleyLayout.Shots)
        {
            var offset = p - shot.Eye;
            var distance = offset.Length();
            if (distance < 60f && Vector2.Dot(offset / MathF.Max(distance, 1e-3f), Vector2.Normalize(shot.Target - shot.Eye)) > MathF.Cos(float.DegreesToRadians(40f)))
                return true;
        }

        return false;
    }

    /// <summary>Driftwood along the beach and boulders where the headlands meet the water.</summary>
    private static void PlaceBeachProps(List<PropPlacement> props, ValleyGenerator valley, int seed)
    {
        var placed = 0;
        for (var attempt = 0; attempt < 600 && placed < 7; attempt++)
        {
            var x = 30f + 260f * Hash01(attempt, 0, seed);
            var shore = valley.LakeShoreAt(x);
            var p = new Vector2(x, shore - 1.5f - 9f * Hash01(attempt, 1, seed));
            var h = valley.GroundHeight(p.X, p.Y);
            if (valley.BeachMask(p.X, p.Y, h) < 0.8f || h < ValleyLayout.LakeLevel + 0.15f)
                continue;
            var tooClose = false;
            foreach (var other in props)
                if (Vector2.Distance(other.Position, p) < 9f)
                    tooClose = true;
            if (tooClose)
                continue;
            var log = Hash01(attempt, 2, seed) < 0.6f ? ForestAssets.DeadTrunk : ForestAssets.DeadTrunk02;
            var scale = log == ForestAssets.DeadTrunk ? 1.1f + 0.4f * Hash01(attempt, 3, seed) : 0.8f + 0.2f * Hash01(attempt, 3, seed);
            props.Add(new PropPlacement(log, p, Hash01(attempt, 4, seed) * 360f, new Vector3(scale), log == ForestAssets.DeadTrunk ? 0.06f * scale : 0.3f, "wood"));
            placed++;
        }

        placed = 0;
        for (var attempt = 0; attempt < 2000 && placed < 9; attempt++)
        {
            var x = 10f + 300f * Hash01(attempt, 5, seed);
            var z = 250f + 66f * Hash01(attempt, 6, seed);
            var h = valley.GroundHeight(x, z);
            // The waterline at the headlands' feet: just above the water, where the ground rises steeply to one side.
            if (MathF.Abs(h - ValleyLayout.LakeLevel - 0.3f) > 0.35f)
                continue;
            if (MathF.Max(valley.GroundHeight(x - 3f, z), valley.GroundHeight(x + 3f, z)) - h < 1.5f)
                continue;
            var p = new Vector2(x, z);
            var tooClose = false;
            foreach (var other in props)
                if (Vector2.Distance(other.Position, p) < 6f)
                    tooClose = true;
            if (tooClose)
                continue;
            var scale = 1.1f + 0.9f * Hash01(attempt, 7, seed);
            props.Add(new PropPlacement(ForestAssets.Boulder, p, Hash01(attempt, 8, seed) * 360f, new Vector3(scale), 0.2f * scale, "rock"));
            placed++;
        }
    }

    private static void Scatter(List<PropPlacement> props, ValleyGenerator valley, Func<float, float, float> slopeDegrees, ForestPropAsset prop, int count,
        float minPath, float maxPath, int seed, string surface, float sink, Vector2 scaleRange, bool collision = true)
    {
        var placed = 0;
        for (var attempt = 0; attempt < 4000 && placed < count; attempt++)
        {
            // A point near the path: a random path offset, then a random side distance.
            var path = valley.PathPolyline;
            var k = (int)(Hash01(attempt, 0, seed) * (path.Length - 1));
            var along = path[k + 1] - path[k];
            if (along.LengthSquared() < 1e-6f)
                continue;
            var normal = Vector2.Normalize(new Vector2(-along.Y, along.X));
            var side = Hash01(attempt, 1, seed) < 0.5f ? -1f : 1f;
            var distance = minPath + (maxPath - minPath) * Hash01(attempt, 2, seed);
            var p = path[k] + normal * side * distance;
            if (!IsClear(valley, p.X, p.Y, minPath, 1.5f, slopeDegrees) || slopeDegrees(p.X, p.Y) > 22f)
                continue;
            var tooClose = false;
            foreach (var other in props)
                if (Vector2.Distance(other.Position, p) < 6f)
                    tooClose = true;
            if (tooClose)
                continue;
            var scale = scaleRange.X + (scaleRange.Y - scaleRange.X) * Hash01(attempt, 3, seed);
            props.Add(new PropPlacement(prop, p, Hash01(attempt, 4, seed) * 360f, new Vector3(scale), sink * scale, surface, collision));
            placed++;
        }
    }
}

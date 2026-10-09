using System.Numerics;
using MainframeEngine;
using MainframeEngine.Trees;
using static Forest.ValleyNoise;

namespace Forest;

/// <summary>Where a prop goes: the model, world XZ, yaw in degrees, scale, how far it sinks into the ground, and its footstep surface.</summary>
public readonly record struct PropPlacement(ForestPropAsset Prop, Vector2 Position, float YawDegrees, Vector3 Scale, float Sink, string Surface, bool Collision = true);

/// <summary>
/// The Forest's trees, bushes and props, placed deterministically from the <see cref="ValleyGenerator"/>'s zones: pines
/// on the east slope, the outcrop and the high ground, oaks and ashes in and around the glade, aspens along the stream,
/// bushes at the edges. Everything keeps clear of the path, the water, the bridge, steep rock and the view clearings.
/// </summary>
public static class ForestVegetation
{
    // Tree species (TreeScatter.Species indices).
    public const int PineLarge = 0, PineMedium = 1, PineSmall = 2, OakLarge = 3, OakMedium = 4, AshLarge = 5, AshMedium = 6,
        AspenLarge = 7, AspenMedium = 8, AspenSmall = 9;

    /// <summary>The tree species: Ez Tree presets in the Realistic style, with their seeds (one variant per seed).</summary>
    public static TreeSpecies[] CreateTreeSpecies() =>
    [
        new() { Preset = "Pine Large", Seeds = [13977, 4410] },
        new() { Preset = "Pine Medium", Seeds = [52, 8812] },
        new() { Preset = "Pine Small", Seeds = [3071] },
        new() { Preset = "Oak Large", Seeds = [35729] },
        new() { Preset = "Oak Medium", Seeds = [1201] },
        new() { Preset = "Ash Large", Seeds = [2290] },
        new() { Preset = "Ash Medium", Seeds = [6113] },
        new() { Preset = "Aspen Large", Seeds = [18020, 777] },
        new() { Preset = "Aspen Medium", Seeds = [9123, 4417] },
        new() { Preset = "Aspen Small", Seeds = [271] },
    ];

    /// <summary>The bush species.</summary>
    public static TreeSpecies[] CreateBushSpecies() =>
    [
        new() { Preset = "Bush 1", Seeds = [101, 202] },
        new() { Preset = "Bush 2", Seeds = [303] },
        new() { Preset = "Bush 3", Seeds = [404] },
    ];

    /// <summary>Grid cell of the tree placement (one candidate per cell).</summary>
    public const float TreeCell = 4f;

    public const float BushCell = 3f;

    /// <summary>Trees keep this far from the path's centreline.</summary>
    public const float PathClearance = 3.2f;

    /// <summary>
    /// Places the trees on the ground <paramref name="height"/>(x, z) with slope <paramref name="slopeDegrees"/>(x, z):
    /// one jittered candidate per <see cref="TreeCell"/> cell, accepted by the zones' densities.
    /// </summary>
    public static TreePlacement[] PlaceTrees(ValleyGenerator valley, Func<float, float, float> height, Func<float, float, float> slopeDegrees)
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

                var pPine = pine * 0.6f + mixed * 0.1f;
                var shore = SmoothStep(16f, 4f, ValleyGenerator.PondEdgeDistance(x, z)) * (1f - pine);
                var pAspen = (bank * 0.62f + shore * 0.35f) * (1f - 0.6f * pine);
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

                var scale = 0.82f + 0.36f * Hash01(ci, cj, seed + 5);
                var yaw = Hash01(ci, cj, seed + 6) * MathF.Tau;
                placements.Add(new TreePlacement(new Vector3(x, height(x, z) - 0.15f, z), yaw, scale, species));
            }

        return [.. placements];
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

    /// <summary>
    /// Whether a tree or bush may stand at (x, z): inside the map, away from the path, the stream, the pond, the bridge
    /// and the clearings, and not on steep rock.
    /// </summary>
    public static bool IsClear(ValleyGenerator valley, float x, float z, float pathClearance, float waterClearance, Func<float, float, float> slopeDegrees)
    {
        const float margin = 1.5f;
        if (x < margin || z < margin || x > ValleyLayout.SizeMeters - margin || z > ValleyLayout.SizeMeters - margin)
            return false;
        if (valley.PathField.Distance(x, z) < pathClearance)
            return false;
        if (valley.StreamEdgeDistance(x, z) < waterClearance)
            return false;
        if (ValleyGenerator.PondEdgeDistance(x, z) < waterClearance + 1f)
            return false;
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

        // Fallen logs, stumps and branches near the path (seen on the walk), placed by hashed search.
        var seed = valley.Seed * 31337;
        Scatter(props, valley, slopeDegrees, ForestAssets.DeadTrunk, 7, 3.5f, 14f, seed + 1, "wood", 0.08f, new Vector2(1.0f, 1.4f));
        Scatter(props, valley, slopeDegrees, ForestAssets.DeadTrunk02, 3, 5f, 20f, seed + 2, "wood", 0.25f, new Vector2(0.9f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.Stump01, 4, 3f, 12f, seed + 3, "wood", 0.08f, new Vector2(0.8f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.Stump02, 4, 3f, 14f, seed + 4, "wood", 0.06f, new Vector2(0.8f, 1.1f));
        Scatter(props, valley, slopeDegrees, ForestAssets.DryBranches, 16, 1.6f, 9f, seed + 5, "leaves", 0.02f, new Vector2(0.9f, 1.4f), collision: false);
        return props;
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

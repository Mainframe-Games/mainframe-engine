using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>
/// The map's edges and the places added with them: cliffs west, north and east that nobody can climb, the lake along the
/// south edge too deep to wade, the sand beach, the cabin's level pad and its spur, and the low morning sun still clearing
/// the cliffs and the distant mountains from the valley's showpieces.
/// </summary>
public sealed class ValleyEdgeTests
{
    private static readonly Lazy<(ValleyGenerator Valley, TerrainData Data)> Shared = new(() =>
    {
        var valley = ValleyGenerator.Generate();
        var data = valley.CreateTerrainData();
        ValleyGenerator.ApplyPond(data);
        ValleyGenerator.ApplyLake(data);
        valley.PaintWeights(data);
        return (valley, data);
    });

    private static ValleyGenerator Valley => Shared.Value.Valley;

    private static TerrainData Data => Shared.Value.Data;

    private static float Height(float x, float z) => Data.Grid.HeightAt(Data.BedHeights, x, z);

    /// <summary>
    /// The longest run of ground steeper than 50° climbed walking from <paramref name="inner"/> to <paramref name="edge"/>
    /// (metres of height): a cliff face the player cannot walk up.
    /// </summary>
    private static float SteepestClimb(Vector2 inner, Vector2 edge)
    {
        const float step = 0.5f;
        var direction = Vector2.Normalize(edge - inner);
        var length = Vector2.Distance(inner, edge);
        float run = 0f, best = 0f;
        var previous = Height(inner.X, inner.Y);
        for (var t = step; t <= length; t += step)
        {
            var p = inner + direction * t;
            var h = Height(p.X, p.Y);
            var rise = h - previous;
            run = rise > step * MathF.Tan(float.DegreesToRadians(50f)) ? run + rise : 0f;
            best = MathF.Max(best, run);
            previous = h;
        }

        return best;
    }

    [Fact]
    public void CliffsCloseTheWestNorthAndEastEdges()
    {
        var size = ValleyLayout.SizeMeters;
        const float inward = 40f;
        var open = 0f;
        // Past the corners, where the side cliffs' plateaus meet the north one (nobody gets onto those).
        for (var t = 20f; t < size - 20f; t += 3f)
        {
            // The south end of the side edges is the lake's (see below).
            if (t < size - 40f)
            {
                Assert.True(SteepestClimb(new Vector2(inward, t), new Vector2(0.5f, t)) > 3f, $"the west edge is climbable at z = {t}");
                Assert.True(SteepestClimb(new Vector2(size - inward, t), new Vector2(size - 0.5f, t)) > 3f, $"the east edge is climbable at z = {t}");
            }

            // The shots that look north into the sun (R6 at the old rim's pines, R2 and R7 at the fall) keep the ground
            // they were composed with (ValleyGenerator.ShotFrame): there the map's wall alone closes the edge.
            if (SteepestClimb(new Vector2(t, inward), new Vector2(t, 0.5f)) > 3f)
                continue;
            Assert.True(ValleyGenerator.ShotFrame(t, 8f) > 0.9f, $"the north edge is climbable at x = {t}");
            open += 3f;
        }

        Assert.True(open <= 70f, $"{open} m of the north edge has no cliff");
    }

    [Fact]
    public void TheLakeAlongTheSouthEdgeIsTooDeepToWade()
    {
        var size = ValleyLayout.SizeMeters;
        var wade = new FirstPersonController().MaxWadeImmersion;
        for (var x = 20f; x < size - 20f; x += 2f)
        {
            var bed = Height(x, size - 1f);
            Assert.True(ValleyLayout.LakeLevel - bed > wade + 0.3f, $"the lake is {ValleyLayout.LakeLevel - bed:0.00} m deep at x = {x}");
        }
    }

    [Fact]
    public void TheBeachIsSandDownToTheWater()
    {
        var sandy = 0;
        var samples = 0;
        for (var x = 100f; x <= 170f; x += 5f)
        {
            var shore = Valley.LakeShoreAt(x);
            for (var s = -9f; s <= -2f; s += 1f)
            {
                samples++;
                if (Data.GetSurface((int)(x / Data.CellSize), (int)((shore + s) / Data.CellSize)) == ValleyGenerator.Sand)
                    sandy++;
            }
        }

        Assert.True(sandy > samples * 0.85f, $"{sandy} of {samples} beach cells are sand");
        Assert.Equal(ValleyGenerator.Sand, ForestAssets.LayerIndex("sand"));
    }

    [Fact]
    public void TheCabinStandsOnALevelPadAtTheEndOfItsSpur()
    {
        var level = Valley.CabinPadLevel;
        foreach (var corner in (Vector2[])[new(-4f, -3.5f), new(4f, -3.5f), new(-4f, 3.5f), new(4f, 3.5f), Vector2.Zero])
        {
            var p = ValleyLayout.CabinToWorld(corner);
            Assert.InRange(Height(p.X, p.Y), level - 0.05f, level + 0.05f);
        }

        // The spur leaves the loop and ends at the door step, walkable all the way, and never crosses the stream.
        var spur = Valley.SpurPolyline;
        Assert.True(Valley.PathField.Distance(spur[0].X, spur[0].Y) < ValleyLayout.PathHalfWidth);
        Assert.True(Vector2.Distance(spur[^1], ValleyLayout.CabinToWorld(ValleyLayout.CabinDoorLocal)) < 1.5f);
        for (var i = 1; i < spur.Length; i++)
        {
            Assert.True(Valley.StreamEdgeDistance(spur[i].X, spur[i].Y) > 3f);
            var slope = float.RadiansToDegrees(MathF.Acos(Data.Grid.SmoothNormalAt(Data.BedHeights, spur[i].X, spur[i].Y).Y));
            Assert.True(slope < 28f, $"the spur at {spur[i]} is {slope:0}° steep");
        }

        Assert.Equal(1, Valley.PathStreamCrossings());
    }

    [Fact]
    public void TheMorningSunClearsTheCliffsAndMountainsFromTheShowpieces()
    {
        var sun = new Vector2(MathF.Sin(float.DegreesToRadians(ValleyLayout.SunAzimuthDegrees)), -MathF.Cos(float.DegreesToRadians(ValleyLayout.SunAzimuthDegrees)));
        var bed = Data.BedHeights.ToArray();
        var size = ValleyLayout.SizeMeters;
        foreach (var eye in (Vector2[])[new(83f, 39.5f), ValleyLayout.Shots[1].Eye, ValleyLayout.Shots[5].Eye, ValleyLayout.Shots[6].Eye])
        {
            var h0 = Height(eye.X, eye.Y) + 1.6f;
            for (var d = 2f; d < 1200f; d += 2f)
            {
                var q = eye + sun * d;
                var inside = q.X is >= 0f and <= ValleyLayout.SizeMeters && q.Y is >= 0f and <= ValleyLayout.SizeMeters;
                // Only what the cliffs and the mountains add may not rise into the sun: the valley's own ridges were
                // there before (R7's already stands at 24.6°).
                var h = inside ? Valley.GroundHeight(q.X, q.Y, cliffs: true) : ForestMountains.Height(Valley, Data.Grid, bed, q.X, q.Y);
                var before = inside ? Valley.GroundHeight(q.X, q.Y, cliffs: false) : float.NegativeInfinity;
                if (h <= before + 0.05f)
                    continue;
                var elevation = float.RadiansToDegrees(MathF.Atan2(h - h0, d));
                Assert.True(elevation < ValleyLayout.SunElevationDegrees - 1f,
                    $"from {eye} the ground at {q} (h {h:0}) rises {elevation:0.0}° towards the sun ({size} m map)");
            }
        }
    }
}

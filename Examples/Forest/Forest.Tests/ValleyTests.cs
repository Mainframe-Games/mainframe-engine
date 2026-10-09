using System.Numerics;
using MainframeEngine;
using MainframeEngine.Trees;

namespace Forest.Tests;

/// <summary>The generated valley: determinism, the layout's rules (one bridge crossing, clear paths) and a headless build.</summary>
public sealed class ValleyTests
{
    private static readonly Lazy<ValleyGenerator> Shared = new(() => ValleyGenerator.Generate());

    private static ValleyGenerator Valley => Shared.Value;

    [Fact]
    public void TheSameSeedGeneratesTheSameValley()
    {
        var other = ValleyGenerator.Generate();
        Assert.Equal(Hash(Valley.Heights), Hash(other.Heights));
        Assert.Equal(Valley.BridgeCentre, other.BridgeCentre);

        var data = Valley.CreateTerrainData();
        var again = other.CreateTerrainData();
        Valley.PaintWeights(data);
        other.PaintWeights(again);
        Assert.Equal(Hash(data.BedHeights), Hash(again.BedHeights));
        Assert.Equal(TreesHash(Valley, data), TreesHash(other, again));
    }

    [Fact]
    public void AnotherSeedGeneratesAnotherValley() =>
        Assert.NotEqual(Hash(Valley.Heights), Hash(ValleyGenerator.Generate(2).Heights));

    [Fact]
    public void TheValleyFallsFromTheOutcropToThePond()
    {
        var head = Valley.StreamCurve.GetPointPosition(0);
        Assert.InRange(head.Y, 14f, 20f);
        Assert.True(Valley.SurfaceAtZ(40f) - Valley.SurfaceAtZ(45f) > 2.5f, "the fall drops several metres");
        Assert.InRange(Valley.SurfaceAtZ(ValleyLayout.PondCentre.Y - ValleyLayout.PondRadii.Y - 2f), ValleyLayout.PondLevel - 0.05f, ValleyLayout.PondLevel + 0.5f);
        // The ridges stand well above the floor: east higher than west.
        var floor = Valley.GroundHeight(Valley.StreamXAtZ(128f), 128f);
        Assert.True(Valley.GroundHeight(250f, 128f) - floor > 35f);
        Assert.True(Valley.GroundHeight(4f, 128f) - floor > 12f);
    }

    [Fact]
    public void ThePathCrossesTheStreamOnceAtTheBridge()
    {
        Assert.Equal(1, Valley.PathStreamCrossings());
        Assert.True(Valley.StreamField.Distance(Valley.BridgeCentre.X, Valley.BridgeCentre.Y) < 0.6f);
        Assert.True(Valley.BridgeBankHeight > Valley.BridgeWaterLevel + 1f);
    }

    [Fact]
    public void ThePathLoopIsAFewMinutesWalk()
    {
        var length = 0f;
        var path = Valley.PathPolyline;
        for (var i = 0; i < path.Length - 1; i++)
            length += Vector2.Distance(path[i], path[i + 1]);
        Assert.InRange(length / 2.5f / 60f, 2.5f, 5f); // minutes at walk speed
    }

    [Fact]
    public void TreesKeepClearOfThePathTheWaterTheBridgeAndTheShots()
    {
        var data = Valley.CreateTerrainData();
        var trees = Trees(Valley, data);
        Assert.InRange(trees.Length, 1200, 2600);
        foreach (var tree in trees)
        {
            var p = new Vector2(tree.Position.X, tree.Position.Z);
            Assert.True(Valley.PathField.Distance(p.X, p.Y) >= ForestVegetation.PathClearance - 0.05f, $"tree at {p} on the path");
            Assert.True(Valley.StreamEdgeDistance(p.X, p.Y) > 1f, $"tree at {p} in the stream");
            Assert.True(ValleyGenerator.PondEdgeDistance(p.X, p.Y) > 1f, $"tree at {p} in the pond");
            Assert.True(Vector2.Distance(p, Valley.BridgeCentre) > 6.9f, $"tree at {p} on the bridge");
            foreach (var (centre, radius) in ValleyLayout.Clearings)
                Assert.True(Vector2.Distance(p, centre) >= radius, $"tree at {p} in the clearing at {centre}");
        }

        // Every zone has its species.
        var species = trees.Select(t => t.Species).ToHashSet();
        foreach (var expected in (int[])[ForestVegetation.PineLarge, ForestVegetation.AspenMedium, ForestVegetation.OakLarge, ForestVegetation.AshMedium,
                     ForestVegetation.BirchLarge, ForestVegetation.BeechMedium, ForestVegetation.SpruceLarge, ForestVegetation.FirMedium])
            Assert.Contains(expected, species);
        Assert.Equal(ForestVegetation.FirMedium + 1, ForestVegetation.CreateTreeSpecies().Length);
    }

    [Fact]
    public void ThePathIsWalkable()
    {
        var data = Valley.CreateTerrainData();
        var grid = data.Grid;
        var bed = data.BedHeights.ToArray();
        foreach (var p in Valley.PathPolyline)
        {
            var slope = float.RadiansToDegrees(MathF.Acos(grid.SmoothNormalAt(bed, p.X, p.Y).Y));
            Assert.True(slope < 35f, $"the path at {p} is {slope:0}° steep");
        }
    }

    [Fact]
    public void TheValleyBuildsHeadlessWithThePlayerOnTheGroundAtTheTrailhead()
    {
        using var h = new ControllerHarness(floor: false);
        var scene = ForestScene.Build();
        scene.GetNode<ForestValley>("Valley").Art = false; // no LFS content in CI
        h.Tree.ChangeScene(scene);

        var valley = scene.GetNode<ForestValley>("Valley");
        var terrain = valley.Terrain!;
        var player = scene.GetNode<FirstPersonController>("Player");
        var spawn = ValleyLayout.Spawn;
        Assert.InRange(player.GlobalPosition.Y - terrain.HeightAt(spawn.X, spawn.Y), 0f, 0.1f);

        h.RunSeconds(1f);
        Assert.True(player.IsOnFloor(), $"the player stands at {player.GlobalPosition}");
        Assert.InRange(player.GlobalPosition.Y - terrain.HeightAt(player.GlobalPosition.X, player.GlobalPosition.Z), -0.05f, 0.1f);
        Assert.Equal(0f, h.Water.WaterDepthAt(player.GlobalPosition));
        Assert.NotNull(player.SurfaceResolver);

        // Water: the stream in pool 1 and the pond (terrain water layer).
        var pool = ValleyLayout.StreamPoints[4].Position;
        Assert.True(h.Water.WaterDepthAt(new Vector3(pool.X, 0f, pool.Z)) > 0.5f);
        var pond = ValleyLayout.PondCentre;
        Assert.True(h.Water.WaterDepthAt(new Vector3(pond.X, 0f, pond.Y)) > 1f);
        Assert.InRange(h.Water.SurfaceHeightAt(new Vector3(pond.X, 0f, pond.Y)), ValleyLayout.PondLevel - 0.05f, ValleyLayout.PondLevel + 0.01f);

        // The bridge's walkway spans the stream above the water.
        var bridge = valley.Bridge!;
        Assert.Equal("wood", bridge.Surface);
        var centre = new Vector3(valley.Generator!.BridgeCentre.X, 0f, valley.Generator.BridgeCentre.Y);
        Assert.True(bridge.GlobalPosition.Y > h.Water.SurfaceHeightAt(centre) + 0.5f);

        Assert.InRange(valley.Forest!.PlacementCount, 1200, 2600);
        Assert.True(valley.Bushes!.PlacementCount > 100);
    }

    [Fact]
    public void FootstepsUseTheTerrainLayersTags()
    {
        Assert.Equal("leaves", ForestValley.FootstepSurface("needles"));
        Assert.Equal("moss", ForestValley.FootstepSurface("moss"));
        Assert.Null(ForestValley.FootstepSurface(null));
        // The splat order matches the generator's channels.
        Assert.Equal(ValleyGenerator.Grass, ForestAssets.LayerIndex("grass"));
        Assert.Equal(ValleyGenerator.Rock, ForestAssets.LayerIndex("rock"));
        Assert.Equal(ValleyGenerator.Needles, ForestAssets.LayerIndex("needles"));
        Assert.Equal(ValleyGenerator.Mud, ForestAssets.LayerIndex("mud"));
    }

    [Fact]
    public void ReferenceShotsAndTheBenchmarkStayOnTheMap()
    {
        foreach (var shot in ValleyLayout.Shots)
        {
            Assert.InRange(shot.Eye.X, 0f, ValleyLayout.SizeMeters);
            Assert.InRange(shot.Eye.Y, 0f, ValleyLayout.SizeMeters);
            Assert.NotNull(ForestDev.FindShot(shot.Name));
        }

        Assert.Equal(ValleyLayout.Shots[0], ForestDev.FindShot("1"));
        Assert.Equal(ValleyLayout.Shots[2], ForestDev.FindShot("r3"));
        foreach (var p in ForestBenchmark.Spline)
            Assert.True(p.X is > 0f and < ValleyLayout.SizeMeters && p.Z is > 0f and < ValleyLayout.SizeMeters);
        Assert.Equal(new Vector2I(1920, 1080), ForestDev.ParseResolution("1920x1080"));
        Assert.Equal(3f, ForestBenchmark.Percentile([1f, 2f, 3f, 4f], 0.5f) + 1f);
    }

    private static TreePlacement[] Trees(ValleyGenerator valley, TerrainData data)
    {
        var grid = data.Grid;
        var bed = data.BedHeights.ToArray();
        return ForestVegetation.PlaceTrees(valley, (x, z) => grid.HeightAt(bed, x, z),
            (x, z) => float.RadiansToDegrees(MathF.Acos(grid.SmoothNormalAt(bed, x, z).Y)));
    }

    private static ulong TreesHash(ValleyGenerator valley, TerrainData data)
    {
        var hash = 14695981039346656037ul;
        foreach (var tree in Trees(valley, data))
        {
            foreach (var value in (ReadOnlySpan<float>)[tree.Position.X, tree.Position.Y, tree.Position.Z, tree.Yaw, tree.Scale, tree.Species])
                hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211ul;
        }

        return hash;
    }

    private static ulong Hash(ReadOnlySpan<float> values)
    {
        var hash = 14695981039346656037ul;
        foreach (var value in values)
            hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211ul;
        return hash;
    }
}

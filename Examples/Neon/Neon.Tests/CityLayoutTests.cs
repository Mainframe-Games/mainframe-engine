using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// The city's layout (World/CityLayout.cs): the same seed gives the same city, the towers stand in their blocks clear of
/// the streets and of each other, and the traffic loops phase N4 follows are closed, inside the city and where they should
/// be (on street centre lines, between the towers, over the roofs).
/// </summary>
public sealed class CityLayoutTests
{
    private static readonly CityLayout Layout = CityLayout.Generate();

    [Fact]
    public void TheSameSeedGivesTheSameCity()
    {
        var again = CityLayout.Generate(CityLayout.DefaultSeed);
        Assert.Equal(Layout.Towers, again.Towers);
        Assert.Equal(Layout.Signs, again.Signs);
        Assert.Equal(Layout.Holograms, again.Holograms);
        Assert.Equal(Layout.EastWest, again.EastWest);
        Assert.Equal(Layout.NorthSouth, again.NorthSouth);
        Assert.Equal(AllLoops(Layout).Select(l => l.Points.ToArray()), AllLoops(again).Select(l => l.Points.ToArray()));

        var other = CityLayout.Generate(CityLayout.DefaultSeed + 1);
        Assert.NotEqual(Layout.Towers, other.Towers);
    }

    [Fact]
    public void TheGridIsFixedRoundTheHomeTower()
    {
        Assert.Contains(Layout.EastWest, s => s.Center == CityLayout.BoulevardZ && s.Width == CityLayout.BoulevardWidth);
        Assert.True(Layout.EastWest.Zip(Layout.EastWest.Skip(1)).All(p => p.First.Center + p.First.Width / 2 < p.Second.Center - p.Second.Width / 2));
        Assert.True(Layout.NorthSouth.Zip(Layout.NorthSouth.Skip(1)).All(p => p.First.Center + p.First.Width / 2 < p.Second.Center - p.Second.Width / 2));
        var home = Layout.HomeBlock;
        var tower = CityLayout.HomeTower;
        Assert.True(home.MinX <= tower.Min.X && tower.Max.X <= home.MaxX && home.MinZ <= tower.Min.Z && tower.Max.Z <= home.MaxZ);
        Assert.Single(Layout.Blocks, b => b.Kind == CityBlockKind.Plaza);
    }

    [Fact]
    public void TowersStandInTheirBlocksClearOfEachOther()
    {
        Assert.InRange(Layout.Towers.Count, 500, 2000);
        Assert.Equal(CityLayout.Archetypes.Length, Layout.Towers.Select(t => t.Archetype).Distinct().Count());
        var footprints = new List<(Vector2 Min, Vector2 Max)>();
        foreach (var tower in Layout.Towers)
        {
            var centre = new Vector2(tower.Position.X, tower.Position.Z);
            var half = tower.HalfExtents;
            var block = Layout.Blocks.Single(b => b.MinX <= centre.X && centre.X <= b.MaxX && b.MinZ <= centre.Y && centre.Y <= b.MaxZ);
            Assert.NotEqual(CityBlockKind.Home, block.Kind);
            Assert.True(centre.X - half.X >= block.MinX && centre.X + half.X <= block.MaxX, $"tower at {centre} sticks out of its block along X");
            Assert.True(centre.Y - half.Y >= block.MinZ && centre.Y + half.Y <= block.MaxZ, $"tower at {centre} sticks out of its block along Z");
            Assert.Equal(CityLayout.GroundY, tower.Position.Y);
            Assert.True(tower.RoofY - CityLayout.GroundY <= CityLayout.MaxTowerTop + 0.01f);
            Assert.All([tower.Custom.X, tower.Custom.Y, tower.Custom.Z, tower.Custom.W], c => Assert.InRange(c, 0f, 1f));
            footprints.Add((centre - half, centre + half));
        }

        for (var i = 0; i < footprints.Count; i++)
            for (var j = i + 1; j < footprints.Count; j++)
            {
                var (a, b) = (footprints[i], footprints[j]);
                var overlap = a.Min.X < b.Max.X - 0.01f && b.Min.X < a.Max.X - 0.01f && a.Min.Y < b.Max.Y - 0.01f && b.Min.Y < a.Max.Y - 0.01f;
                Assert.False(overlap, $"towers {i} and {j} overlap");
            }
    }

    [Fact]
    public void LoopsAreClosedAndInsideTheCity()
    {
        Assert.InRange(Layout.StreetLoops.Count, 6, 12);
        Assert.InRange(Layout.SkyLanes.Count, 3, 8);
        Assert.Equal(2, Layout.AirshipLoops.Count);
        var bounds = CityLayout.Bounds;
        foreach (var loop in AllLoops(Layout))
        {
            Assert.True(loop.Points.Count >= 8, $"{loop.Name} has {loop.Points.Count} points");
            Assert.True(loop.Length > 100f, $"{loop.Name} is {loop.Length} m");
            Assert.NotEqual(loop.Points[0], loop.Points[^1]); // closed by joining the last to the first
            for (var i = 0; i < loop.Points.Count; i++)
            {
                var p = loop.Points[i];
                Assert.True(bounds.Contains(p), $"{loop.Name}[{i}] {p} is outside the city");
                var step = Vector3.Distance(p, loop.Points[(i + 1) % loop.Points.Count]);
                Assert.True(step > 0.01f && step < (loop.Kind == CityLoopKind.Airship ? 200f : 45f), $"{loop.Name}[{i}] is {step} m from the next point");
            }

            var curve = loop.CreateCurve();
            Assert.True(curve.Closed);
            Assert.InRange(curve.GetBakedLength(), loop.Length * 0.9f, loop.Length * 1.02f);
        }
    }

    [Fact]
    public void StreetLoopsFollowStreetCentreLinesOnTheRoad()
    {
        Assert.Contains(Layout.StreetLoops[0].Points, p => MathF.Abs(p.Z - CityLayout.BoulevardZ) < 0.01f);
        foreach (var loop in Layout.StreetLoops)
        {
            Assert.Equal(CityLoopKind.Street, loop.Kind);
            Assert.True(loop.Width > 10f && loop.Speed > 0f);
            foreach (var p in loop.Points)
            {
                Assert.Equal(CityLayout.GroundY + 0.05f, p.Y, 3);
                Assert.True(OnAStreet(p, 0f), $"{loop.Name}: {p} is off the streets");
            }
        }
    }

    [Fact]
    public void SkyLanesFlyBetweenTheTowersAndTheRailRunsOverTheBoulevard()
    {
        foreach (var lane in Layout.SkyLanes)
            foreach (var p in lane.Points)
            {
                Assert.InRange(p.Y - CityLayout.GroundY, 40f, 120f);
                // Over a street, or within a tower's setback of one where the corners are rounded.
                Assert.True(OnAStreet(p, 6.5f), $"{lane.Name}: {p} is over a block");
            }

        Assert.Contains(Layout.SkyLanes[0].Points, p => MathF.Abs(p.Z - CityLayout.BoulevardZ) < 0.01f && MathF.Abs(p.Y) < 10f);
        var rail = Layout.RailLoop;
        Assert.Equal(CityLoopKind.Rail, rail.Kind);
        Assert.All(rail.Points, p => Assert.Equal(CityLayout.GroundY + CityLayout.RailHeight, p.Y, 3));
        Assert.Contains(rail.Points, p => MathF.Abs(p.Z - CityLayout.BoulevardZ) < 0.01f && MathF.Abs(p.X) < 30f);
        Assert.All(rail.Points, p => Assert.True(OnAStreet(p, 6.5f), $"rail point {p} is over a block"));
        var tallest = Layout.Towers.Max(t => t.RoofY);
        foreach (var ship in Layout.AirshipLoops)
            Assert.All(ship.Points, p => Assert.True(p.Y > tallest + 30f, $"{ship.Name} at {p} is not over the roofs ({tallest})"));
    }

    [Fact]
    public void SignsAndHologramsAreThere()
    {
        Assert.Contains(Layout.Signs, s => s.Kind == CitySignKind.Billboard);
        Assert.True(Layout.Signs.Count(s => s.Kind == CitySignKind.Blade) > 200);
        Assert.Contains(Layout.Signs, s => s.Kind == CitySignKind.Board);
        Assert.All(Layout.Signs.Where(s => s.Kind != CitySignKind.Billboard), s => Assert.InRange(s.Custom.X, 0f, CitySign.VerticalWords - 1));
        Assert.Equal([CityHologramKind.Koi, CityHologramKind.Dancer, CityHologramKind.Logo], Layout.Holograms.Select(h => h.Kind));
        var koi = Layout.Holograms[0];
        Assert.InRange(koi.Position.Z, CityLayout.BoulevardZ, 160f); // across the boulevard, in the window's view
    }

    private static IEnumerable<CityLoop> AllLoops(CityLayout layout) =>
        layout.StreetLoops.Concat(layout.SkyLanes).Append(layout.RailLoop).Concat(layout.AirshipLoops);

    /// <summary>Whether (x, z) is on a street (or within <paramref name="margin"/> of one).</summary>
    private static bool OnAStreet(Vector3 p, float margin) =>
        Layout.EastWest.Any(s => MathF.Abs(p.Z - s.Center) <= s.Width / 2 + margin) ||
        Layout.NorthSouth.Any(s => MathF.Abs(p.X - s.Center) <= s.Width / 2 + margin);
}

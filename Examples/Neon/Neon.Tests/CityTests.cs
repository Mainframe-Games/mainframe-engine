using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// The city's scene contract and its kit (World/NeonCity.cs, World/CityTraffic.cs; the kit tests need the LFS art): the
/// scene has the city and no lights for it, the kit has every archetype at CityLayout's size, the city builds, and the
/// traffic moves without allocating.
/// </summary>
public sealed class CityTests
{
    [Fact]
    public void TheSceneHasTheCityItsLookAndNoCityLights()
    {
        var root = NeonScene.Build();
        try
        {
            var city = root.GetNode<NeonCity>("City");
            Assert.Equal(CityLayout.DefaultSeed, city.Seed);
            Assert.Empty(city.Children); // built at load, never saved
            Assert.Empty(root.Children.OfType<OmniLight3D>());
            var environment = root.GetNode<WorldEnvironment>("Environment");
            Assert.NotNull(environment.PostProcess);
            Assert.True(environment.PostProcess!.GlowEnabled);
            Assert.Equal(Tonemapper.Agx, environment.PostProcess.Tonemapper);
            Assert.NotNull(environment.CameraAttributes);
            Assert.True(environment.FogEnabled);
            Assert.True(environment.Sky!.HorizonColor.X > environment.Sky.SkyColor.X * 5f, "no glow at the horizon");
            Assert.True(root.GetNode<Camera3D>("Player/Head/Camera").Far >= CityLayout.SkylineRadii[^1] + 100f);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void TheLookFilesAreCommitted()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, NeonLook.ProfilePath)));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, NeonLook.LensPath)));
        foreach (var shader in (string[])[NeonCity.FacadeShader, NeonCity.SkylineShader, NeonCity.StreetShader, NeonCity.HologramShader, NeonCity.ScreenShader, NeonCity.SignShader, CityTraffic.BeamShader])
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, shader)), shader);
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, shader + ".frag.spv")), shader + " has no SPIR-V");
        }
    }

    [Fact]
    public void TheKitHasEveryArchetypeAtItsSize()
    {
        SkipWithoutArt();
        var towers = CityKit.Load(CityKit.TowersPath);
        foreach (var archetype in CityLayout.Archetypes)
        {
            Assert.True(towers.TryGetValue(archetype.Name, out var mesh), archetype.Name);
            var bounds = mesh!.Bounds;
            // The footprint (the trims stand up to 0.6 m proud), the roof (masts and clutter go above it).
            Assert.InRange(bounds.Size.X, archetype.Width, archetype.Width + 1.5f);
            Assert.InRange(bounds.Size.Z, archetype.Depth, archetype.Depth + 1.5f);
            Assert.InRange(bounds.Min.Y, -0.01f, 0.01f);
            Assert.True(bounds.Max.Y >= archetype.Height, $"{archetype.Name} is {bounds.Max.Y} m tall");
        }

        Assert.Contains("Rail_Track", CityKit.Load(CityKit.RailPath).Keys);
        Assert.Contains("Train_Car", CityKit.Load(CityKit.RailPath).Keys);
        var vehicles = CityKit.Load(CityKit.VehiclesPath);
        Assert.Contains("Car_Ground", vehicles.Keys);
        Assert.Contains("Car_Hover", vehicles.Keys);
        Assert.Contains("Airship", vehicles.Keys);
        Assert.Contains("Holo_Koi", CityKit.Load(CityKit.SignsPath).Keys);
    }

    [Fact]
    public void TheCityBuildsAndItsTrafficMovesWithoutAllocating()
    {
        SkipWithoutArt();
        var city = new NeonCity { Rain = false, Traffic = false };
        try
        {
            city.Build();
            Assert.Equal(CityLayout.Archetypes.Length, city.Towers.Count);
            Assert.Equal(city.Layout!.Towers.Count, city.Towers.Sum(t => t.Multimesh!.InstanceCount));
            Assert.NotNull(city.GetNodeOrNull<MultiMeshInstance3D>("HomeTower"));
            Assert.NotNull(city.GetNodeOrNull<MeshInstance3D>("Ground"));
            Assert.NotNull(city.GetNodeOrNull<MeshInstance3D>("HologramKoi"));

            var traffic = new CityTraffic { Layout = city.Layout };
            city.AddChild(traffic);
            traffic.Build();
            Assert.True(traffic.VehicleCount > 200, $"{traffic.VehicleCount} vehicles");
            for (var i = 0; i < 10; i++)
                traffic.Advance(1f / 60f);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 120; i++)
                traffic.Advance(1f / 60f);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            city.Free();
        }
    }

    private static void SkipWithoutArt()
    {
        var bin = Path.Combine(AppContext.BaseDirectory, CityKit.TowersBufferPath);
        if (!File.Exists(bin) || File.ReadAllText(bin).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            Assert.Skip("Neon's LFS art is not checked out (git lfs pull).");
    }
}

using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Lighting;

/// <summary>Per-light shadow settings: defaults, clamping, and their exported node properties in scene files.</summary>
public sealed class LightShadowSettingsTests
{
    [Fact]
    public void DefaultsDependOnTheLightType()
    {
        Assert.True(new DirectionalLight().CastsShadows);
        Assert.Equal(2048, new DirectionalLight().ShadowResolution);
        Assert.Equal(1024, new SpotLight().ShadowResolution);
        Assert.Equal(512, new PointLight().ShadowResolution);
        var sun = new DirectionalLight();
        Assert.Equal(4, sun.CascadeCount);
        Assert.Equal(0.75f, sun.CascadeSplitLambda);
        Assert.Equal(100f, sun.MaxShadowDistance);
    }

    [Fact]
    public void SettingsAreClamped()
    {
        var sun = new DirectionalLight { ShadowResolution = 10, CascadeCount = 9, CascadeSplitLambda = 2f, CascadeBlend = 1f, ShadowBias = -1f, ShadowNormalBias = 99f };
        Assert.Equal(Light.MinShadowResolution, sun.ShadowResolution);
        Assert.Equal(4, sun.CascadeCount);
        Assert.Equal(1f, sun.CascadeSplitLambda);
        Assert.Equal(0.5f, sun.CascadeBlend);
        Assert.Equal(0f, sun.ShadowBias);
        Assert.Equal(16f, sun.ShadowNormalBias);
        sun.ShadowResolution = 100_000;
        Assert.Equal(Light.MaxShadowResolution, sun.ShadowResolution);
    }

    [Theory]
    [InlineData(1000, 1024)]
    [InlineData(700, 512)]
    [InlineData(768, 1024)] // ties round up
    [InlineData(2048, 2048)]
    public void ResolutionRoundsToTheNearestPowerOfTwo(int requested, int expected)
    {
        Assert.Equal(expected, new SpotLight { ShadowResolution = requested }.ShadowResolutionPow2);
    }

    [Fact]
    public void ShadowPropertiesRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            CastsShadows = false,
            ShadowResolution = 4096,
            ShadowBias = 0.25f,
            ShadowNormalBias = 2f,
            ShadowCascades = 3,
            ShadowSplitLambda = 0.5f,
            ShadowMaxDistance = 60f,
            ShadowCascadeBlend = 0.2f,
        };
        var spot = new SpotLight3D { Name = "Spot", ShadowResolution = 256 };
        var lamp = new OmniLight3D { Name = "Lamp" };
        foreach (var child in new Node[] { sun, spot, lamp })
        {
            root.AddChild(child);
            child.Owner = root;
        }

        var json = SceneSaver.ToJson(root);
        var copy = PackedScene.Parse(json).Instantiate();
        var sunCopy = (DirectionalLight3D)copy.GetChild(0);
        Assert.False(sunCopy.CastsShadows);
        Assert.Equal(4096, sunCopy.ShadowResolution);
        Assert.Equal(0.25f, sunCopy.ShadowBias);
        Assert.Equal(2f, sunCopy.ShadowNormalBias);
        Assert.Equal(3, sunCopy.ShadowCascades);
        Assert.Equal(0.5f, sunCopy.ShadowSplitLambda);
        Assert.Equal(60f, sunCopy.ShadowMaxDistance);
        Assert.Equal(0.2f, sunCopy.ShadowCascadeBlend);
        Assert.Equal(256, ((SpotLight3D)copy.GetChild(1)).ShadowResolution);

        // Defaults are not written: the point light saves no shadow properties.
        using var doc = JsonDocument.Parse(json);
        var lampEntry = doc.RootElement.GetProperty("root").GetProperty("children").EnumerateArray().First(n => n.GetProperty("name").GetString() == "Lamp");
        Assert.False(lampEntry.TryGetProperty("props", out _));

        copy.Free();
        root.Free();
    }
}

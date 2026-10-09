using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.Tests.Water;

/// <summary>ADR 0173: refraction settings, the water-scene push block, falls, spray cards and the waterSsr setting.</summary>
public sealed class WaterSceneTests
{
    [Fact]
    public void RefractionIsOffByDefaultWithTheProposalsSettings()
    {
        var material = new WaterMaterial3D();
        Assert.False(material.RefractionEnabled);
        Assert.Equal(1f, material.RefractionStrength);
        Assert.Equal(0.15f, material.RefractionRoughness);
        Assert.True(material.ScreenSpaceReflections);
        Assert.Null(material.CausticsTexture);
        Assert.Equal(3f, material.CausticsScale);
        Assert.Equal(0.5f, material.CausticsStrength);
        Assert.Equal(3f, material.CausticsMaxDepth);
    }

    [Fact]
    public void TheNewSettersTouchTheMaterial()
    {
        var material = new WaterMaterial3D();
        var version = material.Version;
        material.RefractionEnabled = true;
        material.RefractionStrength = 0.5f;
        material.RefractionRoughness = 0.3f;
        material.ScreenSpaceReflections = false;
        material.CausticsTexture = WaterTextures.Caustics;
        material.CausticsScale = 2f;
        material.CausticsStrength = 1f;
        material.CausticsMaxDepth = 2f;
        Assert.Equal(version + 8, material.Version);
        material.RefractionEnabled = true; // unchanged
        Assert.Equal(version + 8, material.Version);
    }

    [Fact]
    public void ThePushBlockCarriesTheMaterialAndTheQuality()
    {
        Assert.Equal((int)MeshRenderer.WaterScenePush.Size, Marshal.SizeOf<MeshRenderer.WaterScenePush>());
        Assert.True(MeshRenderer.WaterScenePush.Size <= FrameContext.PushConstantSize);
        var material = new WaterMaterial3D
        {
            RefractionEnabled = true,
            RefractionStrength = 0.8f,
            RefractionRoughness = 0.25f,
            CausticsScale = 4f,
            CausticsStrength = 0.7f,
            CausticsMaxDepth = 2.5f,
        };
        var push = MeshRenderer.WaterScenePush.For(material, null, WaterSsrQuality.High);
        Assert.Equal(new Vector4(0.8f, 0.25f, 0f, 0f), push.Refraction); // no copy: level 0, not the view's
        Assert.Equal(new Vector4(0.25f, 0.7f, 2.5f, 1f), push.Caustics);
        Assert.Equal(MeshRenderer.WaterScenePush.SsrSettings(WaterSsrQuality.High), push.Ssr);
        Assert.Equal(MeshRenderer.WaterSceneReactivity, push.Extra.X);

        Assert.Equal(0f, MeshRenderer.WaterScenePush.For(material, null, WaterSsrQuality.Off).Caustics.W);
        material.ScreenSpaceReflections = false;
        Assert.Equal(0f, MeshRenderer.WaterScenePush.For(material, null, WaterSsrQuality.High).Caustics.W);
        Assert.Equal(Vector4.Zero, MeshRenderer.WaterScenePush.SsrSettings(WaterSsrQuality.Off));
        Assert.True(MeshRenderer.WaterScenePush.SsrSettings(WaterSsrQuality.High).X > MeshRenderer.WaterScenePush.SsrSettings(WaterSsrQuality.Low).X);
    }

    [Theory]
    [InlineData(1920u, 1080u, 6)]
    [InlineData(64u, 32u, 6)]
    [InlineData(8u, 8u, 4)]
    [InlineData(1u, 1u, 1)]
    [InlineData(3u, 2u, 2)]
    public void TheCopyHasAtMostSixLevels(uint width, uint height, int levels) =>
        Assert.Equal(levels, WaterSceneTextures.MipsFor(width, height));

    [Fact]
    public void EveryShaderSetHasPipelineSlots()
    {
        foreach (var shaders in Enum.GetValues<ShaderSetId>())
            Assert.True((int)shaders < MaterialGpu.ShaderSetCount, $"{shaders} has no pipeline slots in MaterialGpu.");
        Assert.True(MeshRenderer.UsesSceneLayout(ShaderSetId.MeshWaterScene));
        Assert.True(MeshRenderer.UsesSceneLayout(ShaderSetId.MeshSpray));
        Assert.False(MeshRenderer.UsesSceneLayout(ShaderSetId.MeshWater));
    }

    [Fact]
    public void TheNewTexturesAreDeterministicAndTile()
    {
        foreach (var generate in new Func<byte[]>[] { WaterTextures.GenerateCausticsPixels, WaterTextures.GenerateMistPixels })
        {
            var a = generate();
            Assert.Equal(a, generate());
            var size = WaterTextures.Size;
            // Opposite edges continue each other: the mean step across the wrap is like the mean step inside.
            double wrap = 0, inside = 0;
            for (var y = 0; y < size; y++)
            {
                wrap += Math.Abs(a[(y * size + size - 1) * 4] - a[y * size * 4]);
                inside += Math.Abs(a[(y * size + size / 2) * 4] - a[(y * size + size / 2 - 1) * 4]);
            }

            Assert.True(wrap < inside * 2 + size * 2, $"the texture does not tile (wrap step {wrap / size:0.0}, inside {inside / size:0.0})");
            Assert.Contains(a, b => b > 200);
            Assert.Contains(a, b => b < 40);
        }
    }

    [Fact]
    public void FallsAndSprayPackIntoTheMaterialBlock()
    {
        var fall = MaterialParams.From(new WaterfallMaterial3D { Opacity = 0.5f, StreakScale = 3f, StreakLength = 6f, FlowScale = 2f, Roughness = 0.3f }, 0);
        Assert.Equal(0.5f, fall.Albedo.W);
        Assert.Equal(new Vector4(3f, 6f, 2f, 0.3f), fall.UvTransform);
        var spray = MaterialParams.From(new SprayMaterial3D { Opacity = 0.4f, Cycle = 2f, Rise = 1f, Growth = 1.5f, SoftDistance = 0.5f }, 0);
        Assert.Equal(0.4f, spray.Albedo.W);
        Assert.Equal(new Vector4(2f, 1f, 1.5f, 0.5f), spray.UvTransform);
        Assert.True(new WaterfallMaterial3D().RenderState.IsTransparent);
        Assert.Equal(CullMode.Disabled, new WaterfallMaterial3D().RenderState.EffectiveCull);
        Assert.False(new SprayMaterial3D().RenderState.CastsShadows);
    }

    // A river over a 4 m step: flat to the lip, a drop, flat from the foot (handles flat at both, as the Forest's).
    private static Curve3D StepCurve(float drop = 4f)
    {
        var curve = new Curve3D();
        float[] zs = [0f, -10f, -14f, -26f];
        float[] ys = [drop + 0.2f, drop, 0f, -0.2f];
        for (var i = 0; i < zs.Length; i++)
        {
            var handle = new Vector3(0f, 0f, i is 1 or 2 ? -1.2f : -3f);
            curve.AddPoint(new Vector3(0f, ys[i], zs[i]), -handle, handle);
            curve.SetPointWidth(i, 3f);
            curve.SetPointDepth(i, 0.6f);
        }

        return curve;
    }

    [Fact]
    public void AStepInTheProfileIsAFallWithAJet()
    {
        var builder = new RiverBuilder();
        builder.Build(StepCurve(), RiverSettings.Default);
        var fall = Assert.Single(builder.Falls);
        Assert.True(fall.Drop > 2.5f, $"drop {fall.Drop}");
        Assert.True(fall.Lip.Y > fall.Foot.Y);
        Assert.True(Vector2.Distance(fall.Downstream, new Vector2(0f, -1f)) < 1e-3f);
        Assert.Equal(3f, fall.Width, 2);

        // The ribbon leaves out the steep run; the jet starts at the lip and ends at the foot, facing downstream.
        var columns = RiverSettings.Default.CrossSegments + 1;
        var segments = builder.SectionCount - 1;
        Assert.True(builder.Mesh.IndexCount < segments * (columns - 1) * 6);
        var jet = builder.FallMesh;
        Assert.True(jet.VertexCount >= 2 * columns);
        Assert.Equal(0, jet.IndexCount % 6);
        var top = (jet.Positions[0] + jet.Positions[columns - 1]) * 0.5f;
        var bottom = (jet.Positions[^1] + jet.Positions[^columns]) * 0.5f;
        Assert.True(Vector3.Distance(top, fall.Lip) < 1e-3f);
        Assert.True(Vector3.Distance(bottom, fall.Foot) < 1e-3f);
        Assert.Equal(0f, jet.Custom0[0].X);
        Assert.Equal(1f, jet.Custom0[^1].X, 4);
        Assert.True(jet.Custom0[^1].Y > jet.Custom0[0].Y); // faster at the base
        Assert.True(jet.Custom0[^1].W > jet.Custom0[0].W); // whiter at the base
        var middle = jet.VertexCount / columns / 2 * columns;
        Assert.True(jet.Normals[middle].Z < -0.5f, $"the sheet faces {jet.Normals[middle]}"); // downstream (−Z)

        // Plunge foam on the ribbon right below the foot.
        var footVertex = Array.FindIndex(builder.Mesh.Positions, p => Vector3.Distance(p, fall.Foot) < 1e-3f);
        Assert.True(footVertex >= 0);
        Assert.True(builder.Mesh.Custom0[footVertex].W > 0.5f);
    }

    [Fact]
    public void GentleRiversAndZeroFallSlopeHaveNoFalls()
    {
        var builder = new RiverBuilder();
        builder.Build(RiverBuilderTests.StraightCurve(length: 100f, drop: 10f), RiverSettings.Default);
        Assert.Empty(builder.Falls);
        Assert.Equal(0, builder.FallMesh.VertexCount);

        var off = RiverSettings.Default;
        off.FallSlope = 0f;
        builder.Build(StepCurve(), off);
        Assert.Empty(builder.Falls);
        var high = RiverSettings.Default;
        high.FallMinHeight = 10f;
        builder.Build(StepCurve(), high);
        Assert.Empty(builder.Falls);
    }

    [Fact]
    public void ARiverInATreeDrawsItsFallsAndSpray()
    {
        var tree = new SceneTree(new ServerRegistry());
        var scene = new Node3D { Name = "Scene" };
        tree.ChangeScene(scene);
        var river = new River3D { Name = "River", Curve = StepCurve() };
        scene.AddChild(river);
        try
        {
            Assert.Single(river.Falls);
            Assert.NotNull(river.FallsNode?.Mesh);
            Assert.IsType<WaterfallMaterial3D>(river.FallsNode!.MaterialOverride);
            var spray = Assert.Single(river.SprayNodes);
            Assert.Null(spray.Owner); // generated: never saved
            Assert.Null(river.FallsNode.Owner);

            river.Spray = false;
            Assert.Empty(river.SprayNodes);
            river.Spray = true;
            Assert.Single(river.SprayNodes);
            river.FallSlope = 0f;
            Assert.Empty(river.Falls);
            Assert.Null(river.FallsNode.Mesh);
            Assert.Empty(river.SprayNodes);
        }
        finally
        {
            tree.Shutdown();
            tree.Servers.Dispose();
        }
    }

    [Fact]
    public void SprayCardsAreDeterministicBillboardQuads()
    {
        var a = new SprayCards3D { Count = 6, Seed = 3 };
        var b = new SprayCards3D { Count = 6, Seed = 3 };
        var surface = a.CardMesh!.Surfaces[0];
        Assert.Equal(24, surface.Positions.Length);
        Assert.Equal(36, surface.Indices.Length);
        Assert.Equal(surface.Positions, b.CardMesh!.Surfaces[0].Positions);
        Assert.Equal(surface.Custom0, b.CardMesh!.Surfaces[0].Custom0);
        for (var c = 0; c < 6; c++)
        {
            // Four vertices at the card's centre, corners ±1 in the normal stream.
            Assert.Equal(surface.Positions[c * 4], surface.Positions[c * 4 + 3]);
            Assert.Equal(-1f, surface.Normals[c * 4].X);
            Assert.Equal(1f, surface.Normals[c * 4 + 3].Y);
            Assert.True(a.CustomAabb!.Value.Contains(surface.Positions[c * 4]));
        }

        a.Count = 9;
        Assert.Equal(36, a.CardMesh!.Surfaces[0].Positions.Length);
        Assert.NotEqual(surface.Positions[0], new SprayCards3D { Count = 6, Seed = 4 }.CardMesh!.Surfaces[0].Positions[0]);
    }

    [Fact]
    public void TheWaterSsrSettingRoundTrips()
    {
        Assert.Equal(WaterSsrQuality.Low, new ProjectSettings().Rendering.WaterSsr);
        Assert.DoesNotContain("waterSsr", Encoding.UTF8.GetString(new ProjectSettings().ToJson()), StringComparison.Ordinal);
        var settings = new ProjectSettings();
        settings.Rendering.WaterSsr = WaterSsrQuality.High;
        var parsed = ProjectSettings.Parse(settings.ToJson(), "test.mfproj");
        Assert.Equal(WaterSsrQuality.High, parsed.Rendering.WaterSsr);
        Assert.Equal(WaterSsrQuality.Off,
            ProjectSettings.Parse(Encoding.UTF8.GetBytes("""{ "format": 2, "rendering": { "waterSsr": "off" } }"""), "test.mfproj").Rendering.WaterSsr);
    }
}

using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0172: the foliage block of <see cref="MaterialParams"/>, alpha antialiasing and the coverage import settings.</summary>
public sealed class FoliageMaterialBlockTests
{
    [Fact]
    public void DefaultsPackTheOldFoliageExactly()
    {
        var p = MaterialParams.From(new FoliageMaterial3D(), 0);
        Assert.Equal(new Vector4(0.5f, 1f, 1f, 1f), p.Emission); // translucency, wind, bend, trunk sway
        Assert.Equal(new Vector4(4f, 1f, 1f, 1f), p.Foliage0);   // scatter 4, white: the old back-light
        Assert.Equal(Vector4.Zero, p.Foliage1);                  // no per-instance range
        Assert.Equal(0f, p.Foliage2.X);                          // no moss
        Assert.Equal(0f, p.Foliage2.Y);                          // no detail normals
        Assert.Equal(AlphaMode.Cutout, new FoliageMaterial3D().RenderState.Alpha);
        Assert.Equal(AlphaMode.Opaque, new FoliageMaterial3D { AlphaCutout = false }.RenderState.Alpha);
    }

    [Fact]
    public void AlphaToCoverageMapsOntoTheDitherEdge()
    {
        Assert.Equal(1f, MaterialParams.From(new FoliageMaterial3D { AlphaDither = true }, 0).Pbr.W);
        Assert.Equal(0.6f, MaterialParams.From(new FoliageMaterial3D { AlphaAntialiasingMode = AlphaAntialiasing.AlphaToCoverage }, 0).Pbr.W, 5);
        Assert.Equal(1f, MaterialParams.From(new FoliageMaterial3D
        {
            AlphaAntialiasingMode = AlphaAntialiasing.AlphaToCoverage,
            AlphaAntialiasingEdge = 0.5f,
        }, 0).Pbr.W, 5); // edge 0.5 = AlphaDither's width
        Assert.Equal(0f, MaterialParams.From(new FoliageMaterial3D
        {
            AlphaAntialiasingMode = AlphaAntialiasing.AlphaToCoverage,
            AlphaCutout = false,
        }, 0).Pbr.W);
    }

    [Fact]
    public void InstanceVisibilityPacksItsRangeAndCutsOut()
    {
        var bark = new FoliageMaterial3D
        {
            AlphaCutout = false,
            InstanceVisibility = true,
            InstanceVisibilityBegin = 20f,
            InstanceVisibilityEnd = 55f,
            InstanceVisibilityMargin = 4f,
        };
        Assert.Equal(new Vector4(20f, 55f, 4f, 1f), MaterialParams.From(bark, 0).Foliage1);
        Assert.Equal(AlphaMode.Cutout, bark.RenderState.Alpha); // its bands discard
        Assert.Equal(0f, MaterialParams.From(bark, 0).Params.Z); // an opaque surface's cutoff stays 0
    }

    [Fact]
    public void ImpostorPacksItsViews()
    {
        var p = MaterialParams.From(new ImpostorMaterial3D { Frames = 8, Center = new Vector3(0f, 9f, 0f), Radius = 12f, Hemi = true }, 0);
        Assert.Equal(new Vector4(0f, 9f, 0f, 12f), p.Foliage2);
        Assert.Equal(8f, p.Foliage3.X);
        Assert.Equal(1f, p.Foliage3.Y);
        Assert.Equal(0f, p.Foliage3.W); // views blended (not dithered) by default
        Assert.Equal(AlphaMode.Cutout, new ImpostorMaterial3D().RenderState.Alpha);
        Assert.Equal(CullMode.Disabled, new ImpostorMaterial3D().RenderState.EffectiveCull);
    }

    [Fact]
    public void ShadowRangePacksIntoVariationZwAndFollowsTheVisibilityRangeByDefault()
    {
        // ADR 0179: each end + 1; 0 = the visibility range's (casterInstanceVisible in include/foliage_caster.slang).
        Assert.Equal(new Vector4(0.1f, 0.2f, 0f, 0f), MaterialParams.From(new FoliageMaterial3D { InstanceValueJitter = 0.1f, InstanceHueJitter = 0.2f }, 0).Variation);
        Assert.Equal(Vector2.Zero, ZW(MaterialParams.From(new ImpostorMaterial3D(), 0).Variation));

        var leaves = new FoliageMaterial3D { InstanceVisibility = true, InstanceShadowBegin = 55f, InstanceShadowEnd = 0f, InstanceValueJitter = 0.1f };
        var p = MaterialParams.From(leaves, 0);
        Assert.Equal(new Vector2(56f, 1f), ZW(p.Variation)); // from 55 m, unbounded
        Assert.Equal(0.1f, p.Variation.X);                    // the colour jitter is untouched
        Assert.Equal(new Vector2(21f, 0f), ZW(MaterialParams.From(new ImpostorMaterial3D { InstanceShadowBegin = 20f }, 0).Variation));
        Assert.Equal(0f, MaterialParams.ShadowRange(-1f));
        Assert.Equal(1f, MaterialParams.ShadowRange(0f));

        var version = leaves.Version;
        leaves.InstanceShadowBegin = 40f;
        Assert.Equal(version + 1, leaves.Version);

        static Vector2 ZW(Vector4 v) => new(v.Z, v.W);
    }

    [Fact]
    public void ShadowDensityPacksForTheLeafCasters()
    {
        Assert.Equal(1f, MaterialParams.From(new FoliageMaterial3D(), 0).Foliage3.W); // casts everything by default
        Assert.Equal(0.6f, MaterialParams.From(new FoliageMaterial3D { ShadowDensity = 0.6f }, 0).Foliage3.W, 5);
        Assert.Equal(0f, MaterialParams.From(new FoliageMaterial3D { ShadowDensity = -1f }, 0).Foliage3.W);
        Assert.Equal(1f, MaterialParams.From(new ImpostorMaterial3D(), 0).Emission.Y);
        Assert.Equal(0.5f, MaterialParams.From(new ImpostorMaterial3D { ShadowDensity = 0.5f }, 0).Emission.Y, 5);

        var material = new FoliageMaterial3D();
        var version = material.Version;
        material.ShadowDensity = 0.4f;
        Assert.Equal(version + 1, material.Version);

        // The cluster cards take TreeOptions.ClusterShadowDensity; single cards keep 1.
        var options = TreePresets.Load("Oak Medium");
        options.ClusterShadowDensity = 0.55f;
        Assert.Equal(1f, Assert.IsType<FoliageMaterial3D>(TreeMaterials.Leaves(options, TreeStyle.Realistic)).ShadowDensity);
        Assert.Equal(0.55f, options.Clone().ClusterShadowDensity);
    }

    [Fact]
    public void CoverageSettingsRoundTripThroughTheMeta()
    {
        var settings = new TextureImportSettings { PreserveAlphaCoverage = true, AlphaCoverageCutoff = 0.3f };
        var meta = new AssetMeta { Settings = settings.ToMetaSettings() };
        var read = TextureImportSettings.FromMeta(meta);
        Assert.True(read.PreserveAlphaCoverage);
        Assert.Equal(0.3f, read.AlphaCoverageCutoff);
        Assert.False(TextureImportSettings.FromMeta(new AssetMeta { Settings = TextureImportSettings.Default.ToMetaSettings() }).PreserveAlphaCoverage);
    }
}

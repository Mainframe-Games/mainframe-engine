using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary><see cref="TreeOptions"/>, <see cref="TreeLevel"/>, <see cref="EzTreeJson"/> and the preset files.</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class TreeOptionsTests
{
    private const string OakMediumJson = """
        {
          "seed": 35729, "type": "deciduous",
          "bark": { "type": "Bark001", "tint": 16774097, "flatShading": false, "textured": true, "textureScale": { "x": 1, "y": 10 } },
          "branch": {
            "levels": 3,
            "angle": { "1": 54, "2": 58, "3": 32 },
            "children": { "0": 6, "1": 4, "2": 3 },
            "force": { "direction": { "x": 0, "y": 1, "z": 0 }, "strength": 0.02 },
            "gnarliness": { "0": 0, "1": -0.1, "2": -0.15, "3": 0.09 },
            "length": { "0": 37.24, "1": 11.08, "2": 12.39, "3": 7.16 },
            "radius": { "0": 1.41, "1": 0.9, "2": 0.69, "3": 1.19 },
            "sections": { "0": 8, "1": 6, "2": 3, "3": 1 },
            "segments": { "0": 7, "1": 5, "2": 3, "3": 3 },
            "start": { "1": 0.49, "2": 0.06, "3": 0.12 },
            "taper": { "0": 0.73, "1": 0.42, "2": 0.69, "3": 0.75 },
            "twist": { "0": -0.23, "1": 0.42, "2": 0, "3": 0 }
          },
          "leaves": { "type": "oak", "billboard": "double", "angle": 42, "count": 18, "start": 0.16, "size": 2.5, "sizeVariance": 0.7, "tint": 14013901, "alphaTest": 0.5 },
          "trellis": { "enabled": false }
        }
        """;

    [Fact]
    public void EzTreeJsonMapsEveryOption()
    {
        var options = EzTreeJson.Read(OakMediumJson, "Oak Medium");
        Assert.Equal(35729, options.Seed);
        Assert.Equal(TreeType.Deciduous, options.Type);
        Assert.Equal(3, options.Levels);
        Assert.Equal(0xFF, options.BarkTint.A);
        Assert.Equal(unchecked((int)0xFFFFF3D1), options.BarkTint.ToArgb()); // 16774097 = #FFF3D1, opaque
        Assert.Equal(new Vector2(1, 10), options.BarkTextureScale);
        Assert.Equal(37.24, options.Level[0].Length);
        Assert.Equal(-0.15, options.Level[2].Gnarliness);
        Assert.Equal(54, options.Level[1].Angle);
        Assert.Equal(0, options.Level[0].Angle); // levels have no angle at 0: Ez Tree's default object has none
        Assert.Equal(6, options.Level[0].Children);
        Assert.Equal(0.02, options.GrowthForce);
        Assert.Equal(TreeBillboard.Double, options.LeafBillboard);
        Assert.Equal(18, options.LeafCount);
        Assert.Equal(0.5f, options.LeafAlphaCutoff);
        Assert.True(options.LeafRoundedNormals); // missing in every preset: keeps Ez Tree's default
        Assert.Equal(0.3, options.Scale);
        Assert.Equal(BarkUvMode.Continuous, options.BarkUv);
    }

    [Fact]
    public void MissingKeysKeepEzTreesDefaults()
    {
        var options = EzTreeJson.Read("""{ "seed": 7, "branch": { "length": { "2": 3.5 } }, "leaves": { "billboard": "single" } }""");
        var defaults = TreeLevel.EzTreeDefaults();
        Assert.Equal(7, options.Seed);
        Assert.Equal(3.5, options.Level[2].Length);
        Assert.Equal(defaults[2].Radius, options.Level[2].Radius);
        Assert.Equal(defaults[0].Length, options.Level[0].Length);
        Assert.Equal(TreeBillboard.Single, options.LeafBillboard);
        Assert.Equal(2.5, options.LeafSize);
        Assert.Equal("Bark001", options.BarkTexture);
    }

    [Fact]
    public void PresetFilesHoldEzTreesValuesAsDoubles()
    {
        // Aspen Large carries values a float would change: 69.60000000000001 and 0.021739130434782622.
        var aspen = TreePresets.Load("Aspen Large");
        Assert.Equal(69.60000000000001, aspen.Level[0].Length);
        Assert.Equal(0.021739130434782622, aspen.GrowthForce);
        Assert.Equal(0.15217391304347827, aspen.LeafStart);
        Assert.Equal(30631, aspen.Seed);
        Assert.Equal("Aspen Large", aspen.ResourceName);

        // The JSON importer and the preset file agree on every generator input.
        var fromJson = EzTreeJson.Read(OakMediumJson).ToParams();
        var fromFile = TreePresets.Load("Oak Medium").ToParams();
        Assert.Equal(fromJson.Length, fromFile.Length);
        Assert.Equal(fromJson.Gnarliness, fromFile.Gnarliness);
        Assert.Equal(fromJson.Twist, fromFile.Twist);
        Assert.Equal(fromJson.Start, fromFile.Start);
        Assert.Equal(fromJson.Children, fromFile.Children);
        Assert.Equal(fromJson.LeafSize, fromFile.LeafSize);
    }

    [Fact]
    public void EveryPresetLoadsWithItsEzTreeSeedAndTextures()
    {
        foreach (var (name, index) in TreePresets.Names.Select((n, i) => (n, i)))
        {
            var options = TreePresets.Load(name);
            if (index < TreePresets.EzTreeCount)
                Assert.Equal(TreeFixture.Preset(name).GetProperty("seed").GetInt32(), options.Seed);
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Content", "Trees", "Leaves", options.LeafTexture + ".png")), $"{name}: leaf texture {options.LeafTexture}");
            // ambientCG sets (JPG) or the engine's painted ones (PNG, ADR 0172): TreeMaterials.BarkMapPath finds either.
            foreach (var map in new[] { "Color", "NormalGL", "Roughness" })
                Assert.True(File.Exists(ContentPaths.Resolve(TreeMaterials.BarkMapPath(options.BarkTexture, map), ContentPaths.BaseDirectory)),
                    $"{name}: bark {options.BarkTexture} {map}");
        }

        Assert.Throws<ArgumentException>(() => TreePresets.FileName("Trellis"));
    }

    [Fact]
    public void MresRoundTripKeepsDoublesAndLevels()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-trees-" + Guid.NewGuid().ToString("N"));
        var previous = AssetDatabase.Current;
        Directory.CreateDirectory(Path.Combine(project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(project);
        ResourceLoader.ClearCache();
        try
        {
            var options = TreePresets.Load("Aspen Large");
            options.Scale = 0.25;
            options.Level[3].Twist = 0.1 + 0.2; // 0.30000000000000004
            var path = Path.Combine(project, AssetDatabase.ContentFolder, "aspen.mres");
            var uid = ResourceSaver.Save(options, path);

            ResourceLoader.ClearCache();
            var loaded = ResourceLoader.Load<TreeOptions>(uid);
            Assert.Equal(0.25, loaded.Scale);
            Assert.Equal(0.1 + 0.2, loaded.Level[3].Twist);
            Assert.Equal(69.60000000000001, loaded.Level[0].Length);
            Assert.Equal(4, loaded.Level.Length);
            Assert.Equal(options.ToParams().Radius, loaded.ToParams().Radius);
            loaded.Release();
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void CloneIsDeepAndLevelChangesRaiseChanged()
    {
        var options = TreePresets.Load("Oak Medium");
        var changes = 0;
        options.Changed += () => changes++;

        options.Level[1].Length = 99;
        Assert.Equal(1, changes);
        options.Seed = 5;
        Assert.Equal(2, changes);
        options.Seed = 5; // unchanged: no event
        Assert.Equal(2, changes);

        var copy = options.Clone();
        Assert.NotSame(options.Level[1], copy.Level[1]);
        Assert.Equal(99, copy.Level[1].Length);
        copy.Level[1].Length = 1;
        Assert.Equal(99, options.Level[1].Length);
        Assert.Equal(2, changes); // the copy's levels are not the original's

        // Presets are loaded as copies: editing one never touches the file's cached instance.
        Assert.NotEqual(99, TreePresets.Load("Oak Medium").Level[1].Length);
    }

    [Fact]
    public void ToParamsFillsMissingLevelsWithDefaults()
    {
        var options = new TreeOptions { Level = [new TreeLevel { Length = 5, Sections = 3, Segments = 4, Radius = 1 }] };
        var p = options.ToParams();
        Assert.Equal(5, p.Length[0]);
        Assert.Equal(TreeLevel.EzTreeDefaults()[1].Length, p.Length[1]);
        p.Validate();
    }
}

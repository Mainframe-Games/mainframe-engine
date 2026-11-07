using System.Text.Json;

namespace MainframeEngine.Tests.Animation.Humanoid;

/// <summary>G1.12 (ADR 0281): the <c>.mfmeta</c> <c>humanoid</c> overrides and the bone, limit and pose tables.</summary>
public class HumanoidSettingsTests
{
    [Fact]
    public void HumanoidOverridesReadWriteAndCompareByValue()
    {
        var settings = new ModelImportSettings
        {
            Humanoid = new Dictionary<string, string> { ["LeftUpperLeg"] = "Thigh.L", ["LeftLowerLeg"] = "Shin.L" },
        };
        var meta = new AssetMeta { Uid = "mdl_cccccccccccc", Importer = ModelImportSettings.ImporterName, Settings = settings.ToMetaSettings() };

        var read = ModelImportSettings.FromMeta(meta);

        Assert.Equal("Thigh.L", read.Humanoid["LeftUpperLeg"]);
        Assert.Equal("Shin.L", read.Humanoid["LeftLowerLeg"]);
        Assert.Equal(settings, read);
        Assert.NotEqual(settings, settings with { Humanoid = new Dictionary<string, string> { ["LeftUpperLeg"] = "Other" } });
    }

    [Fact]
    public void SettingsWithoutOverridesWriteNoHumanoidKey()
    {
        Assert.False(ModelImportSettings.Default.ToMetaSettings().ContainsKey("humanoid"));
        Assert.Empty(ModelImportSettings.Default.Humanoid);
    }

    [Fact]
    public void NonStringOverridesAreSkippedAndANonObjectIsIgnored()
    {
        var meta = new AssetMeta
        {
            Uid = "mdl_dddddddddddd",
            Importer = ModelImportSettings.ImporterName,
            Settings = new Dictionary<string, JsonElement>
            {
                ["humanoid"] = JsonDocument.Parse("""{ "Head": "head", "Neck": 3 }""").RootElement,
            },
        };

        var read = ModelImportSettings.FromMeta(meta);

        Assert.Equal("head", Assert.Single(read.Humanoid).Value);

        var bad = new AssetMeta
        {
            Uid = "mdl_eeeeeeeeeeee",
            Importer = ModelImportSettings.ImporterName,
            Settings = new Dictionary<string, JsonElement> { ["humanoid"] = JsonDocument.Parse("[1]").RootElement },
        };
        Assert.Empty(ModelImportSettings.FromMeta(bad).Humanoid);
    }

    [Fact]
    public void EveryBoneHasAParentDirectionAndMirror()
    {
        foreach (var bone in HumanoidBones.All)
        {
            Assert.Equal(bone == HumanoidBone.Hips, HumanoidBones.GetParent(bone) is null);
            Assert.Equal(1f, HumanoidBones.GetDirection(bone).Length(), 1e-5f);
            Assert.Equal(bone, HumanoidBones.Mirror(HumanoidBones.Mirror(bone)));
            Assert.Equal(HumanoidBones.GetSide(bone) == HumanoidSide.Centre, HumanoidBones.Mirror(bone) == bone);
            foreach (var component in Enum.GetValues<JointComponent>())
            {
                var axis = HumanoidBones.GetAxis(bone, component);
                Assert.Equal(1f, axis.Length(), 1e-5f);
                var mirrored = HumanoidBones.GetAxis(HumanoidBones.Mirror(bone), component);
                Assert.Equal(new System.Numerics.Vector3(axis.X, -axis.Y, -axis.Z), bone == HumanoidBones.Mirror(bone) ? new(axis.X, -axis.Y, -axis.Z) : mirrored);
            }
        }
    }

    [Fact]
    public void LimitsHoldTheTPoseAndHingesOnlyFlex()
    {
        foreach (var bone in HumanoidBones.All)
        {
            foreach (var component in Enum.GetValues<JointComponent>())
            {
                var range = HumanoidLimits.Get(bone, component);
                Assert.True(range.Min <= range.Max, $"{bone} {component}");
                Assert.True(range.Contains(0f), $"{bone} {component} excludes the T-pose");
                if (HumanoidBones.IsHinge(bone) && component != JointComponent.Flex)
                    Assert.Equal(new AngleRange(0f, 0f), range);
            }
        }
    }

    [Fact]
    public void NamedPoses()
    {
        Assert.Equal([HumanoidPoses.TPose, HumanoidPoses.Stand, HumanoidPoses.Rest], HumanoidPoses.Names);
        var stand = HumanoidPoses.Get(HumanoidPoses.Stand)!;
        Assert.Equal(-80f, stand[HumanoidBone.LeftUpperArm].Abduct);
        Assert.Equal(-80f, stand[HumanoidBone.RightUpperArm].Abduct);
        Assert.Equal(10f, stand[HumanoidBone.RightLowerArm].Flex);
        Assert.Equal(default, HumanoidPoses.Get(HumanoidPoses.TPose)![HumanoidBone.LeftUpperArm]);
        Assert.Null(HumanoidPoses.Get(HumanoidPoses.Rest));
        Assert.Null(HumanoidPoses.Get("jump"));
    }

    [Theory]
    [InlineData("LeftUpperLeg", HumanoidBone.LeftUpperLeg)]
    [InlineData("leftUpperLeg", HumanoidBone.LeftUpperLeg)]
    [InlineData("hips", HumanoidBone.Hips)]
    public void BoneNamesParse(string name, HumanoidBone expected)
    {
        Assert.True(HumanoidBones.TryParse(name, out var bone));
        Assert.Equal(expected, bone);
    }

    [Fact]
    public void UnknownBoneNamesGetSuggestions()
    {
        Assert.False(HumanoidBones.TryParse("LeftKnee", out _));
        Assert.False(HumanoidBones.TryParse("3", out _));
        Assert.Equal(HumanoidBone.LeftUpperLeg, HumanoidBones.Suggest("LeftUperLeg"));
        Assert.Null(HumanoidBones.Suggest("Tail"));
    }
}

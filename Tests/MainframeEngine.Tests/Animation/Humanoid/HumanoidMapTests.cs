using MainframeEngine.Tests.Scene;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation.Humanoid;

/// <summary>G1.12 (ADR 0281): <see cref="HumanoidMap.Detect"/> on every naming family, on structure alone, and with overrides.</summary>
[Collection(nameof(SerialResources))]
public class HumanoidMapTests
{
    private const string CharacterPath = "Assets/Models/Character/" + SkinnedCharacter.File;

    private static readonly HumanoidBone[] Required = [.. HumanoidBones.All.Where(HumanoidBones.IsRequired)];

    public static TheoryData<RigFamily> NamedFamilies => [RigFamily.Mixamo, RigFamily.Unreal, RigFamily.Blender, RigFamily.VRoid];

    [Theory]
    [MemberData(nameof(NamedFamilies))]
    public void EveryNamedBoneOfAFamilyMapsByName(RigFamily family)
    {
        var skeleton = SyntheticHumanoid.Build(family);

        var map = HumanoidMap.Detect(skeleton, null, out var report);

        Assert.True(report.IsComplete, string.Join(", ", report.MissingRequired));
        foreach (var bone in HumanoidBones.All)
        {
            var expected = SyntheticHumanoid.NameOf(family, bone);
            if (expected is null)
                continue;
            Assert.Equal(expected, map.Get(bone));
            Assert.Equal(HumanoidMapSource.Name, report.Entries.Single(e => e.Bone == bone).Source);
        }
    }

    [Fact]
    public void MixamoSpinesNumberIntoSpineChestAndUpperChest()
    {
        var map = HumanoidMap.Detect(SyntheticHumanoid.Build(RigFamily.Mixamo), null, out _);

        Assert.Equal("mixamorig:Spine", map.Get(HumanoidBone.Spine));
        Assert.Equal("mixamorig:Spine1", map.Get(HumanoidBone.Chest));
        Assert.Equal("mixamorig:Spine2", map.Get(HumanoidBone.UpperChest));
        Assert.Equal("mixamorig:LeftUpLeg", map.Get(HumanoidBone.LeftUpperLeg));
        Assert.Equal("mixamorig:LeftLeg", map.Get(HumanoidBone.LeftLowerLeg));
        Assert.Equal("mixamorig:RightHandThumb1", map.Get(HumanoidBone.RightThumbMetacarpal));
        Assert.Equal("mixamorig:RightHandIndex3", map.Get(HumanoidBone.RightIndexDistal));
    }

    [Fact]
    public void UnrealTwistIkAndRootBonesStayUnmapped()
    {
        var map = HumanoidMap.Detect(SyntheticHumanoid.Build(RigFamily.Unreal), null, out var report);

        Assert.Equal("pelvis", map.Get(HumanoidBone.Hips));
        Assert.Equal("ball_r", map.Get(HumanoidBone.RightToes));
        foreach (var name in new[] { "root", "upperarm_twist_01_l", "thigh_twist_01_r", "ik_foot_root", "ik_foot_l" })
            Assert.Contains(name, report.UnmappedRigBones);
    }

    [Fact]
    public void AnUnnamedRigMapsByStructure()
    {
        var skeleton = SyntheticHumanoid.Build(RigFamily.Unnamed);
        var reference = SyntheticHumanoid.Build(RigFamily.Mixamo);
        var referenceMap = HumanoidMap.Detect(reference, null, out _);

        var map = HumanoidMap.Detect(skeleton, null, out var report);

        Assert.True(report.IsComplete, string.Join(", ", report.MissingRequired));
        // Both skeletons list the same bones in the same order; the structural map must pick the same indices.
        foreach (var bone in Required.Concat([HumanoidBone.Chest, HumanoidBone.UpperChest, HumanoidBone.Neck,
                     HumanoidBone.LeftShoulder, HumanoidBone.RightToes]))
        {
            Assert.Equal(reference.FindBone(referenceMap.Get(bone)!), skeleton.FindBone(map.Get(bone)!));
            Assert.Equal(HumanoidMapSource.Structure, report.Entries.Single(e => e.Bone == bone).Source);
        }
    }

    [Fact]
    public void StructureFindsSidesOnARigFacingSideways()
    {
        var facingX = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f);
        var skeleton = SyntheticHumanoid.Build(RigFamily.Unnamed, facing: facingX, aPose: true);
        var reference = SyntheticHumanoid.Build(RigFamily.Mixamo);
        var referenceMap = HumanoidMap.Detect(reference, null, out _);

        var map = HumanoidMap.Detect(skeleton, null, out var report);

        Assert.True(report.IsComplete);
        foreach (var bone in new[] { HumanoidBone.LeftUpperLeg, HumanoidBone.LeftHand, HumanoidBone.RightLowerArm })
            Assert.Equal(reference.FindBone(referenceMap.Get(bone)!), skeleton.FindBone(map.Get(bone)!));
    }

    [Fact]
    public void TheTestCharacterMapsCompletely()
    {
        var skeleton = ImportCharacter();

        var map = HumanoidMap.Detect(skeleton, null, out var report);

        Assert.True(report.IsComplete);
        Assert.Equal("UpperLeg.L", map.Get(HumanoidBone.LeftUpperLeg));
        Assert.Equal("Toe.R", map.Get(HumanoidBone.RightToes));
        Assert.Equal("UpperChest", map.Get(HumanoidBone.UpperChest));
        Assert.Equal("Thumb1.L", map.Get(HumanoidBone.LeftThumbMetacarpal));
        Assert.Equal("Index2.R", map.Get(HumanoidBone.RightIndexIntermediate));
        Assert.Contains("HeadTop", report.UnmappedRigBones);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void OverridesWinAndBadOnesAreNoted()
    {
        var skeleton = ImportCharacter();
        var overrides = new Dictionary<string, string>
        {
            ["Chest"] = "UpperChest", // moves Chest; UpperChest loses its bone
            ["LeftUperLeg"] = "UpperLeg.L",
            ["Neck"] = "NoSuchBone",
        };

        var map = HumanoidMap.Detect(skeleton, overrides, out var report);

        Assert.Equal("UpperChest", map.Get(HumanoidBone.Chest));
        Assert.Null(map.Get(HumanoidBone.UpperChest));
        Assert.Equal(HumanoidMapSource.Override, report.Entries.Single(e => e.Bone == HumanoidBone.Chest).Source);
        Assert.Equal("Neck", map.Get(HumanoidBone.Neck));
        Assert.Contains(report.Notes, n => n.Contains("did you mean LeftUpperLeg", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("NoSuchBone", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyOverrideUnmapsABone()
    {
        var map = HumanoidMap.Detect(ImportCharacter(), new Dictionary<string, string> { ["LeftToes"] = "" }, out _);

        Assert.Null(map.Get(HumanoidBone.LeftToes));
        Assert.Equal("Toe.R", map.Get(HumanoidBone.RightToes));
    }

    [Fact]
    public void AMissingRequiredBoneIsReported()
    {
        var map = HumanoidMap.Detect(ImportCharacter(), new Dictionary<string, string> { ["LeftHand"] = "" }, out var report);

        Assert.False(report.IsComplete);
        Assert.Equal([HumanoidBone.LeftHand], report.MissingRequired);
        Assert.False(map.Has(HumanoidBone.LeftHand));
    }

    [Theory]
    [InlineData("mixamorig:LeftHandIndex1", "left|hand|index|1")]
    [InlineData("DEF-upper_arm.L", "upper|arm|l")]
    [InlineData("J_Bip_C_UpperChest", "c|upper|chest")]
    [InlineData("thigh_twist_01_l", "thigh|twist|01|l")]
    public void NamesSplitIntoTokens(string name, string expected) =>
        Assert.Equal(expected, string.Join('|', HumanoidDetector.Tokenize(name)));

    internal static Skeleton3D ImportCharacter() =>
        ModelImporter.ImportNodes(CharacterPath, ModelImportSettings.Default).GetNode<Skeleton3D>("Armature");
}

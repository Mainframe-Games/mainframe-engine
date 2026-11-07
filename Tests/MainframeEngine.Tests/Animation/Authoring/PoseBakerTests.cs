using System.Numerics;
using MainframeEngine.Tests.Animation.Humanoid;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.13: pose scripts bake into sparse cubic keys on the test character; pins hold, arcs rise, notes explain.</summary>
[Collection(nameof(SerialResources))]
public class PoseBakerTests
{
    private static readonly string[] LegChains =
        ["UpperLeg.L", "LowerLeg.L", "Foot.L", "UpperLeg.R", "LowerLeg.R", "Foot.R"];

    internal static (Node Root, Skeleton3D Skeleton, HumanoidRig Rig) Character()
    {
        var root = ModelImporter.ImportNodes("Assets/Models/Character/" + TestAssets.SkinnedCharacter.File, ModelImportSettings.Default);
        var skeleton = root.GetNode<Skeleton3D>("Armature");
        var map = HumanoidMap.Detect(skeleton, null, out _);
        return (root, skeleton, new HumanoidRig(skeleton, map));
    }

    private static PoseBakeResult Bake(string json, out HumanoidRig rig, float gravity = 9.8f)
    {
        var (root, skeleton, characterRig) = Character();
        rig = characterRig;
        return PoseBaker.Bake(PoseScriptParser.Parse(json), rig, root.GetPathTo(skeleton), gravity);
    }

    private static float[] ScriptTimes => [0f, 0.12f, 0.24f, 0.55f, 0.82f, 1.1f];

    [Fact]
    public void KeysAreAtTheScriptsTimesExceptRefinementsOnPinnedChains()
    {
        var result = Bake(PoseScriptParserTests.Jump, out _);
        var clip = result.Animation;

        Assert.Equal("jump", clip.ResourceName);
        Assert.Equal(1.1f, clip.Length);
        Assert.NotEmpty(clip.Tracks);
        foreach (var track in clip.Tracks.OfType<InterpolatedTrack>())
        {
            Assert.Equal(AnimationInterpolation.Cubic, track.Interpolation);
            var bone = track switch { Rotation3DTrack r => r.Bone, Position3DTrack p => p.Bone, _ => "" };
            if (!LegChains.Contains(bone))
                Assert.Equal(ScriptTimes, track.Times);
            else
                Assert.Superset(ScriptTimes.ToHashSet(), track.Times.ToHashSet());
            Assert.Equal(track.Times.Length, track.Transitions.Length);
        }

        Assert.Equal(AnimationHash.Compute(clip), clip.BakedHash);
        Assert.Equal("Hips", result.HipsBone);
    }

    [Fact]
    public void EasesGoOnTheSegmentAfterTheirKey()
    {
        var clip = Bake(PoseScriptParserTests.Jump, out _).Animation;
        var spine = clip.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");

        Assert.Equal([1f, 0.5f, 1f, 1f, 1f, 1f], spine.Transitions);
    }

    [Fact]
    public void BonesThatNeverMoveGetNoTrack()
    {
        var clip = Bake(PoseScriptParserTests.Jump, out _).Animation;
        var bones = clip.Tracks.OfType<Rotation3DTrack>().Select(t => t.Bone).ToHashSet();

        Assert.Contains("Spine", bones);
        Assert.Contains("UpperArm.L", bones);
        Assert.DoesNotContain("Head", bones);
        Assert.DoesNotContain("Index1.L", bones);
        Assert.DoesNotContain("HeadTop", bones);
    }

    [Fact]
    public void KeysHoldValuesAndMirror()
    {
        var result = Bake(PoseScriptParserTests.Jump, out var rig);
        var path = new NodePath("Armature");

        HumanoidPose At(float t)
        {
            rig.Skeleton.ResetBonePoses();
            ClipSampler.Apply(result.Animation, rig.Skeleton, path, t);
            return rig.ReadPose();
        }

        // 0.24 keeps 0.12's spine flex; 0.55 mirrors the left leg onto the right; the stand pose's arms come back at 1.1.
        Assert.Equal(8f, At(0.24f)[HumanoidBone.Spine].Flex, 0.05f);
        Assert.Equal(70f, At(0.24f)[HumanoidBone.LeftUpperArm].Flex, 0.05f);
        var tuck = At(0.55f);
        Assert.True(tuck[HumanoidBone.RightUpperLeg].ApproximatelyEquals(tuck[HumanoidBone.LeftUpperLeg], 0.05f));
        Assert.Equal(72f, tuck[HumanoidBone.LeftUpperLeg].Flex, 0.05f);
        Assert.Equal(-80f, At(1.1f)[HumanoidBone.RightUpperArm].Abduct, 0.05f);
        Assert.Equal(-0.22f, At(0.12f).Hips.Offset.Y, 0.002f);
    }

    [Fact]
    public void PinnedFeetStayWithinACentimetreAtSixtyHertz()
    {
        var result = Bake(PoseScriptParserTests.Jump, out var rig);
        var skeleton = rig.Skeleton;
        var path = new NodePath("Armature");
        var feet = new[] { skeleton.FindBone("Foot.L"), skeleton.FindBone("Foot.R") };

        Vector3[] FeetAt(double t)
        {
            skeleton.ResetBonePoses();
            ClipSampler.Apply(result.Animation, skeleton, path, t);
            return [.. feet.Select(f => skeleton.GetBoneGlobalPose(f).Origin)];
        }

        foreach (var (from, to) in new[] { (0.0, 0.24), (0.82, 1.1) })
        {
            var pinned = FeetAt(from);
            for (var t = from; t < to - 1e-6; t += 1.0 / 60)
            {
                var now = FeetAt(t);
                for (var i = 0; i < 2; i++)
                    Assert.True(Vector3.Distance(pinned[i], now[i]) <= PoseBaker.DefaultTolerance + 1e-4f,
                        $"foot {i} slid {Vector3.Distance(pinned[i], now[i]):0.0000} m at {t:0.000} s; refinements {result.RefinementKeys}; " +
                        string.Join(" / ", result.Animation.Tracks.OfType<Rotation3DTrack>().Where(r => LegChains.Contains(r.Bone)).Select(r => r.Bone + ":" + string.Join(",", r.Times.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))))) +
                        "; notes " + string.Join(" | ", result.Notes));
            }
        }

        Assert.True(result.RefinementKeys >= 0);
    }

    [Fact]
    public void InPlaceIgnoresArcsWithANote()
    {
        var result = Bake(PoseScriptParserTests.Jump, out _);

        Assert.Contains(result.Notes, n => n.Contains("inPlace", StringComparison.Ordinal));
        var hips = result.Animation.Tracks.OfType<Position3DTrack>().Single();
        Assert.Equal(ScriptTimes, hips.Times);
    }

    private const string Hop = """
        { "model": "m.glb", "library": "l.mfres", "name": "hop", "length": 1.0, "root": "motion",
          "keys": [ { "t": 0, "pose": "stand" }, { "t": 0.3, "hips": { "z": 0.1 } },
                    { "t": 0.8, "hips": { "z": 0.6 } }, { "t": 1.0, "hips": { "y": 0, "z": 0.7 } } ],
          "arcs": [ ARC ] }
        """;

    [Theory]
    [InlineData("""{ "from": 0.3, "to": 0.8 }""", 0.30625f)] // g·T²/8 with T = 0.5 s
    [InlineData("""{ "from": 0.3, "to": 0.8, "height": 0.5 }""", 0.5f)]
    public void MotionArcsRiseToTheirApex(string arc, float apex)
    {
        var result = Bake(Hop.Replace("ARC", arc, StringComparison.Ordinal), out var rig);
        var path = new NodePath("Armature");

        var highest = 0f;
        for (var t = 0.3; t <= 0.8; t += 1.0 / 120)
        {
            rig.Skeleton.ResetBonePoses();
            ClipSampler.Apply(result.Animation, rig.Skeleton, path, t);
            highest = MathF.Max(highest, rig.ReadPose().Hips.Offset.Y);
        }

        Assert.Equal(apex, highest, 0.012f);
        Assert.True(result.RefinementKeys > 0, "an arc between two keys needs refinement keys on the hips");
        Assert.All(result.Animation.Tracks.Where(t => t is Rotation3DTrack), track => Assert.Equal(4, track.Times.Length));
        if (arc.Contains("height", StringComparison.Ordinal))
            Assert.Contains(result.Notes, n => n.Contains("gravity", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutOfReachTargetIsNoted()
    {
        var result = Bake("""
            { "model": "m.glb", "library": "l.mfres", "name": "reach", "length": 1,
              "keys": [ { "t": 0, "pose": "stand" }, { "t": 1, "ik": { "rightHand": { "pos": [-2, 1.5, 0] } } } ] }
            """, out _);

        Assert.Contains(result.Notes, n => n.Contains("RightHand cannot reach", StringComparison.Ordinal));
    }

    [Fact]
    public void APinTheHipsRiseOutOfReachOfIsNoted()
    {
        var result = Bake("""
            { "model": "m.glb", "library": "l.mfres", "name": "rise", "length": 1,
              "keys": [ { "t": 0, "pose": "stand", "ik": { "leftFoot": "pin" } }, { "t": 1, "hips": { "y": 0.15 } } ] }
            """, out _);

        Assert.Contains(result.Notes, n => n.Contains("LeftFoot", StringComparison.Ordinal) &&
                                           (n.Contains("out of reach", StringComparison.Ordinal) || n.Contains("cannot reach", StringComparison.Ordinal)));
    }

    [Fact]
    public void BakingLeavesTheSkeletonsPoseAlone()
    {
        var (root, skeleton, rig) = Character();
        var bone = skeleton.FindBone("Spine");
        var before = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        skeleton.SetBonePoseRotation(bone, before);

        PoseBaker.Bake(PoseScriptParser.Parse(PoseScriptParserTests.Jump), rig, root.GetPathTo(skeleton));

        Assert.Equal(before, skeleton.GetBonePoseRotation(bone));
    }
}

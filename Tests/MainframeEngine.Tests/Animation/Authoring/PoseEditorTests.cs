using System.Numerics;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>
/// G1.14: edit scripts change a clip on top of its keys — only what they name, key times, counts and eases otherwise kept,
/// hand-placed keys included; arcs rescale, pins hold, and a hand-edited clip stays marked as edited.
/// </summary>
[Collection(nameof(SerialResources))]
public class PoseEditorTests
{
    private static readonly NodePath Armature = new("Armature");

    private static (MainframeEngine.Animation Clip, HumanoidRig Rig) Baked(string json = PoseScriptParserTests.Jump)
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        return (PoseBaker.Bake(PoseScriptParser.Parse(json), rig, root.GetPathTo(skeleton)).Animation, rig);
    }

    private static string Script(string edits, string name = "jump") => $$"""
        { "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres",
          "base": "jump", "name": "{{name}}", "edits": [ {{edits}} ] }
        """;

    private static PoseEditResult Edit(MainframeEngine.Animation clip, HumanoidRig rig, string edits, string name = "jump") =>
        PoseEditor.Apply(PoseScriptParser.ParseEdit(Script(edits, name)), clip, rig, Armature);

    private static HumanoidPose PoseAt(MainframeEngine.Animation clip, HumanoidRig rig, float time) =>
        ClipDescriber.Describe(clip, rig, Armature, [time]).Times[0].Pose;

    private static Dictionary<string, (float[] Times, float[] Transitions)> Shape(MainframeEngine.Animation clip) =>
        clip.Tracks.OfType<InterpolatedTrack>().ToDictionary(
            t => t.GetType().Name + ":" + ClipDescriber.BoneOf(t), t => (t.Times.ToArray(), t.Transitions.ToArray()));

    [Fact]
    public void ARelativeEditChangesOnlyTheNamedKeys()
    {
        var (clip, rig) = Baked();
        var before = Shape(clip);
        var spineBefore = PoseAt(clip, rig, 0.12f)[HumanoidBone.Spine];

        var result = Edit(clip, rig, """{ "t": 0.12, "joints": { "LeftUpperArm": { "flex": "-=10" } }, "mirror": true }""");
        var edited = result.Animation;

        Assert.Equal(before.Keys, Shape(edited).Keys);
        foreach (var (track, shape) in Shape(edited))
        {
            Assert.Equal(before[track].Times, shape.Times);
            Assert.Equal(before[track].Transitions, shape.Transitions);
        }

        var crouch = PoseAt(edited, rig, 0.12f);
        Assert.Equal(-65f, crouch[HumanoidBone.LeftUpperArm].Flex, 0.5f);
        Assert.Equal(-65f, crouch[HumanoidBone.RightUpperArm].Flex, 0.5f);
        Assert.True(crouch[HumanoidBone.Spine].ApproximatelyEquals(spineBefore, 0.01f));
        Assert.Equal(70f, PoseAt(edited, rig, 0.24f)[HumanoidBone.LeftUpperArm].Flex, 0.5f);
        Assert.Equal(2, result.ChangedKeys);
        Assert.Equal(0, result.AddedKeys);

        // Every other track's values are untouched.
        foreach (var (a, b) in clip.Tracks.OfType<Rotation3DTrack>().Zip(edited.Tracks.OfType<Rotation3DTrack>()))
            if (a.Bone is not ("UpperArm.L" or "UpperArm.R"))
                Assert.Equal(a.Values, b.Values);
    }

    [Fact]
    public void TheBaseClipIsNotChanged()
    {
        var (clip, rig) = Baked();
        var hash = AnimationHash.Compute(clip);

        Edit(clip, rig, """{ "range": [0, 1.1], "joints": { "Spine": { "flex": "*2" } } }""", "jump_big");

        Assert.Equal(hash, AnimationHash.Compute(clip));
    }

    [Fact]
    public void ScaledAndAbsoluteValuesOverARange()
    {
        var (clip, rig) = Baked();

        var edited = Edit(clip, rig, """{ "range": [0.1, 0.3], "joints": { "Spine": { "flex": "*2", "twist": 5 } } }""").Animation;

        Assert.Equal(16f, PoseAt(edited, rig, 0.12f)[HumanoidBone.Spine].Flex, 0.5f);
        Assert.Equal(5f, PoseAt(edited, rig, 0.12f)[HumanoidBone.Spine].Twist, 0.5f);
        Assert.Equal(5f, PoseAt(edited, rig, 0.24f)[HumanoidBone.Spine].Twist, 0.5f);
        Assert.Equal(0f, PoseAt(edited, rig, 0f)[HumanoidBone.Spine].Twist, 0.5f);
    }

    [Fact]
    public void AnInsertAddsOneKeyToEachTouchedTrack()
    {
        var (clip, rig) = Baked();
        var before = Shape(clip);

        var result = Edit(clip, rig, """{ "t": 0.4, "insert": true, "joints": { "Spine": { "flex": -10 } } }""");

        var after = Shape(result.Animation);
        Assert.Equal(before["Rotation3DTrack:Spine"].Times.Length + 1, after["Rotation3DTrack:Spine"].Times.Length);
        Assert.Contains(0.4f, after["Rotation3DTrack:Spine"].Times);
        foreach (var (track, shape) in after.Where(t => t.Key != "Rotation3DTrack:Spine"))
            Assert.Equal(before[track].Times, shape.Times);
        Assert.Equal(-10f, PoseAt(result.Animation, rig, 0.4f)[HumanoidBone.Spine].Flex, 0.5f);
        Assert.Equal(1, result.AddedKeys);
    }

    [Fact]
    public void ABoneWithoutATrackGetsOneKeyedAroundTheEdit()
    {
        var (clip, rig) = Baked();
        Assert.DoesNotContain(clip.Tracks.OfType<Rotation3DTrack>(), t => t.Bone == "Head");

        var result = Edit(clip, rig, """{ "t": 0.55, "joints": { "Head": { "flex": 20 } } }""");

        var head = result.Animation.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Head");
        Assert.Equal([0.24f, 0.55f, 0.82f], head.Times);
        Assert.Equal(20f, PoseAt(result.Animation, rig, 0.55f)[HumanoidBone.Head].Flex, 0.5f);
        Assert.Equal(0f, PoseAt(result.Animation, rig, 0.12f)[HumanoidBone.Head].Flex, 0.05f);
        Assert.Equal(0f, PoseAt(result.Animation, rig, 1f)[HumanoidBone.Head].Flex, 0.05f);
        Assert.Contains(result.Notes, n => n.Contains("Head had no rotation track", StringComparison.Ordinal));
    }

    [Fact]
    public void HandPlacedKeysAreKeptAndEditable()
    {
        var (clip, rig) = Baked();
        // A person adds a key to the spine at 0.4 s in the editor.
        var spine = clip.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");
        rig.Skeleton.ResetBonePoses();
        TrackKeys.SetRotation(spine, 0.4f, rig.ToBoneRotation(HumanoidBone.Spine, new JointAngles(Flex: 25f)));
        clip.NotifyChanged();
        var times = spine.Times.ToArray();

        var edited = Edit(clip, rig, """{ "t": 0.4, "joints": { "Spine": { "flex": "+=5" } } }""").Animation;

        Assert.Equal(times, edited.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine").Times);
        Assert.Equal(30f, PoseAt(edited, rig, 0.4f)[HumanoidBone.Spine].Flex, 0.5f);
    }

    [Fact]
    public void AnEaseEditSetsTheSegmentAfterTheKeyOnEveryTrackKeyedThere()
    {
        var (clip, rig) = Baked();

        var edited = Edit(clip, rig, """{ "t": 0.24, "ease": "in" }""").Animation;

        foreach (var track in edited.Tracks.OfType<InterpolatedTrack>())
            if (TrackKeys.Find(track.Times, 0.24) is var at and >= 0)
                Assert.Equal(2f, track.Transitions[at]);
        Assert.Equal(0.5f, edited.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine").Transitions[1]);
    }

    [Theory]
    [InlineData("*1.3")]
    [InlineData("0.5")]
    public void AnArcEditReachesItsNewApex(string height)
    {
        var (clip, rig) = Baked(PoseScriptParserTests.Jump.Replace("\"inPlace\"", "\"motion\"", StringComparison.Ordinal));
        var hipsBefore = Shape(clip)["Position3DTrack:Hips"];
        var value = height.StartsWith('*') ? $"\"{height}\"" : height;

        var result = Edit(clip, rig, $$"""{ "range": [0.24, 0.82], "arc": { "height": {{value}} } }""");

        var apexBefore = Apex(clip, rig);
        var expected = height.StartsWith('*') ? apexBefore * 1.3f : 0.5f;
        Assert.Equal(expected, Apex(result.Animation, rig), 0.01f);
        Assert.Equal(hipsBefore.Times, Shape(result.Animation)["Position3DTrack:Hips"].Times);
    }

    private static float Apex(MainframeEngine.Animation clip, HumanoidRig rig)
    {
        float Height(float t)
        {
            rig.Skeleton.ResetBonePoses();
            ClipSampler.Apply(clip, rig.Skeleton, Armature, t);
            return rig.ToCharacterSpace(rig.Skeleton.GetBoneGlobalPose(rig.GetRigBone(HumanoidBone.Hips)).Origin).Y;
        }

        var start = Height(0.24f);
        var apex = 0f;
        for (var i = 0; i <= 120; i++)
            apex = MathF.Max(apex, Height(0.24f + 0.58f * i / 120f) - start);
        return apex;
    }

    [Fact]
    public void PinnedFeetHoldWhenTheHipsGoLower()
    {
        var (clip, rig) = Baked();
        var skeleton = rig.Skeleton;
        var feet = new[] { skeleton.FindBone("Foot.L"), skeleton.FindBone("Foot.R") };

        Vector3[] FeetAt(MainframeEngine.Animation animation, double t)
        {
            skeleton.ResetBonePoses();
            ClipSampler.Apply(animation, skeleton, Armature, t);
            return [.. feet.Select(f => skeleton.GetBoneGlobalPose(f).Origin)];
        }

        var planted = FeetAt(clip, 0);
        var result = Edit(clip, rig, """{ "t": 0.12, "hips": { "y": "-=0.06" }, "ik": { "leftFoot": "pin", "rightFoot": "pin" } }""");

        Assert.Equal(-0.28f, PoseAt(result.Animation, rig, 0.12f).Hips.Offset.Y, 0.005f);
        for (var t = 0.0; t <= 0.24 + 1e-6; t += 1.0 / 60)
        {
            var now = FeetAt(result.Animation, t);
            for (var i = 0; i < 2; i++)
                Assert.True(Vector3.Distance(planted[i], now[i]) * rig.MetresPerUnit <= PoseBaker.DefaultTolerance + 1e-4f,
                    $"foot {i} slid {Vector3.Distance(planted[i], now[i]) * rig.MetresPerUnit:0.0000} m at {t:0.000} s; notes {string.Join(" | ", result.Notes)}");
        }
    }

    [Fact]
    public void AnIkTargetMovesTheHand()
    {
        var (clip, rig) = Baked();
        var target = new Vector3(0.35f, 1.2f, 0.25f);

        var result = Edit(clip, rig, """{ "t": 0.55, "ik": { "leftHand": { "pos": [0.35, 1.2, 0.25] } } }""");

        var hand = ClipDescriber.Describe(result.Animation, rig, Armature, [0.55f]).Times[0].Effectors[IkEffector.LeftHand];
        Assert.True(Vector3.Distance(target, hand.Position) < 0.01f, $"hand at {hand.Position}");
    }

    [Fact]
    public void EditingAnUntouchedBakeKeepsItUntouched()
    {
        var (clip, rig) = Baked();

        var edited = Edit(clip, rig, """{ "t": 0.12, "joints": { "Spine": { "flex": 12 } } }""").Animation;

        Assert.True(PoseLibrary.IsUntouchedBake(edited));
        Assert.NotEqual(clip.BakedHash, edited.BakedHash);
    }

    [Fact]
    public void EditingAHandEditedClipKeepsItMarkedEdited()
    {
        var (clip, rig) = Baked();
        var spine = clip.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");
        spine.Values[1] = Quaternion.Normalize(spine.Values[1] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f));
        var staleHash = clip.BakedHash;

        var result = Edit(clip, rig, """{ "t": 0.12, "joints": { "LeftUpperArm": { "flex": "+=5" } } }""");

        Assert.Equal(staleHash, result.Animation.BakedHash);
        Assert.False(PoseLibrary.IsUntouchedBake(result.Animation));
        Assert.Equal(spine.Values[1], result.Animation.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine").Values[1]);
        Assert.Contains(result.Notes, n => n.Contains("hand edits", StringComparison.Ordinal));
    }

    [Fact]
    public void AHandMadeClipStaysUnbaked()
    {
        var (clip, rig) = Baked();
        clip.BakedHash = "";

        Assert.Equal("", Edit(clip, rig, """{ "t": 0.12, "joints": { "Spine": { "flex": 12 } } }""").Animation.BakedHash);
    }

    [Theory]
    [InlineData("""{ "t": 0.3, "joints": { "Spine": { "flex": 1 } } }""", "edits[0].t", "no key at 0.3 s")]
    [InlineData("""{ "t": 2, "insert": true, "joints": { "Spine": { "flex": 1 } } }""", "edits[0].t", "outside the clip")]
    [InlineData("""{ "range": [0.3, 0.5], "joints": { "Spine": { "flex": 1 } } }""", "edits[0].range", "no keys in")]
    [InlineData("""{ "range": [0, 0.1], "arc": { "height": "*2" } }""", "edits[0].range", "no hips keys inside")]
    public void ApplyErrorsNameTheirEdit(string edit, string path, string message)
    {
        var (clip, rig) = Baked();

        var error = Assert.Throws<PoseScriptException>(() => Edit(clip, rig, edit));

        Assert.Contains(error.Errors, e => e.Path == path && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnmappedBoneIsAnError()
    {
        var (clip, rig) = Baked();
        var unmapped = HumanoidBones.All.First(b => b != HumanoidBone.Hips && rig.GetRigBone(b) < 0 && !HumanoidBones.IsHinge(b));

        var error = Assert.Throws<PoseScriptException>(() =>
            Edit(clip, rig, $$"""{ "t": 0.12, "joints": { "{{unmapped}}": { "flex": 1 } } }"""));

        Assert.Contains(error.Errors, e => e.Path == $"edits[0].joints.{unmapped}" && e.Message.Contains("not mapped", StringComparison.Ordinal));
    }
}

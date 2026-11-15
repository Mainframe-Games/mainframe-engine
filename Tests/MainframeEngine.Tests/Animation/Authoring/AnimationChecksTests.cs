using System.Numerics;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>
/// G1.15: checks measure a clip on the test character — a clean squat passes, the G1.13 test jump's real problems are found,
/// and a clip built to break each check reports it with its severity, range and subject; tolerances move the thresholds.
/// </summary>
[Collection(nameof(SerialResources))]
public class AnimationChecksTests
{
    private static readonly NodePath Armature = new("Armature");

    /// <summary>A squat that should pass every check: eased, feet pinned throughout, back to the start pose.</summary>
    internal const string Squat = """
        {
          "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "name": "squat",
          "length": 1.6, "loop": "Linear", "root": "inPlace",
          "keys": [
            { "t": 0.0, "pose": "stand", "ease": "inOut", "ik": { "leftFoot": "pin", "rightFoot": "pin" } },
            { "t": 0.8, "ease": "inOut", "hips": { "y": -0.12, "pitch": 10 },
              "joints": { "Spine": { "flex": 6 }, "LeftUpperArm": { "flex": 60 }, "RightUpperArm": { "flex": 60 } } },
            { "t": 1.6, "pose": "stand" }
          ]
        }
        """;

    private static string Script(string keys, string extra = "", string root = "inPlace", float length = 1f) => $$"""
        { "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "name": "test",
          "length": {{length.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "root": "{{root}}", {{extra}}
          "keys": [ {{keys}} ] }
        """;

    private static (AnimationCheckReport Report, PoseScript Script) Check(string json, Func<AnimationCheckOptions, AnimationCheckOptions>? options = null)
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        var script = PoseScriptParser.Parse(json);
        var clip = PoseBaker.Bake(script, rig, root.GetPathTo(skeleton)).Animation;
        var settings = AnimationCheckOptions.ForScript(script);
        return (AnimationChecks.Run(clip, rig, root.GetPathTo(skeleton), options?.Invoke(settings) ?? settings), script);
    }

    private static AnimationCheckReport CheckClip(Func<HumanoidRig, MainframeEngine.Animation> build, AnimationCheckOptions? options = null)
    {
        var (_, _, rig) = PoseBakerTests.Character();
        return AnimationChecks.Run(build(rig), rig, Armature, options);
    }

    [Fact]
    public void ACleanSquatPassesEveryCheck()
    {
        var (report, _) = Check(Squat);

        Assert.True(report.Findings.Count == 0, report.ToText());
        Assert.Equal(["physics: only root \"motion\" clips carry their jump."], report.Skipped);
        Assert.StartsWith("Checks: 0 error(s), 0 warning(s)", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestJumpsProblemsAreFound()
    {
        var (report, _) = Check(PoseScriptParserTests.Jump);

        // Toes pinned pointing down at the landing, ankles past their range in the deep crouch, a fast arm swing, the
        // hips dropping at full speed from straight knees: all real problems of that script, not of the baker.
        Assert.Contains(report.Findings, f => f is { Check: "ground", Subject: "LeftToes", Severity: CheckSeverity.Error } && f.From >= 0.75f);
        Assert.Contains(report.Findings, f => f is { Check: "range", Subject: "LeftFoot.flex" } && f.To <= 0.2f);
        Assert.Contains(report.Findings, f => f is { Check: "spike", Subject: "LeftUpperArm" });
        Assert.Contains(report.Findings, f => f is { Check: "end", Severity: CheckSeverity.Warning });
        Assert.DoesNotContain(report.Findings, f => f.Check is "flip" or "footSlide" or "loop");
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void FeetUnderTheGroundAreAnError()
    {
        var (report, _) = Check(Script("""{ "t": 0, "pose": "stand" }, { "t": 1, "hips": { "y": -0.2 } }"""));

        var sunk = report.Findings.Where(f => f.Check == "ground").ToList();
        Assert.Contains(sunk, f => f.Subject == "LeftToes" && f.Severity == CheckSeverity.Error && f.To == 1f);
        Assert.Contains(sunk, f => f.Subject == "RightFoot");
        Assert.Equal(0.2f, sunk.Max(f => f.Value), 0.03f);
        Assert.Contains("below the ground", sunk[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFootSlidingOnTheGroundAndHipsDriftAreWarnings()
    {
        var json = Script("""{ "t": 0, "pose": "stand" }, { "t": 1, "hips": { "x": 0.1 } }""");

        var (report, _) = Check(json);

        var slide = report.Findings.Single(f => f is { Check: "footSlide", Subject: "leftFoot" });
        Assert.Equal(CheckSeverity.Warning, slide.Severity);
        Assert.Equal(0.1f, slide.Value, 0.01f);
        Assert.Contains(report.Findings, f => f is { Check: "root", Severity: CheckSeverity.Warning });
        Assert.False(report.HasErrors);

        var (tolerant, _) = Check(json, o => o with { Tolerances = o.Tolerances.With("footSlide=0.2, rootDrift=0.2") });
        Assert.DoesNotContain(tolerant.Findings, f => f.Check is "footSlide" or "root");
    }

    [Fact]
    public void AJointPastItsRangeIsAnError()
    {
        var json = Script("""{ "t": 0, "pose": "stand", "ease": "inOut" }, { "t": 1, "joints": { "LeftUpperLeg": { "flex": 160 } } }""");

        var (report, _) = Check(json);

        var range = report.Findings.Single(f => f.Check == "range");
        Assert.Equal("LeftUpperLeg.flex", range.Subject);
        Assert.Equal(30f, range.Value, 0.5f);
        Assert.Equal(1f, range.To);
        Assert.Contains("-30°…130°", range.Message, StringComparison.Ordinal);

        var (tolerant, _) = Check(json, o => o with { Tolerances = o.Tolerances.With("range", 40f) });
        Assert.DoesNotContain(tolerant.Findings, f => f.Check == "range");
    }

    [Fact]
    public void AHingeTurningSidewaysIsAnError()
    {
        var report = CheckClip(rig =>
        {
            // Twist the shin 30° about its own bone (towards the foot): a hinge cannot do that.
            var skeleton = rig.Skeleton;
            var shin = skeleton.FindBone("LowerLeg.L");
            var along = Vector3.Normalize(skeleton.GetBoneRest(skeleton.FindBone("Foot.L")).Origin);
            var rest = skeleton.GetBoneRest(shin).Basis.GetRotation();
            return new MainframeEngine.Animation
            {
                ResourceName = "twisted",
                Length = 1f,
                Tracks =
                {
                    new Rotation3DTrack
                    {
                        Path = Armature, Bone = "LowerLeg.L", Times = [0f, 1f],
                        Values = [rest, Quaternion.Normalize(rest * Quaternion.CreateFromAxisAngle(along, MathF.PI / 6f))],
                    },
                },
            };
        });

        var hinge = report.Findings.Single(f => f.Check == "range");
        Assert.Equal("LeftLowerLeg.twist", hinge.Subject);
        Assert.Contains("a hinge only flexes", hinge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysInOppositeHemispheresAreAFlip()
    {
        var report = CheckClip(rig =>
        {
            var a = rig.ToBoneRotation(HumanoidBone.Spine, default);
            var b = rig.ToBoneRotation(HumanoidBone.Spine, new JointAngles(Flex: 20f));
            return new MainframeEngine.Animation
            {
                ResourceName = "flip",
                Length = 1f,
                Tracks = { new Rotation3DTrack { Path = Armature, Bone = "Spine", Times = [0f, 1f], Values = [a, -b] } },
            };
        });

        var flip = report.Findings.Single(f => f.Check == "flip");
        Assert.Equal("Spine", flip.Subject);
        Assert.Equal((0f, 1f), (flip.From, flip.To));
        Assert.Equal(CheckSeverity.Error, flip.Severity);
    }

    [Fact]
    public void AFastTurnIsASpike()
    {
        var json = Script("""{ "t": 0, "pose": "stand" }, { "t": 0.03, "joints": { "Spine": { "flex": 60 } } }, { "t": 1 }""");

        var (report, _) = Check(json);

        var spike = report.Findings.First(f => f is { Check: "spike", Subject: "Spine" });
        Assert.True(spike.Value > 1500f);
        Assert.True(spike.From < 0.04f);
        var (tolerant, _) = Check(json, o => o with { Tolerances = o.Tolerances.With("angularSpeed", 5000f) });
        Assert.DoesNotContain(tolerant.Findings, f => f.Check == "spike");
    }

    [Fact]
    public void ALoopThatDoesNotCloseIsAnError()
    {
        var (open, _) = Check(Squat.Replace("""{ "t": 1.6, "pose": "stand" }""", """{ "t": 1.6 }""", StringComparison.Ordinal));
        Assert.Contains(open.Findings, f => f is { Check: "loop", Severity: CheckSeverity.Error } && f.Message.StartsWith("pose:", StringComparison.Ordinal));

        // Ping-pong turns back at both ends, so the motion has to stop there.
        var (pingPong, _) = Check(Script(
            """{ "t": 0, "pose": "stand" }, { "t": 0.1, "joints": { "Spine": { "flex": 55 } } }, { "t": 0.2, "joints": { "Spine": { "flex": 0 } } }""",
            "\"loop\": \"PingPong\",", length: 0.2f));
        Assert.Contains(pingPong.Findings, f => f.Check == "loop" && f.Message.StartsWith("start:", StringComparison.Ordinal));
        Assert.Contains(pingPong.Findings, f => f.Check == "loop" && f.Message.StartsWith("end:", StringComparison.Ordinal));
        Assert.DoesNotContain(Check(Squat.Replace("\"Linear\"", "\"PingPong\"", StringComparison.Ordinal)).Report.Findings, f => f.Check == "loop");
    }

    [Fact]
    public void AStartAwayFromItsDeclaredPoseIsAWarning()
    {
        var (report, _) = Check(Squat, o => o with { StartPose = HumanoidPoses.TPose });

        var start = report.Findings.Single(f => f.Check == "start");
        Assert.Equal(CheckSeverity.Warning, start.Severity);
        Assert.Contains("Arm", start.Subject, StringComparison.Ordinal);
        Assert.Equal(80f, start.Value, 1f);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ARootThatJumpsIsAnErrorAndFastYawAWarning()
    {
        var (jump, _) = Check(Script(
            """{ "t": 0, "pose": "stand" }, { "t": 0.5 }, { "t": 0.52, "hips": { "z": 1 } }, { "t": 1 }""", root: "motion"));
        Assert.Contains(jump.Findings, f => f is { Check: "root", Severity: CheckSeverity.Error } && f.Message.Contains("jumps", StringComparison.Ordinal));

        var (spin, _) = Check(Script("""{ "t": 0, "pose": "stand" }, { "t": 0.25, "hips": { "yaw": 90 } }, { "t": 1 }""", root: "motion"));
        Assert.Contains(spin.Findings, f => f is { Check: "root", Severity: CheckSeverity.Warning } && f.Message.Contains("turns", StringComparison.Ordinal));
    }

    [Fact]
    public void AFlightThatDisagreesWithGravityIsAWarning()
    {
        var jump = PoseScriptParserTests.Jump.Replace("\"inPlace\"", "\"motion\"", StringComparison.Ordinal);

        var (high, _) = Check(jump);
        var physics = high.Findings.Single(f => f.Check == "physics");
        Assert.Equal(CheckSeverity.Warning, physics.Severity);
        Assert.Equal(0.6f, physics.Value, 0.03f);

        var (gravity, _) = Check(jump.Replace(", \"height\": 0.6", "", StringComparison.Ordinal));
        Assert.DoesNotContain(gravity.Findings, f => f.Check == "physics");
    }

    [Fact]
    public void WithoutOptionsTheDeclaredChecksAreSkipped()
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        var clip = PoseBaker.Bake(PoseScriptParser.Parse(Squat), rig, root.GetPathTo(skeleton)).Animation;

        var report = AnimationChecks.Run(clip, rig, Armature);

        Assert.Equal(4, report.Skipped.Count);
        Assert.Contains("skipped: root: no root mode given.", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksLeaveTheSkeletonsPoseAlone()
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        var clip = PoseBaker.Bake(PoseScriptParser.Parse(Squat), rig, root.GetPathTo(skeleton)).Animation;
        var spine = skeleton.FindBone("Spine");
        var posed = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        skeleton.SetBonePoseRotation(spine, posed);

        AnimationChecks.Run(clip, rig, Armature);

        Assert.Equal(posed, skeleton.GetBonePoseRotation(spine));
    }

    [Fact]
    public void TolerancesAreSetByName()
    {
        var tolerances = new AnimationCheckTolerances().With("footSlide=0.03, range=10");

        Assert.Equal(0.03f, tolerances.FootSlide);
        Assert.Equal(10f, tolerances.Range);
        Assert.Equal(0.01f, tolerances.Ground);
        Assert.Contains("unknown tolerance 'slide'", Assert.Throws<ArgumentException>(() => tolerances.With("slide", 1f)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => tolerances.With("ground", -1f));
        Assert.Throws<ArgumentException>(() => tolerances.With("ground"));
        Assert.Equal(11, AnimationCheckTolerances.Names.Count);
    }
}

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.13: pose scripts parse, and every problem is reported with its JSON path.</summary>
public class PoseScriptParserTests
{
    internal const string Jump = """
        {
          "model": "Assets/Characters/hero.glb",
          "library": "Assets/Characters/hero_anims.mfres",
          "name": "jump",
          "length": 1.1,
          "loop": "None",
          "root": "inPlace",
          "keys": [
            { "t": 0.0, "pose": "stand", "ik": { "leftFoot": "pin", "rightFoot": "pin" } },
            { "t": 0.12, "ease": "out", "hips": { "y": -0.22, "pitch": 20 },
              "joints": { "Spine": { "flex": 8 }, "LeftUpperArm": { "flex": -55 }, "RightUpperArm": { "flex": -55 } } },
            { "t": 0.24, "hips": { "y": -0.03, "pitch": 8 },
              "joints": { "LeftUpperArm": { "flex": 70 }, "RightUpperArm": { "flex": 70 } },
              "ik": { "leftFoot": "release", "rightFoot": "release" } },
            { "t": 0.55, "joints": { "LeftUpperLeg": { "flex": 72 }, "LeftLowerLeg": { "flex": 92 } }, "mirror": true },
            { "t": 0.82, "hips": { "y": -0.16, "pitch": 0 },
              "joints": { "LeftUpperLeg": { "flex": 35 }, "LeftLowerLeg": { "flex": 70 } }, "mirror": true,
              "ik": { "leftFoot": "pin", "rightFoot": "pin" } },
            { "t": 1.1, "pose": "stand", "hips": { "y": -0.01 } }
          ],
          "arcs": [ { "from": 0.24, "to": 0.82, "height": 0.6 } ]
        }
        """;

    [Fact]
    public void AFullScriptParses()
    {
        var script = PoseScriptParser.Parse(Jump);

        Assert.Equal("jump", script.Name);
        Assert.Equal(1.1f, script.Length);
        Assert.Equal(PoseRootMode.InPlace, script.Root);
        Assert.Equal(6, script.Keys.Count);
        Assert.Equal("stand", script.Keys[0].Pose);
        Assert.Equal(new IkCommand(IkCommandKind.Pin), script.Keys[0].Ik[IkEffector.LeftFoot]);
        Assert.Equal(0.5f, script.Keys[1].Ease);
        Assert.Equal(-0.22f, script.Keys[1].Hips!.Value.Y);
        Assert.Null(script.Keys[1].Hips!.Value.X);
        Assert.Equal(8f, script.Keys[1].Joints[HumanoidBone.Spine].Flex);
        Assert.Null(script.Keys[1].Joints[HumanoidBone.Spine].Abduct);
        Assert.True(script.Keys[3].Mirror);
        Assert.Equal(new PoseArc(0.24f, 0.82f, 0.6f), Assert.Single(script.Arcs));
    }

    [Fact]
    public void TargetsEasesAndLoopsParse()
    {
        var script = PoseScriptParser.Parse("""
            { "model": "m.glb", "library": "l.mfres", "name": "reach", "length": 1, "loop": true, "root": "motion",
              "keys": [ { "t": 0, "pose": "tpose", "ease": -1.5, "ik": { "rightHand": { "pos": [0.3, 1.6, 0.4] } } },
                        { "t": 1, "ease": "inOut" } ] }
            """);

        Assert.Equal(AnimationLoopMode.Linear, script.Loop);
        Assert.Equal(PoseRootMode.Motion, script.Root);
        Assert.Equal(-1.5f, script.Keys[0].Ease);
        Assert.Equal(-2f, script.Keys[1].Ease);
        Assert.Equal(new IkCommand(IkCommandKind.Target, new System.Numerics.Vector3(0.3f, 1.6f, 0.4f)), script.Keys[0].Ik[IkEffector.RightHand]);
    }

    [Theory]
    [InlineData("""{ "t": 0.5, "joints": { "LeftKnee": { "flex": 20 } } }""", "keys[1].joints.LeftKnee", "unknown bone")]
    [InlineData("""{ "t": 0.5, "joints": { "LeftUperLeg": { "flex": 20 } } }""", "keys[1].joints.LeftUperLeg", "did you mean LeftUpperLeg")]
    [InlineData("""{ "t": 0.5, "joints": { "LeftLowerLeg": { "flex": 20, "twist": 5 } } }""", "keys[1].joints.LeftLowerLeg", "hinge")]
    [InlineData("""{ "t": 0.5, "joints": { "Hips": { "flex": 20 } } }""", "keys[1].joints.Hips", "\"hips\"")]
    [InlineData("""{ "t": 0.5, "joints": { "Spine": { "bend": 20 } } }""", "keys[1].joints.Spine.bend", "unknown property")]
    [InlineData("""{ "t": 0.5, "pose": "crouch" }""", "keys[1].pose", "unknown pose")]
    [InlineData("""{ "t": 0.5, "ease": "bouncy" }""", "keys[1].ease", "\"linear\"")]
    [InlineData("""{ "t": 0.5, "ik": { "leftKnee": "pin" } }""", "keys[1].ik.leftKnee", "unknown effector")]
    [InlineData("""{ "t": 0.5, "ik": { "leftFoot": { "pos": [0, 0, 0], "space": "world" } } }""", "keys[1].ik.leftFoot.space", "character-space")]
    [InlineData("""{ "t": 0.5, "ik": { "leftFoot": "hold" } }""", "keys[1].ik.leftFoot", "\"pin\"")]
    [InlineData("""{ "t": 0.5, "hips": { "height": 1 } }""", "keys[1].hips.height", "unknown property")]
    [InlineData("""{ "t": 0.5, "mirror": "yes" }""", "keys[1].mirror", "true or false")]
    [InlineData("""{ "t": 3 }""", "keys[1].t", "outside the clip")]
    [InlineData("""{ "t": 0 }""", "keys[1].t", "not after the previous key")]
    [InlineData("""{ "pose": "stand" }""", "keys[1].t", "is required")]
    public void KeyErrorsNameTheirPath(string key, string path, string message)
    {
        var json = $$"""{ "model": "m.glb", "library": "l.mfres", "name": "c", "length": 1, "keys": [ { "t": 0, "pose": "stand" }, {{key}} ] }""";

        var error = Assert.Throws<PoseScriptException>(() => PoseScriptParser.Parse(json));

        Assert.Contains(error.Errors, e => e.Path == path && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "library": "l.mfres", "name": "c", "length": 1, "keys": [ { "t": 0, "pose": "stand" } ] }""", "model")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 0, "keys": [ { "t": 0, "pose": "stand" } ] }""", "length")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "keys": [] }""", "keys")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "keys": [ { "t": 0 } ] }""", "keys[0].pose")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "loop": "Forever", "keys": [ { "t": 0, "pose": "stand" } ] }""", "loop")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "root": "world", "keys": [ { "t": 0, "pose": "stand" } ] }""", "root")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "keys": [ { "t": 0, "pose": "stand" } ], "arcs": [ { "from": 0.5, "to": 0.2 } ] }""", "arcs[0]")]
    [InlineData("""{ "model": "m", "library": "l.mfres", "name": "c", "length": 1, "keys": [ { "t": 0, "pose": "stand" } ], "speed": 2 }""", "speed")]
    public void ScriptErrorsNameTheirPath(string json, string path) =>
        Assert.Contains(Assert.Throws<PoseScriptException>(() => PoseScriptParser.Parse(json)).Errors, e => e.Path == path);

    [Fact]
    public void EveryErrorIsCollectedAndInvalidJsonIsOneError()
    {
        var many = Assert.Throws<PoseScriptException>(() => PoseScriptParser.Parse(
            """{ "model": "m", "library": "l", "name": "c", "length": 1, "keys": [ { "t": 0, "pose": "x" }, { "t": 2, "ease": "?" } ] }"""));
        Assert.True(many.Errors.Count >= 3, string.Join("; ", many.Errors));
        Assert.Contains("keys[1].t: ", many.Message, StringComparison.Ordinal);

        var invalid = Assert.Throws<PoseScriptException>(() => PoseScriptParser.Parse("{ \"model\": "));
        Assert.StartsWith("not valid JSON", Assert.Single(invalid.Errors).Message, StringComparison.Ordinal);
    }
}

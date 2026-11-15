namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.14: edit scripts parse into absolute, relative and scaled changes; every error names its JSON path.</summary>
public class PoseEditScriptParserTests
{
    internal const string Edit = """
        {
          "model": "Assets/Characters/hero.glb",
          "library": "Assets/Characters/hero_anims.mfres",
          "base": "jump",
          "name": "jump_high",
          "edits": [
            { "range": [0.24, 0.82], "arc": { "height": "*1.3" } },
            { "t": 0.12, "joints": { "LeftUpperArm": { "flex": "-=10" } }, "mirror": true, "ease": "inOut" },
            { "t": 0.40, "insert": true, "joints": { "Spine": { "flex": -10, "twist": "+=2.5" } } },
            { "range": [0, 0.12], "hips": { "y": "-=0.03" }, "ik": { "leftFoot": "pin", "rightHand": { "pos": [0.3, 1, 0.2] } } }
          ]
        }
        """;

    [Fact]
    public void AFullEditScriptParses()
    {
        var script = PoseScriptParser.ParseEdit(Edit);

        Assert.True(PoseScriptParser.IsEditScript(Edit));
        Assert.False(PoseScriptParser.IsEditScript(PoseScriptParserTests.Jump));
        Assert.Equal("jump", script.Base);
        Assert.Equal("jump_high", script.Name);
        Assert.Equal(4, script.Edits.Count);

        var arc = script.Edits[0];
        Assert.Equal((0.24f, 0.82f), arc.Range);
        Assert.Equal(new EditValue(EditOperation.Scale, 1.3f), arc.ArcHeight);

        var arms = script.Edits[1];
        Assert.Equal(0.12f, arms.Time);
        Assert.Equal(new EditValue(EditOperation.Add, -10f), arms.Joints[HumanoidBone.LeftUpperArm].Flex);
        Assert.True(arms.Mirror);
        Assert.Equal(-2f, arms.Ease);

        var insert = script.Edits[2];
        Assert.True(insert.Insert);
        Assert.Equal(new EditValue(EditOperation.Set, -10f), insert.Joints[HumanoidBone.Spine].Flex);
        Assert.Equal(new EditValue(EditOperation.Add, 2.5f), insert.Joints[HumanoidBone.Spine].Twist);
        Assert.Null(insert.Joints[HumanoidBone.Spine].Abduct);

        var hips = script.Edits[3];
        Assert.Equal(new EditValue(EditOperation.Add, -0.03f), hips.Hips!.Value.Y);
        Assert.Equal(new IkCommand(IkCommandKind.Pin), hips.Ik[IkEffector.LeftFoot]);
        Assert.Equal(IkCommandKind.Target, hips.Ik[IkEffector.RightHand].Kind);
        Assert.Equal("edits[3]", hips.Path);
    }

    [Fact]
    public void NameDefaultsToTheBase()
    {
        var script = PoseScriptParser.ParseEdit("""
            { "model": "m.glb", "library": "l.mfres", "base": "jump", "edits": [ { "t": 0, "hips": { "y": 0.1 } } ] }
            """);

        Assert.Equal("jump", script.Name);
    }

    [Theory]
    [InlineData("""{ "t": 0.1 }""", "edits[0]", "changes nothing")]
    [InlineData("""{ "hips": { "y": 1 } }""", "edits[0]", "needs \"t\"")]
    [InlineData("""{ "t": 0.1, "range": [0, 1], "hips": { "y": 1 } }""", "edits[0]", "not both")]
    [InlineData("""{ "range": [1, 0], "hips": { "y": 1 } }""", "edits[0].range", "not be before")]
    [InlineData("""{ "range": [0, 1], "insert": true, "hips": { "y": 1 } }""", "edits[0].insert", "use \"t\"")]
    [InlineData("""{ "t": 0.1, "arc": { "height": 2 } }""", "edits[0].arc", "needs \"range\"")]
    [InlineData("""{ "range": [0, 1], "arc": { "height": -1 } }""", "edits[0].arc.height", "more than 0")]
    [InlineData("""{ "t": 0.1, "hips": { "y": "/2" } }""", "edits[0].hips.y", "\"+=n\"")]
    [InlineData("""{ "t": 0.1, "hips": { "y": "+=x" } }""", "edits[0].hips.y", "\"*n\"")]
    [InlineData("""{ "t": 0.1, "joints": { "LeftUperArm": { "flex": 1 } } }""", "edits[0].joints.LeftUperArm", "did you mean LeftUpperArm")]
    [InlineData("""{ "t": 0.1, "joints": { "LeftKnee": { "flex": 1 } } }""", "edits[0].joints.LeftKnee", "unknown bone")]
    [InlineData("""{ "t": 0.1, "joints": { "LeftLowerLeg": { "twist": "+=1" } } }""", "edits[0].joints.LeftLowerLeg", "hinge")]
    [InlineData("""{ "t": 0.1, "ik": { "leftFoot": "release" } }""", "edits[0].ik.leftFoot", "nothing to release")]
    [InlineData("""{ "t": 0.1, "hips": { "y": 1 }, "bend": 3 }""", "edits[0].bend", "unknown property")]
    public void EditErrorsNameTheirPath(string edit, string path, string message)
    {
        var json = $$"""{ "model": "m.glb", "library": "l.mfres", "base": "jump", "edits": [ {{edit}} ] }""";

        var error = Assert.Throws<PoseScriptException>(() => PoseScriptParser.ParseEdit(json));

        Assert.Contains(error.Errors, e => e.Path == path && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void TopLevelErrorsAreCollected()
    {
        var error = Assert.Throws<PoseScriptException>(() => PoseScriptParser.ParseEdit("""{ "model": "m.glb", "edits": [] }"""));

        Assert.Contains(error.Errors, e => e.Path == "library");
        Assert.Contains(error.Errors, e => e.Path == "base");
        Assert.Contains(error.Errors, e => e.Path == "edits");
    }
}

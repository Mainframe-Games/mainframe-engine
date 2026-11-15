using System.Text.Json;
using MainframeEngine.Cli;
using MainframeEngine.Tests.Scene;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>
/// G1.16: <c>mfe anim</c> in-process on a temp game project with the test character, as Claude runs it: rig, poses, bake
/// (pose and edit scripts), describe, check, list; text and JSON; exit codes 0, 1 and 2.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class MfeCliTests : IDisposable
{
    private const string Model = "Assets/Characters/hero.glb";
    private const string Library = "Assets/Characters/hero_anims.mfres";

    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private readonly string _root = Directory.CreateTempSubdirectory("mf-mfe-cli").FullName;
    private readonly string _scratch = Directory.CreateTempSubdirectory("mf-mfe-scratch").FullName;
    private StringWriter _output = new();
    private StringWriter _error = new();

    public MfeCliTests()
    {
        var characters = Path.Combine(_root, AssetDatabase.ContentFolder, "Characters");
        Directory.CreateDirectory(characters);
        var source = Path.Combine(ContentPaths.Root, "Models", "Character", SkinnedCharacter.File);
        File.Copy(source, Path.Combine(characters, "hero.glb"));
        File.Copy(source + ".mfmeta", Path.Combine(characters, "hero.glb.mfmeta"));
        new ProjectSettings { Name = "Hero" }.Save(_root);
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_scratch, recursive: true);
    }

    private int Mfe(params string[] args)
    {
        _output = new StringWriter();
        _error = new StringWriter();
        return MfeCli.Run([.. args, "--project", _root], _output, _error);
    }

    private string Output => _output.ToString();

    private string Script(string name, string json)
    {
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, json);
        return path;
    }

    private string Squat() => Script("squat.json", AnimationChecksTests.Squat);

    [Fact]
    public void HelpAndUsageErrors()
    {
        Assert.Equal(0, MfeCli.Run(["help"], _output, _error));
        Assert.Contains("mfe anim <command>", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, MfeCli.Run(["anim", "help"], _output, _error));
        Assert.Contains("bake <script.json>", _output.ToString(), StringComparison.Ordinal);

        Assert.Equal(2, MfeCli.Run([], _output, _error));
        Assert.Equal(2, Mfe("scene", "list"));
        Assert.Contains("unknown command group 'scene'", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Mfe("anim", "dance"));
        Assert.Equal(2, Mfe("anim", "list", Library, "--bogus", "1"));
        Assert.Contains("unknown option --bogus", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, MfeCli.Run(["anim", "list", Library, "--project", _scratch], _output, _error));
        Assert.Contains("has no project.mfproj", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RigPrintsTheMapAndWritesItOnce()
    {
        Assert.Equal(0, Mfe("anim", "rig", Model));
        Assert.Contains("Rest pose: TPose", Output, StringComparison.Ordinal);
        Assert.Contains("Missing required bones: none", Output, StringComparison.Ordinal);
        Assert.Contains("LeftUpperLeg", Output, StringComparison.Ordinal);
        Assert.Contains("UpperLeg.L", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "rig", Model, "--write-meta"));
        Assert.Contains("Wrote the map", Output, StringComparison.Ordinal);
        var meta = File.ReadAllText(Path.Combine(_root, Model + ".mfmeta"));
        Assert.Contains("\"humanoid\"", meta, StringComparison.Ordinal);
        Assert.Contains("\"craft\"", meta, StringComparison.Ordinal); // the rest of the sidecar kept
        Assert.Equal(0, Mfe("anim", "rig", Model, "--write-meta"));
        Assert.Contains("already holds this map", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "rig", Model, "--json"));
        using var json = JsonDocument.Parse(Output);
        Assert.Equal("TPose", json.RootElement.GetProperty("restPose").GetString());
        Assert.Contains(json.RootElement.GetProperty("map").EnumerateArray(),
            e => e.GetProperty("bone").GetString() == "Hips" && e.GetProperty("rigBone").GetString() == "Hips");
    }

    [Fact]
    public void PosesListTheBonesAndLimits()
    {
        Assert.Equal(0, Mfe("anim", "poses"));
        Assert.Contains("Named poses: tpose, stand, rest", Output, StringComparison.Ordinal);
        Assert.Contains("LeftLowerLeg* (hinge)", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "poses", "--json"));
        using var json = JsonDocument.Parse(Output);
        var knee = json.RootElement.GetProperty("bones").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "LeftLowerLeg");
        Assert.True(knee.GetProperty("hinge").GetBoolean());
        Assert.Equal(150, knee.GetProperty("limits").GetProperty("flex")[1].GetSingle());
    }

    [Fact]
    public void BakeWritesLinksAndChecks()
    {
        Assert.Equal(0, Mfe("anim", "bake", Squat()));
        Assert.Contains("Baked 'squat' (1.6 s) into Assets/Characters/hero_anims.mfres (a new library); linked to Assets/Characters/hero.glb.",
            Output, StringComparison.Ordinal);
        Assert.Contains("Checks: 0 error(s), 0 warning(s)", Output, StringComparison.Ordinal);
        Assert.Contains("Play it: AnimationPlayer.Play(\"squat\")", Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, Library)));
        Assert.Contains("hero_anims.mfres", File.ReadAllText(Path.Combine(_root, Model + ".mfmeta")), StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "list", Library));
        Assert.Contains("squat", Output, StringComparison.Ordinal);
        Assert.Contains("baked", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABakeWithCheckErrorsExitsOneButWritesTheClip()
    {
        Assert.Equal(1, Mfe("anim", "bake", Script("jump.json", PoseScriptParserTests.Jump), "--json"));

        using var json = JsonDocument.Parse(Output);
        Assert.Equal("jump", json.RootElement.GetProperty("clip").GetString());
        Assert.True(json.RootElement.GetProperty("checks").GetProperty("errors").GetInt32() > 0);
        Assert.Contains(json.RootElement.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("imports its own 'jump'", StringComparison.Ordinal));
        Assert.Equal(0, Mfe("anim", "list", Library, "--json"));
        using var list = JsonDocument.Parse(Output);
        Assert.Equal("jump", list.RootElement.GetProperty("clips")[0].GetProperty("name").GetString());

        // Tolerances can let it through.
        Assert.Equal(0, Mfe("anim", "check", Library, "jump", "--model", Model,
            "--tolerance", "ground=0.2,range=30,angularSpeed=3000"));
    }

    [Fact]
    public void AScriptErrorExitsTwoWithItsPathAndWritesNothing()
    {
        var bad = Script("bad.json", AnimationChecksTests.Squat.Replace("\"Spine\"", "\"Spline\"", StringComparison.Ordinal));

        Assert.Equal(2, Mfe("anim", "bake", bad));

        Assert.Contains("keys[1].joints.Spline: unknown bone (did you mean Spine?)", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", _error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, Library)));
        Assert.Equal(2, Mfe("anim", "bake", Path.Combine(_scratch, "missing.json")));
    }

    [Fact]
    public void AHandEditedClipIsOnlyReplacedWithReplace()
    {
        Assert.Equal(0, Mfe("anim", "bake", Squat()));
        var library = ResourceLoaderLoad();
        var spine = library.Get("squat")!.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");
        spine.Values[1] = System.Numerics.Quaternion.Normalize(spine.Values[1] * System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, 0.1f));
        SaveLibrary(library);

        Assert.Equal(2, Mfe("anim", "bake", Squat()));
        Assert.Contains("changed since it was baked", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Mfe("anim", "bake", Squat(), "--replace"));
        Assert.Contains("replacing the previous clip", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditScriptEditsTheClip()
    {
        Assert.Equal(0, Mfe("anim", "bake", Squat()));
        var edit = Script("edit.json", """
            { "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "base": "squat",
              "edits": [ { "t": 0.8, "joints": { "LeftUpperArm": { "flex": "+=10" } }, "mirror": true } ] }
            """);

        Assert.Equal(0, Mfe("anim", "bake", edit));
        Assert.StartsWith("Edited 'squat'", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "describe", Library, "squat", "--model", Model, "--times", "0.8", "--json"));
        using var json = JsonDocument.Parse(Output);
        var arm = json.RootElement.GetProperty("times")[0].GetProperty("joints").GetProperty("RightUpperArm");
        Assert.Equal(70f, arm.GetProperty("flex").GetSingle(), 0.5f);
        Assert.Equal(2, Mfe("anim", "bake", edit, "--no-link"));
    }

    [Fact]
    public void DescribeAndCheckReadTheClipBack()
    {
        Assert.Equal(0, Mfe("anim", "bake", Squat()));

        Assert.Equal(0, Mfe("anim", "describe", Library, "squat", "--model", Model));
        Assert.Contains("squat: 1.6 s, loop Linear", Output, StringComparison.Ordinal);
        Assert.Contains("LeftUpperArm: flex 60", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "check", Library, "squat", "--model", Model, "--root", "inPlace", "--start", "tpose"));
        Assert.Contains("warning start", Output, StringComparison.Ordinal);
        Assert.Contains("skipped: end", Output, StringComparison.Ordinal);

        Assert.Equal(0, Mfe("anim", "check", Library, "squat", "--model", Model, "--json"));
        using var json = JsonDocument.Parse(Output);
        Assert.Equal(0, json.RootElement.GetProperty("errors").GetInt32());

        Assert.Equal(2, Mfe("anim", "check", Library, "hop", "--model", Model));
        Assert.Contains("no clip 'hop'", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Mfe("anim", "check", Library, "squat"));
        Assert.Equal(2, Mfe("anim", "check", Library, "squat", "--model", Model, "--root", "sideways"));
        Assert.Equal(2, Mfe("anim", "check", Library, "squat", "--model", Model, "--tolerance", "slide=1"));
    }

    [Fact]
    public void RenderOptionsAreCheckedBeforeAnythingRenders()
    {
        Assert.Equal(0, Mfe("anim", "bake", Squat()));
        string[] clip = ["anim", "render", Library, "squat", "--model", Model];

        Assert.Equal(2, Mfe(clip));
        Assert.Contains("-o <sheet.png> is required", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Mfe([.. clip, "-o", "s.png", "--views", "top"]));
        Assert.Contains("unknown view 'top'", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Mfe([.. clip, "-o", "s.png", "--frames", "4", "--frame", "0.5"]));
        Assert.Equal(2, Mfe([.. clip, "-o", "s.png", "--frames", "40"]));
        Assert.Equal(2, Mfe([.. clip, "-o", "s.png", "--size", "100"]));
    }

    [Fact]
    public void TheProjectsGlobalsAreRestored()
    {
        var assets = AssetDatabase.Current;
        var directory = ContentPaths.ProjectDirectory;

        Mfe("anim", "list", Library);

        Assert.Same(assets, AssetDatabase.Current);
        Assert.Equal(directory, ContentPaths.ProjectDirectory);
    }

    private AnimationLibrary ResourceLoaderLoad()
    {
        AssetDatabase.Current = new AssetDatabase(_root);
        ResourceLoader.ClearCache();
        return ResourceLoader.Load<AnimationLibrary>(Library);
    }

    private void SaveLibrary(AnimationLibrary library)
    {
        ResourceSaver.Save(library, Library);
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
    }
}

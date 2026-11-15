using System.Numerics;
using System.Text.Json;
using MainframeEngine.Tests.Scene;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>
/// G1.13: baked clips go into library <c>.mfres</c> files, never over a hand-edited clip by accident; libraries link to the
/// model's <c>.mfmeta</c> and its importer plays them before its own clips.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class PoseLibraryTests : IDisposable
{
    private const string ModelPath = "Assets/Characters/hero.glb";
    private const string LibraryPath = "Assets/Characters/hero_anims.mfres";

    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private readonly string _root = Directory.CreateTempSubdirectory("mf-pose-library").FullName;

    public PoseLibraryTests()
    {
        var characters = Path.Combine(_root, AssetDatabase.ContentFolder, "Characters");
        Directory.CreateDirectory(characters);
        var source = Path.Combine(ContentPaths.Root, "Models", "Character", SkinnedCharacter.File);
        File.Copy(source, Path.Combine(characters, "hero.glb"));
        File.Copy(source + ".mfmeta", Path.Combine(characters, "hero.glb.mfmeta"));
        AssetDatabase.Current = new AssetDatabase(_root);
        AssetDatabase.Current.Refresh(createMissingMeta: true);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        Directory.Delete(_root, recursive: true);
    }

    private string Absolute(string projectPath) => Path.Combine(_root, projectPath);

    private MainframeEngine.Animation BakeJump(string name = "jump")
    {
        var root = ModelImporter.ImportNodes(Absolute(ModelPath), ModelImportSettings.Default);
        var skeleton = root.GetNode<Skeleton3D>("Armature");
        var rig = new HumanoidRig(skeleton, HumanoidMap.Detect(skeleton, null, out _));
        var script = PoseScriptParser.Parse(PoseScriptParserTests.Jump.Replace("\"jump\"", $"\"{name}\"", StringComparison.Ordinal));
        return PoseBaker.Bake(script, rig, root.GetPathTo(skeleton)).Animation;
    }

    [Fact]
    public void TheFirstBakeCreatesTheLibraryAndLinksItOnce()
    {
        var first = PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath, importedClips: SkinnedCharacter.Clips);
        var second = PoseLibrary.Write(BakeJump("hop"), LibraryPath, ModelPath);

        Assert.True(first.CreatedLibrary);
        Assert.True(first.Linked);
        Assert.Contains(first.Notes, n => n.Contains("imports its own 'jump'", StringComparison.Ordinal));
        Assert.False(second.CreatedLibrary);
        Assert.False(second.Linked);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        Assert.Equal(["jump", "hop"], library.Animations.Select(a => a.ResourceName));
        Assert.True(PoseLibrary.IsUntouchedBake(library.Get("jump")!));

        var meta = AssetDatabase.Current.ReadOrCreateMeta(ModelPath, create: false)!;
        Assert.Equal([LibraryPath], ModelImportSettings.FromMeta(meta).Libraries);
        Assert.Equal("craft", ModelImportSettings.FromMeta(meta).Clips.Single().Name); // the rest of the sidecar kept
    }

    [Fact]
    public void AnUntouchedBakeIsReplacedButAHandEditNeedsReplace()
    {
        PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath);
        var rebake = PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath);
        Assert.True(rebake.ReplacedClip);

        // A person moves a key in the editor and saves.
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        var spine = library.Get("jump")!.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");
        spine.Values[2] = Quaternion.Normalize(spine.Values[2] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f));
        ResourceSaver.Save(library, LibraryPath);
        ResourceLoader.ClearCache();

        var refused = Assert.Throws<PoseLibraryException>(() => PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath));
        Assert.Contains("changed since it was baked", refused.Message, StringComparison.Ordinal);
        Assert.True(PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath, replace: true).ReplacedClip);
    }

    [Fact]
    public void AnEditOfAHandEditedClipWritesOverItsBaseAndKeepsTheFix()
    {
        PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        var spine = library.Get("jump")!.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine");
        var fixedValue = Quaternion.Normalize(spine.Values[2] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f));
        spine.Values[2] = fixedValue;
        ResourceSaver.Save(library, LibraryPath);
        ResourceLoader.ClearCache();

        var root = ModelImporter.ImportNodes(Absolute(ModelPath), ModelImportSettings.Default);
        var skeleton = root.GetNode<Skeleton3D>("Armature");
        var rig = new HumanoidRig(skeleton, HumanoidMap.Detect(skeleton, null, out _));
        var edit = PoseScriptParser.ParseEdit("""
            { "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "base": "jump",
              "edits": [ { "t": 0.12, "joints": { "LeftUpperArm": { "flex": "-=10" } } } ] }
            """);
        var result = PoseEditor.Apply(edit, PoseLibrary.Read(LibraryPath, "jump"), rig, root.GetPathTo(skeleton));

        Assert.True(PoseLibrary.WriteEdit(result.Animation, LibraryPath, "jump").ReplacedClip);
        ResourceLoader.ClearCache();
        var written = PoseLibrary.Read(LibraryPath, "jump");
        Assert.Equal(fixedValue, written.Tracks.OfType<Rotation3DTrack>().Single(t => t.Bone == "Spine").Values[2]);
        Assert.False(PoseLibrary.IsUntouchedBake(written));
        // A full re-bake still refuses: the hand fix is in there.
        Assert.Throws<PoseLibraryException>(() => PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath));
    }

    [Fact]
    public void AVariantOverAHandEditedClipIsGuarded()
    {
        PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath);
        var handMade = new MainframeEngine.Animation { ResourceName = "jump_high", Length = 1f };
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        library.Add("jump_high", handMade);
        ResourceSaver.Save(library, LibraryPath);
        ResourceLoader.ClearCache();
        var variant = (MainframeEngine.Animation)PoseLibrary.Read(LibraryPath, "jump").Duplicate(deep: true);
        variant.ResourceName = "jump_high";

        Assert.Throws<PoseLibraryException>(() => PoseLibrary.WriteEdit(variant, LibraryPath, "jump"));
        Assert.True(PoseLibrary.WriteEdit(variant, LibraryPath, "jump", replace: true).ReplacedClip);
    }

    [Fact]
    public void ReadingAMissingClipNamesTheClipsThere()
    {
        PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath);

        var missing = Assert.Throws<PoseLibraryException>(() => PoseLibrary.Read(LibraryPath, "hop"));

        Assert.Contains("no clip 'hop'", missing.Message, StringComparison.Ordinal);
        Assert.Contains("jump", missing.Message, StringComparison.Ordinal);
        Assert.Throws<PoseLibraryException>(() => PoseLibrary.Read("Assets/Characters/none.mfres", "jump"));
    }

    [Fact]
    public void AClipTheBakerDidNotMakeIsNotReplacedSilently()
    {
        var library = new AnimationLibrary();
        library.Add("jump", new MainframeEngine.Animation { Length = 1f });
        ResourceSaver.Save(library, LibraryPath);
        ResourceLoader.ClearCache();

        var refused = Assert.Throws<PoseLibraryException>(() => PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath));

        Assert.Contains("not made by the baker", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLinkLeavesTheSidecarAlone()
    {
        var before = File.ReadAllText(Absolute(ModelPath) + ".mfmeta");

        var result = PoseLibrary.Write(BakeJump(), LibraryPath, ModelPath, link: false);

        Assert.False(result.Linked);
        Assert.Equal(before, File.ReadAllText(Absolute(ModelPath) + ".mfmeta"));
    }

    [Fact]
    public void TheModelsPlayerPlaysTheLinkedClipAsTheSamplerPosesIt()
    {
        var baked = BakeJump();
        PoseLibrary.Write(baked, LibraryPath, ModelPath);
        ResourceLoader.ClearCache();
        var settings = ModelImportSettings.FromMeta(AssetDatabase.Current.ReadOrCreateMeta(ModelPath, create: false));
        var character = ModelImporter.ImportNodes(Absolute(ModelPath), settings);
        var player = character.GetNode<AnimationPlayer>("AnimationPlayer");
        var skeleton = character.GetNode<Skeleton3D>("Armature");

        Assert.Equal(2, player.Libraries.Count);
        Assert.Equal(baked.BakedHash, player.GetAnimation("jump")!.BakedHash); // the baked jump, not the imported one
        Assert.NotNull(player.GetAnimation("swing"));

        var tree = new SceneTree();
        tree.Root.AddChild(character);
        player.Play("jump");
        player.Seek(0.4, update: true);
        var played = Enumerable.Range(0, skeleton.GetBoneCount()).Select(skeleton.GetBonePoseRotation).ToArray();

        skeleton.ResetBonePoses();
        ClipSampler.Apply(baked, skeleton, new NodePath("Armature"), 0.4);
        for (var i = 0; i < played.Length; i++)
            Assert.True(MathF.Abs(Quaternion.Dot(played[i], skeleton.GetBonePoseRotation(i))) > 0.99999f, skeleton.GetBoneName(i));
        tree.Shutdown();
    }

    [Fact]
    public void LibrariesRoundTripThroughModelSettings()
    {
        var settings = new ModelImportSettings { Libraries = [LibraryPath, "Assets/other.mfres"] };
        var meta = new AssetMeta { Uid = "mdl_ffffffffffff", Importer = ModelImportSettings.ImporterName, Settings = settings.ToMetaSettings() };

        var read = ModelImportSettings.FromMeta(meta);

        Assert.Equal(settings.Libraries, read.Libraries);
        Assert.Equal(settings, read);
        Assert.False(ModelImportSettings.Default.ToMetaSettings().ContainsKey("libraries"));
        Assert.Equal(JsonValueKind.Array, settings.ToMetaSettings()["libraries"].ValueKind);
    }
}

using MainframeEngine.Serialization;
using System.Numerics;

namespace MainframeEngine.Editor.Tests.FileSystem;

/// <summary>
/// G1.18: files changed outside the editor show up in open scenes — a linked animation library rewritten on disk reloads
/// into the instanced model's player, a model whose sidecar now links a library reopens the clean scenes that use it,
/// unsaved edits ask first — and the Animation panel previews an instanced model's clips read-only.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class EditorReloadTests : IDisposable
{
    private const string Model = "Assets/Characters/hero.glb";
    private const string Library = "Assets/Characters/hero_anims.mfres";

    private readonly SessionHost _host = new();
    private readonly List<MessageRequest> _asked = [];
    private readonly List<Resource> _reloaded = [];
    private EditedResource? _inspected;
    private readonly ResourceReloader _reloader;

    public EditorReloadTests()
    {
        var characters = Abs("Assets/Characters");
        Directory.CreateDirectory(characters);
        var source = Path.Combine(RepositoryRoot(), "Tests", "Assets", "Models", "Character", "character.glb");
        File.Copy(source, Path.Combine(characters, "hero.glb"));
        File.Copy(source + ".mfmeta", Path.Combine(characters, "hero.glb.mfmeta"));
        AssetDatabase.Current = new AssetDatabase(_host.ProjectDirectory);
        ContentPaths.ProjectDirectory = _host.ProjectDirectory;
        AssetDatabase.Current.Scan(createMissingMeta: true);
        _host.Session.UseProjectOf(_host.ScenePath("Main.mfscene"));
        _reloader = new ResourceReloader(_host.Session, () => _inspected, _asked.Add);
        _reloader.Reloaded += _reloaded.Add;
    }

    public void Dispose()
    {
        _inspected?.Dispose();
        _host.Dispose();
    }

    private string Abs(string projectPath) => Path.Combine(_host.ProjectDirectory, projectPath);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No repository root above the test output.");
    }

    private static AnimationLibrary Clips(params (string Name, float Length)[] clips)
    {
        var library = new AnimationLibrary();
        foreach (var (name, length) in clips)
            library.Add(name, new MainframeEngine.Animation { ResourceName = name, Length = length });
        return library;
    }

    // Writes the library file as another process (mfe) would: different bytes, a newer time, the loaded instance untouched.
    private void WriteFromOutside(AnimationLibrary library)
    {
        ResourceSaver.Save(library, "Assets/Characters/elsewhere.mfres");
        File.WriteAllBytes(Abs(Library), File.ReadAllBytes(Abs("Assets/Characters/elsewhere.mfres")));
        File.SetLastWriteTimeUtc(Abs(Library), DateTime.UtcNow.AddSeconds(5));
    }

    // A saved scene instancing the model, opened (clean); returns it and the instance.
    private (EditedScene Scene, Node Instance) OpenSceneWithHero()
    {
        var scene = _host.Session.NewScene();
        scene.InstanceScene(Abs(Model));
        _host.Session.Save(scene, _host.ScenePath("Main.mfscene"));
        var instance = scene.Root.Children.Single(c => c.SceneFilePath is not null);
        return (scene, instance);
    }

    private static AnimationPlayer PlayerOf(Node instance) => AnimationEditor.Resolve(instance, instance.Owner!, out _)!;

    [Fact]
    public void ALinkedLibraryRewrittenOnDiskReloadsIntoTheOpenScene()
    {
        ResourceSaver.Save(Clips(("wave", 1f)), Library);
        PoseLibrary.Link(Model, Library);
        ResourceLoader.ClearCache();
        var (_, instance) = OpenSceneWithHero();
        var player = PlayerOf(instance);
        var wave = player.GetAnimation("wave")!;
        Assert.Equal(1f, wave.Length);

        WriteFromOutside(Clips(("wave", 2f), ("hop", 0.5f)));
        _reloader.FilesChanged([Library]);

        Assert.Same(wave, player.GetAnimation("wave"));
        Assert.Equal(2f, wave.Length);
        Assert.NotNull(player.GetAnimation("hop"));
        Assert.Single(_reloaded);
        Assert.Empty(_asked);
    }

    [Fact]
    public void TheEditorsOwnSaveIsNotAReload()
    {
        ResourceSaver.Save(Clips(("wave", 1f)), Library);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(Library);
        library.Get("wave")!.Length = 3f;
        ResourceSaver.Save(library, Library);

        _reloader.FilesChanged([Library]);

        Assert.Empty(_reloaded);
        Assert.Equal(3f, library.Get("wave")!.Length);
    }

    [Fact]
    public void UnsavedEditsAskBeforeTheFileReplacesThem()
    {
        ResourceSaver.Save(Clips(("wave", 1f)), Library);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(Library);
        _inspected = new EditedResource(library, Abs(Library));
        var wave = library.Get("wave")!;
        var length = TypeRegistry.Get(typeof(MainframeEngine.Animation))!.FindProperty(nameof(MainframeEngine.Animation.Length))!;
        _inspected.SetProperty(wave, length, 4f);
        Assert.True(_inspected.IsDirty);

        WriteFromOutside(Clips(("wave", 2f)));
        _reloader.FilesChanged([Library]);
        _reloader.FilesChanged([Library]); // a second change while the first question is open asks once

        var question = Assert.Single(_asked);
        Assert.Equal(["Keep mine", "Load from disk"], question.Buttons);
        Assert.Equal(4f, wave.Length); // nothing replaced yet
        question.Callback!(0, "");
        Assert.Equal(4f, wave.Length);
        Assert.True(_inspected.IsDirty);

        _reloader.FilesChanged([Library]);
        Assert.Equal(2, _asked.Count);
        _asked[1].Callback!(1, "");
        Assert.Equal(2f, wave.Length);
        Assert.False(_inspected.IsDirty);
    }

    [Fact]
    public void AModelThatNowLinksALibraryReopensTheCleanSceneAndAsksForADirtyOne()
    {
        var (scene, instance) = OpenSceneWithHero();
        Assert.Null(PlayerOf(instance).GetAnimation("wave"));
        var dirty = _host.Session.NewScene();
        dirty.InstanceScene(Abs(Model));
        Assert.True(dirty.IsDirty);

        ResourceSaver.Save(Clips(("wave", 1f)), Library);
        PoseLibrary.Link(Model, Library);
        _reloader.FilesChanged([Model + AssetDatabase.MetaExtension]);

        Assert.DoesNotContain(scene, _host.Session.Scenes); // reopened as a new tab
        var reopened = _host.Session.Scenes.Single(s => s.FilePath is not null);
        Assert.False(reopened.IsDirty);
        var hero = reopened.Root.Children.Single(c => c.SceneFilePath is not null);
        Assert.NotNull(PlayerOf(hero).GetAnimation("wave"));
        Assert.Contains(dirty, _host.Session.Scenes);
        Assert.Contains("Save and reopen", Assert.Single(_asked).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASidecarOfAModelNobodyUsesChangesNothing()
    {
        PoseLibrary.Link(Model, Library);
        _reloader.FilesChanged([Model + AssetDatabase.MetaExtension, "Assets/Characters/none.mfres"]);

        Assert.Empty(_asked);
        Assert.Empty(_reloaded);
    }

    [Fact]
    public void AnInstancedModelsPlayerPreviewsReadOnly()
    {
        ResourceSaver.Save(Clips(), Library);
        var library = ResourceLoader.Load<AnimationLibrary>(Library);
        var skeletonBone = "Spine";
        var clip = new MainframeEngine.Animation { ResourceName = "bend", Length = 1f };
        clip.Tracks.Add(new Rotation3DTrack
        {
            Path = new NodePath("Armature"),
            Bone = skeletonBone,
            Times = [0f, 1f],
            Values = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)],
        });
        library.Add("bend", clip);
        ResourceSaver.Save(library, Library);
        PoseLibrary.Link(Model, Library);
        ResourceLoader.ClearCache();
        var (scene, instance) = OpenSceneWithHero();
        var history = new UndoRedo();
        var editor = new AnimationEditor(null);
        editor.SelectForTests(instance, scene.Root, history);

        Assert.NotNull(editor.Player);
        Assert.True(editor.ReadOnly);
        Assert.False(editor.KeysShown);
        Assert.Equal("", editor.NewAnimation("mine"));
        editor.SelectAnimation("bend");
        editor.DeleteAnimation();
        Assert.Equal(0, history.Position);
        Assert.NotNull(editor.Player!.GetAnimation("bend"));

        var skeleton = instance.GetNode<Skeleton3D>("Armature");
        var bone = skeleton.FindBone(skeletonBone);
        var rest = skeleton.GetBonePoseRotation(bone);
        editor.Scrub(1.0);
        Assert.True(MathF.Abs(Quaternion.Dot(skeleton.GetBonePoseRotation(bone), rest)) < 0.99f); // posed
        editor.OnResourceReloaded(library); // a reload restarts the preview where it was
        Assert.Equal(1.0, editor.Playhead, 3);
        Assert.True(MathF.Abs(Quaternion.Dot(skeleton.GetBonePoseRotation(bone), rest)) < 0.99f);
        editor.Stop();
        Assert.Equal(rest, skeleton.GetBonePoseRotation(bone)); // the scene's own pose came back
        Assert.Equal(0, history.Position);

        // The scene's root and a plain node are not instances: no player.
        Assert.Null(AnimationEditor.Resolve(scene.Root, scene.Root, out _));
    }
}

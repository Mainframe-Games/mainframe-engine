using System.Drawing;
using System.Text;
using System.Text.Json;

namespace MainframeEngine.Tests.Scene;

/// <summary>Edge cases of the tree and the scene files (callbacks that mutate the tree, re-saves, versions).</summary>
[Collection(nameof(SerialResources))]
public sealed class SceneEdgeCaseTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-scene-edge", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private readonly SceneTree _tree = new();

    public SceneEdgeCaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    [Fact]
    public void FreeingItselfFromOnExitTreeIsDeferred()
    {
        var parent = new PlainNode { Name = "Parent" };
        var child = new LoggingNode { Name = "Child" };
        parent.AddChild(child);
        _tree.Root.AddChild(parent);
        child.Log = [];

        var selfFreeing = new SelfFreeingNode();
        parent.AddChild(selfFreeing);

        _tree.Root.RemoveChild(parent); // selfFreeing.OnExitTree calls Free()
        Assert.False(selfFreeing.IsFreed);
        Assert.Same(parent, selfFreeing.Parent);
        parent.Free();
        Assert.True(selfFreeing.IsFreed);
    }

    [Fact]
    public void RemovingAnEarlierSiblingDuringEnterTreeDoesNotSkipTheNextOne()
    {
        var parent = new PlainNode { Name = "Parent" };
        var a = new LoggingNode { Name = "A" };
        var b = new LoggingNode { Name = "B" };
        var c = new LoggingNode { Name = "C" };
        parent.AddChild(a);
        parent.AddChild(b);
        parent.AddChild(c);
        b.OnEnterAction = _ => parent.RemoveChild(a);

        _tree.Root.AddChild(parent);

        Assert.True(c.IsInsideTree);
        Assert.True(c.IsNodeReady);
        Assert.False(a.IsInsideTree);
        a.Free();
    }

    [Fact]
    public void AutoRenameOnReparentKeepsUniqueNamesResolvable()
    {
        var root = new PlainNode { Name = "Root" };
        var a = new PlainNode { Name = "A" };
        var b = new PlainNode { Name = "B" };
        root.AddChild(a);
        root.AddChild(b);
        a.AddChild(new PlainNode { Name = "Gun" });
        var gun = new PlainNode { Name = "Gun" };
        b.AddChild(gun);
        a.Owner = root;
        b.Owner = root;
        gun.Owner = root;
        gun.UniqueNameInOwner = true;

        gun.Reparent(a); // collides with a's "Gun" → "Gun2"
        Assert.Equal("Gun2", gun.Name);
        Assert.Same(gun, root.GetNode("%Gun2"));
        Assert.Null(root.GetNodeOrNull("%Gun"));

        gun.Free();
        Assert.Null(root.GetNodeOrNull("%Gun2"));

        var clash = new PlainNode { Name = "Unique" };
        root.AddChild(clash);
        clash.Owner = root;
        clash.UniqueNameInOwner = true;
        var other = new PlainNode { Name = "Other" };
        a.AddChild(other); // a different parent: only the owner's unique-name registry can clash
        other.Owner = root;
        other.UniqueNameInOwner = true;
        Assert.Throws<InvalidOperationException>(() => other.Name = "Unique");
        Assert.Equal("Other", other.Name); // unchanged after the failed rename
        root.Free();
    }

    [Fact]
    public void AThrowingDeferredCallSurfacesOnceAndLaterCallsStillRun()
    {
        var ran = new List<string>();
        _tree.CallDeferred(() => ran.Add("first"));
        _tree.CallDeferred(() => throw new InvalidOperationException("boom"));
        _tree.CallDeferred(() => ran.Add("third"));

        Assert.Throws<InvalidOperationException>(_tree.FlushDeferred);
        Assert.Equal(["first"], ran);

        _tree.FlushDeferred();
        Assert.Equal(["first", "third"], ran);
    }

    [Fact]
    public void OnlyOneCameraKeepsTheCurrentFlag()
    {
        var a = new Camera3D { Name = "A" };
        var b = new Camera3D { Name = "B" };
        _tree.Root.AddChild(a);
        _tree.Root.AddChild(b);

        a.Current = true;
        b.Current = true;
        Assert.False(a.Current);
        Assert.Same(b, _tree.Root.ActiveCamera3D);

        a.Current = true;
        Assert.Same(a, _tree.Root.ActiveCamera3D);
        Assert.False(b.Current);

        var late = new Camera3D { Name = "Late", Current = true };
        _tree.Root.AddChild(late);
        Assert.Same(late, _tree.Root.ActiveCamera3D);
        Assert.False(a.Current);
    }

    [Fact]
    public void ResavingACachedSceneKeepsResourcesUsedByLiveInstances()
    {
        var uid = ResourceSaver.Save(new TestResource { Value = 3 }, ContentPath("Shared.mres"));
        var root = new AllTypesNode { Name = "Main" };
        root.Data = ResourceLoader.Load<TestResource>(uid);
        var scenePath = ContentPath("S.mscene");
        SceneSaver.Save(root, scenePath);
        root.Data.Release();
        root.Free();
        ResourceLoader.ClearCache();

        var scene = ResourceLoader.Load<PackedScene>(scenePath);
        var live = scene.Instantiate<AllTypesNode>();
        var resource = live.Data!;

        // Re-save (the editor's Save): the cached PackedScene gets the new content.
        live.Int = 42;
        SceneSaver.Save(live, scenePath);

        Assert.True(ResourceLoader.IsCached(uid));
        Assert.Same(resource, ResourceLoader.Load<TestResource>(uid));
        Assert.Equal(42, scene.Instantiate<AllTypesNode>().Int);
        live.Free();
    }

    [Fact]
    public void InstanceOverridesAreMigratedWithTheirOwnVersion()
    {
        Directory.CreateDirectory(ContentPath("Scenes"));
        var sub = new Node3D { Name = "Sub" };
        var migrated = new MigratedNode { Name = "Engine" };
        sub.AddChild(migrated);
        migrated.Owner = sub;
        var subUid = SceneSaver.Save(sub, ContentPath("Scenes/Sub.mscene"));
        sub.Free();

        // An override written when MigratedNode was version 1 ("Velocity").
        var json = $$"""
            { "format": 1,
              "root": { "type": "Node3D", "name": "Level",
                "children": [ { "instance": "{{subUid}}", "path": "Content/Scenes/Sub.mscene", "name": "Sub",
                                "overrides": { "Engine": { "Velocity": 9 } }, "overrideVersions": { "Engine": 1 } } ] } }
            """;
        var level = PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate();
        Assert.Equal(9f, level.GetNode<MigratedNode>("Sub/Engine").Speed);

        // Written back with the current version.
        var saved = SceneJson.Node(SceneJson.Parse(SceneSaver.ToJson(level)), "Sub");
        Assert.Equal(3, saved.GetProperty("overrideVersions").GetProperty("Engine").GetInt32());
        level.Free();
    }

    [Fact]
    public void ReloadedSubScenesDoNotTurnInlineResourcesIntoOverrides()
    {
        Directory.CreateDirectory(ContentPath("Scenes"));
        var sub = new AllTypesNode { Name = "Sub", Data = new TestResource { Value = 7 } };
        SceneSaver.Save(sub, ContentPath("Scenes/Sub.mscene"));
        sub.Free();

        var level = new Node3D { Name = "Level" };
        var instance = ResourceLoader.Load<PackedScene>("Content/Scenes/Sub.mscene").Instantiate();
        level.AddChild(instance);
        instance.Owner = level;
        ResourceLoader.ClearCache(); // the writer reloads the sub-scene: new inline resource instances

        var entry = SceneJson.Node(SceneJson.Parse(SceneSaver.ToJson(level)), "Sub");
        Assert.False(entry.TryGetProperty("props", out _));
        level.Free();
    }

    [Fact]
    public void EquivalentColorsAreNotWrittenAsOverrides()
    {
        var quad = new Sprite3D { Modulate = Color.FromArgb(255, 255, 255, 255) }; // equal to Color.White by value
        var root = SceneJson.Root(SceneJson.Parse(SceneSaver.ToJson(quad)));
        Assert.False(root.TryGetProperty("props", out _));
        quad.Free();
    }

    [Fact]
    public void ChangeSceneToFileHoldsAndReleasesItsReference()
    {
        var uid = SceneSaver.Save(new Node3D { Name = "Level" }, ContentPath("Level.mscene"));
        ResourceLoader.ClearCache();

        _tree.ChangeSceneToFile(uid);
        Assert.True(ResourceLoader.IsCached(uid));

        _tree.UnloadCurrentScene();
        Assert.False(ResourceLoader.IsCached(uid));
    }

    [Fact]
    public void ATreeLessSpineNodeKeepsItsRendererWhenAddedToATree()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "Spine", "SpineBoy");
        var renderer = new HeadlessRenderer();
        using var servers = new ServerRegistry();
        servers.Register(new RenderServer(renderer));
        var tree = new SceneTree(servers);

        var spine = new SpineNode(renderer, new SpineFolder(folder));
        var before = spine.SpineRenderer;
        tree.Root.AddChild(spine);
        Assert.Same(before, spine.SpineRenderer);
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        tree.Shutdown();
    }

    private sealed class SelfFreeingNode : Node
    {
        protected override void OnExitTree() => Free();
    }

    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Silk.NET.Maths.Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}

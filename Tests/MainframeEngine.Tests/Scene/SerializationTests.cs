using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// Scene/resource files, the loader cache and the asset database (process-wide state: serial). Each test runs
/// against a fresh project folder.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class SerializationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-scene-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public SerializationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
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

    private static JsonElement Json(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static T Own<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }

    // ---------------------------------------------------------------------------------------------
    // Property values
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryPropertyTypeRoundTrips()
    {
        var shared = new TestResource { Value = 42, Label = "shared" };
        var source = new AllTypesNode
        {
            Name = "All",
            Flag = true,
            Byte = 200,
            Int = -5,
            Long = long.MaxValue,
            UInt = uint.MaxValue,
            ULong = ulong.MaxValue,
            Short = short.MinValue,
            Float = 0.1f,
            Double = Math.PI,
            Text = "line 1\nline \"2\" ünïcode",
            NullableText = null,
            Mode = TestMode.Third,
            Flags = TestFlags.A | TestFlags.C,
            Vec2 = new Vector2(1.5f, -2.25f),
            Vec4 = new Vector4(float.Epsilon, float.MaxValue, -0f, 1e-30f),
            Quat = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
            Tint = Color.FromArgb(10, 230, 120, 80),
            Xform = Transform3D.FromTrs(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f), new Vector3(2)),
            Xform2D = Transform2D.FromTrs(new Vector2(4, 5), 0.25f, Vector2.One),
            Target = "../Other",
            Weights = [1f, 2.5f, float.PositiveInfinity],
            Tags = ["a", "b"],
            Data = new TestResource { Value = 1, Next = shared },
            DataList = [shared, new TestResource { Value = 3 }],
            Field = 99,
            Position = new Vector3(1, 2, 3),
            RotationDegrees = new Vector3(10, 20, 30),
            Scale = new Vector3(1, 2, 1),
            Visible = false,
            ProcessPriority = 4,
            ProcessMode = ProcessMode.Always,
        };

        var copy = PackedScene.Pack(source).Instantiate<AllTypesNode>();

        var info = TypeRegistry.Get(typeof(AllTypesNode))!;
        foreach (var property in info.Properties.Where(p => !typeof(Resource).IsAssignableFrom(p.ValueType)
                                                             && p.ValueType != typeof(List<TestResource>)))
            Assert.True(property.ValueEquals(source, copy), $"{property.Name}: {property.GetValue(source)} != {property.GetValue(copy)}");

        // Resources: same structure, and a resource used twice is still one shared instance.
        Assert.Equal(1, copy.Data!.Value);
        Assert.Equal(42, copy.Data.Next!.Value);
        Assert.Equal("shared", copy.Data.Next.Label);
        Assert.Same(copy.Data.Next, copy.DataList![0]);
        Assert.Equal(3, copy.DataList[1].Value);
        Assert.NotSame(source.Data, copy.Data);

        source.Free();
        copy.Free();
    }

    [Fact]
    public void OnlyNonDefaultValuesAreWritten()
    {
        var node = new AllTypesNode { Name = "N" };
        var json = Json(SceneSaver.ToJson(node));
        Assert.False(SceneJson.Root(json).TryGetProperty("props", out _));
        Assert.False(json.TryGetProperty("resources", out _));

        node.Int = 8;
        node.Position = new Vector3(0, 1, 0);
        json = Json(SceneSaver.ToJson(node));
        var props = SceneJson.Root(json).GetProperty("props");
        Assert.Equal(["Position", "Int"], props.EnumerateObject().Select(p => p.Name));
        Assert.Equal("[0, 1, 0]", props.GetProperty("Position").GetRawText()); // short arrays stay on one line
        node.Free();
    }

    [Fact]
    public void SavedFilesUseLfLineEndingsOnEveryOs()
    {
        // Scene, resource, .meta and index files are committed content: byte-identical on Windows, macOS and Linux.
        var node = new AllTypesNode { Name = "N", Int = 8, Position = new Vector3(0, 1, 0) };
        var scene = ContentPath("Lf.mscene");
        SceneSaver.Save(node, scene);
        node.Free();
        ResourceSaver.Save(new TestResource { Value = 1, Label = "a" }, ContentPath("Lf.mres"));
        File.WriteAllBytes(ContentPath("lf.png"), [1, 2, 3]);
        var db = new AssetDatabase(_project);
        db.Scan(createMissingMeta: true);
        db.WriteIndex();

        string[] files = [scene, ContentPath("Lf.mres"), ContentPath("lf.png.meta"), ContentPath(AssetDatabase.IndexFileName)];
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.Contains((byte)'\n', bytes);
            Assert.DoesNotContain((byte)'\r', bytes);
        }
    }

    [Fact]
    public void ValuesUseReadableEncodings()
    {
        var node = new AllTypesNode { Mode = TestMode.Second, Flags = TestFlags.A | TestFlags.B, Tint = Color.FromArgb(255, 255, 0, 51) };
        var props = SceneJson.Root(Json(SceneSaver.ToJson(node))).GetProperty("props");
        Assert.Equal("Second", props.GetProperty("Mode").GetString());
        Assert.Equal("A, B", props.GetProperty("Flags").GetString());
        Assert.Equal([1f, 0f, 0.2f, 1f], props.GetProperty("Tint").EnumerateArray().Select(e => e.GetSingle()));
        node.Free();
    }

    // ---------------------------------------------------------------------------------------------
    // Tree structure, ownership, groups, connections
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyNodesOwnedByTheRootAreSaved()
    {
        var root = new Node3D { Name = "Main" };
        var owned = Own(root, root, new Node3D { Name = "Owned" });
        Own(root, owned, new PlainNode { Name = "Nested" });
        root.AddChild(new PlainNode { Name = "Spawned" }); // runtime: no owner

        var copy = PackedScene.Pack(root).Instantiate();

        Assert.Equal(["Owned"], copy.Children.Select(c => c.Name));
        Assert.NotNull(copy.GetNodeOrNull("Owned/Nested"));
        Assert.Same(copy, copy.GetNode("Owned/Nested").Owner);
        Assert.Null(copy.Owner);
        root.Free();
        copy.Free();
    }

    [Fact]
    public void InstantiateBuildsOutsideTheTree()
    {
        var root = new Node3D { Name = "Main" };
        Own(root, root, new Timer { Name = "T", Autostart = true, WaitTime = 0.5f });
        var scene = PackedScene.Pack(root);

        var instance = scene.Instantiate();
        var timer = instance.GetNode<Timer>("T");
        Assert.False(instance.IsInsideTree);
        Assert.False(timer.IsNodeReady);
        Assert.True(timer.IsStopped); // Autostart only runs in OnReady

        var tree = new SceneTree();
        tree.Root.AddChild(instance);
        Assert.True(timer.IsNodeReady);
        Assert.Equal(0.5f, timer.TimeLeft);
        tree.Shutdown();
        root.Free();
    }

    [Fact]
    public void PersistentGroupsAndUniqueNamesAreSaved()
    {
        var root = new Node3D { Name = "Main" };
        var a = Own(root, root, new PlainNode { Name = "A" });
        a.AddToGroup("saved", persistent: true);
        a.AddToGroup("runtime");
        a.UniqueNameInOwner = true;

        var copy = PackedScene.Pack(root).Instantiate();
        var copyA = copy.GetNode("%A");
        Assert.True(copyA.IsInGroup("saved"));
        Assert.False(copyA.IsInGroup("runtime"));
        root.Free();
        copy.Free();
    }

    [Fact]
    public void PersistedConnectionsAreSavedAndRebound()
    {
        var root = new Node3D { Name = "Main" };
        var emitter = Own(root, root, new SignalNode { Name = "Emitter" });
        var receiver = Own(root, root, new SignalNode { Name = "Receiver" });
        emitter.Connect("Counted", receiver, nameof(SignalNode.OnCounted), ConnectFlags.Persist);
        emitter.Connect("Pinged", root.GetNode<SignalNode>("Receiver"), nameof(SignalNode.OnPinged)); // not persisted

        var bytes = SceneSaver.ToJson(root);
        var connections = Json(bytes).GetProperty("connections");
        Assert.Equal(1, connections.GetArrayLength());
        Assert.Equal("Emitter", connections[0].GetProperty("from").GetString());
        Assert.Equal("Receiver", connections[0].GetProperty("to").GetString());

        var copy = PackedScene.Parse(bytes).Instantiate();
        copy.GetNode<SignalNode>("Emitter").Count(7);
        copy.GetNode<SignalNode>("Emitter").Ping();
        Assert.Equal(["counted 7"], copy.GetNode<SignalNode>("Receiver").Received);
        Assert.Same(copy, copy.GetNode("Emitter").GetSignalConnections()[0].OriginScene);
        root.Free();
        copy.Free();
    }

    [Fact]
    public void DeferredAndOneShotFlagsArePersisted()
    {
        var root = new Node3D { Name = "Main" };
        var emitter = Own(root, root, new SignalNode { Name = "Emitter" });
        emitter.Connect("Counted", root.GetNode("Emitter"), nameof(SignalNode.OnCounted), ConnectFlags.Persist | ConnectFlags.OneShot);

        var copy = PackedScene.Pack(root).Instantiate();
        var connection = copy.GetNode("Emitter").GetSignalConnections().Single();
        Assert.Equal(ConnectFlags.Persist | ConnectFlags.OneShot, connection.Flags);
        root.Free();
        copy.Free();
    }

    // ---------------------------------------------------------------------------------------------
    // Nested scene instances
    // ---------------------------------------------------------------------------------------------

    private string SavePlayerScene()
    {
        var player = new Node3D { Name = "Player" };
        var hitbox = Own(player, player, new AllTypesNode { Name = "Hitbox", Int = 10 });
        var signals = Own(player, hitbox, new SignalNode { Name = "Signals" });
        signals.Connect("Pinged", signals, nameof(SignalNode.OnPinged), ConnectFlags.Persist);
        var path = ContentPath("Scenes/Player.mscene");
        SceneSaver.Save(player, path);
        player.Free();
        return path;
    }

    [Fact]
    public void NestedInstancesStoreOnlyOverridesAndAddedChildren()
    {
        var playerPath = SavePlayerScene();
        var playerScene = ResourceLoader.Load<PackedScene>(playerPath);

        var level = new Node3D { Name = "Level" };
        var player = Own(level, level, playerScene.Instantiate());
        player.Name = "Hero";
        ((Node3D)player).Position = new Vector3(0, 0, 2);
        player.GetNode<AllTypesNode>("Hitbox").Int = 99;           // override inside the instance
        Own(level, player, new PlainNode { Name = "Hat" });         // added under the instance root
        Own(level, player.GetNode("Hitbox"), new PlainNode { Name = "Badge" }); // added deeper
        Assert.Equal("Content/Scenes/Player.mscene", player.SceneFilePath);

        var bytes = SceneSaver.ToJson(level);
        var file = Json(bytes);
        var entry = SceneJson.Node(file, "Hero");
        Assert.Equal(playerScene.Uid, entry.GetProperty("instance").GetString());
        Assert.Equal("Content/Scenes/Player.mscene", entry.GetProperty("path").GetString());
        Assert.Equal("Hero", entry.GetProperty("name").GetString());
        Assert.Equal(["Position"], entry.GetProperty("props").EnumerateObject().Select(p => p.Name));
        var overrides = entry.GetProperty("overrides");
        Assert.Equal(["Hitbox"], overrides.EnumerateObject().Select(p => p.Name));
        Assert.Equal(99, overrides.GetProperty("Hitbox").GetProperty("Int").GetInt32());
        // Added nodes are listed after the instance in tree order (Hitbox is Hero's first child), parent paths through it.
        Assert.Equal(["Level", "Hero", "Badge", "Hat"], file.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()));
        Assert.Equal("Hero", SceneJson.Node(file, "Hero/Hat").GetProperty("parent").GetString());
        Assert.Equal("Hero/Hitbox", SceneJson.Node(file, "Hero/Hitbox/Badge").GetProperty("parent").GetString());
        Assert.False(Json(bytes).TryGetProperty("connections", out _)); // the sub-scene owns its connection

        var copy = PackedScene.Parse(bytes).Instantiate();
        var hero = copy.GetNode<Node3D>("Hero");
        Assert.Equal(new Vector3(0, 0, 2), hero.Position);
        Assert.Equal(99, copy.GetNode<AllTypesNode>("Hero/Hitbox").Int);
        Assert.NotNull(copy.GetNodeOrNull("Hero/Hat"));
        Assert.NotNull(copy.GetNodeOrNull("Hero/Hitbox/Badge"));
        Assert.Same(copy, hero.Owner);
        Assert.Same(hero, copy.GetNode("Hero/Hitbox").Owner);     // inner nodes stay owned by the instance
        Assert.Same(copy, copy.GetNode("Hero/Hat").Owner);
        copy.GetNode<SignalNode>("Hero/Hitbox/Signals").Ping();
        Assert.Equal(["pinged"], copy.GetNode<SignalNode>("Hero/Hitbox/Signals").Received);

        level.Free();
        copy.Free();
        playerScene.Release();
    }

    [Fact]
    public void ChangesToTheSubSceneFlowIntoInstancesThatDidNotOverrideThem()
    {
        var playerPath = SavePlayerScene();
        var level = new Node3D { Name = "Level" };
        var player = Own(level, level, ResourceLoader.Load<PackedScene>(playerPath).Instantiate());
        player.GetNode<AllTypesNode>("Hitbox").Text = "overridden";
        var levelPath = ContentPath("Scenes/Level.mscene");
        SceneSaver.Save(level, levelPath);
        level.Free();

        // Edit the sub-scene: change a value the level did not override.
        var playerRoot = ResourceLoader.Load<PackedScene>(playerPath).Instantiate();
        playerRoot.GetNode<AllTypesNode>("Hitbox").Int = 555;
        SceneSaver.Save(playerRoot, playerPath);
        playerRoot.Free();

        ResourceLoader.ClearCache();
        var reloaded = ResourceLoader.Load<PackedScene>(levelPath).Instantiate();
        var hitbox = reloaded.GetNode<AllTypesNode>("Player/Hitbox");
        Assert.Equal(555, hitbox.Int);
        Assert.Equal("overridden", hitbox.Text);
        reloaded.Free();
    }

    [Fact]
    public void ASceneThatInstancesItselfIsRejected()
    {
        var path = ContentPath("Scenes/Loop.mscene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "format": 1, "uid": "scn_00000000aaaa",
              "root": { "type": "Node3D", "name": "Loop",
                "children": [ { "instance": "scn_00000000aaaa", "path": "Content/Scenes/Loop.mscene", "name": "Again" } ] } }
            """);
        AssetDatabase.Current.Register("scn_00000000aaaa", path);

        var scene = ResourceLoader.Load<PackedScene>(path);
        Assert.Throws<InvalidDataException>(() => scene.Instantiate());
    }

    // ---------------------------------------------------------------------------------------------
    // Resources, UIDs, cache
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ExternalResourcesAreReferencedByUidAndShared()
    {
        var resource = new TestResource { Value = 5, Label = "external", Next = new TestResource { Value = 6 } };
        var resourcePath = ContentPath("Data/Stats.mres");
        var uid = ResourceSaver.Save(resource, resourcePath);
        Assert.StartsWith("res_", uid, StringComparison.Ordinal);
        Assert.True(resource.IsExternal);
        Assert.Equal("Content/Data/Stats.mres", resource.ResourcePath);

        var root = new AllTypesNode { Name = "Main", Data = resource };
        var scenePath = ContentPath("Scenes/UsesStats.mscene");
        SceneSaver.Save(root, scenePath);
        root.Free();

        var table = Json(File.ReadAllBytes(scenePath)).GetProperty("resources");
        var reference = table.EnumerateObject().Single().Value;
        Assert.Equal(uid, reference.GetProperty("ref").GetString());
        Assert.Equal("Content/Data/Stats.mres", reference.GetProperty("path").GetString());

        ResourceLoader.ClearCache();
        var scene = ResourceLoader.Load<PackedScene>(scenePath);
        var a = scene.Instantiate<AllTypesNode>();
        var b = scene.Instantiate<AllTypesNode>();
        Assert.Same(a.Data, b.Data);
        Assert.Equal(5, a.Data!.Value);
        Assert.Equal(6, a.Data.Next!.Value); // the inline sub-resource of the .mres
        Assert.Same(a.Data, ResourceLoader.Load<TestResource>(uid));
        Assert.Same(a.Data, ResourceLoader.Load<TestResource>("Content/Data/Stats.mres"));
        a.Free();
        b.Free();
    }

    [Fact]
    public void ReleasingTheLastReferenceEvictsTheResource()
    {
        var uid = ResourceSaver.Save(new TestResource { Value = 1 }, ContentPath("R.mres"));
        ResourceLoader.ClearCache();

        var first = ResourceLoader.Load<TestResource>(uid);
        var second = ResourceLoader.Load<TestResource>(uid);
        Assert.Same(first, second);
        Assert.Equal(2, first.ReferenceCount);

        first.Release();
        Assert.True(ResourceLoader.IsCached(uid));
        second.Release();
        Assert.False(ResourceLoader.IsCached(uid));
        Assert.Throws<InvalidOperationException>(first.Release);

        var reloaded = ResourceLoader.Load<TestResource>(uid);
        Assert.NotSame(first, reloaded);
    }

    [Fact]
    public void UidsSurviveResavingAndMovingFiles()
    {
        var sub = new Node3D { Name = "Sub" };
        var subPath = ContentPath("Scenes/Sub.mscene");
        var uid = SceneSaver.Save(sub, subPath);
        Assert.Equal(uid, SceneSaver.Save(sub, subPath)); // stable on re-save
        Assert.True(AssetUid.IsUid(uid));
        Assert.StartsWith("scn_", uid, StringComparison.Ordinal);

        var level = new Node3D { Name = "Level" };
        Own(level, level, ResourceLoader.Load<PackedScene>(subPath).Instantiate());
        var levelPath = ContentPath("Scenes/Level.mscene");
        SceneSaver.Save(level, levelPath);
        level.Free();
        sub.Free();

        // Move the sub-scene; the level's path hint goes stale but its UID still resolves.
        AssetDatabase.Current.Move("Content/Scenes/Sub.mscene", "Content/Moved/Renamed.mscene");
        ResourceLoader.ClearCache();
        Assert.Equal("Content/Moved/Renamed.mscene", AssetDatabase.Current.GetPath(uid));

        var reloaded = ResourceLoader.Load<PackedScene>(levelPath).Instantiate();
        Assert.Equal("Content/Moved/Renamed.mscene", reloaded.GetNode("Sub").SceneFilePath);
        reloaded.Free();

        // A fresh database finds it again by scanning.
        var scanned = new AssetDatabase(_project);
        scanned.Scan();
        Assert.Equal("Content/Moved/Renamed.mscene", scanned.GetPath(uid));
    }

    [Fact]
    public void AssetDatabaseScansMetaSidecarsAndWritesAnIndex()
    {
        File.WriteAllBytes(ContentPath("icon.png"), [1, 2, 3]);
        // A canvas shader's SPIR-V and lock are part of the shader, not assets: no .meta of their own.
        File.WriteAllText(ContentPath("wave.gdshader"), "shader_type canvas_item;");
        File.WriteAllBytes(ContentPath("wave.gdshader.vert.spv"), [1]);
        File.WriteAllBytes(ContentPath("wave.gdshader.frag.spv"), [1]);
        File.WriteAllText(ContentPath("wave.gdshader.spvlock"), "x");
        var sceneUid = SceneSaver.Save(new Node3D { Name = "S" }, ContentPath("S.mscene"));

        var db = new AssetDatabase(_project);
        db.Scan(createMissingMeta: true);
        var meta = db.ReadOrCreateMeta("Content/icon.png", create: false)!;
        Assert.StartsWith("tex_", meta.Uid, StringComparison.Ordinal);
        Assert.True(File.Exists(ContentPath("icon.png.meta")));
        Assert.True(File.Exists(ContentPath("wave.gdshader.meta")));
        Assert.False(File.Exists(ContentPath("wave.gdshader.vert.spv.meta")));
        Assert.False(File.Exists(ContentPath("wave.gdshader.frag.spv.meta")));
        Assert.False(File.Exists(ContentPath("wave.gdshader.spvlock.meta")));
        Assert.Equal("Content/icon.png", db.GetPath(meta.Uid));
        Assert.Equal("Content/S.mscene", db.GetPath(sceneUid));
        Assert.Equal(meta.Uid, db.GetUid("Content/icon.png"));

        // A second scan keeps the meta's UID.
        var again = new AssetDatabase(_project);
        again.Scan();
        Assert.Equal("Content/icon.png", again.GetPath(meta.Uid));

        // Shipped builds read the index instead of scanning.
        db.WriteIndex();
        var index = Json(File.ReadAllBytes(ContentPath(AssetDatabase.IndexFileName)));
        Assert.Equal(1, index.GetProperty("format").GetInt32());
        Assert.Equal("Content/S.mscene", index.GetProperty("assets").GetProperty(sceneUid).GetString());

        File.Delete(ContentPath("S.mscene"));
        var fromIndex = new AssetDatabase(_project);
        fromIndex.Refresh();
        Assert.Equal("Content/S.mscene", fromIndex.GetPath(sceneUid)); // came from the index, not a scan
    }

    [Fact]
    public void AssetUidFormat()
    {
        var uid = AssetUid.Generate("scn");
        Assert.Matches("^scn_[0-9a-f]{12}$", uid);
        Assert.True(AssetUid.IsUid(uid));
        Assert.True(AssetUid.IsUid("res_7b21aa90")); // the 8-digit form from the design sketch
        Assert.False(AssetUid.IsUid("Content/Scenes/Main.mscene"));
        Assert.False(AssetUid.IsUid("SCN_3f9a1c2e"));
        Assert.Throws<ArgumentException>(() => AssetUid.Generate("toolong"));
        Assert.Equal("aud", AssetUid.PrefixForExtension(".OGG"));
    }

    [Fact]
    public void ResourceDuplicateCopiesValues()
    {
        var original = new TestResource { Value = 3, Label = "x", Next = new TestResource { Value = 4 } };
        var shallow = (TestResource)original.Duplicate();
        var deep = (TestResource)original.Duplicate(deep: true);
        Assert.Equal(3, shallow.Value);
        Assert.Same(original.Next, shallow.Next);
        Assert.NotSame(original.Next, deep.Next);
        Assert.Equal(4, deep.Next!.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Versioning, unknown data
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OldTypeVersionsAreMigratedOnLoad()
    {
        const string json = """
            { "format": 1,
              "root": { "type": "MigratedNode", "name": "Old", "v": 1, "props": { "Velocity": 3.5, "Size": 2 } } }
            """;
        var node = PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate<MigratedNode>();
        Assert.Equal(3.5f, node.Speed);
        Assert.Equal(new Vector3(2), node.Size);

        var saved = SceneJson.Root(Json(SceneSaver.ToJson(node)));
        Assert.Equal(3, saved.GetProperty("v").GetInt32());
        Assert.True(saved.GetProperty("props").TryGetProperty("Speed", out _));
        node.Free();
    }

    [Fact]
    public void UnknownTypesArePreservedAsMissingNodes()
    {
        const string json = """
            { "format": 1,
              "root": { "type": "Node3D", "name": "Main",
                "children": [ { "type": "DeletedEnemy", "name": "Boss", "v": 2, "props": { "Health": 100, "Tags": ["x"] },
                                "children": [ { "type": "Node3D", "name": "Weapon", "props": { "Position": [1, 0, 0] } } ] } ] } }
            """;
        var root = PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate();
        var boss = Assert.IsType<MissingNode>(root.GetNode("Boss"));
        Assert.Equal("DeletedEnemy", boss.OriginalType);
        Assert.Equal(new Vector3(1, 0, 0), root.GetNode<Node3D>("Boss/Weapon").Position);

        var file = Json(SceneSaver.ToJson(root));
        var saved = SceneJson.Node(file, "Boss");
        Assert.Equal("DeletedEnemy", saved.GetProperty("type").GetString());
        Assert.Equal(2, saved.GetProperty("v").GetInt32());
        Assert.Equal(100, saved.GetProperty("props").GetProperty("Health").GetInt32());
        Assert.Equal("Node3D", SceneJson.Node(file, "Boss/Weapon").GetProperty("type").GetString());
        root.Free();
    }

    [Fact]
    public void UnknownPropertiesAreIgnoredAndBadValuesReportTheirLocation()
    {
        var ok = PackedScene.Parse(Encoding.UTF8.GetBytes("""{ "root": { "type": "Node3D", "name": "N", "props": { "Gone": 1 } } }""")).Instantiate();
        Assert.Equal("N", ok.Name);
        ok.Free();

        var bad = PackedScene.Parse(Encoding.UTF8.GetBytes("""{ "root": { "type": "Node3D", "name": "N", "props": { "Position": [1, 2] } } }"""));
        var e = Assert.Throws<InvalidDataException>(() => bad.Instantiate());
        Assert.Contains("Node3D.Position", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerFileFormatsAreRejected()
    {
        var json = Encoding.UTF8.GetBytes($$"""{ "format": {{SceneFormat.Current + 1}}, "root": { "type": "Node", "name": "N" } }""");
        Assert.Throws<InvalidDataException>(() => PackedScene.Parse(json));
        Assert.Throws<InvalidDataException>(() => PackedScene.Parse(Encoding.UTF8.GetBytes("""{ "format": 1 }""")));
        Assert.Throws<InvalidDataException>(() => PackedScene.Parse(Encoding.UTF8.GetBytes("""{ "root": { "name": "typeless" } }""")));
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        var scene = PackedScene.Parse(Encoding.UTF8.GetBytes("""
            // hand-edited
            { "root": { "type": "Node3D", "name": "N", "props": { "Position": [1, 2, 3], }, }, }
            """));
        var node = scene.Instantiate<Node3D>();
        Assert.Equal(new Vector3(1, 2, 3), node.Position);
        node.Free();
    }

    [Fact]
    public void TheSandboxSceneParsesAndResavesIdentically()
    {
        // FlyCamera and SpinningBox live in the Sandbox assembly: here they load as MissingNodes, which must
        // write back exactly what they read (including the inline resources they reference). The scene instances
        // the glTF test model, which the test output carries, so resolve assets against the output folder.
        AssetDatabase.Current = new AssetDatabase(AppContext.BaseDirectory);
        var path = Path.Combine(AppContext.BaseDirectory, "Content", "Scenes", "Sandbox.mscene");
        var original = File.ReadAllText(path).ReplaceLineEndings("\n");
        var scene = PackedScene.Parse(File.ReadAllBytes(path));
        var root = scene.Instantiate();

        Assert.IsType<MissingNode>(root.GetNode("Camera"));
        Assert.Equal("Content/Sky/sky_10_2k.png", root.GetNode<WorldEnvironment>("Environment").Sky!.Panorama);
        Assert.Equal(5, root.Children.Count(c => c is Light3D));

        var resaved = Encoding.UTF8.GetString(SceneSaver.ToJson(root, scene.Uid)).ReplaceLineEndings("\n");
        Assert.Equal(original.TrimEnd(), resaved.TrimEnd());
        root.Free();
    }
}

[CollectionDefinition(nameof(SerialResources), DisableParallelization = true)]
public sealed class SerialResources;

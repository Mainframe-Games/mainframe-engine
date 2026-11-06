using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>A scattered scene root that records what it knew when it entered the tree and became ready.</summary>
internal sealed class TestTerrainRock : Node3D, ITerrainObject
{
    public int TerrainObjectId { get; set; }

    public Terrain3D? Terrain { get; set; }

    public int IdOnEnter { get; private set; } = -1;

    public Terrain3D? TerrainOnEnter { get; private set; }

    public int IdOnReady { get; private set; } = -1;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        IdOnEnter = TerrainObjectId;
        TerrainOnEnter = Terrain;
    }

    protected override void OnReady()
    {
        base.OnReady();
        IdOnReady = TerrainObjectId;
    }
}

/// <summary>G8a.9: terrain objects — the id list, objects.json, and one scene instance per object on the ground.</summary>
public sealed class TerrainObjectsTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static TerrainData Data()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        return data;
    }

    private static PackedScene RockScene(string mesh = "Mesh")
    {
        var root = new TestTerrainRock { Name = "Rock" };
        var child = new MeshInstance3D { Name = mesh, Mesh = new BoxMesh() };
        root.AddChild(child);
        child.Owner = root;
        return PackedScene.Pack(root);
    }

    private static ScatterObjectType RockType(float sink = 0f, params PackedScene[] scenes) =>
        new() { Name = "rock", Scenes = scenes.Length > 0 ? scenes : [RockScene()], SinkMeters = sink };

    private Terrain3D AddTerrain(TerrainData data)
    {
        var terrain = _h.Add(new Terrain3D { Name = "Terrain", Data = data });
        terrain.Owner = _h.Scene;
        return terrain;
    }

    [Fact]
    public void IdsAreStableAndNeverReused()
    {
        var data = Data();
        Assert.Equal(1, data.NextObjectId);
        var a = data.AddObject(0, 4f, 4f);
        var b = data.AddObject(0, 8f, 4f, 90f, 1.5f);
        var c = data.AddObject(1, 12f, 4f);
        Assert.Equal([1, 2, 3], new[] { a, b, c });
        var removed = data.GetObject(b)!.Value;
        Assert.Equal(new TerrainObject(2, 0, 8f, 4f, 90f, 1.5f), removed);

        Assert.True(data.RemoveObject(b));
        Assert.False(data.RemoveObject(b));
        Assert.Null(data.GetObject(b));
        Assert.True(data.RemoveObject(c));
        Assert.Equal(4, data.AddObject(0, 1f, 1f)); // 3 was used: the next id never moves back

        Assert.True(data.RestoreObject(removed)); // undo of a removal: the same id
        Assert.False(data.RestoreObject(removed)); // already there
        Assert.Equal(removed, data.GetObject(2));
        Assert.Equal(5, data.NextObjectId);
        Assert.True(data.RestoreObject(new TerrainObject(9, 0, 2f, 2f, 0f, 1f))); // a redo past the counter moves it on
        Assert.Equal(10, data.NextObjectId);
        Assert.Equal([1, 4, 2, 9], data.Objects.Select(o => o.Id));
    }

    [Fact]
    public void TypesAreFoundByName()
    {
        var data = Data();
        data.ObjectTypes = [new ScatterObjectType { Name = "rock" }, new ScatterObjectType { Name = "stump" }];
        Assert.Equal(1, data.ObjectTypeIndex("stump"));
        Assert.Equal(-1, data.ObjectTypeIndex("crate"));
        data.ObjectTypes = null!;
        Assert.Empty(data.ObjectTypes);
    }

    [Fact]
    public void TheListSavesOnlyWhenChangedAndLoadsBack()
    {
        var folder = Directory.CreateTempSubdirectory("mf-objects").FullName;
        try
        {
            var data = Data();
            var file = Path.Combine(folder, "objects.json");
            data.SaveLayers(folder);
            Assert.False(File.Exists(file)); // never had an object: no file

            data.AddObject(0, 3.25f, 7.5f, 45f, 1.25f);
            data.AddObject(2, 10f, 11f);
            data.SaveLayers(folder);
            Assert.True(File.Exists(file));
            File.Delete(file);
            data.SaveLayers(folder);
            Assert.False(File.Exists(file)); // unchanged: not written again

            data.AddObject(1, 20f, 20f);
            data.SaveLayers(folder);
            var loaded = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = folder };
            Assert.Equal(data.Objects, loaded.Objects);
            Assert.Equal(4, loaded.NextObjectId);

            // Every object removed: the file stays, for the next id.
            foreach (var id in loaded.Objects.Select(o => o.Id).ToArray())
                loaded.RemoveObject(id);
            loaded.SaveLayers(folder);
            var again = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = folder };
            Assert.Empty(again.Objects);
            Assert.Equal(4, again.AddObject(0, 1f, 1f));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SavingIntoANewFolderCarriesTheListOver()
    {
        var root = Directory.CreateTempSubdirectory("mf-objects-move").FullName;
        try
        {
            var first = Path.Combine(root, "a");
            var data = Data();
            data.AddObject(0, 5f, 5f);
            data.SaveLayers(first);
            var loaded = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = first };
            loaded.EnsureLoaded(); // the layers are read; the list is not yet
            var second = Path.Combine(root, "b");
            loaded.SaveLayers(second);
            Assert.True(File.Exists(Path.Combine(second, "objects.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ABadFileIsReported()
    {
        var folder = Directory.CreateTempSubdirectory("mf-objects-bad").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "objects.json"), """{ "format": 1, "nextId": 2, "objects": [[5, 0, 1, 1, 0, 1]] }""");
            var data = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = folder };
            Assert.Throws<InvalidDataException>(() => data.Objects);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ATerrainSpawnsOneUnownedSceneInstancePerObjectOnTheGround()
    {
        var data = Data();
        data.SetHeightsFrom(TerrainDataTests.Hills);
        data.ObjectTypes = [RockType(sink: 0.2f)];
        var a = data.AddObject(0, 6f, 9f, 30f, 2f);
        var b = data.AddObject(0, 20f, 14f);
        var terrain = AddTerrain(data);
        _h.Run(1);

        Assert.Equal(2, terrain.ObjectCount);
        Assert.Equal(2, terrain.ObjectsRoot!.ChildCount);
        var rock = Assert.IsType<TestTerrainRock>(terrain.GetObjectNode(a));
        Assert.Equal(a, rock.IdOnEnter); // set before it entered the tree
        Assert.Same(terrain, rock.TerrainOnEnter);
        Assert.Equal(a, rock.IdOnReady);
        Assert.Equal(6f, rock.Position.X, 4);
        Assert.Equal(terrain.HeightAt(6f, 9f) - 0.2f * 2f, rock.Position.Y, 4);
        Assert.Equal(9f, rock.Position.Z, 4);
        Assert.Equal(new Vector3(2f), rock.Scale);
        Assert.Equal(30f, rock.RotationDegrees.Y, 3);
        Assert.NotNull(rock.GetNodeOrNull("Mesh"));
        Assert.Equal(b, ((TestTerrainRock)terrain.GetObjectNode(b)!).TerrainObjectId);

        Assert.Null(terrain.ObjectsRoot.Owner);
        Assert.Null(rock.Owner);
        // A saved scene holds the data, not the instances. (In-memory PackedScenes cannot be written: swap in a type
        // without scenes; the nodes stay until the next frame's sync.)
        data.ObjectTypes = [new ScatterObjectType { Name = "rock" }];
        Assert.True(rock.IsInsideTree);
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_h.Scene));
        Assert.DoesNotContain(nameof(TestTerrainRock), json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Objects\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingAndRemovingSpawnAndFreeNodes()
    {
        var data = Data();
        data.ObjectTypes = [RockType()];
        var terrain = AddTerrain(data);
        var id = data.AddObject(0, 10f, 10f);
        var node = Assert.IsType<TestTerrainRock>(terrain.GetObjectNode(id));
        Assert.Equal(id, node.IdOnReady);
        Assert.True(node.IsInsideTree);

        Assert.True(terrain.RemoveObject(id));
        Assert.Null(terrain.GetObjectNode(id));
        Assert.Equal(0, terrain.ObjectCount);
        _h.Run(1); // freed at the end of the frame (the object's own script may have asked)
        Assert.True(node.IsFreed);
        Assert.False(terrain.RemoveObject(id));
    }

    [Fact]
    public void HeightEditsPutTheInstancesBackOnTheGround()
    {
        var data = Data();
        data.ObjectTypes = [RockType(sink: 0.1f)];
        var id = data.AddObject(0, 12.3f, 7.7f);
        var far = data.AddObject(0, 30f, 30f);
        var terrain = AddTerrain(data);
        var node = terrain.GetObjectNode(id)!;
        var flat = terrain.HeightAt(12.3f, 7.7f) - 0.1f; // 0 m quantised to the 16-bit step, minus the sink
        Assert.Equal(flat, node.Position.Y, 5);

        terrain.Edit.Begin(TerrainLayers.Height);
        var rect = new Rect2I(20, 10, 10, 10); // vertices 20..29 × 10..19: 10–14.5 m × 5–9.5 m
        var heights = new float[rect.Area];
        Array.Fill(heights, 3f);
        terrain.SetHeights(rect, heights);
        var record = terrain.Edit.End();
        Assert.Equal(terrain.HeightAt(12.3f, 7.7f) - 0.1f, node.Position.Y, 4);
        Assert.Equal(2.9f, node.Position.Y, 2);
        Assert.Equal(flat, terrain.GetObjectNode(far)!.Position.Y, 5); // outside the edit

        record.Undo();
        Assert.Equal(flat, node.Position.Y, 5);

        // Water lowers the bed: the instance sinks with it.
        data.SetWaterDepthFrom((x, z) => 1f);
        Assert.Equal(terrain.HeightAt(12.3f, 7.7f) - 0.1f, node.Position.Y, 4);
        Assert.True(node.Position.Y < -1f);
    }

    [Fact]
    public void VariantsComeFromTheIdAndTypeEditsRespawn()
    {
        var data = Data();
        var type = RockType(0f, RockScene("MeshA"), RockScene("MeshB"));
        data.ObjectTypes = [type];
        for (var i = 0; i < 8; i++)
            data.AddObject(0, 2f + i * 3f, 5f);
        var terrain = AddTerrain(data);
        var variants = new HashSet<string>();
        foreach (var o in data.Objects)
        {
            var expected = Terrain3D.VariantHash(o.Id) % 2 == 0 ? "MeshA" : "MeshB";
            Assert.NotNull(terrain.GetObjectNode(o.Id)!.GetNodeOrNull(expected));
            variants.Add(expected);
        }

        Assert.Equal(2, variants.Count); // eight ids use both

        var before = terrain.GetObjectNode(1)!;
        type.SinkMeters = 0.5f;
        _h.Run(1); // a type change re-spawns every instance
        Assert.True(before.IsFreed);
        Assert.Equal(terrain.HeightAt(2f, 5f) - 0.5f, terrain.GetObjectNode(1)!.Position.Y, 5);

        data.ObjectTypes = [];
        _h.Run(1);
        Assert.Null(terrain.GetObjectNode(1)); // no type: no node, but the object stays
        Assert.Equal(8, terrain.ObjectCount);
    }

    [Fact]
    public void ChangesWhileOutOfTheTreeAreCaughtUpOnReentry()
    {
        var data = Data();
        data.ObjectTypes = [RockType()];
        var terrain = AddTerrain(data);
        _h.Scene.RemoveChild(terrain);
        var id = data.AddObject(0, 10f, 10f);
        Assert.Null(terrain.GetObjectNode(id)); // not listening outside the tree
        _h.Scene.AddChild(terrain);
        Assert.NotNull(terrain.GetObjectNode(id));
    }
}

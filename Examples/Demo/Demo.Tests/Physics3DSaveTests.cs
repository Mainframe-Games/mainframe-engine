using System.Numerics;
using System.Text.Json.Serialization;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>The Physics 3D save slots headless: save the crates, change the scene, load them back; old saves migrate.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class Physics3DSaveTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mf-demo-saves").FullName;
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly SaveServer _saves;
    private uint _frame;

    public Physics3DSaveTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _tree = new SceneTree(_servers);
        _saves = new SaveServer(new LocalSaveStorage(_folder), new SaveOptions(), _tree);
        _servers.Register(_saves);
        _tree.ChangeScene(Physics3DScene.Build());
        Tick();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Dropper3D Crates => _tree.CurrentScene!.GetNode<Dropper3D>("Crates");

    private Physics3DPanel Panel => _tree.CurrentScene!.GetNode<Physics3DPanel>("Ui/Panel");

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    private T Await<T>(Task<T> task)
    {
        for (var i = 0; i < 600 && !task.IsCompleted; i++)
        {
            Tick();
            Thread.Sleep(1);
        }

        Assert.True(task.IsCompleted, "the save did not complete");
        return task.Result;
    }

    [Fact]
    public void SavedCratesComeBackAfterTheSceneChanges()
    {
        Crates.Drop(new Vector3(2, 5, 1));
        for (var i = 0; i < 30; i++)
            Tick();
        Panel.SelectedSlot = 1;
        var saved = Crates.Snapshot(); // what SaveAsync serializes now (the crates keep moving while it writes)
        var result = Await(Panel.SaveAsync());
        Assert.Equal(SaveStatus.Ok, result.Status);
        Assert.Equal(16, saved.Crates.Count);
        Assert.Equal("16 crates", result.Info!.Description);

        // A fresh scene (15 crates in the starting stack), then load the slot.
        _tree.ChangeScene(Physics3DScene.Build());
        Tick();
        Assert.Equal(15, Crates.Count);
        Panel.SelectedSlot = 1;
        Assert.Equal(LoadStatus.Ok, Panel.Load());

        var loaded = Crates.Snapshot();
        Assert.Equal(saved.Spawned, loaded.Spawned);
        Assert.Equal(saved.Crates.Count, loaded.Crates.Count);
        for (var i = 0; i < saved.Crates.Count; i++)
        {
            Assert.Equal(saved.Crates[i].Position, loaded.Crates[i].Position);
            Assert.Equal(saved.Crates[i].Rotation, loaded.Crates[i].Rotation);
            Assert.Equal(saved.Crates[i].Color, loaded.Crates[i].Color);
        }

        Assert.Contains(_saves.ListSlots(), s => s.Slot == Physics3DPanel.SlotNames[1] && s.Description == "16 crates");
    }

    [Fact]
    public void AnEmptySlotSaysSo()
    {
        Panel.SelectedSlot = 2;
        Assert.Equal(LoadStatus.NotFound, Panel.Load());
        Assert.Equal(15, Crates.Count); // nothing changed
        Assert.Equal("This slot is empty.", Panel.Status);
    }

    [Fact]
    public void AVersion1SaveMigratesToTheCurrentSchema()
    {
        // What the first release wrote: crates only, no spawn counter.
        var v1 = new SaveSchema<CrateSaveV1>(V1Json.Default.CrateSaveV1, version: 1);
        var crates = new List<CrateState> { new(new Vector3(0, 3, 0), Quaternion.Identity, Vector3.Zero, 2), new(new Vector3(1, 3, 0), Quaternion.Identity, Vector3.Zero, 1) };
        Assert.Equal(SaveStatus.Ok, _saves.Save(Physics3DPanel.SlotNames[0], new CrateSaveV1(crates), v1).Status);

        Panel.SelectedSlot = 0;
        Assert.Equal(LoadStatus.Ok, Panel.Load());

        var loaded = Crates.Snapshot();
        Assert.Equal(2, loaded.Crates.Count);
        Assert.Equal(2, loaded.Spawned); // the migration counted the crates
        Assert.Equal([2, 1], loaded.Crates.Select(c => c.Color));
    }
}

public sealed record CrateSaveV1(List<CrateState> Crates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, IncludeFields = true)]
[JsonSerializable(typeof(CrateSaveV1))]
internal sealed partial class V1Json : JsonSerializerContext;

using System.Text.Json.Serialization;
using MainframeEngine;

namespace MyGame;

/// <summary>What a save holds: the game's own record. Add members freely; renaming or removing one needs a new schema
/// version in <see cref="SaveGame"/> and a JsonMigration that rewrites older saves.</summary>
public sealed record SaveData(int Level = 1, int Coins = 0);

/// <summary>Source-generated JSON for <see cref="SaveData"/>: no reflection, so saves work trimmed and under AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SaveData))]
internal sealed partial class SaveDataJson : JsonSerializerContext;

/// <summary>
/// An autoload (project.mfproj "autoloads"; from any node: <c>GetNode&lt;SaveGame&gt;("/root/SaveGame")</c>) that keeps
/// the game's <see cref="Data"/>: loaded at start, <see cref="Save"/>/<see cref="Load"/> over the engine's SaveServer
/// (<c>{user data}/saves/*.msave</c>, the editor's Project › Open User Data Folder) and an autosave when the player
/// closes the window.
/// </summary>
public class SaveGame : Node
{
    public const string Autosave = "autosave";

    private static readonly SaveSchema<SaveData> Schema = new(SaveDataJson.Default.SaveData, version: 1);

    public SaveData Data { get; set; } = new();

    private SaveServer Saves => Tree!.Servers.GetRequired<SaveServer>();

    /// <summary>Saves <see cref="Data"/> on the save worker (no hitch); the task completes on the main thread.</summary>
    public Task<SaveResult> Save(string slot = Autosave) => Saves.SaveAsync(slot, Data, Schema, Info);

    /// <summary>Loads <paramref name="slot"/> into <see cref="Data"/>; false (Data unchanged) when there is no usable save.</summary>
    public bool Load(string slot = Autosave)
    {
        var load = Saves.Load(slot, Schema);
        if (load.IsOk && load.Data is { } data)
            Data = data;
        else if (load.Status != LoadStatus.NotFound)
            Log.Warning($"[SaveGame] Could not load '{slot}': {load.Status} {load.Error}");
        return load.IsOk;
    }

    protected override void OnReady()
    {
        Load();
        Tree!.CloseRequested += OnCloseRequested;
    }

    protected override void OnExitTree()
    {
        Tree!.CloseRequested -= OnCloseRequested;
        Saves.Flush(TimeSpan.FromSeconds(2)); // queued saves reach the disk before the game exits
    }

    private SaveInfo Info => new($"Level {Data.Level}");

    // The window is closing and the tree is freed right after: save now, blocking.
    private void OnCloseRequested() => Saves.Save(Autosave, Data, Schema, Info);
}

using System.Numerics;
using System.Text.Json.Serialization;
using MainframeEngine;

namespace Demo;

/// <summary>One crate in a Physics 3D save: its pose, its velocity and its colour index.</summary>
public sealed record CrateState(Vector3 Position, Quaternion Rotation, Vector3 Velocity, int Color);

/// <summary>The Physics 3D save game: every crate and the spawn counter (crate names and colours continue from it).</summary>
public sealed record CrateSave(List<CrateState> Crates, int Spawned)
{
    /// <summary>
    /// Version 2 added <see cref="Spawned"/>; version-1 files migrate by counting their crates. Source-generated JSON, so
    /// the save stays trim- and AOT-safe.
    /// </summary>
    public static readonly SaveSchema<CrateSave> Schema = new(DemoSaveJson.Default.CrateSave, version: 2,
        new JsonMigration(1, static root => root["spawned"] ??= root["crates"]!.AsArray().Count));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, IncludeFields = true)] // Vector3 has fields
[JsonSerializable(typeof(CrateSave))]
internal sealed partial class DemoSaveJson : JsonSerializerContext;

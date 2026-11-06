using System.Globalization;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// One kind of object scattered on a terrain (G8a.9, docs/design/terrain.md#objects): rocks, stumps, trees, crates — things
/// that need collision, scripts or removal. List it in <see cref="TerrainData.ObjectTypes"/> (append new ones at the end:
/// instances store the index). Each instance is one of <see cref="Scenes"/> (variant = a hash of its id), placed on the
/// ground by its terrain. The placement fields steer the Terrain dock's Scatter brush; changing them moves nothing that is
/// already placed, except <see cref="SinkMeters"/>, which the terrain applies when it places each node.
/// </summary>
[EditorIcon("cube")]
public sealed class ScatterObjectType : Resource
{
    private int _version;

    /// <summary>Changes whenever a property changes (the terrain re-spawns its objects).</summary>
    public int Version => _version;

    /// <summary>The stable key games look the type up by (<see cref="TerrainData.ObjectTypeIndex"/>).</summary>
    [Export]
    public string Name { get; set => Set(ref field, value ?? ""); } = "";

    /// <summary>The variants: an instance shows <c>Scenes[hash(id) % Scenes.Length]</c>.</summary>
    [Export]
    public PackedScene[] Scenes { get; set => Set(ref field, value ?? []); } = [];

    /// <summary>Grid spacing of the Scatter brush (metres): at most one instance of the type per grid cell.</summary>
    [Export(Range = "0.25,64,0.05")]
    public float SpacingMeters { get; set => Set(ref field, Math.Max(0.25f, value)); } = 4f;

    /// <summary>Random offset within a grid cell (a fraction of the spacing, at most 0.45 so each instance stays in its cell).</summary>
    [Export(Range = "0,0.45,0.01")]
    public float Jitter { get; set => Set(ref field, Math.Clamp(value, 0f, 0.45f)); } = 0.4f;

    /// <summary>A random rotation about the up axis for each new instance.</summary>
    [Export]
    public bool RandomYaw { get; set => Set(ref field, value); } = true;

    /// <summary>Uniform scale range of new instances.</summary>
    [Export(Range = "0.05,10,0.01")]
    public float ScaleMin { get; set => Set(ref field, Math.Max(0.05f, value)); } = 0.9f;

    [Export(Range = "0.05,10,0.01")]
    public float ScaleMax { get; set => Set(ref field, Math.Max(0.05f, value)); } = 1.1f;

    /// <summary>How far an instance's base sits under the ground (metres × its scale), so it never floats on slopes.</summary>
    [Export(Range = "0,5,0.01")]
    public float SinkMeters { get; set => Set(ref field, Math.Max(0f, value)); }

    /// <summary>The Scatter brush skips ground steeper than this (degrees).</summary>
    [Export(Range = "0,90,0.5")]
    public float SlopeMaxDegrees { get; set => Set(ref field, value); } = 40f;

    /// <summary>The Scatter brush skips water.</summary>
    [Export]
    public bool AvoidWater { get; set => Set(ref field, value); } = true;

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// One scattered object (terrain-local metres; Y follows the ground, so it is not stored). <paramref name="Id"/> is stable:
/// a saved id always means the same object.
/// </summary>
/// <param name="Id">Unique within the terrain, from 1; never reused.</param>
/// <param name="Type">The index in <see cref="TerrainData.ObjectTypes"/>.</param>
/// <param name="X">Terrain-local X (metres from the map corner).</param>
/// <param name="Z">Terrain-local Z.</param>
/// <param name="YawDegrees">Rotation about up.</param>
/// <param name="Scale">Uniform scale.</param>
public readonly record struct TerrainObject(int Id, int Type, float X, float Z, float YawDegrees, float Scale);

/// <summary>
/// Implemented by a scattered scene's root to learn its object id and terrain (both set before it enters the tree), so
/// game code can remove it (<see cref="Terrain3D.RemoveObject"/>) or keep its state by id in a save game.
/// </summary>
public interface ITerrainObject
{
    /// <summary>The object's id (<see cref="TerrainObject.Id"/>).</summary>
    int TerrainObjectId { get; set; }

    /// <summary>The terrain that spawned it.</summary>
    Terrain3D? Terrain { get; set; }
}

/// <summary>A change to a terrain's object list (<see cref="TerrainData.ObjectsChanged"/>): an added or removed instance.</summary>
public readonly record struct TerrainObjectChange(TerrainObject Instance, bool Added);

/// <summary>The objects file (<c>objects.json</c> beside the layers): the next id and every instance.</summary>
internal static class TerrainObjectFile
{
    public const string FileName = "objects.json";

    // [id, type, x, z, yaw, scale] per object: compact, and still readable in a diff.
    public static void Write(Stream stream, int nextId, IReadOnlyList<TerrainObject> objects)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("format", 1);
        writer.WriteNumber("nextId", nextId);
        writer.WriteStartArray("objects");
        foreach (var o in objects)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(o.Id);
            writer.WriteNumberValue(o.Type);
            writer.WriteNumberValue(o.X);
            writer.WriteNumberValue(o.Z);
            writer.WriteNumberValue(o.YawDegrees);
            writer.WriteNumberValue(o.Scale);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static (int NextId, List<TerrainObject> Objects) Read(Stream stream)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var next = root.GetProperty("nextId").GetInt32();
            var list = new List<TerrainObject>();
            var ids = new HashSet<int>();
            foreach (var e in root.GetProperty("objects").EnumerateArray())
            {
                var o = new TerrainObject(e[0].GetInt32(), e[1].GetInt32(), e[2].GetSingle(), e[3].GetSingle(), e[4].GetSingle(), e[5].GetSingle());
                if (o.Id <= 0 || o.Id >= next || o.Type < 0 || !ids.Add(o.Id))
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                        $"Object id {o.Id} (type {o.Type}) is not unique, positive and below nextId {next}."));
                list.Add(o);
            }

            return (Math.Max(next, 1), list);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"Unreadable terrain objects file: {e.Message}", e);
        }
    }
}

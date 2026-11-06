using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

// G8a.9 (docs/design/terrain.md#objects): each scattered object is its scene, instanced under an unowned "Objects"
// container (never saved) on the ground at (X, HeightAt − SinkMeters × scale, Z). A scene root implementing
// ITerrainObject gets its id and terrain before it enters the tree. Height and water edits put the instances in the
// touched area back on the ground; a change to the type list or a type re-spawns them all.
public sealed partial class Terrain3D
{
    private readonly Dictionary<int, Node3D> _objectNodes = [];
    private Action<TerrainObjectChange>? _onObjectsChanged;
    private Node3D? _objectsRoot;
    private int _objectsVersion;
    private int _objectTypesStamp;

    /// <summary>The number of scattered objects (<see cref="TerrainData.Objects"/>).</summary>
    public int ObjectCount => _data?.Objects.Count ?? 0;

    /// <summary>The unowned container of the object nodes (null before the terrain is built).</summary>
    public Node3D? ObjectsRoot => _objectsRoot;

    /// <summary>Object <paramref name="id"/>'s node, or null (no such object, no scene for its type, or not built yet).</summary>
    public Node3D? GetObjectNode(int id) => _objectNodes.GetValueOrDefault(id);

    /// <summary>
    /// Removes object <paramref name="id"/> from the data; its node is freed at the end of the frame (safe from the
    /// object's own script). False when there is none.
    /// </summary>
    public bool RemoveObject(int id) => _data is { } data && data.RemoveObject(id);

    // From Build, after the foliage child.
    private void SpawnObjects()
    {
        var data = _data!;
        _objectsRoot = new Node3D { Name = "Objects" };
        AddChild(_objectsRoot); // unowned: generated, never saved
        foreach (var o in data.Objects)
            SpawnObject(data, o);
        _objectsVersion = data.ObjectsVersion;
        _objectTypesStamp = ObjectTypesStamp(data);
    }

    // From FreeChunks.
    private void FreeObjects()
    {
        _objectNodes.Clear();
        _objectsRoot?.Free();
        _objectsRoot = null;
    }

    private void RespawnObjects()
    {
        FreeObjects();
        SpawnObjects();
    }

    private void SubscribeObjects(TerrainData data)
    {
        _onObjectsChanged ??= OnObjectsChanged;
        data.ObjectsChanged += _onObjectsChanged;
    }

    private void UnsubscribeObjects(TerrainData data)
    {
        if (_onObjectsChanged is not null)
            data.ObjectsChanged -= _onObjectsChanged;
    }

    // Entering the tree, and every frame: catches up with changes made while the terrain was out of the tree, and with
    // edits to the type list (an added scene variant, a new sink depth). Allocation-free when nothing changed.
    private void SyncObjects()
    {
        if (_data is not { } data || _objectsRoot is null)
            return;
        if (_objectsVersion != data.ObjectsVersion || _objectTypesStamp != ObjectTypesStamp(data))
            RespawnObjects();
    }

    private static int ObjectTypesStamp(TerrainData data)
    {
        var hash = new HashCode();
        hash.Add(data.ObjectTypes);
        foreach (var type in data.ObjectTypes)
        {
            hash.Add(type);
            hash.Add(type?.Version ?? 0);
        }

        return hash.ToHashCode();
    }

    private void OnObjectsChanged(TerrainObjectChange change)
    {
        if (_data is not { } data || _objectsRoot is null)
            return;
        if (change.Added)
            SpawnObject(data, change.Instance);
        else if (_objectNodes.Remove(change.Instance.Id, out var node))
            node.QueueFree(); // the object's own script may be the caller
        _objectsVersion = data.ObjectsVersion;
    }

    private void SpawnObject(TerrainData data, TerrainObject o)
    {
        if ((uint)o.Type >= (uint)data.ObjectTypes.Length || data.ObjectTypes[o.Type] is not { Scenes.Length: > 0 } type)
            return;
        var scene = type.Scenes[(int)(VariantHash(o.Id) % (uint)type.Scenes.Length)];
        if (scene?.Instantiate() is not Node3D node)
            return;
        if (node is ITerrainObject terrainObject)
        {
            terrainObject.TerrainObjectId = o.Id;
            terrainObject.Terrain = this;
        }

        node.Name = "Object" + o.Id.ToString(CultureInfo.InvariantCulture);
        PlaceObject(data, type, o, node);
        _objectNodes[o.Id] = node;
        _objectsRoot!.AddChild(node);
    }

    private static void PlaceObject(TerrainData data, ScatterObjectType type, TerrainObject o, Node3D node)
    {
        var height = data.Grid.HeightAt(data.BedHeights, o.X, o.Z);
        node.Position = new Vector3(o.X, height - type.SinkMeters * o.Scale, o.Z);
        node.RotationDegrees = new Vector3(0f, o.YawDegrees, 0f);
        node.Scale = new Vector3(o.Scale);
    }

    // After a height or water edit of vertex rectangle `rect`: instances over its triangles go back onto the ground.
    private void ReheightObjects(Rect2I rect)
    {
        if (_data is not { } data || _objectNodes.Count == 0)
            return;
        var s = data.VertexSpacing;
        float x0 = (rect.Position.X - 1) * s, z0 = (rect.Position.Y - 1) * s, x1 = rect.End.X * s, z1 = rect.End.Y * s;
        var types = data.ObjectTypes;
        foreach (var o in data.Objects)
            if (o.X >= x0 && o.X <= x1 && o.Z >= z0 && o.Z <= z1 && _objectNodes.TryGetValue(o.Id, out var node) &&
                (uint)o.Type < (uint)types.Length && types[o.Type] is { } type)
                PlaceObject(data, type, o, node);
    }

    /// <summary>Which of a type's scenes object <paramref name="id"/> shows: <c>VariantHash(id) % Scenes.Length</c>.</summary>
    public static uint VariantHash(int id)
    {
        var x = (uint)id * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        return x ^ (x >> 13);
    }
}

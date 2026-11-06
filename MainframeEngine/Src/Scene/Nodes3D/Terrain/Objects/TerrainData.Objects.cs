namespace MainframeEngine;

// G8a.9 (docs/design/terrain.md#objects): the scattered objects: a list with stable ids, saved as objects.json beside the
// layers. Y is not stored: the terrain puts every instance on the ground (and back on it after height edits).
public sealed partial class TerrainData
{
    private List<TerrainObject>? _objects;
    private int _nextObjectId = 1;
    private int _objectsVersion;
    private bool _objectsDirty;

    /// <summary>
    /// The kinds of scattered objects (rocks, stumps, crates: scenes with collision or scripts). Instances store the index:
    /// append new types at the end.
    /// </summary>
    [Export]
    public ScatterObjectType[] ObjectTypes { get; set => field = value ?? []; } = [];

    /// <summary>Every scattered object, in the order they were added (read from <c>objects.json</c> on first use).</summary>
    public IReadOnlyList<TerrainObject> Objects => LoadObjects();

    /// <summary>The id the next added object gets (ids start at 1 and are never reused).</summary>
    public int NextObjectId
    {
        get
        {
            LoadObjects();
            return _nextObjectId;
        }
    }

    /// <summary>Bumped by every add and removal.</summary>
    internal int ObjectsVersion => _objectsVersion;

    /// <summary>Raised after an object is added or removed (the terrain spawns or frees its node).</summary>
    public event Action<TerrainObjectChange>? ObjectsChanged;

    /// <summary>The index of the object type named <paramref name="name"/> (<see cref="ScatterObjectType.Name"/>), or −1.</summary>
    public int ObjectTypeIndex(string name)
    {
        for (var i = 0; i < ObjectTypes.Length; i++)
            if (ObjectTypes[i] is { } type && string.Equals(type.Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>
    /// Adds an object of type <paramref name="type"/> (an index in <see cref="ObjectTypes"/>) at terrain-local
    /// (<paramref name="x"/>, <paramref name="z"/>) metres; returns its new id.
    /// </summary>
    public int AddObject(int type, float x, float z, float yawDegrees = 0f, float scale = 1f)
    {
        if (type < 0)
            throw new ArgumentOutOfRangeException(nameof(type), type, "Object types are indices from 0.");
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(yawDegrees) || !(scale > 0f) || !float.IsFinite(scale))
            throw new ArgumentException("An object needs a finite position and yaw and a positive scale.");
        var list = LoadForWrite();
        var o = new TerrainObject(_nextObjectId++, type, x, z, yawDegrees, scale);
        list.Add(o);
        RaiseObjectsChanged(new TerrainObjectChange(o, Added: true));
        return o.Id;
    }

    /// <summary>
    /// Puts back an object with its own id (undo of a removal, redo of an add): false when that id is already there. The next
    /// id never moves back.
    /// </summary>
    public bool RestoreObject(TerrainObject o)
    {
        if (o.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(o), o.Id, "Object ids start at 1.");
        var list = LoadForWrite();
        if (IndexOfObject(o.Id) >= 0)
            return false;
        list.Add(o);
        _nextObjectId = Math.Max(_nextObjectId, o.Id + 1);
        RaiseObjectsChanged(new TerrainObjectChange(o, Added: true));
        return true;
    }

    /// <summary>Removes object <paramref name="id"/>; false when there is none. Allocation-free.</summary>
    public bool RemoveObject(int id)
    {
        var list = LoadForWrite();
        var index = IndexOfObject(id);
        if (index < 0)
            return false;
        var o = list[index];
        list.RemoveAt(index);
        RaiseObjectsChanged(new TerrainObjectChange(o, Added: false));
        return true;
    }

    /// <summary>Object <paramref name="id"/>, or null.</summary>
    public TerrainObject? GetObject(int id)
    {
        var index = IndexOfObject(id);
        return index >= 0 ? _objects![index] : null;
    }

    private void RaiseObjectsChanged(TerrainObjectChange change)
    {
        _objectsDirty = true;
        _objectsVersion++;
        ObjectsChanged?.Invoke(change);
        EmitChanged();
    }

    private int IndexOfObject(int id)
    {
        var list = LoadObjects();
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return -1;
    }

    // Edits load the layers too, so SaveLayers (which skips data that was never loaded) writes the list.
    private List<TerrainObject> LoadForWrite()
    {
        EnsureLoaded();
        return LoadObjects();
    }

    private List<TerrainObject> LoadObjects()
    {
        if (_objects is not null)
            return _objects;
        var path = LayerFolder is { } folder ? Path.Combine(folder, TerrainObjectFile.FileName) : null;
        if (path is not null && ContentFiles.Exists(path))
        {
            using var stream = ContentFiles.OpenRead(path);
            var (next, objects) = TerrainObjectFile.Read(stream);
            _nextObjectId = next;
            return _objects = objects;
        }

        return _objects = [];
    }

    // From SaveLayers: writes the list when it changed (or everything goes into a new folder). A terrain that never had an
    // object leaves no file; one whose objects were all removed keeps the file for its next id, so no id is ever reused.
    private void SaveObjects(string folder, bool everything)
    {
        if (_objects is null && everything)
            LoadObjects(); // moving folders: carry the list over from the old one (LayerFolder is still the old folder here)
        if (_objects is null || (!_objectsDirty && !everything))
            return;
        var path = Path.Combine(folder, TerrainObjectFile.FileName);
        if (_objects.Count == 0 && _nextObjectId == 1)
            DeleteIfExists(path);
        else
            Atomically(path, temp =>
            {
                using var stream = File.Create(temp); // disk-io: an editor save
                TerrainObjectFile.Write(stream, _nextObjectId, _objects);
            });
        _objectsDirty = false;
    }
}

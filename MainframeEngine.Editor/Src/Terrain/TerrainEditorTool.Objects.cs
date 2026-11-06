using System.Globalization;
using System.Numerics;
using System.Text;

namespace MainframeEngine.Editor;

/// <summary>What the Objects mode's left button does.</summary>
public enum TerrainObjectTool : byte
{
    /// <summary>Fills the grid cells under the brush (repainting adds nothing; Shift removes the type under the brush).</summary>
    Scatter,

    /// <summary>One click, one object at the pointer (Shift removes the nearest one of the type under the brush).</summary>
    Place,
}

// G8a.9: the Terrain dock's Objects mode (docs/design/terrain.md#objects). A Scatter stroke adds an object of the chosen
// ScatterObjectType in each grid cell (the type's SpacingMeters) under the brush whose hash is below the brush weight and
// that holds none of that type yet, so painting over the same ground adds nothing; Shift removes the type's objects under
// the brush. Every stroke or click is one TerrainObjectsAction (the added and removed objects, put back with their ids).
public sealed partial class TerrainEditorTool
{
    /// <summary>Above this many objects the dock warns (each is a scene instance: a few thousand get slow).</summary>
    public const int ObjectWarningCount = 2000;

    private readonly List<TerrainObject> _addedObjects = [];
    private readonly List<TerrainObject> _removedObjects = [];
    private readonly HashSet<(int X, int Z)> _occupied = [];
    private bool _objectStroke;
    private bool _objectRemove;

    /// <summary>The object type (index in <see cref="TerrainData.ObjectTypes"/>) the Objects mode places and removes.</summary>
    public int ObjectType
    {
        get;
        set
        {
            field = Math.Max(0, value);
            Touch();
        }
    }

    public TerrainObjectTool ObjectTool
    {
        get;
        set
        {
            field = value;
            Touch();
        }
    }

    private ScatterObjectType? ChosenObjectType(TerrainData data) =>
        (uint)ObjectType < (uint)data.ObjectTypes.Length ? data.ObjectTypes[ObjectType] : null;

    private void PressObjects(Terrain3D terrain, Vector2 local)
    {
        var data = terrain.Data!;
        if (ChosenObjectType(data) is not { } type)
            return;
        _addedObjects.Clear();
        _removedObjects.Clear();
        _objectRemove = Inverted;
        if (ObjectTool == TerrainObjectTool.Place)
        {
            if (_objectRemove)
                RemoveNearestObject(data, local);
            else
                PlaceObject(data, type, local);
            CommitObjects(terrain, _objectRemove ? "Terrain Remove Object" : "Terrain Add Object");
            return;
        }

        _objectStroke = true;
        if (!_objectRemove)
            FillOccupancy(data, type);
        StampObjects(terrain, _stroke.Begin(local));
    }

    private void PlaceObject(TerrainData data, ScatterObjectType type, Vector2 local)
    {
        var size = data.Grid.Size;
        var x = Math.Clamp(local.X, 0f, size);
        var z = Math.Clamp(local.Y, 0f, size);
        var seed = (uint)data.NextObjectId;
        var yaw = type.RandomYaw ? Hash01(seed, 11u, 0u) * 360f : 0f;
        var scale = float.Lerp(type.ScaleMin, type.ScaleMax, Hash01(seed, 7u, 0u));
        var id = data.AddObject(ObjectType, x, z, yaw, scale);
        _addedObjects.Add(new TerrainObject(id, ObjectType, x, z, yaw, scale));
    }

    // Shift+click with Place: the chosen type's object nearest the pointer, within the brush.
    private void RemoveNearestObject(TerrainData data, Vector2 p)
    {
        var objects = data.Objects;
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            var d = Vector2.DistanceSquared(new Vector2(o.X, o.Z), p);
            if (o.Type == ObjectType && d < bestDistance && Brush.Shape01(o.X - p.X, o.Z - p.Y) > 0f)
            {
                best = i;
                bestDistance = d;
            }
        }

        if (best >= 0)
        {
            var o = objects[best];
            if (data.RemoveObject(o.Id))
                _removedObjects.Add(o);
        }
    }

    // The grid cells that already hold an object of the type (jitter ≤ 0.45 keeps every instance inside its own cell).
    private void FillOccupancy(TerrainData data, ScatterObjectType type)
    {
        _occupied.Clear();
        var spacing = type.SpacingMeters;
        foreach (var o in data.Objects)
            if (o.Type == ObjectType)
                _occupied.Add(((int)MathF.Floor(o.X / spacing), (int)MathF.Floor(o.Z / spacing)));
    }

    private void StrokeObjectsTo(Terrain3D terrain, Vector2 local)
    {
        _stamps.Clear();
        _stroke.MoveTo(local, Brush.StampDistance, _stamps);
        foreach (var stamp in _stamps)
            StampObjects(terrain, stamp);
    }

    private void StampObjects(Terrain3D terrain, Vector2 p)
    {
        var data = terrain.Data!;
        if (ChosenObjectType(data) is not { } type)
            return;
        if (_objectRemove)
        {
            // The type's objects under the brush, from the end (removal shifts the later ones down).
            var objects = data.Objects;
            for (var i = objects.Count - 1; i >= 0; i--)
            {
                var o = objects[i];
                if (o.Type == ObjectType && Brush.Shape01(o.X - p.X, o.Z - p.Y) > 0f && data.RemoveObject(o.Id))
                    _removedObjects.Add(o);
            }

            return;
        }

        var r = Brush.Radius;
        var spacing = type.SpacingMeters;
        var last = (int)MathF.Floor(data.Grid.Size / spacing) - 1; // whole cells only
        var gx0 = Math.Max(0, (int)MathF.Floor((p.X - r) / spacing));
        var gz0 = Math.Max(0, (int)MathF.Floor((p.Y - r) / spacing));
        var gx1 = Math.Min(last, (int)MathF.Floor((p.X + r) / spacing));
        var gz1 = Math.Min(last, (int)MathF.Floor((p.Y + r) / spacing));
        var origin = terrain.GlobalPosition;
        for (var gz = gz0; gz <= gz1; gz++)
            for (var gx = gx0; gx <= gx1; gx++)
            {
                if (_occupied.Contains((gx, gz)))
                    continue;
                var seed = (uint)gx * 73856093u ^ (uint)gz * 19349663u ^ (uint)ObjectType * 83492791u;
                var x = (gx + 0.5f + (Hash01(seed, 1u, 0u) - 0.5f) * 2f * type.Jitter) * spacing;
                var z = (gz + 0.5f + (Hash01(seed, 2u, 0u) - 0.5f) * 2f * type.Jitter) * spacing;
                if (Brush.Shape01(x - p.X, z - p.Y) * Brush.Strength <= Hash01(seed, 3u, 0u))
                    continue;
                var n = terrain.NormalAt(origin.X + x, origin.Z + z);
                var slope = MathF.Acos(Math.Clamp(n.Y, -1f, 1f)) * (180f / MathF.PI);
                if (slope > type.SlopeMaxDegrees || !Brush.SlopePasses(slope))
                    continue;
                if (type.AvoidWater && terrain.WaterDepthAt(origin.X + x, origin.Z + z) > 0f)
                    continue;
                var yaw = type.RandomYaw ? Hash01(seed, 4u, 0u) * 360f : 0f;
                var scale = float.Lerp(type.ScaleMin, type.ScaleMax, Hash01(seed, 5u, 0u));
                var id = data.AddObject(ObjectType, x, z, yaw, scale);
                _addedObjects.Add(new TerrainObject(id, ObjectType, x, z, yaw, scale));
                _occupied.Add((gx, gz));
            }
    }

    private void EndObjectStroke(Terrain3D terrain)
    {
        if (!_objectStroke)
            return;
        _objectStroke = false;
        _stroke.End();
        _occupied.Clear();
        CommitObjects(terrain, _objectRemove ? "Terrain Remove Objects" : "Terrain Scatter Objects");
    }

    private void CommitObjects(Terrain3D terrain, string name)
    {
        if (_context is not null && (_addedObjects.Count > 0 || _removedObjects.Count > 0))
            _context.History.Commit(new TerrainObjectsAction(terrain, name, [.. _addedObjects], [.. _removedObjects]), alreadyApplied: true);
        _addedObjects.Clear();
        _removedObjects.Clear();
        Touch();
    }

    // Cmd/Ctrl+click: the type of the object nearest the pointer (within the brush) becomes the chosen one.
    private void PickObjectType(TerrainData data, Vector2 p)
    {
        var bestDistance = float.MaxValue;
        foreach (var o in data.Objects)
        {
            var d = Vector2.DistanceSquared(new Vector2(o.X, o.Z), p);
            if (d < bestDistance && Brush.Shape01(o.X - p.X, o.Z - p.Y) > 0f)
            {
                bestDistance = d;
                ObjectType = o.Type;
            }
        }
    }

    private void AppendObjects(StringBuilder rml, TerrainData data)
    {
        rml.Append("<div class=\"td-tools\">");
        IconButton(rml, "object-tool", nameof(TerrainObjectTool.Scatter), "grain",
            "Scatter — fill the grid under the brush (repainting adds nothing; Shift removes the type)", ObjectTool == TerrainObjectTool.Scatter);
        IconButton(rml, "object-tool", nameof(TerrainObjectTool.Place), "target",
            "Place — one object where you click (Shift removes the nearest)", ObjectTool == TerrainObjectTool.Place);
        rml.Append("</div><div class=\"td-head\">Object types</div><div class=\"td-layers\">");
        var types = data.ObjectTypes;
        var objects = data.Objects;
        Span<int> counts = types.Length <= 64 ? stackalloc int[types.Length] : new int[types.Length];
        foreach (var o in objects)
            if ((uint)o.Type < (uint)counts.Length)
                counts[o.Type]++;
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            var name = type is { Name.Length: > 0 } ? type.Name : type is { ResourceName.Length: > 0 } ? type.ResourceName : "Type " + i;
            var scenes = type?.Scenes.Length ?? 0;
            var tooltip = name + " — " + scenes + " scene variant" + (scenes == 1 ? "" : "s") +
                          (type is null ? "" : ", one per " + Num(type.SpacingMeters) + " m");
            rml.Append("<div class=\"td-swatch").Append(i == ObjectType ? " active" : "").Append("\" data-tool-action=\"object\" data-tool-value=\"")
                .Append(i).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-cube icon-sm\"></span>")
                .Append("<span class=\"td-name\">").Append(RmlText.Escape(name)).Append("</span><span class=\"td-value\">")
                .Append(counts[i].ToString(CultureInfo.InvariantCulture)).Append("</span></div>");
        }

        rml.Append("</div>");
        if (types.Length == 0)
            rml.Append("<div class=\"td-hint\">Add Object Types (scenes to scatter) to the terrain data in the Inspector.</div>");
        var over = objects.Count > ObjectWarningCount;
        rml.Append("<div class=\"td-hint").Append(over ? " td-over" : "").Append("\">").Append(objects.Count.ToString("N0", CultureInfo.InvariantCulture))
            .Append(objects.Count == 1 ? " object" : " objects")
            .Append(over ? " — more than about 2,000 scene instances get slow" : "").Append("</div>");
    }

    private static float Hash01(uint a, uint b, uint c)
    {
        var x = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c * 0xC2B2AE3Du;
        x ^= x >> 15;
        x *= 0x2C1B3C6Du;
        x ^= x >> 12;
        x *= 0x297A2D39u;
        x ^= x >> 15;
        return (x >> 8) * (1f / 16777216f);
    }
}

/// <summary>
/// A Scatter stroke, a removal or a placement as one history entry: the added and removed objects, put back with
/// their own ids (so a game's saved id still means the same object after undo and redo).
/// </summary>
public sealed class TerrainObjectsAction(Terrain3D terrain, string name, TerrainObject[] added, TerrainObject[] removed) : IEditorAction
{
    public Terrain3D Terrain { get; } = terrain;

    public string Name { get; } = name;

    public IReadOnlyList<TerrainObject> Added { get; } = added;

    public IReadOnlyList<TerrainObject> Removed { get; } = removed;

    public void Do()
    {
        if (Terrain.Data is not { } data)
            return;
        foreach (var o in added)
            data.RestoreObject(o);
        foreach (var o in removed)
            data.RemoveObject(o.Id);
    }

    public void Undo()
    {
        if (Terrain.Data is not { } data)
            return;
        foreach (var o in added)
            data.RemoveObject(o.Id);
        foreach (var o in removed)
            data.RestoreObject(o);
    }
}

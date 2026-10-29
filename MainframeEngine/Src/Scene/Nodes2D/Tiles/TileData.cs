using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A collision polygon of a tile (Godot's per-physics-layer polygons of <c>TileData</c>): points in tile-local pixels
/// with the origin at the tile's centre, on one of the tile set's <see cref="TileSet.PhysicsLayers"/>.
/// </summary>
[EditorIcon("polygon")]
public sealed class TileCollisionPolygon : Resource
{
    private int _physicsLayer;
    private Vector2[] _points = [];

    /// <summary>The index of the <see cref="TileSetPhysicsLayer"/> this polygon belongs to.</summary>
    [Export(Range = "0,31,1")]
    public int PhysicsLayer
    {
        get => _physicsLayer;
        set
        {
            if (_physicsLayer == value)
                return;
            _physicsLayer = Math.Max(0, value);
            EmitChanged();
        }
    }

    /// <summary>The outline (tile-local pixels, origin at the tile centre, as Godot).</summary>
    [Export]
    public Vector2[] Points
    {
        get => _points;
        set
        {
            _points = value ?? [];
            EmitChanged();
        }
    }

    /// <summary>A full-tile square for <paramref name="tileSize"/> on <paramref name="physicsLayer"/>.</summary>
    public static TileCollisionPolygon FullTile(Vector2I tileSize, int physicsLayer = 0)
    {
        var h = new Vector2(tileSize.X, tileSize.Y) * 0.5f;
        return new TileCollisionPolygon { PhysicsLayer = physicsLayer, Points = [new(-h.X, -h.Y), new(h.X, -h.Y), new(h.X, h.Y), new(-h.X, h.Y)] };
    }
}

/// <summary>
/// The data of one tile or alternative tile (Godot's <c>TileData</c>): texture origin, modulate, z index, y-sort
/// origin, the random-paint probability, collision polygons and one custom data value per
/// <see cref="TileSet.CustomDataLayers"/> entry. A <see cref="TileSetAtlasSource"/> stores one only for tiles with
/// non-default values (<see cref="TileSetAtlasSource.TileOverrides"/>).
/// </summary>
/// <remarks>
/// Custom data is stored as invariant text aligned to the layers (the engine has no <c>Variant</c>, and an
/// <c>object</c> export would box): the typed getters parse a value once, cache it and then allocate nothing.
/// </remarks>
[EditorIcon("square")]
public sealed class TileData : Resource
{
    /// <summary>The values every tile without an override has (never edited: use <see cref="TileSetAtlasSource.GetOrCreateTileData"/>).</summary>
    internal static readonly TileData Default = new();

    private Vector2I _atlasCoords;
    private int _alternativeId;
    private Vector2I _textureOrigin;
    private Vector4 _modulate = Vector4.One;
    private int _zIndex;
    private int _ySortOrigin;
    private float _probability = 1f;
    private List<TileCollisionPolygon> _collision = [];
    private string[] _customData = [];
    private CustomValue[]? _parsed;

    private struct CustomValue
    {
        public byte Kinds;      // bit per CustomDataType parsed
        public long Int;
        public double Float;
        public Vector4 Vector;  // Vector2 in XY, Color in XYZW
        public bool Bool;
    }

    /// <summary>The tile this data belongs to.</summary>
    [Export]
    public Vector2I AtlasCoords
    {
        get => _atlasCoords;
        set => Set(ref _atlasCoords, value);
    }

    /// <summary>0 for the base tile, 1…4095 for an alternative tile.</summary>
    [Export(Range = "0,4095,1")]
    public int AlternativeId
    {
        get => _alternativeId;
        set => Set(ref _alternativeId, value & TileSetAtlasSource.AlternativeMask);
    }

    /// <summary>Shifts the drawn texture from the cell (pixels; positive moves it up and left, as Godot's).</summary>
    [Export]
    public Vector2I TextureOrigin
    {
        get => _textureOrigin;
        set => Set(ref _textureOrigin, value);
    }

    /// <summary>Colour multiplied into the tile.</summary>
    [Export]
    public Vector4 Modulate
    {
        get => _modulate;
        set => Set(ref _modulate, value);
    }

    /// <summary>Added to the layer's z index (tiles with one draw in their own chunk sub-item).</summary>
    [Export(Range = "-4096,4096,1")]
    public int ZIndex
    {
        get => _zIndex;
        set => Set(ref _zIndex, value);
    }

    /// <summary>Added to the cell's y when the layer is y-sorted (pixels; a tree's trunk row sorts at its base).</summary>
    [Export]
    public int YSortOrigin
    {
        get => _ySortOrigin;
        set => Set(ref _ySortOrigin, value);
    }

    /// <summary>The relative weight of this tile when the paint tools pick among several (random scatter).</summary>
    [Export(Range = "0,1000,0.01")]
    public float Probability
    {
        get => _probability;
        set => Set(ref _probability, MathF.Max(0f, value));
    }

    /// <summary>Collision polygons, each on one physics layer.</summary>
    [Export]
    public List<TileCollisionPolygon> Collision
    {
        get => _collision;
        set
        {
            Unsubscribe(_collision);
            _collision = value ?? [];
            Subscribe(_collision);
            EmitChanged();
        }
    }

    /// <summary>One invariant-text value per custom data layer ("" or missing = the type's default).</summary>
    [Export]
    public string[] CustomData
    {
        get => _customData;
        set
        {
            _customData = value ?? [];
            _parsed = null;
            EmitChanged();
        }
    }

    /// <summary>True when the data equals a tile's defaults (no collision, no custom data).</summary>
    public bool IsDefault =>
        _textureOrigin == default && _modulate == Vector4.One && _zIndex == 0 && _ySortOrigin == 0 && _probability == 1f &&
        _collision.Count == 0 && Array.TrueForAll(_customData, string.IsNullOrEmpty);

    /// <summary>Raises <see cref="Resource.Changed"/> after <see cref="Collision"/> was edited in place.</summary>
    public void NotifyChanged() => EmitChanged();

    /// <summary>Adds a collision polygon (pixels around the tile centre) on <paramref name="physicsLayer"/>.</summary>
    public TileCollisionPolygon AddCollisionPolygon(int physicsLayer, ReadOnlySpan<Vector2> points)
    {
        var polygon = new TileCollisionPolygon { PhysicsLayer = physicsLayer, Points = points.ToArray() };
        polygon.Changed += OnChildChanged;
        _collision.Add(polygon);
        EmitChanged();
        return polygon;
    }

    /// <summary>The number of polygons on <paramref name="physicsLayer"/>.</summary>
    public int GetCollisionPolygonsCount(int physicsLayer)
    {
        var count = 0;
        foreach (var polygon in _collision)
            if (polygon.PhysicsLayer == physicsLayer)
                count++;
        return count;
    }

    // ── Custom data ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The raw text of custom data layer <paramref name="layer"/> ("" when unset).</summary>
    public string GetCustomString(int layer) => (uint)layer < (uint)_customData.Length ? _customData[layer] ?? "" : "";

    public bool GetCustomBool(int layer)
    {
        return Parsed(layer, CustomDataType.Bool).Bool;
    }

    public long GetCustomInt(int layer)
    {
        return Parsed(layer, CustomDataType.Int).Int;
    }

    public double GetCustomFloat(int layer)
    {
        return Parsed(layer, CustomDataType.Float).Float;
    }

    public Vector2 GetCustomVector2(int layer)
    {
        var v = Parsed(layer, CustomDataType.Vector2).Vector;
        return new Vector2(v.X, v.Y);
    }

    /// <summary>A colour value (RGBA; Godot's default <c>Color()</c> is opaque black).</summary>
    public Vector4 GetCustomColor(int layer)
    {
        return Parsed(layer, CustomDataType.Color).Vector;
    }

    /// <summary>Sets custom data layer <paramref name="layer"/> to <paramref name="text"/> (invariant text).</summary>
    public void SetCustomData(int layer, string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        var values = _customData;
        if (layer >= values.Length)
        {
            var grown = new string[layer + 1];
            values.CopyTo(grown, 0);
            for (var i = values.Length; i < grown.Length; i++)
                grown[i] = "";
            values = grown;
        }
        else
        {
            values = (string[])values.Clone();
        }

        values[layer] = text ?? "";
        CustomData = values;
    }

    public void SetCustomData(int layer, bool value) => SetCustomData(layer, value ? "true" : "false");

    public void SetCustomData(int layer, long value) => SetCustomData(layer, value.ToString(CultureInfo.InvariantCulture));

    public void SetCustomData(int layer, double value) => SetCustomData(layer, value.ToString("R", CultureInfo.InvariantCulture));

    public void SetCustomData(int layer, Vector2 value) =>
        SetCustomData(layer, string.Create(CultureInfo.InvariantCulture, $"{value.X},{value.Y}"));

    public void SetCustomData(int layer, Vector4 color) =>
        SetCustomData(layer, string.Create(CultureInfo.InvariantCulture, $"{color.X},{color.Y},{color.Z},{color.W}"));

    private CustomValue Parsed(int layer, CustomDataType type)
    {
        if ((uint)layer >= (uint)_customData.Length)
            return new CustomValue { Vector = type == CustomDataType.Color ? new Vector4(0, 0, 0, 1) : Vector4.Zero };

        _parsed ??= new CustomValue[_customData.Length];
        ref var v = ref _parsed[layer];
        var bit = (byte)(1 << (int)type);
        if ((v.Kinds & bit) != 0)
            return v;
        v.Kinds |= bit;
        var text = (_customData[layer] ?? "").AsSpan().Trim();
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case CustomDataType.Bool:
                v.Bool = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("1");
                break;
            case CustomDataType.Int:
                v.Int = long.TryParse(text, NumberStyles.Integer, inv, out var l) ? l
                    : double.TryParse(text, NumberStyles.Float, inv, out var d) ? (long)d : 0;
                break;
            case CustomDataType.Float:
                v.Float = double.TryParse(text, NumberStyles.Float, inv, out var f) ? f : 0;
                break;
            case CustomDataType.Vector2:
            case CustomDataType.Color:
                {
                    Span<float> c = [0, 0, 0, type == CustomDataType.Color ? 1 : 0];
                    var index = 0;
                    foreach (var range in text.Split(','))
                    {
                        if (index >= 4)
                            break;
                        if (float.TryParse(text[range].Trim(), NumberStyles.Float, inv, out var component))
                            c[index] = component;
                        index++;
                    }

                    v.Vector = new Vector4(c[0], c[1], c[2], c[3]);
                    break;
                }
        }

        return v;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        EmitChanged();
    }

    private void Subscribe(List<TileCollisionPolygon> list)
    {
        foreach (var polygon in list)
            if (polygon is not null)
                polygon.Changed += OnChildChanged;
    }

    private void Unsubscribe(List<TileCollisionPolygon> list)
    {
        foreach (var polygon in list)
            if (polygon is not null)
                polygon.Changed -= OnChildChanged;
    }

    private void OnChildChanged() => EmitChanged();
}

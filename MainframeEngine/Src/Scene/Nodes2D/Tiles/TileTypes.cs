namespace MainframeEngine;

/// <summary>The shape of a tile set's cells (Godot's <c>TileSet.TileShape</c>). Only <see cref="Square"/> is supported.</summary>
public enum TileShape : byte
{
    Square,

    /// <summary>Reserved (Godot's isometric shape); treated as <see cref="Square"/>.</summary>
    Isometric,

    /// <summary>Reserved (Godot's half-offset square shape); treated as <see cref="Square"/>.</summary>
    HalfOffsetSquare,

    /// <summary>Reserved (Godot's hexagon shape); treated as <see cref="Square"/>.</summary>
    Hexagon,
}

/// <summary>A neighbouring cell (Godot's <c>TileSet.CellNeighbor</c>; square tiles use the eight side and corner values).</summary>
public enum TileCellNeighbor : byte
{
    RightSide = 0,
    RightCorner = 1,
    BottomRightSide = 2,
    BottomRightCorner = 3,
    BottomSide = 4,
    BottomCorner = 5,
    BottomLeftSide = 6,
    BottomLeftCorner = 7,
    LeftSide = 8,
    LeftCorner = 9,
    TopLeftSide = 10,
    TopLeftCorner = 11,
    TopSide = 12,
    TopCorner = 13,
    TopRightSide = 14,
    TopRightCorner = 15,
}

/// <summary>When a tile map layer draws its collision shapes (Godot's <c>TileMapLayer.DebugVisibilityMode</c>).</summary>
public enum DebugVisibilityMode : byte
{
    /// <summary>When physics debug drawing is on (and in the editor).</summary>
    Default,

    /// <summary>Never.</summary>
    ForceHide,

    /// <summary>Always, at run time too.</summary>
    ForceShow,
}

#pragma warning disable CA1720 // Godot's variant type names
/// <summary>The type of a <see cref="TileSetCustomDataLayer"/> (the Godot variant types tiles use).</summary>
public enum CustomDataType : byte
{
    Bool,
    Int,
    Float,
    String,
    Vector2,
    Color,
}
#pragma warning restore CA1720

/// <summary>
/// One cell of a <see cref="TileMapLayer"/>: the atlas source id, the tile's atlas coordinates and its alternative id
/// (whose high bits are the flip and transpose flags, <see cref="TileSetAtlasSource.TransformFlipH"/>…). Godot 4.3's
/// <c>tile_map_data</c> record without the coordinates.
/// </summary>
public readonly record struct TileCell(short SourceId, ushort AtlasX, ushort AtlasY, ushort Alternative)
{
    /// <summary>No tile.</summary>
    public static readonly TileCell Empty = new(-1, 0xFFFF, 0xFFFF, 0);

    /// <summary>A tile of <paramref name="sourceId"/> at <paramref name="atlasCoords"/>.</summary>
    public TileCell(int sourceId, Vector2I atlasCoords, int alternative = 0)
        : this((short)sourceId, (ushort)atlasCoords.X, (ushort)atlasCoords.Y, (ushort)alternative)
    {
    }

    /// <summary>True for <see cref="Empty"/> (any negative source id).</summary>
    public bool IsEmpty => SourceId < 0;

    public Vector2I AtlasCoords => new(AtlasX, AtlasY);

    /// <summary>The alternative id without the transform flags.</summary>
    public int BaseAlternative => Alternative & TileSetAtlasSource.AlternativeMask;

    public bool FlipH => (Alternative & TileSetAtlasSource.TransformFlipH) != 0;

    public bool FlipV => (Alternative & TileSetAtlasSource.TransformFlipV) != 0;

    public bool Transpose => (Alternative & TileSetAtlasSource.TransformTranspose) != 0;
}

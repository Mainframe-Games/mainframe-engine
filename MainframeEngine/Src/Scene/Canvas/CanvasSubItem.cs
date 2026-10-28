namespace MainframeEngine;

/// <summary>
/// A draw list a <see cref="CanvasItem"/> owns besides its own (Godot's rendering quadrants: canvas items that are not
/// nodes): a <see cref="TileMapLayer"/> chunk or y-sorted row. The culler treats an item's sub-items as extra entries
/// that draw right after the item's own commands and before its node children, each culled on its own bounds; under
/// y-sort they are sorted by their own origin with the item's children. Modulate, material, light mask and texture
/// state come from the owner. Engine-internal (docs/design/tile-maps.md).
/// </summary>
internal sealed class CanvasSubItem
{
    /// <summary>The commands; vertices are relative to <see cref="Transform"/>.</summary>
    public readonly CanvasDrawList DrawList = new();

    /// <summary>The sub-item's transform relative to its owner (its origin is the y-sort position).</summary>
    public Transform2D Transform = Transform2D.Identity;

    /// <summary>Added to the owner's absolute z (tiles with their own <c>ZIndex</c>).</summary>
    public int ZOffset;
}

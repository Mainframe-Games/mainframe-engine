using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The tile map source of <see cref="NavigationGrid2D"/> (G3.6): the layer at <see cref="NavigationGrid2D.TileMapLayerPath"/>
/// owns the layout (its tile size is the cell size, its global position the grid's, its used rectangle the region, so grid
/// cell (x, y) is map cell (x, y)), and a cell is solid when its tile has collision on
/// <see cref="NavigationGrid2D.TilePhysicsLayer"/> or its bool custom data <see cref="NavigationGrid2D.SolidDataLayer"/> is
/// true. The float custom data <see cref="NavigationGrid2D.WeightDataLayer"/> gives weights; empty cells are walkable.
/// The live grid re-reads the layer when it raises <see cref="TileMapLayer.Changed"/>, at most once per sync: O(used cells)
/// through public tile map API only, allocation-free once its cell list is sized. Square tiles only.
/// </summary>
public sealed class TileMapLayerBaker : INavigationGridBaker
{
    private readonly Dictionary<NavigationGrid2D, Link> _links = new(ReferenceEqualityComparer.Instance);

    /// <summary>The grids attached to this baker (tests).</summary>
    internal int LinkCount => _links.Count;

    public void PrepareLayout(NavigationGrid2D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Resolve(node) is not { } layer)
            return;
        var used = layer.GetUsedRect();
        if (used.Size.X > 0 && used.Size.Y > 0 && (long)used.Size.X * used.Size.Y <= AStarGrid2D.MaxCells)
            node.Region = used;
        var tile = layer.TileSize;
        node.CellSize = new Vector2(tile.X, tile.Y);
        var position = layer.GlobalPosition;
        if (node.GlobalPosition != position)
            node.GlobalPosition = position;
    }

    public bool Bake(NavigationGrid2D node, AStarGrid2D cells, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(cells);
        failure = null;
        if (Resolve(node) is not { } layer)
        {
            failure = node.TileMapLayerPath.IsEmpty
                ? "no TileMapLayerPath is set"
                : $"'{node.TileMapLayerPath.Path}' is not a TileMapLayer in the tree";
            return false;
        }

        var used = layer.GetUsedRect();
        if ((long)used.Size.X * used.Size.Y > AStarGrid2D.MaxCells)
        {
            failure = $"the layer's used cells span {used.Size.X} × {used.Size.Y}, more than {AStarGrid2D.MaxCells} cells";
            return false;
        }

        if (layer.TileSet is not { } set)
            return true; // no tiles have data: all walkable
        if (set.TileShape != TileShape.Square)
        {
            failure = $"its tile shape is {set.TileShape}; navigation reads square tiles only";
            return false;
        }

        var physics = node.TilePhysicsLayer;
        if (physics < 0 || (set.PhysicsLayers.Count > 0 && physics >= set.PhysicsLayers.Count))
        {
            failure = $"the tile set has no physics layer {physics}";
            return false;
        }

        var solid = -1;
        if (node.SolidDataLayer.Length > 0)
        {
            solid = set.GetCustomDataLayerByName(node.SolidDataLayer);
            if (solid >= 0 && set.CustomDataLayers[solid].Type != CustomDataType.Bool)
            {
                failure = $"custom data layer '{node.SolidDataLayer}' is {set.CustomDataLayers[solid].Type}, not Bool";
                return false;
            }
        }

        var weight = -1;
        if (node.WeightDataLayer.Length > 0)
        {
            weight = set.GetCustomDataLayerByName(node.WeightDataLayer);
            if (weight < 0)
            {
                failure = $"the tile set has no custom data layer named '{node.WeightDataLayer}'";
                return false;
            }

            if (set.CustomDataLayers[weight].Type != CustomDataType.Float)
            {
                failure = $"custom data layer '{node.WeightDataLayer}' is {set.CustomDataLayers[weight].Type}, not Float";
                return false;
            }
        }

        // A grid outside the tree (an editor bake into Data) has no link: a list of its own then.
        var list = _links.TryGetValue(node, out var link) ? link.Cells : new List<Vector2I>(layer.UsedCellCount);
        list.Clear();
        layer.GetUsedCells(list);
        foreach (var cell in list)
        {
            if (!cells.IsInBounds(cell) || layer.GetCellTileData(cell) is not { } data)
                continue;
            var index = cells.Index(cell.X, cell.Y);
            if (data.GetCollisionPolygonsCount(physics) > 0 || (solid >= 0 && data.GetCustomBool(solid)))
            {
                cells.SetSolid(index, true);
                continue;
            }

            if (weight >= 0 && data.GetCustomString(weight).Length > 0)
            {
                var scale = (float)data.GetCustomFloat(weight);
                if (scale >= 0f && float.IsFinite(scale))
                    cells.SetWeight(index, scale);
            }
        }

        list.Clear();
        cells.BumpVersion();
        return true;
    }

    public void Attach(NavigationGrid2D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_links.ContainsKey(node))
            _links.Add(node, new Link(node));
        Resolve(node); // the layer may enter the tree after the grid: the first re-read resolves it again
    }

    public void Detach(NavigationGrid2D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_links.Remove(node, out var link))
            link.Watch(null, default);
    }

    // The layer at the node's path, watched for changes while the node is attached. The path is resolved again only when it
    // changed or the layer left the tree.
    private TileMapLayer? Resolve(NavigationGrid2D node)
    {
        var path = node.TileMapLayerPath;
        if (!_links.TryGetValue(node, out var link))
            return node.GetNodeOrNull<TileMapLayer>(path);
        if (link.Layer is { IsInsideTree: true } layer && string.Equals(link.Path.Path, path.Path, StringComparison.Ordinal))
            return layer;
        var resolved = node.GetNodeOrNull<TileMapLayer>(path);
        link.Watch(resolved, path);
        return resolved;
    }

    private sealed class Link
    {
        private readonly NavigationGrid2D _grid;
        private readonly Action _onChanged;

        public Link(NavigationGrid2D grid)
        {
            _grid = grid;
            _onChanged = OnChanged;
        }

        public List<Vector2I> Cells { get; } = new(256);

        public TileMapLayer? Layer { get; private set; }

        public NodePath Path { get; private set; }

        public void Watch(TileMapLayer? layer, NodePath path)
        {
            Path = path;
            if (ReferenceEquals(layer, Layer))
                return;
            if (Layer is not null)
                Layer.Changed -= _onChanged;
            Layer = layer;
            if (layer is not null)
                layer.Changed += _onChanged;
        }

        private void OnChanged() => _grid.MarkSourceChanged();
    }
}

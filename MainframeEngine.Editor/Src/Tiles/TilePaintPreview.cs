using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The brush's ghost in the 2D view (<c>TilePaintPreview2D</c> in the proposal): an editor-only, unowned
/// <see cref="TileMapLayer"/> at 50 % alpha placed in the edited scene's view (beside its root, so it is never saved,
/// never in the scene tree and never simulated), following the edited layer's transform, tile set and filtering. Erased
/// cells are listed for the editor's red outlines instead.
/// </summary>
public sealed class TilePaintPreview : IDisposable
{
    private static readonly Vector4 Ghost = new(1f, 1f, 1f, 0.5f);

    private readonly TileMapLayer _node = new()
    {
        Name = "TilePaintPreview2D",
        Modulate = Ghost,
        CollisionEnabled = false,
        ZIndex = CanvasCuller.ZMax,
        ZAsRelative = false,
    };

    private readonly List<Vector2I> _erased = [];
    private readonly List<Vector2I> _shown = [];
    private TileMapLayer? _layer;

    /// <summary>The ghost node (tests read its cells).</summary>
    public TileMapLayer Node => _node;

    /// <summary>The cells the current stroke or hover would erase.</summary>
    public IReadOnlyList<Vector2I> ErasedCells => _erased;

    /// <summary>Puts the ghost in <paramref name="layer"/>'s view, matching it.</summary>
    public void Attach(TileMapLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var viewport = layer.GetViewport();
        if (viewport is null)
            return;
        if (!ReferenceEquals(_node.Parent, viewport))
        {
            _node.Parent?.RemoveChild(_node);
            viewport.AddChild(_node);
        }

        _layer = layer;
        Sync(layer);
    }

    /// <summary>Follows the layer's transform, tile set, quadrant size and texture filter (cheap when nothing changed).</summary>
    public void Sync(TileMapLayer layer)
    {
        if (!ReferenceEquals(layer, _layer) || _node.Parent is null)
            return;
        var transform = layer.GlobalTransform;
        if (_node.Transform != transform)
            _node.Transform = transform;
        if (!ReferenceEquals(_node.TileSet, layer.TileSet))
            _node.TileSet = layer.TileSet;
        if (_node.RenderingQuadrantSize != layer.RenderingQuadrantSize)
            _node.RenderingQuadrantSize = layer.RenderingQuadrantSize;
        var filter = ResolvedFilter(layer);
        if (_node.TextureFilter != filter)
            _node.TextureFilter = filter;
    }

    private static CanvasTextureFilter ResolvedFilter(CanvasItem item)
    {
        for (CanvasItem? n = item; n is not null; n = n.CanvasParent)
            if (n.TextureFilter != CanvasTextureFilter.ParentNode)
                return n.TextureFilter;
        return CanvasTextureFilter.Linear;
    }

    /// <summary>Shows <paramref name="cells"/> (or, for an erase, lists them for outlines).</summary>
    public void Show(IReadOnlyDictionary<Vector2I, TileCell> cells, bool erase)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Clear();
        foreach (var (coords, cell) in cells)
        {
            if (coords.X is < short.MinValue or > short.MaxValue || coords.Y is < short.MinValue or > short.MaxValue)
                continue;
            if (erase || cell.IsEmpty)
            {
                _erased.Add(coords);
                continue;
            }

            _node.SetCellData(coords, cell);
            _shown.Add(coords);
        }
    }

    public void Clear()
    {
        foreach (var coords in _shown)
            _node.EraseCell(coords);
        _shown.Clear();
        _erased.Clear();
    }

    /// <summary>Takes the ghost out of the view (no layer edited).</summary>
    public void Detach()
    {
        Clear();
        _node.Parent?.RemoveChild(_node);
        _layer = null;
    }

    public void Dispose()
    {
        Detach();
        _node.Free();
    }
}

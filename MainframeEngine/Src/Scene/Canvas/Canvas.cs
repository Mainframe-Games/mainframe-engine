using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// One 2D canvas (Godot's server-side canvas): the root canvas of a <see cref="SceneViewport"/> (layer 0) or the canvas
/// of a <see cref="CanvasLayer"/>. Holds the canvas items whose parent is not a canvas item (its roots), the canvas
/// transform (the camera for the root canvas, the layer transform for a layer) and the <see cref="CanvasModulate"/>
/// colour.
/// </summary>
public sealed class Canvas
{
    private readonly List<CanvasItem> _roots = [];
    private readonly List<CanvasModulate> _modulates = [];
    private readonly List<(CanvasItem Item, int Index, int Seq)> _sortScratch = [];
    private readonly List<CanvasItem> _sorted = [];

    internal Canvas(SceneViewport? viewport, CanvasLayer? layer)
    {
        Viewport = viewport;
        Layer = layer;
    }

    /// <summary>The viewport whose root canvas this is (null for a layer's canvas).</summary>
    public SceneViewport? Viewport { get; }

    /// <summary>The layer this canvas belongs to (null for a viewport's root canvas).</summary>
    public CanvasLayer? Layer { get; }

    /// <summary>Canvas → viewport pixels (the camera for the root canvas, the layer transform for a layer).</summary>
    public Transform2D Transform => Layer is { } layer ? layer.GetFinalTransform() : Viewport?.CanvasTransform ?? Transform2D.Identity;

    /// <summary>The colour of the last visible <see cref="CanvasModulate"/> on this canvas (white when none).</summary>
    public Vector4 Modulate
    {
        get
        {
            for (var i = _modulates.Count - 1; i >= 0; i--)
                if (_modulates[i].IsVisibleInTree())
                    return _modulates[i].Color;
            return Vector4.One;
        }
    }

    /// <summary>Number of root items.</summary>
    public int RootCount => _roots.Count;

    internal void AddRoot(CanvasItem item) => _roots.Add(item);

    internal void RemoveRoot(CanvasItem item) => _roots.Remove(item);

    internal void AddModulate(CanvasModulate modulate)
    {
        if (!_modulates.Contains(modulate))
            _modulates.Add(modulate);
    }

    internal void RemoveModulate(CanvasModulate modulate) => _modulates.Remove(modulate);

    /// <summary>
    /// The roots in draw order: by their index among their parent's children (Godot's draw index), then by the order
    /// they joined the canvas. The returned list is reused.
    /// </summary>
    public IReadOnlyList<CanvasItem> SortedRoots()
    {
        _sortScratch.Clear();
        for (var i = 0; i < _roots.Count; i++)
            _sortScratch.Add((_roots[i], _roots[i].GetIndex(), i));
        _sortScratch.Sort(RootOrder.Instance);
        _sorted.Clear();
        for (var i = 0; i < _sortScratch.Count; i++)
            _sorted.Add(_sortScratch[i].Item);
        return _sorted;
    }

    // An IComparer singleton: List.Sort(Comparison) would allocate a wrapper every frame.
    private sealed class RootOrder : IComparer<(CanvasItem Item, int Index, int Seq)>
    {
        public static readonly RootOrder Instance = new();

        public int Compare((CanvasItem Item, int Index, int Seq) a, (CanvasItem Item, int Index, int Seq) b) =>
            a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Seq.CompareTo(b.Seq);
    }
}

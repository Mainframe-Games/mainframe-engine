namespace MainframeEngine;

/// <summary>Draw order of overlay renderers (after the tonemap): 2D canvas, then screen gizmos, then the UI.</summary>
public static class OverlayOrder
{
    public const int Canvas = 0;
    public const int Gizmos = 100;
    public const int Ui = 200;
}

/// <summary>Overlay renderers sorted by order; equal orders keep their registration order. Iterated without allocation.</summary>
internal sealed class OverlayRendererList
{
    private readonly List<(IOverlayRenderer Renderer, int Order, ProfilerMarker Marker)> _items = [];

    public int Count => _items.Count;

    public IOverlayRenderer this[int index] => _items[index].Renderer;

    /// <summary>The GPU zone marker of renderer <paramref name="index"/> (its type name; detailed zones, G10).</summary>
    public ProfilerMarker MarkerAt(int index) => _items[index].Marker;

    public void Add(IOverlayRenderer renderer, int order)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (_items.Exists(i => ReferenceEquals(i.Renderer, renderer)))
            return;
        var at = _items.FindIndex(i => i.Order > order);
        var marker = EngineMarkers.ForType(renderer.GetType(), "", ProfilerMarkerKind.GpuInPass, ProfilerCategory.Ui);
        _items.Insert(at < 0 ? _items.Count : at, (renderer, order, marker));
    }

    public bool Remove(IOverlayRenderer renderer) =>
        _items.RemoveAll(i => ReferenceEquals(i.Renderer, renderer)) > 0;
}

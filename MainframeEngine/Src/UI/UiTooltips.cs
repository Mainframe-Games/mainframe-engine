using System.Diagnostics;
using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// Godot-style tooltips for one loaded document (ADR 0121): an element with a <c>title</c> attribute (or inside one) shows
/// that text after the mouse rests on it for <see cref="UiServer.TooltipDelaySeconds"/>, in the document's
/// <c>#tooltip</c> element, placed at the mouse + <see cref="UiServer.TooltipOffset"/> and kept inside the document.
/// A press or leaving the element hides it. Documents without a <c>#tooltip</c> element have none (the engine cannot
/// create elements; the document styles its own, hidden by default: the engine sets <c>display: block</c> / <c>none</c>).
/// </summary>
internal sealed class UiTooltips : IDisposable
{
    /// <summary>The id of the element that shows the text.</summary>
    public const string ElementId = "tooltip";

    private readonly UiServer? _server;
    private readonly List<RmlEventListener> _listeners = new(4);
    private RmlDocument _document;
    private RmlContext? _context;
    private RmlElement _tip;
    private RmlElement _source;
    private Vector2 _mouse;
    private long _restingSince;
    private bool _shown;
    private bool _hoverDirty;
    private int _placeFrames;

    public UiTooltips(UiServer? server) => _server = server;

    /// <summary>The text shown now, or null (tests, diagnostics).</summary>
    public string? ShownText { get; private set; }

    /// <summary>Hooks <paramref name="document"/> (after a load or reload); false when it has no <c>#tooltip</c>.</summary>
    public bool Attach(RmlDocument document, RmlContext? context)
    {
        Detach();
        if (document.IsNull || context is null)
            return false;
        var tip = document.GetElementById(ElementId);
        if (tip.IsNull)
            return false;
        _document = document;
        _context = context;
        _tip = tip;
        tip.SetProperty("display", "none");
        var root = document.AsElement();
        Listen(root, "mouseover", OnOver);
        Listen(root, "mousemove", OnMove);
        Listen(root, "mouseout", OnOut);
        Listen(root, "mousedown", OnDown);
        return true;
    }

    private void Listen(RmlElement root, string type, RmlEventCallback handler)
    {
        if (root.AddEventListener(type, handler) is { } listener)
            _listeners.Add(listener);
    }

    /// <summary>Drops the hooks (the document is unloading or being replaced).</summary>
    public void Detach()
    {
        foreach (var listener in _listeners)
            if (listener.IsAttached)
                listener.Remove();
        _listeners.Clear();
        _document = default;
        _context = null;
        _tip = default;
        _source = default;
        _shown = false;
        ShownText = null;
    }

    public void Dispose() => Detach();

    // RmlUi sends a mouseover to every element entering hover (deepest first, then its ancestors) and a mouseout to every
    // element leaving it: the element under the mouse is read from the context in Update.
    private void OnOver(RmlEvent e)
    {
        _hoverDirty = true;
        Track(e);
    }

    private void OnMove(RmlEvent e)
    {
        Track(e);
        if (!_shown)
            _restingSince = Stopwatch.GetTimestamp(); // Godot restarts the tooltip timer on every motion until it shows
    }

    private void OnOut(RmlEvent e) => _hoverDirty = true;

    private void OnDown(RmlEvent e)
    {
        Hide();
        _restingSince = long.MaxValue; // no tooltip until the mouse moves again
    }

    private void Track(RmlEvent e)
    {
        if (_shown)
            return; // the tooltip stays where it appeared
        _mouse = new Vector2((float)e.GetParameter("mouse_x", (double)_mouse.X), (float)e.GetParameter("mouse_y", (double)_mouse.Y));
    }

    // The element or its nearest ancestor with a title, if the element is in this document (else none).
    private RmlElement TitleHolder(RmlElement element)
    {
        var root = _document.AsElement();
        RmlElement holder = default;
        for (var node = element; !node.IsNull; node = node.Parent)
        {
            if (node.Equals(root))
                return holder;
            if (holder.IsNull && node.HasAttribute("title"))
                holder = node;
        }

        return default;
    }

    /// <summary>Once per frame (no allocation unless the tooltip appears).</summary>
    public void Update()
    {
        if (_tip.IsNull)
            return;
        if (_hoverDirty)
        {
            _hoverDirty = false;
            var holder = _context is { } context ? TitleHolder(context.HoverElement) : default;
            if (!holder.Equals(_source))
            {
                Hide();
                _source = holder;
                _restingSince = Stopwatch.GetTimestamp();
            }
        }

        if (_shown)
        {
            if (_placeFrames > 0 && --_placeFrames == 0)
                KeepInside();
            return;
        }

        if (_source.IsNull || _restingSince == long.MaxValue)
            return;
        var delay = _server?.TooltipDelaySeconds ?? UiServer.DefaultTooltipDelaySeconds;
        if (Stopwatch.GetElapsedTime(_restingSince).TotalSeconds < delay)
            return;
        Show();
    }

    private void Show()
    {
        var text = _source.GetAttribute("title");
        if (string.IsNullOrEmpty(text))
        {
            _restingSince = long.MaxValue;
            return;
        }

        _tip.SetInnerRml(Escape(text));
        var offset = _server?.TooltipOffset ?? UiServer.DefaultTooltipOffset;
        Place(_mouse + offset);
        _tip.SetProperty("display", "block");
        _shown = true;
        _placeFrames = 1; // the size is known after the next layout
        ShownText = text;
    }

    private void Place(Vector2 position)
    {
        _tip.SetProperty("left", FormattableString.Invariant($"{position.X:0.##}px"));
        _tip.SetProperty("top", FormattableString.Invariant($"{position.Y:0.##}px"));
    }

    // Godot keeps a tooltip inside the viewport: shifted left/up by what overflows.
    private void KeepInside()
    {
        var tip = _tip.Bounds;
        var page = _document.AsElement().Bounds;
        var x = Math.Max(page.X, Math.Min(tip.X, page.X + page.Width - tip.Width));
        var y = Math.Max(page.Y, Math.Min(tip.Y, page.Y + page.Height - tip.Height));
        if (x != tip.X || y != tip.Y)
            Place(new Vector2(x, y));
    }

    private void Hide()
    {
        if (!_shown)
            return;
        _tip.SetProperty("display", "none");
        _shown = false;
        ShownText = null;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

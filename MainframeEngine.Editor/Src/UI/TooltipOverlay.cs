using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's tooltip widget (docs/design/editor.md#tooltips): any element of an editor document (or its nearest
/// ancestor) with <c>data-tooltip="Title (Shortcut) — description"</c> shows a tooltip after <see cref="Delay"/> of
/// hovering, or when it gets keyboard focus (<c>:focus-visible</c>). The title is bold, the shortcut a key cap (Ctrl reads
/// Cmd on macOS), the description and any further lines (<c>\n</c>) below. The box sits under the element, kept inside the
/// window (above the element when there is no room below). A click, key or wheel hides it until the pointer moves to
/// another element. Lives on its own UI layer above the dialogs; idle frames compare two element handles and allocate
/// nothing.
/// </summary>
public sealed class TooltipOverlay : EditorDocument
{
    /// <summary>Hover time before a tooltip shows (seconds).</summary>
    public const double Delay = 0.5;

    /// <summary>Space between the element and the tooltip, and the minimum margin to the window edges (dp).</summary>
    public const float Gap = 6;

    private enum State
    {
        Idle,
        Pending,
        Measuring,
        Shown,
        Dismissed,
    }

    private readonly List<UiLayer> _layers = [];
    private RmlElement _hover;
    private RmlElement _focus;
    private RmlElement _source;
    private State _state;
    private double _timer;

    public TooltipOverlay(EditorWorkspace workspace)
        : base(workspace, "tooltip.rml")
    {
    }

    /// <summary>The text of the tooltip on screen (or being placed), null when none is shown.</summary>
    public string? Text { get; private set; }

    /// <summary>Whether a tooltip is visible.</summary>
    public bool IsShown => _state == State.Shown;

    /// <summary>The tooltip's position (dp) once shown.</summary>
    public LayoutRect Placement { get; private set; }

    /// <summary>Watches <paramref name="layer"/> for hovered and focused elements (the topmost layer added first).</summary>
    public void Watch(UiLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layers.Add(layer);
    }

    /// <summary>Hides the tooltip until the pointer moves to another element (clicks, keys, wheel).</summary>
    public void Dismiss()
    {
        if (_state == State.Idle && _source.IsNull)
            return;
        HideTip();
        _state = _source.IsNull ? State.Idle : State.Dismissed;
    }

    /// <summary>Per frame: follows the hovered/focused element, counts the delay, places and shows the tooltip.</summary>
    public void Tick(double deltaSeconds)
    {
        var hover = FindHover();
        var focus = FindFocus();
        if (hover != _hover || focus != _focus)
        {
            var hoverChanged = hover != _hover;
            _hover = hover;
            _focus = focus;
            var source = TooltipSource(hover);
            if (source.IsNull && !focus.IsNull && focus.IsPseudoClassSet("focus-visible"))
                source = TooltipSource(focus);
            if (source != _source || (hoverChanged && _state == State.Dismissed && source.IsNull))
            {
                HideTip();
                _source = source;
                _timer = 0;
                _state = source.IsNull ? State.Idle : State.Pending;
            }
        }

        switch (_state)
        {
            case State.Pending:
                _timer += deltaSeconds;
                if (_timer >= Delay)
                    Prepare();
                break;
            case State.Measuring:
                Place();
                break;
        }
    }

    // The deepest hovered element of the topmost watched layer under the pointer (lower layers lose hover when a
    // higher one takes the mouse; the context's root element means "nothing there").
    private RmlElement FindHover()
    {
        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Context is not { IsDisposed: false } context)
                continue;
            var hover = context.HoverElement;
            if (!hover.IsNull && !hover.OwnerDocument.IsNull)
                return hover;
        }

        return default;
    }

    private RmlElement FindFocus()
    {
        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Context is not { IsDisposed: false } context)
                continue;
            var focus = context.FocusElement;
            if (!focus.IsNull && !focus.OwnerDocument.IsNull)
                return focus;
        }

        return default;
    }

    /// <summary>The element itself or its nearest ancestor (inside the same document) that has <c>data-tooltip</c>.</summary>
    private static RmlElement TooltipSource(RmlElement element)
    {
        if (element.IsNull)
            return default;
        var body = element.OwnerDocument.AsElement();
        for (var e = element; !e.IsNull; e = e.Parent)
        {
            if (e.HasAttribute("data-tooltip"))
                return e;
            if (e == body)
                break;
        }

        return default;
    }

    private void Prepare()
    {
        var text = _source.IsNull ? null : _source.GetAttribute("data-tooltip");
        if (string.IsNullOrWhiteSpace(text) || !EnsureLoaded() || Document.GetElementById("tip") is not { IsNull: false } tip)
        {
            _state = State.Idle;
            return;
        }

        Text = text;
        tip.SetClass("shown", false);
        tip.SetInnerRml(ToRml(text));
        _state = State.Measuring; // the size is known after the next UI update
    }

    private void Place()
    {
        if (_source.IsNull || !IsLoaded || Document.GetElementById("tip") is not { IsNull: false } tip)
        {
            HideTip();
            _state = State.Idle;
            return;
        }

        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var anchor = _source.Bounds;
        var box = tip.Bounds;
        var window = Workspace.Host.WindowSize;
        var width = box.Width / scale;
        var height = box.Height / scale;
        var left = anchor.X / scale;
        var top = (anchor.Y + anchor.Height) / scale + Gap;
        if (top + height > window.Y - Gap)
            top = anchor.Y / scale - Gap - height; // no room below: above the element
        left = Math.Clamp(left, Gap, MathF.Max(Gap, window.X - width - Gap));
        top = Math.Clamp(top, Gap, MathF.Max(Gap, window.Y - height - Gap));
        tip.SetProperty("left", RmlText.Dp(left));
        tip.SetProperty("top", RmlText.Dp(top));
        tip.SetClass("shown", true);
        Placement = new LayoutRect(left, top, width, height);
        _state = State.Shown;
    }

    private void HideTip()
    {
        Text = null;
        if (_state is State.Measuring or State.Shown && IsLoaded && Document.GetElementById("tip") is { IsNull: false } tip)
            tip.SetClass("shown", false);
        if (_state != State.Dismissed)
            _state = State.Idle;
    }

    /// <summary>
    /// The tooltip's RML for <paramref name="text"/>: the first line's "Title (Shortcut) — description" split into a bold
    /// title, a key cap and a description; following lines as they are.
    /// </summary>
    public static string ToRml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 96);
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var first = lines[0];
        var dash = first.IndexOf(" — ", StringComparison.Ordinal);
        var title = dash >= 0 ? first[..dash] : first;
        var description = dash >= 0 ? first[(dash + 3)..] : "";
        string? key = null;
        if (title.EndsWith(')') && title.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0)
        {
            key = title[(open + 2)..^1];
            title = title[..open];
        }

        if (dash >= 0 || key is not null)
        {
            builder.Append("<div class=\"tip-head\"><span class=\"tip-title\">").Append(RmlText.Escape(title)).Append("</span>");
            if (key is { Length: > 0 })
                builder.Append("<span class=\"tip-key\">").Append(RmlText.Escape(ShortcutText(key))).Append("</span>");
            builder.Append("</div>");
            if (description.Length > 0)
                builder.Append("<div class=\"tip-desc\">").Append(RmlText.Escape(description)).Append("</div>");
        }
        else
        {
            builder.Append("<div class=\"tip-line\">").Append(RmlText.Escape(first)).Append("</div>");
        }

        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Length > 0)
                builder.Append("<div class=\"tip-line\">").Append(RmlText.Escape(lines[i])).Append("</div>");
        return builder.ToString();
    }

    /// <summary>Shortcuts read Cmd instead of Ctrl on macOS.</summary>
    public static string ShortcutText(string shortcut) =>
        OperatingSystem.IsMacOS() ? shortcut.Replace("Ctrl+", "Cmd+", StringComparison.Ordinal) : shortcut;
}

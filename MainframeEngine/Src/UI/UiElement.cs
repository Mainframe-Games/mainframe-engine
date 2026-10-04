using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>An element event delivered to C#; valid only during the handler (a ref struct).</summary>
public readonly ref struct UiEvent
{
    internal UiEvent(UiElement element, RmlEvent raw)
    {
        Element = element;
        Raw = raw;
    }

    /// <summary>The element whose C# event is being raised.</summary>
    public UiElement Element { get; }

    /// <summary>The underlying RmlUi event (parameters, target, phase).</summary>
    public RmlEvent Raw { get; }

    /// <summary>The event type, e.g. "click" (allocates).</summary>
    public string Type => Raw.Type;

    /// <summary>The <c>value</c> parameter of change/submit events (allocates).</summary>
    public string Value => Raw.Value;

    /// <summary>The element the event was dispatched to (may be a child of <see cref="Element"/>).</summary>
    public RmlElement Target => Raw.Target;

    public RmlVariant GetParameter(string name) => Raw.GetParameter(name);

    public void StopPropagation() => Raw.StopPropagation();
}

/// <summary>Handles a <see cref="UiElement"/> event.</summary>
public delegate void UiEventCallback(UiEvent e);

/// <summary>
/// A document element as a C# object with events (<see cref="Click"/>, <see cref="Change"/>, <see cref="Submit"/>,
/// ...), returned by <see cref="UiDocument.GetElementById"/> and cached per document. Native listeners are attached
/// only for event types that have subscribers. After a hot reload the element is looked up again by id and its
/// listeners re-attached, so subscriptions survive editing the <c>.rml</c>.
/// </summary>
public sealed class UiElement
{
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private RmlElement _element;
    private bool _removing; // our own listener removals (not the element going away)

    private sealed class Subscription(string type)
    {
        public string Type { get; } = type;
        public UiEventCallback? Handlers;
        public RmlEventListener? Listener;
    }

    internal UiElement(UiDocument document, RmlElement element, string? id)
    {
        Document = document;
        _element = element;
        Id = id;
    }

    public UiDocument Document { get; }

    /// <summary>The id the element was looked up by (used to find it again after a reload); null for selector lookups.</summary>
    public string? Id { get; }

    /// <summary>
    /// The underlying element: looked up again by <see cref="Id"/> on every access, so it is null (never dangling) once
    /// the DOM removed it or the document unloaded.
    /// </summary>
    public RmlElement Element
    {
        get
        {
            Refresh();
            return _element;
        }
    }

    public bool IsValid => !Element.IsNull;

    /// <summary>Follows the document: an element replaced by RML changes (or a reload) is looked up again by id.</summary>
    private void Refresh()
    {
        if (Id is null)
            return;
        var current = Document.FindLive(Id);
        if (current != _element)
            Rebind(current);
    }

    // ── Content and style ────────────────────────────────────────────────────────────────────────────────────

    public string InnerRml
    {
        get => Valid.InnerRml;
        set => Valid.SetInnerRml(value);
    }

    /// <summary>Form control value (input, select, textarea); null for other elements.</summary>
    public string? Value
    {
        get => Valid.Value;
        set => Valid.SetValue(value);
    }

    public string? GetAttribute(string name) => Valid.GetAttribute(name);

    public void SetAttribute(string name, string value) => Valid.SetAttribute(name, value);

    public void SetClass(string className, bool active) => Valid.SetClass(className, active);

    public bool IsClassSet(string className) => Valid.IsClassSet(className);

    public bool SetProperty(string name, string value) => Valid.SetProperty(name, value);

    public bool Focus() => Valid.Focus(focusVisible: true);

    /// <summary>Dispatches a synthetic click (raises <see cref="Click"/>).</summary>
    public void PerformClick() => Valid.Click();

    public RmlRect Bounds => Valid.Bounds;

    private RmlElement Valid => Element.IsNull
        ? throw new InvalidOperationException($"UI element '{Id}' is no longer part of a loaded document.")
        : _element;

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────

    public event UiEventCallback? Click
    {
        add => On("click", value);
        remove => Off("click", value);
    }

    public event UiEventCallback? DoubleClick
    {
        add => On("dblclick", value);
        remove => Off("dblclick", value);
    }

    public event UiEventCallback? MouseDown
    {
        add => On("mousedown", value);
        remove => Off("mousedown", value);
    }

    public event UiEventCallback? MouseUp
    {
        add => On("mouseup", value);
        remove => Off("mouseup", value);
    }

    public event UiEventCallback? MouseOver
    {
        add => On("mouseover", value);
        remove => Off("mouseover", value);
    }

    public event UiEventCallback? MouseOut
    {
        add => On("mouseout", value);
        remove => Off("mouseout", value);
    }

    /// <summary>Form controls: the value changed (<see cref="UiEvent.Value"/>).</summary>
    public event UiEventCallback? Change
    {
        add => On("change", value);
        remove => Off("change", value);
    }

    /// <summary>Forms: submitted.</summary>
    public event UiEventCallback? Submit
    {
        add => On("submit", value);
        remove => Off("submit", value);
    }

    public event UiEventCallback? Focused
    {
        add => On("focus", value);
        remove => Off("focus", value);
    }

    public event UiEventCallback? Blurred
    {
        add => On("blur", value);
        remove => Off("blur", value);
    }

    public event UiEventCallback? KeyDown
    {
        add => On("keydown", value);
        remove => Off("keydown", value);
    }

    public event UiEventCallback? KeyUp
    {
        add => On("keyup", value);
        remove => Off("keyup", value);
    }

    /// <summary>Subscribes to any RmlUi event type (e.g. "scroll", "animationend", "tabchange").</summary>
    public void On(string eventType, UiEventCallback? handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        if (handler is null)
            return;
        if (!_subscriptions.TryGetValue(eventType, out var sub))
        {
            sub = new Subscription(eventType);
            _subscriptions.Add(eventType, sub);
        }

        sub.Handlers += handler;
        Attach(sub);
    }

    public void Off(string eventType, UiEventCallback? handler)
    {
        if (handler is null || !_subscriptions.TryGetValue(eventType, out var sub))
            return;
        sub.Handlers -= handler;
        if (sub.Handlers is not null)
            return;
        RemoveListener(sub);
        _subscriptions.Remove(eventType);
    }

    private void Attach(Subscription sub)
    {
        if (_element.IsNull || sub.Listener is { IsAttached: true })
            return;
        var listener = _element.AddEventListener(sub.Type, e => Raise(sub, e));
        if (listener is not null)
            listener.Detached += detached => OnDetached(sub, detached);
        sub.Listener = listener;
    }

    private void RemoveListener(Subscription sub)
    {
        _removing = true;
        try
        {
            sub.Listener?.Remove();
        }
        finally
        {
            _removing = false;
        }

        sub.Listener = null;
    }

    /// <summary>RmlUi detached a listener we did not remove: its element was destroyed, so forget the element.</summary>
    private void OnDetached(Subscription sub, RmlEventListener listener)
    {
        if (_removing || !ReferenceEquals(sub.Listener, listener))
            return;
        sub.Listener = null;
        _element = default;
    }

    private void Raise(Subscription sub, RmlEvent e) => sub.Handlers?.Invoke(new UiEvent(this, e));

    /// <summary>The document was reloaded (or unloaded: <paramref name="element"/> null): rebind and re-attach.</summary>
    internal void Rebind(RmlElement element)
    {
        if (element != _element)
        {
            foreach (var sub in _subscriptions.Values)
                RemoveListener(sub);
            _element = element;
        }

        foreach (var sub in _subscriptions.Values)
            Attach(sub);
    }

    public override string ToString() => $"UiElement #{Id} ({(_element.IsNull ? "detached" : "attached")})";
}

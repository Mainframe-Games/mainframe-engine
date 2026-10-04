using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>
/// A borrowed <c>Rml::Element</c>: valid until RmlUi destroys the element (its document is closed and the context
/// updated, the element is removed, or the context is destroyed). A null element (<see cref="IsNull"/>) is what
/// lookups return when nothing matches. Methods that read strings allocate the result; the span overloads and the
/// setters do not.
/// </summary>
public readonly unsafe struct RmlElement : IEquatable<RmlElement>
{
    internal RmlElement(nint handle) => Handle = handle;

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;

    private nint Valid => Handle != 0 ? Handle : throw new InvalidOperationException("The element is null.");

    // ── Tree ─────────────────────────────────────────────────────────────────────────────────────────────────

    public RmlDocument OwnerDocument => new(RmlNative.ElementGetOwnerDocument(Valid));

    public RmlElement Parent => new(RmlNative.ElementGetParent(Valid));

    public int ChildCount => Math.Max(0, RmlNative.ElementGetNumChildren(Valid));

    public RmlElement GetChild(int index) => new(RmlNative.ElementGetChild(Valid, index));

    /// <summary>A descendant by id, or a null element.</summary>
    public RmlElement GetElementById(ReadOnlySpan<char> id)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var arg = new RmlUtf8Arg(id, scratch);
        fixed (byte* p = arg)
            return new RmlElement(RmlNative.ElementGetElementById(Valid, p));
    }

    /// <summary>The first descendant matching a CSS selector, or a null element.</summary>
    public RmlElement QuerySelector(ReadOnlySpan<char> selector)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var arg = new RmlUtf8Arg(selector, scratch);
        fixed (byte* p = arg)
            return new RmlElement(RmlNative.ElementQuerySelector(Valid, p));
    }

    /// <summary>
    /// Writes up to <c>results.Length</c> matching descendants into <paramref name="results"/> and returns the total
    /// number of matches (call again with a larger span if it exceeds the length). No allocation.
    /// </summary>
    public int QuerySelectorAll(ReadOnlySpan<char> selector, Span<RmlElement> results)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var arg = new RmlUtf8Arg(selector, scratch);
        fixed (byte* p = arg)
        fixed (RmlElement* r = results)
        {
            var count = RmlNative.ElementQuerySelectorAll(Valid, p, (nint*)r, results.Length);
            return Math.Max(0, count);
        }
    }

    /// <summary>All matching descendants (allocates the array).</summary>
    public RmlElement[] QuerySelectorAll(string selector)
    {
        var count = QuerySelectorAll(selector.AsSpan(), []);
        if (count == 0)
            return [];
        var results = new RmlElement[count];
        QuerySelectorAll(selector.AsSpan(), results);
        return results;
    }

    // ── Identity and attributes ──────────────────────────────────────────────────────────────────────────────

    public string TagName => RmlUtf8.Read(Valid, &ReadTag) ?? "";

    private static int ReadTag(nint h, byte* b, int c) => RmlNative.ElementGetTagName(h, b, c);

    public string Id => RmlUtf8.Read(Valid, &ReadId) ?? "";

    private static int ReadId(nint h, byte* b, int c) => RmlNative.ElementGetId(h, b, c);

    /// <summary>The attribute value, or null if it is not set.</summary>
    public string? GetAttribute(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* pn = n)
        {
            var buffer = stackalloc byte[256];
            var length = RmlNative.ElementGetAttribute(Valid, pn, buffer, 256);
            if (length < 0)
                return null;
            if (length < 256)
                return Encoding.UTF8.GetString(buffer, length);
            var big = new byte[length + 1];
            fixed (byte* pb = big)
                length = RmlNative.ElementGetAttribute(Valid, pn, pb, big.Length);
            return length < 0 ? null : Encoding.UTF8.GetString(big, 0, Math.Min(length, big.Length - 1));
        }
    }

    public void SetAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        Span<byte> s1 = stackalloc byte[RmlUtf8Arg.StackSize];
        Span<byte> s2 = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, s1);
        using var v = new RmlUtf8Arg(value, s2);
        fixed (byte* pn = n)
        fixed (byte* pv = v)
            RmlException.ThrowIfFailed(RmlNative.ElementSetAttribute(Valid, pn, pv), "SetAttribute");
    }

    public void RemoveAttribute(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ElementRemoveAttribute(Valid, p), "ElementRemoveAttribute");
    }

    public bool HasAttribute(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* p = n)
            return RmlNative.ElementHasAttribute(Valid, p) == 1;
    }

    // ── Classes ──────────────────────────────────────────────────────────────────────────────────────────────

    public void SetClass(ReadOnlySpan<char> className, bool active)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(className, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ElementSetClass(Valid, p, active ? 1 : 0), "ElementSetClass");
    }

    public bool IsClassSet(ReadOnlySpan<char> className)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(className, scratch);
        fixed (byte* p = n)
            return RmlNative.ElementIsClassSet(Valid, p) == 1;
    }

    /// <summary>Replaces every class with a space-separated list.</summary>
    public void SetClassNames(ReadOnlySpan<char> classNames)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(classNames, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ElementSetClassNames(Valid, p), "ElementSetClassNames");
    }

    public void SetPseudoClass(ReadOnlySpan<char> pseudoClass, bool active)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(pseudoClass, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ElementSetPseudoClass(Valid, p, active ? 1 : 0), "ElementSetPseudoClass");
    }

    public bool IsPseudoClassSet(ReadOnlySpan<char> pseudoClass)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(pseudoClass, scratch);
        fixed (byte* p = n)
            return RmlNative.ElementIsPseudoClassSet(Valid, p) == 1;
    }

    // ── Style and content ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets an inline RCSS property, e.g. <c>("width", "50%")</c>. Returns false if the value does not parse.</summary>
    public bool SetProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        Span<byte> s1 = stackalloc byte[RmlUtf8Arg.StackSize];
        Span<byte> s2 = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, s1);
        using var v = new RmlUtf8Arg(value, s2);
        fixed (byte* pn = n)
        fixed (byte* pv = v)
            return RmlNative.ElementSetProperty(Valid, pn, pv) == RmlNative.Ok;
    }

    public void RemoveProperty(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ElementRemoveProperty(Valid, p), "ElementRemoveProperty");
    }

    /// <summary>The element's content as RML (allocates).</summary>
    public string InnerRml
    {
        get => RmlUtf8.Read(Valid, &ReadInnerRml) ?? "";
        set => SetInnerRml(value);
    }

    private static int ReadInnerRml(nint h, byte* b, int c) => RmlNative.ElementGetInnerRml(h, b, c);

    /// <summary>Replaces the content with RML (no allocation for short text).</summary>
    public void SetInnerRml(ReadOnlySpan<char> rml)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var r = new RmlUtf8Arg(rml, scratch);
        fixed (byte* p = r)
            RmlException.ThrowIfFailed(RmlNative.ElementSetInnerRml(Valid, p), "SetInnerRml");
    }

    /// <summary>Form controls (input, textarea, select): the current value (allocates); null for other elements.</summary>
    public string? Value
    {
        get => RmlUtf8.Read(Valid, &ReadValue);
        set => SetValue(value.AsSpan());
    }

    private static int ReadValue(nint h, byte* b, int c) => RmlNative.ElementGetValue(h, b, c);

    /// <summary>The form value into <paramref name="destination"/> without allocating; -1 if it fails or does not fit.</summary>
    public int GetValue(Span<char> destination) => RmlUtf8.Read(Valid, &ReadValue, destination);

    public void SetValue(ReadOnlySpan<char> value)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var v = new RmlUtf8Arg(value, scratch);
        fixed (byte* p = v)
            RmlException.ThrowIfFailed(RmlNative.ElementSetValue(Valid, p), "SetValue");
    }

    // ── Interaction ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Gives the element focus; false if it cannot be focused.</summary>
    public bool Focus(bool focusVisible = false) => RmlNative.ElementFocus(Valid, focusVisible ? 1 : 0) == 1;

    public void Blur() => RmlException.ThrowIfFailed(RmlNative.ElementBlur(Valid), "ElementBlur");

    /// <summary>Dispatches a synthetic click.</summary>
    public void Click() => RmlException.ThrowIfFailed(RmlNative.ElementClick(Valid), "ElementClick");

    public void ScrollIntoView(bool alignWithTop = true) => RmlException.ThrowIfFailed(RmlNative.ElementScrollIntoView(Valid, alignWithTop ? 1 : 0), "ElementScrollIntoView");

    /// <summary>Border box in context pixels.</summary>
    public RmlRect Bounds
    {
        get
        {
            RmlRect r;
            return RmlNative.ElementGetBounds(Valid, &r) == RmlNative.Ok ? r : default;
        }
    }

    /// <summary>
    /// Attaches a listener for <paramref name="eventType"/> ("click", "change", "submit", "focus", "blur",
    /// "mouseover", "keydown", ...). Returns null if the listener could not be attached.
    /// </summary>
    public RmlEventListener? AddEventListener(ReadOnlySpan<char> eventType, RmlEventCallback handler, bool inCapturePhase = false) =>
        RmlEventListener.Attach(Valid, eventType, handler, inCapturePhase);

    public bool Equals(RmlElement other) => Handle == other.Handle;
    public override bool Equals(object? obj) => obj is RmlElement e && Equals(e);
    public override int GetHashCode() => Handle.GetHashCode();
    public static bool operator ==(RmlElement a, RmlElement b) => a.Handle == b.Handle;
    public static bool operator !=(RmlElement a, RmlElement b) => a.Handle != b.Handle;
    public override string ToString() => IsNull ? "<null element>" : $"<{TagName} id=\"{Id}\">";
}

/// <summary>Modal behaviour when showing a document (<c>Rml::ModalFlag</c>).</summary>
public enum RmlModal
{
    None = 0,
    Modal = 1,
    Keep = 2,
}

/// <summary>Focus behaviour when showing a document (<c>Rml::FocusFlag</c>).</summary>
public enum RmlFocus
{
    None = 0,
    Document = 1,
    Keep = 2,
    Auto = 3,
}

/// <summary>
/// A borrowed <c>Rml::ElementDocument</c>: valid until closed (<see cref="Close"/> + the next context update),
/// replaced by <see cref="Reload"/>, or its context is destroyed. A document is also an element (<see cref="AsElement"/>).
/// </summary>
public readonly unsafe struct RmlDocument : IEquatable<RmlDocument>
{
    internal RmlDocument(nint handle) => Handle = handle;

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;

    private nint Valid => Handle != 0 ? Handle : throw new InvalidOperationException("The document is null.");

    public void Show(RmlModal modal = RmlModal.None, RmlFocus focus = RmlFocus.Auto, bool scrollIntoView = true) =>
        RmlException.ThrowIfFailed(RmlNative.DocumentShow(Valid, (int)modal, (int)focus, scrollIntoView ? 1 : 0), "Document.Show");

    public void Hide() => RmlException.ThrowIfFailed(RmlNative.DocumentHide(Valid), "DocumentHide");

    /// <summary>Schedules the document for unloading; the handle is invalid after the next context update.</summary>
    public void Close() => RmlException.ThrowIfFailed(RmlNative.DocumentClose(Valid), "DocumentClose");

    /// <summary>
    /// Hot reload: clears the style sheet and template caches, closes this document and loads its source again with the
    /// same visibility and modality. Returns the new document (this one is closed), or a null document on failure (this
    /// one is then untouched). Element handles and listeners of the old document detach.
    /// </summary>
    public RmlDocument Reload() => new(RmlNative.DocumentReload(Valid));

    /// <summary>Re-reads the style sheets only; keeps the DOM and its state.</summary>
    public bool ReloadStyleSheet() => RmlNative.DocumentReloadStyleSheet(Valid) == RmlNative.Ok;

    public bool IsVisible => RmlNative.DocumentIsVisible(Valid) == 1;

    public bool IsModal => RmlNative.DocumentIsModal(Valid) == 1;

    public void PullToFront() => RmlException.ThrowIfFailed(RmlNative.DocumentPullToFront(Valid), "DocumentPullToFront");

    public void PushToBack() => RmlException.ThrowIfFailed(RmlNative.DocumentPushToBack(Valid), "DocumentPushToBack");

    public string Title => RmlUtf8.Read(Valid, &ReadTitle) ?? "";

    private static int ReadTitle(nint h, byte* b, int c) => RmlNative.DocumentGetTitle(h, b, c);

    public string SourceUrl => RmlUtf8.Read(Valid, &ReadSource) ?? "";

    private static int ReadSource(nint h, byte* b, int c) => RmlNative.DocumentGetSourceUrl(h, b, c);

    public RmlElement AsElement() => new(RmlNative.DocumentAsElement(Valid));

    public static implicit operator RmlElement(RmlDocument document) => document.IsNull ? default : document.AsElement();

    public RmlElement GetElementById(ReadOnlySpan<char> id) => AsElement().GetElementById(id);

    public RmlElement QuerySelector(ReadOnlySpan<char> selector) => AsElement().QuerySelector(selector);

    public bool Equals(RmlDocument other) => Handle == other.Handle;
    public override bool Equals(object? obj) => obj is RmlDocument d && Equals(d);
    public override int GetHashCode() => Handle.GetHashCode();
    public static bool operator ==(RmlDocument a, RmlDocument b) => a.Handle == b.Handle;
    public static bool operator !=(RmlDocument a, RmlDocument b) => a.Handle != b.Handle;
    public RmlElement ToRmlElement() => AsElement();
}

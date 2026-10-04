namespace MainframeEngine.UI.Rml;

/// <summary>
/// An RmlUi context: a set of documents laid out, rendered and fed input together, at framebuffer-pixel dimensions.
/// Owns its documents and data models (<see cref="CreateDataModel"/>); <see cref="Dispose"/> destroys all of them.
/// </summary>
/// <remarks>
/// Input methods return <c>true</c> when RmlUi <b>consumed</b> the event — the opposite of RmlUi's raw
/// <c>Context::Process*</c> return value. The shim resolves that inversion once (<c>MFRMLUI_INPUT_CONSUMED</c>), so it
/// cannot be misread here.
/// </remarks>
public sealed unsafe class RmlContext : IDisposable
{
    private readonly RmlContextHandle _handle = new();
    private readonly List<RmlDataModel> _models = [];

    public RmlContext(string name, int width, int height, RmlRenderInterface renderInterface)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(renderInterface);
        RmlCore.RequireInitialised();
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        nint native;
        fixed (byte* p = n)
            native = RmlNative.ContextCreate(p, width, height, renderInterface.Handle);
        if (native == 0)
            throw new RmlException($"Creating context '{name}'", RmlNative.ErrorFailed);

        _handle.Attach(native);
        Name = name;
        RenderInterface = renderInterface;
        Width = width;
        Height = height;
    }

    public string Name { get; }

    public RmlRenderInterface RenderInterface { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Framebuffer pixels per density-independent pixel (<c>dp</c>).</summary>
    public float DensityIndependentPixelRatio { get; private set; } = 1f;

    public bool IsDisposed => _handle.IsClosed || _handle.IsInvalid;

    internal nint Handle => _handle.Value;

    /// <summary>Resizes the context (framebuffer pixels).</summary>
    public void SetDimensions(int width, int height)
    {
        if (width == Width && height == Height)
            return;
        RmlException.ThrowIfFailed(RmlNative.ContextSetDimensions(Handle, Math.Max(0, width), Math.Max(0, height)), "SetDimensions");
        Width = width;
        Height = height;
    }

    /// <summary>HiDPI: framebuffer pixels per <c>dp</c> (2 on a Retina display).</summary>
    public void SetDensityIndependentPixelRatio(float ratio)
    {
        if (ratio == DensityIndependentPixelRatio)
            return;
        RmlException.ThrowIfFailed(RmlNative.ContextSetDensityIndependentPixelRatio(Handle, ratio), "SetDensityIndependentPixelRatio");
        DensityIndependentPixelRatio = ratio;
    }

    /// <summary>Lays out documents, runs animations and data-model updates.</summary>
    public void Update() => RmlException.ThrowIfFailed(RmlNative.ContextUpdate(Handle), "Context.Update");

    /// <summary>Issues render-interface callbacks for the visible documents.</summary>
    public void Render() => RmlException.ThrowIfFailed(RmlNative.ContextRender(Handle), "Context.Render");

    /// <summary>Seconds until another update is needed (animations); +∞ when idle.</summary>
    public double NextUpdateDelay => RmlNative.ContextGetNextUpdateDelay(Handle);

    public void EnableMouseCursor(bool enable) => RmlException.ThrowIfFailed(RmlNative.ContextEnableMouseCursor(Handle, enable ? 1 : 0), "ContextEnableMouseCursor");

    /// <summary>Activates or deactivates an RCSS media-query theme (<c>@media (theme: name)</c>).</summary>
    public void ActivateTheme(string themeName, bool activate)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(themeName, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.ContextActivateTheme(Handle, p, activate ? 1 : 0), "ContextActivateTheme");
    }

    public RmlElement RootElement => new(RmlNative.ContextGetRootElement(Handle));

    public RmlElement HoverElement => new(RmlNative.ContextGetHoverElement(Handle));

    public RmlElement FocusElement => new(RmlNative.ContextGetFocusElement(Handle));

    public RmlElement GetElementAtPoint(float x, float y) => new(RmlNative.ContextGetElementAtPoint(Handle, x, y));

    public int DocumentCount => Math.Max(0, RmlNative.ContextGetNumDocuments(Handle));

    public RmlDocument GetDocument(int index) => new(RmlNative.ContextGetDocument(Handle, index));

    /// <summary>True while the mouse hovers or drags a UI element (not the context root).</summary>
    public bool IsMouseInteracting => RmlNative.ContextIsMouseInteracting(Handle) == 1;

    // ── Input: true = consumed ───────────────────────────────────────────────────────────────────────────────

    public bool ProcessMouseMove(int x, int y, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessMouseMove(Handle, x, y, (int)modifiers));

    public bool ProcessMouseButtonDown(int button, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessMouseButtonDown(Handle, button, (int)modifiers));

    public bool ProcessMouseButtonUp(int button, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessMouseButtonUp(Handle, button, (int)modifiers));

    /// <summary>RmlUi convention: positive <paramref name="deltaY"/> scrolls down (pass <c>-sdlWheelY</c>).</summary>
    public bool ProcessMouseWheel(float deltaX, float deltaY, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessMouseWheel(Handle, deltaX, deltaY, (int)modifiers));

    public bool ProcessMouseLeave() => Consumed(RmlNative.ContextProcessMouseLeave(Handle));

    public bool ProcessKeyDown(RmlKey key, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessKeyDown(Handle, (int)key, (int)modifiers));

    public bool ProcessKeyUp(RmlKey key, RmlKeyModifiers modifiers) =>
        Consumed(RmlNative.ContextProcessKeyUp(Handle, (int)key, (int)modifiers));

    /// <summary>Typed text (one or more characters), encoded without allocating.</summary>
    public bool ProcessTextInput(ReadOnlySpan<char> text)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var t = new RmlUtf8Arg(text, scratch);
        fixed (byte* p = t)
            return Consumed(RmlNative.ContextProcessTextInput(Handle, p));
    }

    /// <summary>Typed text as NUL-free UTF-8.</summary>
    public bool ProcessTextInputUtf8(ReadOnlySpan<byte> utf8)
    {
        Span<byte> z = utf8.Length < 128 ? stackalloc byte[utf8.Length + 1] : new byte[utf8.Length + 1];
        utf8.CopyTo(z);
        z[^1] = 0;
        fixed (byte* p = z)
            return Consumed(RmlNative.ContextProcessTextInput(Handle, p));
    }

    private static bool Consumed(int result) => result == RmlNative.InputConsumed;

    // ── Documents ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads an <c>.rml</c> document through the file interface (hidden until shown). Throws on failure.</summary>
    public RmlDocument LoadDocument(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var p = new RmlUtf8Arg(path, scratch);
        nint doc;
        fixed (byte* ptr = p)
            doc = RmlNative.ContextLoadDocument(Handle, ptr);
        return doc != 0 ? new RmlDocument(doc) : throw new RmlException($"Loading document '{path}'", RmlNative.ErrorFailed);
    }

    /// <summary>Loads RML text; <paramref name="sourceUrl"/> resolves relative paths and enables reload.</summary>
    public RmlDocument LoadDocumentFromMemory(string rml, string? sourceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(rml);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var r = new RmlUtf8Arg(rml, Span<byte>.Empty);
        using var s = new RmlUtf8Arg(sourceUrl, scratch);
        nint doc;
        fixed (byte* pr = r)
        fixed (byte* ps = s)
            doc = RmlNative.ContextLoadDocumentFromMemory(Handle, pr, ps);
        return doc != 0 ? new RmlDocument(doc) : throw new RmlException("Loading document from memory", RmlNative.ErrorFailed);
    }

    // ── Data models ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a data model. Bind everything before loading documents that use it (<c>data-model="name"</c>); the
    /// model lives until <see cref="RmlDataModel.Dispose"/> or this context is destroyed.
    /// </summary>
    public RmlDataModel CreateDataModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        nint native;
        fixed (byte* p = n)
            native = RmlNative.DataModelCreate(Handle, p);
        if (native == 0)
            throw new RmlException($"Creating data model '{name}'", RmlNative.ErrorFailed);
        var model = new RmlDataModel(this, name, native);
        _models.Add(model);
        return model;
    }

    internal void ForgetModel(RmlDataModel model) => _models.Remove(model);

    /// <summary>Unloads every document, removes the data models (their bindings are released) and destroys the context.</summary>
    public void Dispose()
    {
        if (_handle.IsClosed)
            return;
        foreach (var model in _models)
            model.MarkRemovedWithContext();
        _models.Clear();
        _handle.ReleaseNow();
    }

    public override string ToString() => $"RmlContext '{Name}' {Width}x{Height}";
}

/// <summary>RmlUi's visual debugger (element inspector, event log, outlines). One per process.</summary>
public static class RmlDebugger
{
    /// <summary>True after <see cref="Initialise"/> until <see cref="Shutdown"/> (or <see cref="RmlCore.Shutdown"/>).</summary>
    public static bool IsInitialised { get; private set; }

    /// <summary>The context the debugger's own documents live in.</summary>
    public static RmlContext? HostContext { get; private set; }

    /// <summary>Creates the debugger documents in <paramref name="host"/> and starts debugging it.</summary>
    public static void Initialise(RmlContext host)
    {
        ArgumentNullException.ThrowIfNull(host);
        RmlException.ThrowIfFailed(RmlNative.DebuggerInitialise(host.Handle), "Debugger.Initialise");
        IsInitialised = true;
        HostContext = host;
        _visible = false;
    }

    /// <summary>Switches the inspected context.</summary>
    public static void SetContext(RmlContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RmlException.ThrowIfFailed(RmlNative.DebuggerSetContext(context.Handle), "Debugger.SetContext");
    }

    private static bool _visible;

    /// <summary>
    /// Whether the debugger is shown. Tracked here because RmlUi only recomputes the menu's visibility on the next
    /// context update (<see cref="IsVisibleInLayout"/> reports RmlUi's view).
    /// </summary>
    public static bool Visible
    {
        get => IsInitialised && _visible;
        set
        {
            if (!IsInitialised)
                throw new InvalidOperationException("The RmlUi debugger is not initialised.");
            RmlException.ThrowIfFailed(RmlNative.DebuggerSetVisible(value ? 1 : 0), "DebuggerSetVisible");
            _visible = value;
        }
    }

    /// <summary>RmlUi's own answer: the debugger menu is visible as of the last update of its host context.</summary>
    public static bool IsVisibleInLayout => IsInitialised && RmlNative.DebuggerIsVisible() == 1;

    public static void Shutdown()
    {
        if (!IsInitialised)
            return;
        IsInitialised = false;
        HostContext = null;
        _visible = false;
        if (RmlCore.IsInitialised)
            RmlException.ThrowIfFailed(RmlNative.DebuggerShutdown(), "DebuggerShutdown");
    }

    /// <summary>The library shut down (the debugger went with it).</summary>
    internal static void Reset()
    {
        IsInitialised = false;
        HostContext = null;
        _visible = false;
    }
}

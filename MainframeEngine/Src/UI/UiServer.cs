using System.Numerics;
using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace MainframeEngine;

/// <summary>Configuration of the <see cref="UiServer"/>.</summary>
public sealed record UiServerOptions
{
    /// <summary>Loads every <c>.ttf</c>/<c>.otf</c> in <see cref="FontDirectory"/> at start-up.</summary>
    public bool LoadDefaultFonts { get; init; } = true;

    /// <summary>The engine's bundled fonts (Lato Latin, Roboto Mono; OFL).</summary>
    public string FontDirectory { get; init; } = "Content/UI/fonts";

    /// <summary>Reload <c>.rml</c>/<c>.rcss</c>/images when they change on disk. Defaults to on in Debug builds.</summary>
    public bool HotReload { get; init; } = DefaultHotReload;

    /// <summary>
    /// Folders standing in for <c>Content/</c> (e.g. the game project's source <c>Content</c>): documents load from
    /// them first, and hot reload watches them, so edits apply without rebuilding.
    /// </summary>
    public IReadOnlyList<string> SourceContentDirectories { get; init; } = [];

    /// <summary>Toggles RmlUi's visual debugger.</summary>
    public Key DebuggerKey { get; init; } = Key.F8;

    /// <summary>Framebuffer size used when there is no window (headless tests).</summary>
    public Vector2 HeadlessViewport { get; init; } = new(1280, 720);

    /// <summary>
    /// Fixed content scale: the dp ratio of <see cref="UiScaleMode.Dpi"/> layers (and the visual debugger) whatever the
    /// display's backing scale. 0 (default) follows the display (<see cref="UiServer.PixelScale"/>). The engine sets it
    /// from <see cref="EngineOptions.ContentScale"/>.
    /// </summary>
    public float ContentScale { get; init; }

    /// <summary>
    /// Localization (M9): document sources go through its <see cref="ITextTranslator.PrepareDocument"/>, text nodes
    /// through its <see cref="ITextTranslator.TryTranslateMarkup(ReadOnlySpan{byte}, out string)"/>, and its
    /// <see cref="ITextTranslator.LocaleChanged"/> reloads the documents. Defaults to the engine's catalogs
    /// (<see cref="Localization.TextTranslator.Current"/>, i.e. <see cref="Tr"/>); null leaves UI text untranslated.
    /// </summary>
    public ITextTranslator? TextTranslator { get; init; } = Localization.TextTranslator.Current;

    /// <summary>
    /// The per-locale font table (<see cref="FontFallbackTable"/>, a <c>.mres</c>) whose faces are loaded as fallback
    /// faces at start-up and on every locale change; nothing happens when the file does not exist. Null disables it.
    /// </summary>
    public string? FontFallbackTablePath { get; init; } = FontFallbackTable.DefaultPath;

    public const bool DefaultHotReload =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The assembly metadata key Debug builds record their project's Content folder under.</summary>
    public const string ContentSourceMetadataKey = "MainframeContentSource";

    /// <summary>
    /// The source Content folders Debug builds of <paramref name="assemblies"/> recorded
    /// (<c>[AssemblyMetadata("MainframeContentSource", ...)]</c>) and that exist on this machine; empty for Release
    /// builds and installed games.
    /// </summary>
    public static IReadOnlyList<string> SourceDirectoriesOf(params System.Reflection.Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var directories = new List<string>();
        foreach (var assembly in assemblies)
            foreach (var attribute in assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (attribute is System.Reflection.AssemblyMetadataAttribute { Key: ContentSourceMetadataKey, Value: { Length: > 0 } value } &&
                    Directory.Exists(value))
                    directories.Add(value);
        return directories;
    }
}

/// <summary>
/// The game UI server (docs/design/game-ui.md): owns RmlUi (initialise/shutdown, fonts, the system and file
/// interfaces), the render interface (<see cref="VulkanUiRenderer"/> on the engine's device, or
/// <see cref="NullUiRenderer"/> headless), one context per <see cref="UiLayer"/>, input routing, hot reload and the
/// visual debugger. Registered by <see cref="Engine"/>; nodes reach it through <c>Tree.Servers.Get&lt;UiServer&gt;()</c>.
/// </summary>
/// <remarks>
/// <para><b>Frame:</b> after the scene tree's process step (<see cref="IFrameServer"/>) every visible layer's
/// documents are loaded if needed, its context is sized to the framebuffer, updated and rendered into the UI command
/// list; the GPU work runs in the overlay pass after tonemapping.</para>
/// <para><b>Input:</b> as an <see cref="IInputServer"/> the server sees every event before the scene tree's nodes,
/// layers top to bottom. RmlUi's raw <c>Process*</c> return is inverted (true = not consumed); the binding resolves it
/// so <see cref="HandleInput"/> returns true only when the UI consumed the event. A mouse button pressed outside the
/// UI keeps the mouse with the game until released; a focused text field takes all keys; a modal document blocks
/// everything below it. Gamepad D-pad/stick and A/B drive RmlUi's focus navigation.</para>
/// </remarks>
public sealed class UiServer : IFrameServer, IInputServer
{
    private const string DebuggerContextName = "__mainframe_debugger";
    private const float NavRepeatDelay = 0.4f;
    private const float NavRepeatInterval = 0.1f;
    private const float StickPress = 0.6f;
    private const float StickRelease = 0.4f;

    private readonly IWindow? _window;
    private readonly IInputContext? _input;
    private readonly UiServerOptions _options;
    private readonly UiSystemInterface _system;
    private readonly List<UiLayer> _layers = [];
    private readonly UiHotReload? _hotReload;
    private readonly UiImeWatch? _ime;
    private readonly ITextTranslator? _textTranslator;
    private readonly HashSet<string> _fallbackFonts = new(StringComparer.Ordinal);
    private int _localeChanged; // set by LocaleChanged (any thread), applied at the start of Process
    private bool _layersDirty;
    private int _contextCounter;
    private RmlContext? _debuggerContext;
    private bool _disposed;

    // Input state.
    private readonly bool[] _uiKeys = new bool[(int)Key.Menu + 1]; // keys whose press the UI consumed
    private int _uiPadButtons;                                     // gamepad buttons whose press the UI consumed
    private int _uiMouseButtons;                                   // mouse buttons whose press the UI consumed
    private RmlKeyModifiers _modifiers;
    private char _highSurrogate;
    private int _gameMouseButtons;   // buttons pressed while the game had the mouse
    private UiLayer? _hoverLayer;
    private RmlKey _navKey;
    private float _navHeldFor;
    private float _navRepeatIn;
    private RmlKey _stickKey;

    public UiServer(IRenderer? renderer = null, IWindow? window = null, IInputContext? input = null, UiServerOptions? options = null)
    {
        _options = options ?? new UiServerOptions();
        _window = window;
        _input = input;
        if (RmlCore.IsInitialised)
            throw new InvalidOperationException("RmlUi is already initialised: only one UiServer can exist at a time.");

        RmlCore.EnsureLibrary(); // clear error when the native library is missing or incompatible

        Files = new UiFileInterface();
        foreach (var directory in _options.SourceContentDirectories)
            Files.AddSourceDirectory(directory);
        _textTranslator = _options.TextTranslator;
        if (_textTranslator is not null)
            Files.DocumentPreprocessor = _textTranslator.PrepareDocument; // no-tr marks, translated attributes
        if (_options.HotReload)
            foreach (var directory in UiServerOptions.SourceDirectoriesOf(typeof(UiServer).Assembly)) // the engine's own widgets
                Files.AddSourceDirectory(directory);

        _system = new UiSystemInterface(window, input);
        RmlCore.Initialise(_system, Files);
        try
        {
            RenderInterface = renderer is IVulkanContext vk
                ? new VulkanUiRenderer(vk, OpenFile)
                : new NullUiRenderer();
            if (_options.LoadDefaultFonts)
                LoadFonts(_options.FontDirectory);
            if (_textTranslator is not null)
                LoadLocaleFonts();
        }
        catch
        {
            RmlCore.Shutdown();
            (RenderInterface as IDisposable)?.Dispose();
            throw;
        }

        if (_textTranslator is not null)
        {
            Translator = TranslateText;
            _textTranslator.LocaleChanged += OnLocaleChanged;
        }

        if (_options.HotReload)
        {
            _hotReload = new UiHotReload();
            _hotReload.Watch(Path.Combine(ContentPaths.Root, "UI"));
            foreach (var directory in Files.SourceDirectories)
                _hotReload.Watch(directory);
        }

        _ime = UiImeWatch.TryCreate(window);
        s_live = new WeakReference<UiServer>(this);
        Log.Info($"[UI] RmlUi {RmlCore.RmlUiVersion} (mfrmlui ABI {RmlCore.AbiVersion >> 16}.{RmlCore.AbiVersion & 0xFFFF}), " +
                 $"{(RenderInterface is VulkanUiRenderer ? "Vulkan renderer" : "headless")}");
    }

    /// <summary>The render interface every context shares (font atlases are shared too).</summary>
    public RmlRenderInterface RenderInterface { get; } = null!;

    /// <summary>The Vulkan renderer, when the engine has one.</summary>
    public VulkanUiRenderer? Renderer => RenderInterface as VulkanUiRenderer;

    public UiFileInterface Files { get; }

    /// <summary>Layers in draw order (lowest <see cref="UiLayer.Layer"/> first).</summary>
    public IReadOnlyList<UiLayer> Layers
    {
        get
        {
            SortLayersIfNeeded();
            return _layers;
        }
    }

    /// <summary>UI time in seconds (animations, transitions), advanced by the frame delta.</summary>
    public double Time => _system.Time;

    /// <summary>Framebuffer size the contexts are laid out at.</summary>
    public Vector2 ViewportSize { get; private set; }

    /// <summary>Framebuffer pixels per OS window point (2 on Retina): converts mouse and IME positions.</summary>
    public float PixelScale { get; private set; } = 1f;

    /// <summary>
    /// Pixels per layout point: the dp ratio of <see cref="UiScaleMode.Dpi"/> layers. <see cref="UiServerOptions.ContentScale"/>
    /// when set, otherwise <see cref="PixelScale"/>.
    /// </summary>
    public float ContentScale => _options.ContentScale > 0f ? _options.ContentScale : PixelScale;

    /// <summary>
    /// Localization hook: RmlUi's <c>TranslateString</c>, called for every text node RmlUi creates (and for data-bound
    /// text whenever its value changes). Set to the <see cref="UiServerOptions.TextTranslator"/>'s
    /// <c>TryTranslateMarkup</c> by default (docs/design/localization.md#game-ui-rmlui); replace or clear it to opt out.
    /// </summary>
    public UiTranslator? Translator
    {
        get => _system.Translator;
        set => _system.Translator = value;
    }

    /// <summary>The translator UI text and documents go through (<see cref="UiServerOptions.TextTranslator"/>), or null.</summary>
    public ITextTranslator? TextTranslator => _textTranslator;

    /// <summary>Fallback font faces loaded from the <see cref="FontFallbackTable"/> so far (content paths).</summary>
    public IReadOnlyCollection<string> FallbackFonts => _fallbackFonts;

    /// <summary>False while the engine skips rendering (minimised): contexts update but record no draw commands.</summary>
    public Func<bool>? CanRender { get; set; }

    /// <summary>The current IME composition (empty when none).</summary>
    public string Composition { get; private set; } = "";

    /// <summary>The IME composition changed (text, cursor position within it).</summary>
    public event Action<string, int>? CompositionChanged;

    /// <summary>True while a UI text field has keyboard focus.</summary>
    public bool TextInputActive => _system.TextInputActive;

    /// <summary>True when the mouse is over (or dragging) an interactive UI element of a visible layer.</summary>
    public bool IsPointerOverUi
    {
        get
        {
            for (var i = 0; i < _layers.Count; i++)
                if (_layers[i] is { Visible: true, Context: { IsDisposed: false } context } && context.IsMouseInteracting)
                    return true;
            return RmlDebugger.Visible && _debuggerContext is { IsDisposed: false } debugger && debugger.IsMouseInteracting;
        }
    }

    // ── Fonts and textures ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads every font in a content directory (through the UI file interface).</summary>
    public void LoadFonts(string directory)
    {
        var full = Files.ResolveDirectory(directory);
        if (full is null)
        {
            Log.Warning($"[UI] Font directory '{directory}' not found.");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(full).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                RmlCore.LoadFontFace(file);
        }
    }

    /// <summary>Publishes an engine texture as <c>engine://name</c> (no-op without the Vulkan renderer).</summary>
    public void RegisterTexture(string name, GpuTexture texture, UiTextureConversion conversion = UiTextureConversion.Auto) =>
        Renderer?.RegisterTexture(name, texture, conversion);

    /// <summary>Publishes a render target's colour attachment as <c>engine://name</c> (no-op without the Vulkan renderer).</summary>
    public void RegisterTexture(string name, RenderTarget target, int colorAttachment = 0, UiTextureConversion conversion = UiTextureConversion.Auto) =>
        Renderer?.RegisterTexture(name, target, colorAttachment, conversion);

    public bool UnregisterTexture(string name) => Renderer?.UnregisterTexture(name) ?? false;

    private Stream? OpenFile(string path) => Files.Open(path);

    // ── Localization ─────────────────────────────────────────────────────────────────────────────────────────

    // RmlLocalization.OptOutMarker (U+FDD0) in UTF-8.
    private static ReadOnlySpan<byte> OptOutMarkerUtf8 => [0xEF, 0xB7, 0x90];

    /// <summary>
    /// RmlUi's <c>TranslateString</c>. Plain text runs go to <see cref="ITextTranslator.TryTranslateMarkup(ReadOnlySpan{byte}, out string)"/>.
    /// A run with a data expression (<c>Health {{ health }}</c>) is the template of a data view, which RmlUi sends
    /// through here again after every substitution (<c>Salud 72</c>): the template is translated once and kept with
    /// the opt-out marker in front, so each substituted text arrives marked and is returned unmarked without a lookup
    /// (no double translation of values, no "missing translation" per value, no allocation per frame). The data view
    /// replaces the template's text on the context's first update, before anything renders.
    /// </summary>
    private bool TranslateText(ReadOnlySpan<byte> utf8, RmlStringSink output)
    {
        if (_textTranslator is not { } translator)
            return false;
        if (utf8.IndexOf("{{"u8) < 0)
        {
            if (utf8.StartsWith(OptOutMarkerUtf8))
            {
                output.Set(utf8[OptOutMarkerUtf8.Length..]); // no-tr text or a substituted data view: as is, no string
                return true;
            }

            if (!translator.TryTranslateMarkup(utf8, out var text))
                return false;
            output.Set(text.AsSpan());
            return true;
        }

        if (utf8.StartsWith(OptOutMarkerUtf8))
            return false; // a no-tr template (PrepareDocument marked it): keep the marker for its substituted text

        if (translator.TryTranslateMarkup(utf8, out var translated))
        {
            output.Set(string.Concat(RmlLocalization.OptOutMarker.ToString(), translated).AsSpan());
            return true;
        }

        // Untranslated template ({{ fps }}, or no catalog entry): the same text, marked.
        var length = OptOutMarkerUtf8.Length + utf8.Length;
        var rented = length > 512 ? System.Buffers.ArrayPool<byte>.Shared.Rent(length) : null;
        var marked = rented is null ? stackalloc byte[length] : rented.AsSpan(0, length);
        try
        {
            OptOutMarkerUtf8.CopyTo(marked);
            utf8.CopyTo(marked[OptOutMarkerUtf8.Length..]);
            output.Set(marked);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        return true;
    }

    // May run on any thread and inside an RmlUi callback (a language dropdown's data binding): only flag it here.
    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e) => Interlocked.Exchange(ref _localeChanged, 1);

    /// <summary>
    /// Applies a locale change at a safe point (start of <see cref="Process"/>, no RmlUi callback running): loads the
    /// new locale's fallback fonts, drops cached templates (their attributes were translated) and reloads every
    /// loaded document, so text nodes and attributes are translated again. Data models survive the reload.
    /// </summary>
    private void ApplyLocaleChange()
    {
        LoadLocaleFonts();
        RmlCore.ClearTemplateCache();
        var reloaded = 0;
        foreach (var layer in _layers)
        {
            foreach (var document in layer.DocumentList.ToArray())
            {
                if (!document.IsLoaded)
                    continue; // loads in the new locale when first needed
                document.Reload();
                reloaded++;
            }
        }

        Log.Info($"[UI] Locale '{_textTranslator?.Locale}': reloaded {reloaded} document(s)");
    }

    /// <summary>Loads the faces the <see cref="FontFallbackTable"/> lists for the current locale chain (once each).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A broken font table must not stop the UI (or a locale switch); it is logged.")]
    private void LoadLocaleFonts()
    {
        if (_textTranslator is null || _options.FontFallbackTablePath is not { Length: > 0 } tablePath)
            return;
        IReadOnlyList<string> fonts;
        try
        {
            var table = FontFallbackTable.TryLoad(tablePath);
            if (table is null)
                return;
            fonts = table.Resolve(_textTranslator.LocaleChain);
            table.Release();
        }
        catch (Exception e)
        {
            Log.Warning($"[UI] Could not read the font fallback table '{tablePath}': {e.Message}");
            return;
        }

        foreach (var font in fonts)
        {
            if (_fallbackFonts.Contains(font))
                continue;
            var full = Files.ResolvePath(font);
            if (full is null)
            {
                Log.Warning($"[UI] Fallback font '{font}' ({tablePath}) not found.");
                continue;
            }

            try
            {
                RmlCore.LoadFontFace(full, fallbackFace: true); // RmlUi cannot unload one face: faces accumulate
                _fallbackFonts.Add(font);
            }
            catch (RmlException e)
            {
                Log.Warning($"[UI] Could not load fallback font '{font}': {e.Message}");
            }
        }
    }

    // ── Layers ───────────────────────────────────────────────────────────────────────────────────────────────

    internal RmlContext AddLayer(UiLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateViewport();
        var context = new RmlContext($"layer{++_contextCounter}:{layer.Name}", (int)ViewportSize.X, (int)ViewportSize.Y, RenderInterface);
        context.SetDensityIndependentPixelRatio(layer.ComputeDpRatio(ViewportSize, ContentScale));
        _layers.Add(layer);
        _layersDirty = true;
        return context;
    }

    internal void RemoveLayer(UiLayer layer)
    {
        _layers.Remove(layer);
        if (ReferenceEquals(_hoverLayer, layer))
            _hoverLayer = null;
        if (RmlDebugger.IsInitialised && _debuggerContext is not null && _layers.Count > 0)
            RmlDebugger.SetContext(TopLayerContext() ?? _debuggerContext);
        layer.Context?.Dispose();
    }

    internal void SortLayers() => _layersDirty = true;

    private void SortLayersIfNeeded()
    {
        if (!_layersDirty)
            return;
        _layersDirty = false;
        // Stable insertion sort (layers are few; no allocation).
        for (var i = 1; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            var j = i - 1;
            while (j >= 0 && _layers[j].Layer > layer.Layer)
            {
                _layers[j + 1] = _layers[j];
                j--;
            }

            _layers[j + 1] = layer;
        }
    }

    private RmlContext? TopLayerContext()
    {
        SortLayersIfNeeded();
        for (var i = _layers.Count - 1; i >= 0; i--)
            if (_layers[i].Visible && _layers[i].Context is { } c)
                return c;
        return null;
    }

    // ── Code reload ──────────────────────────────────────────────────────────────────────────────────────────

    private static WeakReference<UiServer>? s_live; // RmlUi is process-global: at most one server exists

    /// <summary>
    /// Code reload (<see cref="GameAssemblyLoader.Unload"/>, after the game's nodes were freed): removes data models whose
    /// bindings still call or hold code from <paramref name="assembly"/> (a game that bound a model straight on a layer's
    /// context and never disposed it), finishes RmlUi's deferred unload of documents closed since the last frame (their
    /// element listeners hold game delegates) and releases queued handles — so the UI keeps no reference into the
    /// unloading game assembly without waiting for a frame. Must run on the RmlUi thread.
    /// </summary>
    internal static void ReleaseCodeOf(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (s_live is null || !s_live.TryGetTarget(out var server) || server._disposed || !RmlCore.IsInitialised)
            return;
        if (RmlCore.OwnerThreadId != Environment.CurrentManagedThreadId || RmlCore.IsInCallback)
        {
            Log.Warning("[UI] Game code unloaded off the UI thread (or inside a UI callback); UI references to it are released at the next frame.");
            return;
        }

        server.ReleaseCode(assembly);
    }

    private void ReleaseCode(System.Reflection.Assembly assembly)
    {
        if (Translator is { } translator && CodeReload.IsFrom(translator, assembly))
        {
            Log.Warning($"[UI] UiServer.Translator was game code ({assembly.GetName().Name}); restored the default translator.");
            Translator = _textTranslator is null ? null : TranslateText;
        }

        RmlCore.ProcessPendingReleases();
        foreach (var layer in _layers)
        {
            if (layer.Context is not { IsDisposed: false } context)
                continue;
            foreach (var model in context.DataModels.ToArray())
            {
                if (!model.UsesCode(assembly))
                    continue;
                Log.Warning($"[UI] Data model '{model.Name}' was still bound to unloaded game code ({assembly.GetName().Name}); " +
                            "removed it. Dispose models when their owner leaves the tree (UiDocument.CreateDataModel does).");
                model.Dispose();
            }

            context.Update(); // RmlUi destroys documents closed since the last update here (and their listeners)
        }

        RmlCore.ProcessPendingReleases();
    }

    // ── Frame ────────────────────────────────────────────────────────────────────────────────────────────────

    public void Process(in GameTime gameTime)
    {
        if (_disposed)
            return;

        RmlCore.ProcessPendingReleases();
        if (Interlocked.Exchange(ref _localeChanged, 0) != 0)
            ApplyLocaleChange();
        var delta = gameTime.DeltaTime;
        _system.Time += delta;

        if (_ime is not null && _ime.TryTake(out var composition, out var cursor))
        {
            Composition = composition;
            CompositionChanged?.Invoke(composition, cursor);
        }

        UpdateNavigationRepeat(delta);
        ApplyHotReload();
        UpdateViewport();
        SortLayersIfNeeded();

        for (var i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.Context is not { } context)
                continue;
            context.SetDimensions((int)ViewportSize.X, (int)ViewportSize.Y);
            context.SetDensityIndependentPixelRatio(layer.ComputeDpRatio(ViewportSize, ContentScale));
            var documents = layer.DocumentList;
            for (var d = 0; d < documents.Count; d++)
                documents[d].PrepareFrame();
            context.Update();
        }

        var debugger = RmlDebugger.Visible ? _debuggerContext : null;
        if (debugger is not null)
        {
            debugger.SetDimensions((int)ViewportSize.X, (int)ViewportSize.Y);
            debugger.SetDensityIndependentPixelRatio(ContentScale);
            debugger.Update();
        }

        if (CanRender is { } canRender && !canRender())
            return;

        switch (RenderInterface)
        {
            case VulkanUiRenderer vulkan:
                vulkan.BeginFrame();
                break;
            case NullUiRenderer headless:
                headless.BeginFrame();
                break;
        }

        for (var i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (layer.Visible && layer.Context is { } context)
                context.Render();
        }

        debugger?.Render();
    }

    private void UpdateViewport()
    {
        if (_window is null)
        {
            ViewportSize = _options.HeadlessViewport;
            PixelScale = 1f;
        }
        else
        {
            var fb = WindowPixels.FramebufferSize(_window);
            ViewportSize = new Vector2(Math.Max(1, fb.X), Math.Max(1, fb.Y));
            PixelScale = WindowPixels.Scale(_window, fb);
        }

        _system.PixelScale = PixelScale;
    }

    // ── Hot reload ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies a batch of file changes (hot reload; also callable by tools and tests).</summary>
    public void Reload(UiReloadKind kind)
    {
        if (kind == UiReloadKind.None)
            return;
        RmlCore.ClearStyleSheetCache();
        RmlCore.ClearTemplateCache();
        if ((kind & UiReloadKind.Textures) != 0)
            RmlCore.ReleaseTextures(RenderInterface);

        var full = (kind & UiReloadKind.Documents) != 0;
        foreach (var layer in _layers)
        {
            foreach (var document in layer.DocumentList.ToArray())
            {
                document.ClearLoadFailure();
                if (!document.IsLoaded)
                    document.EnsureLoaded();
                else if (full)
                    document.Reload();
                else if ((kind & UiReloadKind.StyleSheets) != 0)
                    document.ReloadStyleSheet();
            }
        }

        Log.Info($"[UI] Hot reload: {kind}");
    }

    private void ApplyHotReload()
    {
        if (_hotReload is not null && _hotReload.TryTake(out var kind))
            Reload(kind);
    }

    // ── Debugger ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows or hides RmlUi's visual debugger (element inspector, event log, outlines) over the top layer.</summary>
    public bool DebuggerVisible
    {
        get => RmlDebugger.Visible;
        set
        {
            if (value == DebuggerVisible)
                return;
            if (value)
            {
                var target = TopLayerContext();
                if (target is null)
                {
                    Log.Info("[UI] Nothing to debug: no visible UI layer.");
                    return;
                }

                if (!RmlDebugger.IsInitialised)
                {
                    _debuggerContext ??= new RmlContext(DebuggerContextName, (int)ViewportSize.X, (int)ViewportSize.Y, RenderInterface);
                    RmlDebugger.Initialise(_debuggerContext);
                }

                RmlDebugger.SetContext(target);
            }

            RmlDebugger.Visible = value;
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Offers an input event to the UI (top layer first). True when the UI consumed it.</summary>
    public bool HandleInput(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (_disposed)
            return false;

        switch (inputEvent)
        {
            case InputEventKey key:
                return HandleKey(key);
            case InputEventText text:
                return HandleText(text.Character);
            case InputEventMouseMotion motion:
                return HandleMouseMove(motion.Position);
            case InputEventMouseButton button:
                return HandleMouseButton(button);
            case InputEventMouseWheel wheel:
                return !MouseIsRaw() && Route(InputKind.Wheel, wheel.Delta.X, -wheel.Delta.Y);
            case InputEventGamepadButton pad:
                return HandleGamepadButton(pad);
            case InputEventGamepadAxis axis when axis.Axis == GamepadAxis.LeftStick:
                return HandleStick(axis.Value);
            default:
                return false;
        }
    }

    private enum InputKind : byte
    {
        KeyDown,
        KeyUp,
        Text,
        MouseDown,
        MouseUp,
        Wheel,
    }

    private bool HandleKey(InputEventKey e)
    {
        var modifier = UiInputMap.ModifierOf(e.Key);
        if (modifier != RmlKeyModifiers.None)
            _modifiers = e.Pressed ? _modifiers | modifier : _modifiers & ~modifier;

        if (e.Pressed && e.Key == _options.DebuggerKey && e.Key != Key.Unknown)
        {
            DebuggerVisible = !DebuggerVisible;
            return true;
        }

        var key = UiInputMap.ToRmlKey(e.Key);
        if (key == RmlKey.Unknown)
            return _system.TextInputActive;
        var consumed = Route(e.Pressed ? InputKind.KeyDown : InputKind.KeyUp, (int)key, 0) || _system.TextInputActive;

        // A release follows its press: the game never sees the up of a key the UI took (and vice versa).
        var index = (int)e.Key;
        if ((uint)index < (uint)_uiKeys.Length)
        {
            if (e.Pressed)
            {
                _uiKeys[index] = consumed;
            }
            else
            {
                consumed = _uiKeys[index];
                _uiKeys[index] = false;
            }
        }

        return consumed; // typing into a field never reaches the game
    }

    private bool HandleText(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return _system.TextInputActive;
        }

        Span<char> text = stackalloc char[2];
        var length = 0;
        if (char.IsLowSurrogate(c) && _highSurrogate != '\0')
            text[length++] = _highSurrogate;
        _highSurrogate = '\0';
        if (char.IsControl(c))
            return _system.TextInputActive; // control characters arrive as keys
        text[length++] = c;
        return RouteText(text[..length]) || _system.TextInputActive;
    }

    private bool MouseIsRaw()
    {
        if (_input is null)
            return false;
        var mice = _input.Mice;
        for (var i = 0; i < mice.Count; i++)
            if (mice[i].Cursor.CursorMode is CursorMode.Raw or CursorMode.Disabled)
                return true;
        return false;
    }

    private bool HandleMouseMove(Vector2 position)
    {
        if (MouseIsRaw() || _gameMouseButtons != 0)
            return false; // the game has the mouse (camera look, a drag that started in the world)

        var x = (int)(position.X * PixelScale);
        var y = (int)(position.Y * PixelScale);
        if (RmlDebugger.Visible && _debuggerContext is not null && _debuggerContext.ProcessMouseMove(x, y, _modifiers))
            return true;

        SortLayersIfNeeded();
        UiLayer? consumer = null;
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.Context is not { } context)
                continue;
            if (context.ProcessMouseMove(x, y, _modifiers) || layer.HasModalDocument)
            {
                consumer = layer;
                break;
            }
        }

        // The layer under the cursor changed: lower layers lose their hover state.
        if (!ReferenceEquals(consumer, _hoverLayer) && _hoverLayer is { Visible: true, Context: { IsDisposed: false } previous } &&
            (consumer is null || consumer.Layer > _hoverLayer.Layer))
            previous.ProcessMouseLeave();
        _hoverLayer = consumer;
        return consumer is not null || _uiMouseButtons != 0; // a UI drag keeps the mouse even off the element
    }

    private bool HandleMouseButton(InputEventMouseButton e)
    {
        if (MouseIsRaw())
            return false;
        var button = UiInputMap.ToRmlButton(e.Button);
        if (button < 0 || button > 30)
            return false;
        var bit = 1 << button;

        if (e.Pressed)
        {
            if (_gameMouseButtons != 0)
            {
                _gameMouseButtons |= bit; // still a game drag
                return false;
            }

            if ((_uiMouseButtons & ~bit) == 0)
                HandleMouseMove(e.Position); // the press position may differ from the last move
            var consumed = Route(InputKind.MouseDown, button, 0);
            if (consumed)
                _uiMouseButtons |= bit;
            else
                _gameMouseButtons |= bit;
            return consumed;
        }

        if ((_gameMouseButtons & bit) != 0)
        {
            _gameMouseButtons &= ~bit;
            return false;
        }

        // A press the UI took (e.g. a slider drag) ends in the UI even if released over the world.
        var routed = Route(InputKind.MouseUp, button, 0);
        var owned = (_uiMouseButtons & bit) != 0;
        _uiMouseButtons &= ~bit;
        return routed || owned;
    }

    private bool HandleGamepadButton(InputEventGamepadButton e)
    {
        var key = UiInputMap.ToNavigationKey(e.Button);
        if (key == RmlKey.Unknown)
            return false;
        var bit = 1 << Math.Clamp((int)e.Button, 0, 30);
        if (!e.Pressed)
        {
            if (_navKey == key)
                _navKey = RmlKey.Unknown;
            Route(InputKind.KeyUp, (int)key, 0);
            var wasUi = (_uiPadButtons & bit) != 0;
            _uiPadButtons &= ~bit;
            return wasUi;
        }

        var consumed = Navigate(key);
        if (consumed)
        {
            _uiPadButtons |= bit;
            if (IsDirection(key))
            {
                _navKey = key;
                _navHeldFor = 0;
                _navRepeatIn = NavRepeatDelay;
            }
        }

        return consumed;
    }

    private bool HandleStick(Vector2 value)
    {
        var threshold = _stickKey == RmlKey.Unknown ? StickPress : StickRelease;
        var key = UiInputMap.StickDirection(value.X, value.Y, threshold);
        if (key == _stickKey)
            return key != RmlKey.Unknown;

        var consumed = false;
        if (_stickKey != RmlKey.Unknown)
        {
            Route(InputKind.KeyUp, (int)_stickKey, 0);
            if (_navKey == _stickKey)
                _navKey = RmlKey.Unknown;
        }

        _stickKey = key;
        if (key != RmlKey.Unknown)
        {
            _navKey = key;
            _navHeldFor = 0;
            _navRepeatIn = NavRepeatDelay;
            consumed = Navigate(key);
        }

        return consumed;
    }

    private static bool IsDirection(RmlKey key) => key is RmlKey.Up or RmlKey.Down or RmlKey.Left or RmlKey.Right;

    private void UpdateNavigationRepeat(float delta)
    {
        if (_navKey == RmlKey.Unknown)
            return;
        _navHeldFor += delta;
        _navRepeatIn -= delta;
        if (_navRepeatIn > 0)
            return;
        _navRepeatIn = NavRepeatInterval;
        Navigate(_navKey);
    }

    /// <summary>
    /// A navigation key press: when nothing in the top focusable layer has focus yet, the first press focuses its
    /// first tab-able element (as Tab would); otherwise the key goes to RmlUi (nav-* properties, Return clicks).
    /// </summary>
    private bool Navigate(RmlKey key)
    {
        SortLayersIfNeeded();
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.Context is not { } context)
                continue;

            if (IsDirection(key) && !HasElementFocus(context.FocusElement))
            {
                context.ProcessKeyDown(RmlKey.Tab, RmlKeyModifiers.None);
                if (HasElementFocus(context.FocusElement))
                    return true;
            }
            else if (context.ProcessKeyDown(key, _modifiers))
            {
                return true;
            }

            if (layer.HasModalDocument)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="focus"/> is an element inside a document — not nothing, the context root (no owner
    /// document) or a document itself.
    /// </summary>
    internal static bool HasElementFocus(RmlElement focus)
    {
        if (focus.IsNull)
            return false;
        var document = focus.OwnerDocument;
        return !document.IsNull && focus != document.AsElement();
    }

    private bool RouteText(ReadOnlySpan<char> text)
    {
        if (RmlDebugger.Visible && _debuggerContext is not null && _debuggerContext.ProcessTextInput(text))
            return true;
        SortLayersIfNeeded();
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.Context is not { } context)
                continue;
            if (context.ProcessTextInput(text) || layer.HasModalDocument)
                return true;
        }

        return false;
    }

    private bool Route(InputKind kind, float a, float b)
    {
        if (RmlDebugger.Visible && _debuggerContext is not null && Dispatch(_debuggerContext, kind, a, b))
            return true;
        SortLayersIfNeeded();
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || layer.Context is not { } context)
                continue;
            if (Dispatch(context, kind, a, b) || layer.HasModalDocument)
                return true;
        }

        return false;
    }

    private bool Dispatch(RmlContext context, InputKind kind, float a, float b) => kind switch
    {
        InputKind.KeyDown => context.ProcessKeyDown((RmlKey)(int)a, _modifiers),
        InputKind.KeyUp => context.ProcessKeyUp((RmlKey)(int)a, _modifiers),
        InputKind.MouseDown => context.ProcessMouseButtonDown((int)a, _modifiers),
        InputKind.MouseUp => context.ProcessMouseButtonUp((int)a, _modifiers),
        InputKind.Wheel => context.ProcessMouseWheel(a, b, _modifiers),
        _ => false,
    };

    // ── Shutdown ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shuts the UI down: the debugger, remaining contexts (layers normally leave with the scene tree first), RmlUi —
    /// releasing every texture and geometry through the renderer — and finally the renderer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (s_live is not null && s_live.TryGetTarget(out var live) && ReferenceEquals(live, this))
            s_live = null;
        if (_textTranslator is not null)
            _textTranslator.LocaleChanged -= OnLocaleChanged;
        _hotReload?.Dispose();
        _ime?.Dispose();

        foreach (var layer in _layers.ToArray())
            foreach (var document in layer.DocumentList.ToArray())
                document.DetachFromLayer();
        RmlDebugger.Shutdown();
        _debuggerContext?.Dispose();
        _debuggerContext = null;
        foreach (var layer in _layers)
            layer.Context?.Dispose();
        _layers.Clear();

        RmlCore.Shutdown();
        RenderInterface.Dispose();
    }
}

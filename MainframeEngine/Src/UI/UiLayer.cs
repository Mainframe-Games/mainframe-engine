using System.Drawing;
using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>How a <see cref="UiLayer"/> maps RCSS <c>dp</c> units to framebuffer pixels.</summary>
public enum UiScaleMode
{
    /// <summary>1 dp = the display's pixels per point (2 on Retina): documents look the same size on every display.</summary>
    Dpi,

    /// <summary>1 dp = 1 framebuffer pixel.</summary>
    Pixels,

    /// <summary>
    /// 1 dp = framebuffer size / <see cref="UiLayer.ReferenceResolution"/> (the smaller axis ratio): a layout authored
    /// for the reference resolution scales with the window, like a game HUD.
    /// </summary>
    ReferenceResolution,
}

/// <summary>
/// A screen-space UI layer (Godot's <c>CanvasLayer</c>): one RmlUi context, sized to the framebuffer (or its
/// <see cref="Region"/>), holding the
/// <see cref="UiDocument"/>s below it. Layers draw and receive input by <see cref="Layer"/> — higher layers draw on top
/// and see input first. Typical games use a HUD layer (0), a menu layer (10) and an overlay layer (100).
/// </summary>
/// <remarks>
/// Needs the <see cref="UiServer"/> (registered by <see cref="Engine"/>); without one the layer stays inert. Author
/// sizes in <c>dp</c> so they follow <see cref="ScaleMode"/>; <c>px</c> are always framebuffer pixels.
/// </remarks>
[EditorIcon("stack-2", Family = EditorIconFamily.Ui)]
public class UiLayer : Node
{
    private readonly List<UiDocument> _documents = [];
    private int _layer;
    private bool _visible = true;
    private bool _inert;

    /// <summary>Draw and input order: higher layers draw on top and receive input first.</summary>
    [Export]
    public int Layer
    {
        get => _layer;
        set
        {
            if (_layer == value)
                return;
            _layer = value;
            Server?.SortLayers();
        }
    }

    /// <summary>Hidden layers are neither updated, drawn nor given input.</summary>
    [Export]
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;
            _visible = value;
            HasPointer = false;
            if (!value && Context is { IsDisposed: false } context)
                context.ProcessMouseLeave();
        }
    }

    /// <summary>
    /// The window rectangle (framebuffer pixels) the layer is laid out in, drawn into and clipped to, and takes the
    /// mouse from — a split-screen player's view, an editor preview; null (default) is the whole window. Can change every
    /// frame.
    /// </summary>
    public Rectangle? Region { get; set; }

    /// <summary>The layer's context has the mouse (a <see cref="Region"/> layer only gets moves inside its region, or while it drags).</summary>
    internal bool HasPointer { get; set; }

    [Export]
    public UiScaleMode ScaleMode { get; set; } = UiScaleMode.Dpi;

    /// <summary>The resolution a <see cref="UiScaleMode.ReferenceResolution"/> layer is authored for (dp).</summary>
    [Export]
    public Vector2 ReferenceResolution { get; set; } = new(1920, 1080);

    /// <summary>The layer's RmlUi context while it is in a tree with a UI server.</summary>
    public RmlContext? Context { get; private set; }

    /// <summary>The UI server owning <see cref="Context"/>.</summary>
    public UiServer? Server { get; private set; }

    /// <summary>The documents below this layer.</summary>
    public IReadOnlyList<UiDocument> Documents => _documents;

    internal List<UiDocument> DocumentList => _documents;

    /// <summary>True when a visible document below this layer is modal: lower layers and the game get no input.</summary>
    public bool HasModalDocument
    {
        get
        {
            foreach (var document in _documents)
                if (document.IsLoaded && document.Modal && document.Visible)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// A layer of a scene being edited (<see cref="SceneTree.EditMode"/>, below a sub-viewport rather than the tree's root
    /// viewport) that is not a <see cref="ToolAttribute"/> type is inert: it gets a context, so data models and elements
    /// still work from <see cref="Node.OnReady"/>, but the server never updates, draws or routes input to it. Otherwise a
    /// game HUD would paint over the editor's panels and steal their input.
    /// </summary>
    private bool IsInertInEditor => Tree is { EditMode: true } tree && !IsTool && !ReferenceEquals(GetViewport(), tree.Root);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Server = Tree?.Servers.Get<UiServer>();
        if (Server is null)
        {
            Log.Warning($"[UI] UiLayer '{Name}' entered a tree without a UiServer; its documents will not load.");
            return;
        }

        if (IsInertInEditor)
        {
            Context = Server.CreateInertContext(this);
            _inert = true;
            return;
        }

        Context = Server.AddLayer(this);
    }

    protected override void OnExitTree()
    {
        // Children (documents) exit first, but documents re-parented elsewhere or loaded by hand must not keep
        // handles into the context destroyed here.
        foreach (var document in _documents.ToArray())
            document.DetachFromLayer();
        _documents.Clear();

        if (Server is not null)
        {
            if (_inert)
                Context?.Dispose();
            else
                Server.RemoveLayer(this);
            _inert = false;
            Context = null;
            Server = null;
        }

        base.OnExitTree();
    }

    /// <summary>The dp ratio for this layer at <paramref name="framebuffer"/> pixels and <paramref name="pixelScale"/> pixels per point.</summary>
    public float ComputeDpRatio(Vector2 framebuffer, float pixelScale) => ScaleMode switch
    {
        UiScaleMode.Pixels => 1f,
        UiScaleMode.ReferenceResolution when ReferenceResolution.X > 0 && ReferenceResolution.Y > 0 =>
            MathF.Max(0.01f, MathF.Min(framebuffer.X / ReferenceResolution.X, framebuffer.Y / ReferenceResolution.Y)),
        _ => MathF.Max(0.01f, pixelScale),
    };

    internal void AddDocument(UiDocument document)
    {
        if (!_documents.Contains(document))
            _documents.Add(document);
    }

    internal void RemoveDocument(UiDocument document) => _documents.Remove(document);
}

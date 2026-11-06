using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>Where a <see cref="UiPanel3D"/>'s pointer comes from.</summary>
public enum PanelInputMode
{
    /// <summary>No pointer input (a display).</summary>
    None,

    /// <summary>The mouse cursor's ray from the viewport's camera (not while the mouse is raw or captured).</summary>
    Mouse,

    /// <summary>
    /// The screen-centre ray, also while the mouse is raw (first-person games): <see cref="UiPanel3D.ClickAction"/>
    /// presses and releases the left button.
    /// </summary>
    Crosshair,

    /// <summary>Only code drives the pointer: <see cref="UiPanel3D.PushPointer"/>, <see cref="UiPanel3D.PushButton"/> (VR hands, NPCs, tests).</summary>
    Manual,
}

/// <summary>
/// A world-space UI panel (G11.6, ADR 0252/0253): a full RmlUi context with the <see cref="UiDocument"/>s below it,
/// rendered into its own texture before the scene pass and shown on a quad of <see cref="Resolution"/> ×
/// <see cref="PixelSize"/> metres facing +Z (or on <see cref="Mesh"/>, UV 0–1 mapping the panel). Terminals, signs with
/// live content, in-world menus. The scene shows the frame's UI the same frame; the panel casts shadows, picks and sorts
/// like any mesh. Input comes from the mouse ray, a crosshair or code (<see cref="InputMode"/>); clicking a panel focuses
/// it, and keys, text and gamepad navigation then go to it after the screen layers.
/// </summary>
/// <remarks>
/// Needs the <see cref="UiServer"/>. At most <see cref="UiServer.MaxPanelRendersPerFrame"/> panels render per frame,
/// nearest first; the others keep their last image. A panel of a scene being edited renders once after each document
/// load and takes no input. A 1024×640 panel costs about 3.5 MB of texture with mips.
/// </remarks>
[EditorIcon("device-desktop", Family = EditorIconFamily.Ui)]
public class UiPanel3D : GeometryInstance3D, IUiContextHost
{
    private readonly List<UiDocument> _documents = [];
    private readonly QuadMesh _quad = new();
    private readonly StandardMaterial3D _material = new() { ShadingMode = ShadingMode.Unshaded };
    private Texture2D? _texture;
    private int _syncedStamp = -1;
    private bool _editMode;

    /// <summary>The panel's context and texture size in pixels (each axis at least 1).</summary>
    [Export]
    public Vector2I Resolution
    {
        get;
        set
        {
            value = new Vector2I(Math.Clamp(value.X, 1, 8192), Math.Clamp(value.Y, 1, 8192));
            if (field == value)
                return;
            field = value;
            Target.Width = value.X;
            Target.Height = value.Y;
            _texture?.SetGpuSourceSize(value.X, value.Y);
            RenderStamp++;
        }
    } = new(1024, 640);

    /// <summary>Panel pixels per RCSS <c>dp</c> (the panel's own UI scale; the player's UI scale does not apply).</summary>
    [Export(Range = "0.25,4,0.05")]
    public float DpScale { get; set; } = 1f;

    /// <summary>Metres per panel pixel: the default quad of a 1024×640 panel is 1.024 × 0.64 m.</summary>
    [Export(Range = "0.0001,0.1,0.0001")]
    public float PixelSize
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    } = 0.001f;

    /// <summary>
    /// Shows the panel on this mesh (its UV 0–1 maps the panel) instead of the quad; null (default) is the quad. Pointer
    /// rays only hit the quad (custom meshes: G11.9).
    /// </summary>
    [Export]
    public Mesh? Mesh { get; set; }

    /// <summary>What the documents are drawn over (sRGB); ignored with <see cref="Transparent"/>.</summary>
    [Export]
    public DrawingColor Background
    {
        get;
        set
        {
            field = value;
            Target.Background = new Vector4(value.R / 255f, value.G / 255f, value.B / 255f, 1f);
        }
    } = DrawingColor.FromArgb(255, 20, 23, 28);

    /// <summary>The documents' own alpha: an alpha-blended panel with no background (holograms, labels).</summary>
    [Export]
    public bool Transparent
    {
        get;
        set
        {
            field = value;
            Target.Transparent = value;
            RenderStamp++;
        }
    }

    /// <summary>Lit by the scene's lights like a sign; unshaded (default) like a screen.</summary>
    [Export]
    public bool Shaded
    {
        get;
        set
        {
            field = value;
            RenderStamp++;
        }
    }

    /// <summary>An unshaded panel's emission multiplier (HDR: above 1 the glow catches it). Shaded panels ignore it.</summary>
    [Export(Range = "0,16,0.01")]
    public float Brightness
    {
        get;
        set
        {
            field = Math.Max(0f, value);
            RenderStamp++;
        }
    } = 1f;

    /// <summary>
    /// When the panel renders: <see cref="SubViewportUpdateMode.Always"/> (every frame it is within
    /// <see cref="MaxUpdateDistance"/> and in the camera's view), <see cref="SubViewportUpdateMode.Once"/> (on the next
    /// frame, then <see cref="SubViewportUpdateMode.Disabled"/>), or never. A panel the pointer is on, or that has focus,
    /// renders every frame whatever the mode.
    /// </summary>
    [Export]
    public SubViewportUpdateMode UpdateMode { get; set; }

    /// <summary>Beyond this distance from the camera an <see cref="SubViewportUpdateMode.Always"/> panel keeps its last image; 0 = no limit.</summary>
    [Export(Range = "0,10000,0.1")]
    public float MaxUpdateDistance { get; set; } = 30f;

    /// <summary>Where the pointer comes from (default the mouse cursor's ray).</summary>
    [Export]
    public PanelInputMode InputMode { get; set; } = PanelInputMode.Mouse;

    /// <summary>The farthest a mouse or crosshair ray reaches the panel from the camera (metres).</summary>
    [Export(Range = "0,10000,0.1")]
    public float MaxInteractDistance { get; set; } = 4f;

    /// <summary>A physics ray from the camera must not hit anything before the panel (the panel's own body excepted).</summary>
    [Export]
    public bool RequireLineOfSight { get; set; } = true;

    /// <summary>In <see cref="PanelInputMode.Crosshair"/> mode, the input action that presses and releases the left button.</summary>
    [Export]
    public string ClickAction { get; set; } = "ui_accept";

    /// <summary>The pointer moved onto the panel.</summary>
    [Signal]
    public event Action? PointerEntered;

    /// <summary>The pointer left the panel.</summary>
    [Signal]
    public event Action? PointerExited;

    /// <summary>The panel gained (true) or lost (false) keyboard focus.</summary>
    [Signal]
    public event Action<bool>? FocusChanged;

    /// <summary>The panel's RmlUi context while it is in a tree with a UI server.</summary>
    public RmlContext? Context { get; private set; }

    /// <summary>The UI server owning <see cref="Context"/>.</summary>
    public UiServer? Server { get; private set; }

    /// <summary>The documents below this panel.</summary>
    public IReadOnlyList<UiDocument> Documents => _documents;

    /// <summary>Keys, text and gamepad navigation go to the panel (after the screen layers).</summary>
    public bool HasFocus => Server?.FocusedPanel == this;

    /// <summary>A pointer (mouse, crosshair or manual) is on the panel.</summary>
    public bool HasPointer { get; internal set; }

    /// <summary>The pointer's last panel pixel (top-left origin), while <see cref="HasPointer"/>.</summary>
    public Vector2 PointerPosition { get; internal set; }

    /// <summary>Times the panel's context was rendered (headless too).</summary>
    public int RenderCount => Target.Renders;

    /// <summary>The panel's texture (sampled by its material; null before the panel first draws).</summary>
    public Texture2D? Texture => _texture;

    internal UiPanelTarget Target { get; } = new() { Width = 1024, Height = 640, Background = new Vector4(20 / 255f, 23 / 255f, 28 / 255f, 1f) };

    /// <summary>A panel of an edited scene: renders once per document load, no input.</summary>
    internal bool IsEditorPanel => _editMode;

    /// <summary>Set by a document load (edit mode renders again).</summary>
    internal bool ContentChanged { get; set; }

    internal List<UiDocument> DocumentList => _documents;

    List<UiDocument> IUiContextHost.DocumentList => _documents;

    void IUiContextHost.AddDocument(UiDocument document)
    {
        if (!_documents.Contains(document))
            _documents.Add(document);
    }

    void IUiContextHost.RemoveDocument(UiDocument document) => _documents.Remove(document);

    void IUiContextHost.OnDocumentLoaded(UiDocument document) => ContentChanged = true;

    /// <summary>Gives the panel keyboard focus (the previous panel loses it).</summary>
    public void GrabFocus() => Server?.SetPanelFocus(this);

    /// <summary>Releases the keyboard focus if the panel has it.</summary>
    public void ReleaseFocus()
    {
        if (HasFocus)
            Server!.SetPanelFocus(null);
    }

    /// <summary>
    /// The panel pixel (top-left origin) where a world ray hits the front of the panel's quad, and the distance along the
    /// ray in metres; false when it misses the quad, hits it from behind, or the panel shows a custom <see cref="Mesh"/>.
    /// </summary>
    public bool TryRayToPixel(Vector3 origin, Vector3 direction, out Vector2 pixel, out float distance) =>
        TryRayToPlane(origin, direction, bounded: true, out pixel, out distance);

    /// <summary>
    /// Moves the panel's pointer along a world ray (<see cref="PanelInputMode.Manual"/>; works in any mode): onto the
    /// pixel the ray hits, or off the panel when it misses. A held button keeps the pointer on the panel's plane.
    /// </summary>
    public void PushPointer(Vector3 origin, Vector3 direction) => Server?.PushPanelPointer(this, origin, direction);

    /// <summary>Presses or releases a mouse button at the pointer (a press focuses the panel).</summary>
    public void PushButton(MouseButton button, bool pressed) => Server?.PushPanelButton(this, button, pressed);

    /// <summary>Scrolls at the pointer (RmlUi's wheel units: +Y scrolls down).</summary>
    public void PushWheel(Vector2 delta) => Server?.PushPanelWheel(this, delta);

    internal void RaisePointerEntered() => PointerEntered?.Invoke();

    internal void RaisePointerExited() => PointerExited?.Invoke();

    internal void RaiseFocusChanged(bool focused) => FocusChanged?.Invoke(focused);

    /// <summary>The quad's size in metres.</summary>
    public Vector2 QuadSize => new(Resolution.X * PixelSize, Resolution.Y * PixelSize);

    internal bool TryRayToPlane(Vector3 origin, Vector3 direction, bool bounded, out Vector2 pixel, out float distance)
    {
        pixel = default;
        distance = 0f;
        if (Mesh is not null)
            return false;
        var inverse = GlobalTransform.AffineInverse();
        var o = inverse * origin;
        var d = inverse.Basis * direction;
        if (!(d.Z < -1e-8f)) // parallel, or from behind
            return false;
        var t = -o.Z / d.Z;
        if (t < 0f)
            return false;
        var hit = o + d * t;
        var size = QuadSize;
        if (!(size.X > 0f && size.Y > 0f))
            return false;
        var u = hit.X / size.X + 0.5f;
        var v = 0.5f - hit.Y / size.Y;
        if (bounded && (u < 0f || u > 1f || v < 0f || v > 1f))
            return false;
        pixel = new Vector2(u * Resolution.X, v * Resolution.Y);
        distance = t * direction.Length();
        return true;
    }

    /// <summary>The panel's bounds in world space (the quad, or the mesh's).</summary>
    internal Aabb WorldBounds()
    {
        var size = QuadSize;
        var local = Mesh is { } mesh ? mesh.Bounds : new Aabb(new Vector3(-size.X / 2, -size.Y / 2, 0), new Vector3(size.X / 2, size.Y / 2, 0));
        return local.Transform(GlobalTransform.ToMatrix4x4());
    }

    // ── Tree ─────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Server = Tree?.Servers.Get<UiServer>();
        if (Server is null)
        {
            Log.Warning($"[UI] UiPanel3D '{Name}' entered a tree without a UiServer; its documents will not load.");
            return;
        }

        _editMode = Tree is { EditMode: true } tree && !IsTool && !ReferenceEquals(GetViewport(), tree.Root);
        Context = Server.AddPanel(this);
    }

    protected override void OnExitTree()
    {
        foreach (var document in _documents.ToArray())
            document.DetachFromLayer();
        _documents.Clear();
        if (Server is not null)
        {
            Server.RemovePanel(this);
            Context = null;
            Server = null;
        }

        HasPointer = false;
        base.OnExitTree();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────────────────

    internal override Mesh? GetRenderMesh()
    {
        Sync();
        return Mesh ?? _quad;
    }

    internal override Material GetRenderMaterial(Mesh mesh, int surface) => MaterialOverride ?? _material;

    private void Sync()
    {
        if (_syncedStamp == RenderStamp && _texture is not null)
            return;
        _syncedStamp = RenderStamp;
        var target = Target;
        _texture ??= Texture2D.FromGpuSource(() => target.SampledImage, Resolution.X, Resolution.Y, Name);
        _quad.Size = QuadSize;
        _material.AlbedoTexture = _texture;
        _material.Transparency = Transparent ? AlphaMode.Blend : AlphaMode.Opaque;
        if (Shaded)
        {
            _material.ShadingMode = ShadingMode.BlinnPhong;
            _material.AlbedoColor = DrawingColor.White;
            _material.EmissionTexture = null;
            _material.EmissionColor = DrawingColor.Black;
            _material.EmissionEnergy = 0f;
        }
        else
        {
            // Unshaded: colour = emission = the panel × Brightness (the albedo only carries its alpha).
            _material.ShadingMode = ShadingMode.Unshaded;
            _material.AlbedoColor = DrawingColor.Black;
            _material.EmissionTexture = _texture;
            _material.EmissionColor = DrawingColor.White;
            _material.EmissionEnergy = Brightness;
        }
    }
}

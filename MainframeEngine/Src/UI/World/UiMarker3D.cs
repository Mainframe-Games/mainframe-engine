using System.Numerics;

namespace MainframeEngine;

/// <summary>What a <see cref="UiMarker3D"/> does when its point is off the screen or behind the camera.</summary>
public enum MarkerOffscreen : byte
{
    /// <summary>Hidden.</summary>
    Hide,

    /// <summary>Kept on the screen's margin along the line from the centre (waypoints); <see cref="UiMarker3D.EdgeAngle"/> points at it.</summary>
    ClampToEdge,
}

/// <summary>How a <see cref="UiMarker3D"/> is hidden by the world.</summary>
public enum MarkerOcclusion : byte
{
    /// <summary>Never hidden by geometry.</summary>
    None,

    /// <summary>Hidden (with a fade) while a physics ray from the camera hits a body before the point.</summary>
    Physics,
}

/// <summary>
/// A screen-space UI marker at a 3D point (G11.4, ADR 0250): a nameplate, health bar, interaction prompt or waypoint drawn
/// by a <see cref="UiMarkerHost"/> on a screen <see cref="UiLayer"/>, so its colours are exact (after the tonemap, with
/// the HUD). Each marker is one row of the host's <c>markers</c> list (<see cref="Kind"/> picks the row's template);
/// with <see cref="TargetDocument"/> and <see cref="TargetElementId"/> it moves that element instead (a boss bar).
/// The host projects it every frame: hidden beyond <see cref="MaxDistance"/>, faded over <see cref="FadeDistance"/>,
/// optionally scaled with distance, clamped to the screen edge, and hidden behind physics bodies.
/// </summary>
[EditorIcon("map-pin", Family = EditorIconFamily.Ui)]
public class UiMarker3D : Node3D
{
    internal const string Group = "__ui_markers";

    /// <summary>The row template (<c>data-if="m.kind == 'nameplate'"</c>): the default document has <c>nameplate</c>, <c>health</c>, <c>prompt</c> and <c>waypoint</c>.</summary>
    [Export]
    public string Kind { get; set; } = "nameplate";

    /// <summary>The main text (a name, a prompt's action, a waypoint's label), shown translated.</summary>
    [Export(Translatable = true)]
    public string Text
    {
        get;
        set
        {
            field = value ?? "";
            ShownText = IsInsideTree ? Atr(field) : field;
        }
    } = "";

    /// <summary>Second text (a title, a prompt's key), shown translated.</summary>
    [Export(Translatable = true)]
    public string Subtext
    {
        get;
        set
        {
            field = value ?? "";
            ShownSubtext = IsInsideTree ? Atr(field) : field;
        }
    } = "";

    /// <summary>A 0–1 value for bars (health, progress).</summary>
    [Export(Range = "0,1,0.01")]
    public float Value { get; set; } = 1f;

    /// <summary>An image for the row (<c>{{ m.icon }}</c>), a path the document can load.</summary>
    [Export(File = "*.png,*.svg")]
    public string Icon { get; set; } = "";

    /// <summary>Where the marker sits relative to the node, in world units (above a head by default).</summary>
    [Export]
    public Vector3 WorldOffset { get; set; } = new(0, 2, 0);

    /// <summary>A screen offset in dp (+Y down).</summary>
    [Export]
    public Vector2 ScreenOffset { get; set; }

    /// <summary>Hidden beyond this distance from the camera (0 = never).</summary>
    [Export]
    public float MaxDistance { get; set; } = 50f;

    /// <summary>Fades out over this many metres before <see cref="MaxDistance"/>.</summary>
    [Export]
    public float FadeDistance { get; set; } = 5f;

    /// <summary>Shrinks with distance: full size at <see cref="ReferenceDistance"/> and nearer, never below <see cref="MinScale"/>.</summary>
    [Export]
    public bool ScaleWithDistance { get; set; }

    [Export]
    public float ReferenceDistance { get; set; } = 10f;

    [Export(Range = "0.05,1,0.01")]
    public float MinScale { get; set; } = 0.5f;

    [Export]
    public MarkerOffscreen Offscreen { get; set; }

    /// <summary>The margin in dp that <see cref="MarkerOffscreen.ClampToEdge"/> keeps from the layer's edges.</summary>
    [Export]
    public float EdgeMargin { get; set; } = 32f;

    [Export]
    public MarkerOcclusion Occlusion { get; set; }

    /// <summary>The layers occlusion rays hit.</summary>
    [Export(Flags = true)]
    public uint OcclusionMask { get; set; } = CollisionLayers.All;

    /// <summary>The host that draws this marker; empty = the first <see cref="UiMarkerHost"/> showing this marker's viewport.</summary>
    [Export(NodeType = typeof(UiMarkerHost))]
    public NodePath Host { get; set; }

    /// <summary>Element mode: the document holding <see cref="TargetElementId"/> (the marker then takes no row).</summary>
    [Export(NodeType = typeof(UiDocument))]
    public NodePath TargetDocument { get; set; }

    /// <summary>Element mode: the element moved to the marker every frame (its <c>transform</c>).</summary>
    [Export]
    public string TargetElementId { get; set; } = "";

    /// <summary>The marker became visible (on screen, in range and not occluded).</summary>
    [Signal]
    public event Action? ScreenEntered;

    /// <summary>The marker stopped being visible.</summary>
    [Signal]
    public event Action? ScreenExited;

    /// <summary>The marker's position in its layer's pixels, after clamping (as of the host's last frame).</summary>
    public Vector2 ScreenPosition { get; internal set; }

    /// <summary>True while the point is inside the layer and in front of the camera.</summary>
    public bool IsOnScreen { get; internal set; }

    /// <summary>True while an occlusion ray hits something before the point.</summary>
    public bool IsOccluded { get; internal set; }

    /// <summary>Distance from the camera in world units.</summary>
    public float Distance { get; internal set; }

    /// <summary>The direction from the layer's centre to the point in degrees (0 = right, 90 = down), for edge arrows.</summary>
    public float EdgeAngle { get; internal set; }

    /// <summary>True while the marker is drawn (signals fire when it changes).</summary>
    public bool IsShown { get; private set; }

    internal string ShownText { get; private set; } = "";

    internal string ShownSubtext { get; private set; } = "";

    /// <summary>Occlusion fade: 1 visible, 0 hidden; eased toward the last ray's answer.</summary>
    internal float OcclusionAlpha = 1f;

    /// <summary>This frame's row values (also for element-mode markers, which take no row).</summary>
    internal MarkerRow Row;

    /// <summary>The host update that last saw this marker (a marker that left the tree drops out of its rows).</summary>
    internal int HostStamp;

    internal bool HasTarget => !TargetDocument.IsEmpty && TargetElementId.Length > 0;

    internal void SetShown(bool shown)
    {
        if (shown == IsShown)
            return;
        IsShown = shown;
        if (shown)
            ScreenEntered?.Invoke();
        else
            ScreenExited?.Invoke();
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        AddToGroup(Group);
    }

    protected override void OnExitTree()
    {
        RemoveFromGroup(Group);
        SetShown(false);
        base.OnExitTree();
    }

    protected override void OnReady() => ApplyText();

    protected override void OnLocaleChanged() => ApplyText();

    private void ApplyText()
    {
        ShownText = Atr(Text);
        ShownSubtext = Atr(Subtext);
    }
}

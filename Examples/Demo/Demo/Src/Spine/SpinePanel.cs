using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// The Spine scene's controls: Camera3D ↔ Camera2D toggle, the animation picker, and (3D view) the shaped shadow,
/// turning SpineBoy away from the camera and the billboard mode that turns him back (ADR 0204).
/// </summary>
public sealed class SpinePanel : UiDocument
{
    /// <summary>Skeleton scale under the Camera3D: SpineBoy is ~540 skeleton units, so 0.005 x 540 = 2.7 m tall.</summary>
    public const float Scale3D = 0.005f;

    /// <summary>Skeleton scale under the Camera2D (1 unit = 1 canvas pixel).</summary>
    public const float Scale2D = 0.7f;

    /// <summary>The backdrop wall's Z: close enough behind SpineBoy that his shadow lands on it.</summary>
    public const float BackdropZ = -1.2f;

    /// <summary>How far "Turned away" turns SpineBoy about Y (degrees), so that only a billboard faces him to the camera.</summary>
    public const float TurnedAwayYaw = 60f;

    /// <summary>The sun lights the skeleton's +Z face (the 3D camera's side).</summary>
    public static readonly Vector3 SunRotation3D = new(-35, 40, 0);

    /// <summary>Under the Camera2D (which looks along +Z) the turned-over skeleton shows its -Z face, so the sun turns too.</summary>
    public static readonly Vector3 SunRotation2D = new(-35, 220, 0);

    private Node? _scene;
    private Camera3D? _camera3d;
    private Camera2D? _camera2d;
    private Node3D? _pivot;
    private SpineNode? _spine;
    private DirectionalLight3D? _sun;
    private MeshInstance3D? _backdrop;
    private bool _mode2d;
    private string _animation = "walk";
    private bool _shapedShadow = true;
    private bool _turnedAway;
    private string _billboard = "off";

    public SpinePanel()
    {
        Source = "Assets/Spine/panel.rml";
        AutoFocus = false;
    }

    /// <summary>Whether the scene is seen by the Camera2D; the panel's checkbox is bound to it.</summary>
    public bool Mode2D
    {
        get => _mode2d;
        set => SetMode(value);
    }

    /// <summary>The animation looping on SpineBoy; the panel's select is bound to it.</summary>
    public string Animation
    {
        get => _animation;
        set
        {
            _animation = value;
            _spine?.SetAnimation(value);
        }
    }

    /// <summary>Alpha-tested shadow (<see cref="SpineNode.ShadowAlphaThreshold"/> 0.5) or the attachments' quads (0).</summary>
    public bool ShapedShadow
    {
        get => _shapedShadow;
        set
        {
            _shapedShadow = value;
            if (_spine is { } spine)
                spine.ShadowAlphaThreshold = value ? SpineNode.DefaultShadowAlphaThreshold : 0f;
        }
    }

    /// <summary>Turns SpineBoy <see cref="TurnedAwayYaw"/>° from the camera (3D view only).</summary>
    public bool TurnedAway
    {
        get => _turnedAway;
        set
        {
            _turnedAway = value;
            ApplyFacing();
        }
    }

    /// <summary>The billboard mode in the 3D view: <c>off</c>, <c>fixed_y</c> or <c>enabled</c>; the panel's select.</summary>
    public string Billboard
    {
        get => _billboard;
        set
        {
            _billboard = value;
            ApplyFacing();
        }
    }

    protected override void OnReady()
    {
        var scene = _scene = Parent!.Parent!;
        _camera3d = scene.GetNode<Camera3D>("Camera3D");
        _camera2d = scene.GetNode<Camera2D>("Camera2D");
        _pivot = scene.GetNode<Node3D>("Pivot");
        _spine = scene.GetNode<SpineNode>("Pivot/SpineBoy");
        _sun = scene.GetNode<DirectionalLight3D>("Sun");
        _backdrop = scene.GetNode<MeshInstance3D>("Backdrop");
        CreateDataModel("spine")
            .Bind("mode2d", this, static d => d.Mode2D, static (d, v) => d.Mode2D = v)
            .Bind("animation", this, static d => d._animation, static (d, v) => d.Animation = v)
            .Bind("shaped", this, static d => d.ShapedShadow, static (d, v) => d.ShapedShadow = v)
            .Bind("turned", this, static d => d.TurnedAway, static (d, v) => d.TurnedAway = v)
            .Bind("billboard", this, static d => d._billboard, static (d, v) => d.Billboard = v);
    }

    // The 2D view turns the skeleton over with its pivot; a billboard would turn it back, so it is 3D-only.
    private void ApplyFacing()
    {
        if (_spine is not { } spine)
            return;
        spine.RotationDegrees = _turnedAway && !_mode2d ? new Vector3(0, TurnedAwayYaw, 0) : Vector3.Zero;
        spine.Billboard = _mode2d ? BillboardMode.Disabled : _billboard switch
        {
            "fixed_y" => BillboardMode.FixedY,
            "enabled" => BillboardMode.Enabled,
            _ => BillboardMode.Disabled,
        };
    }

    private void SetMode(bool mode2d)
    {
        _mode2d = mode2d;
        if (_scene is not { } scene || _camera3d is not { } camera3d || _camera2d is not { } camera2d || _pivot is not { } pivot || _spine is not { } spine
            || _sun is not { } sun || _backdrop is not { } backdrop)
            return;

        // A viewport renders with its 3D camera whenever one is in the tree (even a non-current one), so the Camera3D
        // leaves the tree while the Camera2D is in charge and comes back for the 3D view.
        if (mode2d)
        {
            camera3d.Current = false;
            if (camera3d.IsInsideTree)
                scene.RemoveChild(camera3d);
            camera2d.Enabled = true;
            camera2d.Current = true;
        }
        else
        {
            camera2d.Enabled = false;
            camera2d.Current = false;
            if (!camera3d.IsInsideTree)
                scene.AddChild(camera3d);
            camera3d.Current = true;
        }

        // Under the Y-down Camera2D the Y-up skeleton is turned over about X (ADR 0110); 1 unit = 1 canvas pixel.
        pivot.RotationDegrees = mode2d ? new Vector3(180, 0, 0) : Vector3.Zero;
        pivot.Position = mode2d ? new Vector3(0, 230, 0) : Vector3.Zero;
        spine.SpineScale = mode2d ? Scale2D : Scale3D;
        sun.RotationDegrees = mode2d ? SunRotation2D : SunRotation3D;
        backdrop.Visible = !mode2d; // the 3D view's shadow catcher only
        ApplyFacing();
    }

    protected override void OnExitTree()
    {
        // The Camera3D is out of the tree in 2D mode: nothing else would free it.
        if (_mode2d && _camera3d is { IsInsideTree: false } detached)
            detached.Free();
        _scene = null;
        _camera3d = null;
        _camera2d = null;
        _pivot = null;
        _spine = null;
        _sun = null;
        _backdrop = null;
        base.OnExitTree();
    }
}

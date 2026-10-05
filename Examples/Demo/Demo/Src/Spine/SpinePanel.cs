using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>The Spine scene's controls: Camera3D ↔ Camera2D toggle and the animation picker.</summary>
public sealed class SpinePanel : UiDocument
{
    /// <summary>
    /// Skeleton scale under the Camera3D: the engine default. Small skeleton scales (below ~0.01) distort Spine's IK
    /// poses, so the 3D view shrinks the pivot node instead (<see cref="PivotScale3D"/>).
    /// </summary>
    public const float Scale3D = SpineNode.DefaultSpineScale;

    /// <summary>Pivot scale under the Camera3D: SpineBoy is ~540 skeleton units, so 0.02 x 540 x 0.25 = 2.7 m tall.</summary>
    public const float PivotScale3D = 0.25f;

    /// <summary>Skeleton scale under the Camera2D (1 unit = 1 canvas pixel).</summary>
    public const float Scale2D = 0.7f;

    /// <summary>The sun lights the skeleton's +Z face (the 3D camera's side).</summary>
    public static readonly Vector3 SunRotation3D = new(-40, 25, 0);

    /// <summary>Under the Camera2D (which looks along +Z) the turned-over skeleton shows its -Z face, so the sun turns too.</summary>
    public static readonly Vector3 SunRotation2D = new(-40, 205, 0);

    private Node? _scene;
    private Camera3D? _camera3d;
    private Camera2D? _camera2d;
    private Node3D? _pivot;
    private SpineNode? _spine;
    private DirectionalLight3D? _sun;
    private bool _mode2d;
    private string _animation = "walk";

    public SpinePanel()
    {
        Source = "Content/Spine/panel.rml";
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

    protected override void OnReady()
    {
        var scene = _scene = Parent!.Parent!;
        _camera3d = scene.GetNode<Camera3D>("Camera3D");
        _camera2d = scene.GetNode<Camera2D>("Camera2D");
        _pivot = scene.GetNode<Node3D>("Pivot");
        _spine = scene.GetNode<SpineNode>("Pivot/SpineBoy");
        _sun = scene.GetNode<DirectionalLight3D>("Sun");
        CreateDataModel("spine")
            .Bind("mode2d", this, static d => d.Mode2D, static (d, v) => d.Mode2D = v)
            .Bind("animation", this, static d => d._animation, static (d, v) => d.Animation = v);
    }

    private void SetMode(bool mode2d)
    {
        _mode2d = mode2d;
        if (_scene is not { } scene || _camera3d is not { } camera3d || _camera2d is not { } camera2d || _pivot is not { } pivot || _spine is not { } spine
            || _sun is not { } sun)
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
        pivot.Scale = mode2d ? Vector3.One : new Vector3(PivotScale3D);
        pivot.Position = mode2d ? new Vector3(0, 230, 0) : Vector3.Zero;
        spine.SpineScale = mode2d ? Scale2D : Scale3D;
        sun.RotationDegrees = mode2d ? SunRotation2D : SunRotation3D;
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
        base.OnExitTree();
    }
}

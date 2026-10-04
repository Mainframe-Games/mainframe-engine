using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of the light nodes. Each wraps one of the renderer's <see cref="MainframeEngine.Light"/> objects: while
/// the node is inside a tree the light is registered with its world's <see cref="World3D.Lights"/>, and its
/// position/direction follow the node's global transform (synced after process, only when it changed).
/// </summary>
public abstract class Light3D : Node3D
{
    private World3D? _world;

    private protected Light3D()
    {
        SetNotifyTransform(true);
    }

    /// <summary>The renderer-side light this node drives.</summary>
    public abstract Light Light { get; }

    /// <summary>Linear RGB color.</summary>
    [Export]
    public Vector3 Color
    {
        get => Light.Color;
        set => Light.Color = value;
    }

    /// <summary>Intensity multiplier (Godot's <c>light_energy</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float Energy
    {
        get => Light.Intensity;
        set => Light.Intensity = value;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        SyncTransform();
        _world = GetWorld3D();
        _world?.Lights.AddLight(Light);
    }

    protected override void OnExitTree()
    {
        _world?.Lights.RemoveLight(Light);
        _world = null;
        base.OnExitTree();
    }

    protected override void OnTransformChanged() => SyncTransform();

    /// <summary>Copies the global transform into <see cref="Light"/>.</summary>
    private protected abstract void SyncTransform();
}

/// <summary>A sun-like light shining along the node's <c>-Z</c> axis (wraps <see cref="DirectionalLight"/>).</summary>
public class DirectionalLight3D : Light3D
{
    private readonly DirectionalLight _light = new();

    public override Light Light => _light;

    private protected override void SyncTransform()
    {
        _light.Position = GlobalPosition;
        _light.Direction = GlobalForward;
    }
}

/// <summary>A point light with a range (Godot's <c>OmniLight3D</c>; wraps <see cref="PointLight"/>).</summary>
public class OmniLight3D : Light3D
{
    private readonly PointLight _light = new();

    public override Light Light => _light;

    [Export(Range = "0,4096,0.01")]
    public float Range
    {
        get => _light.Range;
        set => _light.Range = value;
    }

    private protected override void SyncTransform() => _light.Position = GlobalPosition;
}

/// <summary>A cone light shining along the node's <c>-Z</c> axis (wraps <see cref="SpotLight"/>).</summary>
public class SpotLight3D : Light3D
{
    private readonly SpotLight _light = new();

    public override Light Light => _light;

    [Export(Range = "0,4096,0.01")]
    public float Range
    {
        get => _light.Range;
        set => _light.Range = value;
    }

    /// <summary>Half-angle of the fully lit inner cone, in degrees.</summary>
    [Export(Range = "0,90,0.1")]
    public float InnerConeAngle
    {
        get => _light.InnerConeAngle;
        set => _light.InnerConeAngle = value;
    }

    /// <summary>Half-angle of the outer falloff cone, in degrees.</summary>
    [Export(Range = "0,90,0.1")]
    public float OuterConeAngle
    {
        get => _light.OuterConeAngle;
        set => _light.OuterConeAngle = value;
    }

    private protected override void SyncTransform()
    {
        _light.Position = GlobalPosition;
        _light.Direction = GlobalForward;
    }
}

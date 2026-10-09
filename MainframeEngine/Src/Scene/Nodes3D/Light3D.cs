using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of the light nodes. Each wraps one of the renderer's <see cref="MainframeEngine.Light"/> objects: while
/// the node is inside a tree and visible (<see cref="Node3D.IsVisibleInTree"/>) the light is registered with its
/// world's <see cref="World3D.Lights"/>, and its position/direction follow the node's global transform (synced
/// after process, only when it changed). Hiding the node or an ancestor switches the light off.
/// </summary>
[EditorIcon("bulb")]
public abstract class Light3D : Node3D
{
    private World3D? _world;
    private bool _registered;

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

    /// <summary>Whether the light casts shadows (default true; see <see cref="MainframeEngine.Light.CastsShadows"/>).</summary>
    [ExportGroup("Shadow")]
    [Export]
    public bool CastsShadows
    {
        get => Light.CastsShadows;
        set => Light.CastsShadows = value;
    }

    /// <summary>
    /// Shadow map size in texels (see <see cref="MainframeEngine.Light.ShadowResolution"/>): cascades use at most 4096,
    /// cube faces 2048 and atlas tiles half the atlas.
    /// </summary>
    [Export(Range = "64,8192,1")]
    public int ShadowResolution
    {
        get => Light.ShadowResolution;
        set => Light.ShadowResolution = value;
    }

    /// <summary>Receiver offset towards the light, in texels (see <see cref="MainframeEngine.Light.ShadowBias"/>).</summary>
    [Export(Range = "0,16,0.01")]
    public float ShadowBias
    {
        get => Light.ShadowBias;
        set => Light.ShadowBias = value;
    }

    /// <summary>Receiver offset along the normal, in texels (see <see cref="MainframeEngine.Light.ShadowNormalBias"/>).</summary>
    [Export(Range = "0,16,0.01")]
    public float ShadowNormalBias
    {
        get => Light.ShadowNormalBias;
        set => Light.ShadowNormalBias = value;
    }

    /// <summary>How dark the shadow is, 0..1 (Godot's <c>shadow_opacity</c>; see <see cref="MainframeEngine.Light.ShadowOpacity"/>).</summary>
    [Export(Range = "0,1,0.01")]
    public float ShadowOpacity
    {
        get => Light.ShadowOpacity;
        set => Light.ShadowOpacity = value;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        SyncTransform();
        _world = GetWorld3D();
        UpdateRegistration();
    }

    protected override void OnExitTree()
    {
        Unregister();
        _world = null;
        base.OnExitTree();
    }

    private protected override void OnVisibilityInTreeChanged() => UpdateRegistration();

    private void UpdateRegistration()
    {
        if (_world is null)
            return;
        if (IsVisibleInTree())
        {
            if (_registered)
                return;
            SyncTransform();
            _world.Lights.AddLight(Light);
            _registered = true;
        }
        else
        {
            Unregister();
        }
    }

    private void Unregister()
    {
        if (!_registered)
            return;
        _world?.Lights.RemoveLight(Light);
        _registered = false;
    }

    protected override void OnTransformChanged() => SyncTransform();

    /// <summary>Copies the global transform into <see cref="Light"/>.</summary>
    private protected abstract void SyncTransform();
}

/// <summary>A sun-like light shining along the node's <c>-Z</c> axis (wraps <see cref="DirectionalLight"/>).</summary>
[EditorIcon("sun")]
public class DirectionalLight3D : Light3D
{
    private readonly DirectionalLight _light = new();

    public override Light Light => _light;

    /// <summary>Shadow cascades when this is the primary shadowed directional light, 1–4.</summary>
    [ExportGroup("Shadow Cascades")]
    [Export(Range = "1,4,1")]
    public int ShadowCascades
    {
        get => _light.CascadeCount;
        set => _light.CascadeCount = value;
    }

    /// <summary>Practical split blend: 0 uniform, 1 logarithmic (default 0.75).</summary>
    [Export(Range = "0,1,0.01")]
    public float ShadowSplitLambda
    {
        get => _light.CascadeSplitLambda;
        set => _light.CascadeSplitLambda = value;
    }

    /// <summary>View distance the shadows reach (Godot's <c>directional_shadow_max_distance</c>).</summary>
    [Export(Range = "0.1,8192,0.1")]
    public float ShadowMaxDistance
    {
        get => _light.MaxShadowDistance;
        set => _light.MaxShadowDistance = value;
    }

    /// <summary>Fraction of each cascade blended into the next.</summary>
    [Export(Range = "0,0.5,0.01")]
    public float ShadowCascadeBlend
    {
        get => _light.CascadeBlend;
        set => _light.CascadeBlend = value;
    }

    /// <summary>Staggered cascade updates (see <see cref="DirectionalLight.CacheMode"/>; default off).</summary>
    [Export]
    public ShadowCacheMode ShadowCacheMode
    {
        get => _light.CacheMode;
        set => _light.CacheMode = value;
    }

    /// <summary>The last cascades that draw coarse casters (see <see cref="DirectionalLight.CoarseCascades"/>; default 0).</summary>
    [Export(Range = "0,3,1")]
    public int ShadowCoarseCascades
    {
        get => _light.CoarseCascades;
        set => _light.CoarseCascades = value;
    }

    /// <summary>
    /// Angular diameter of the light in degrees (Godot's <c>light_angular_distance</c>; the sun is about 0.5): soft shadows
    /// whose penumbra grows with the distance to the caster (see <see cref="DirectionalLight.AngularDistance"/>).
    /// </summary>
    [ExportGroup("Soft and Contact Shadows")]
    [Export(Range = "0,10,0.01")]
    public float LightAngularDistance
    {
        get => _light.AngularDistance;
        set => _light.AngularDistance = value;
    }

    /// <summary>Screen-space contact shadows (see <see cref="DirectionalLight.ContactShadows"/>; default false).</summary>
    [Export]
    public bool ContactShadows
    {
        get => _light.ContactShadows;
        set => _light.ContactShadows = value;
    }

    /// <summary>How far contact shadows reach towards the light, in metres (default 0.5).</summary>
    [Export(Range = "0.01,10,0.01")]
    public float ContactShadowLength
    {
        get => _light.ContactShadowLength;
        set => _light.ContactShadowLength = value;
    }

    /// <summary>A static shadow map past the cascades (see <see cref="DirectionalLight.FarShadowEnabled"/>; default false).</summary>
    [ExportGroup("Far Shadow")]
    [Export]
    public bool FarShadowEnabled
    {
        get => _light.FarShadowEnabled;
        set => _light.FarShadowEnabled = value;
    }

    /// <summary>Half-size of the box the far shadow covers around the camera; 0 = every caster's bounds (the default).</summary>
    [Export(Range = "0,8192,1")]
    public float FarShadowDistance
    {
        get => _light.FarShadowDistance;
        set => _light.FarShadowDistance = value;
    }

    private protected override void SyncTransform()
    {
        _light.Position = GlobalPosition;
        _light.Direction = GlobalForward;
    }
}

/// <summary>A point light with a range (Godot's <c>OmniLight3D</c>; wraps <see cref="PointLight"/>).</summary>
[EditorIcon("bulb")]
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
[EditorIcon("lamp")]
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

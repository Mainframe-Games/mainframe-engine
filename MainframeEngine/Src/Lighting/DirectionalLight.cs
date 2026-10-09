using System.Numerics;

namespace MainframeEngine;

public class DirectionalLight : Light
{
    public const int DefaultShadowResolution = 2048;
    public const int DefaultCascadeCount = 4;
    public const float DefaultCascadeSplitLambda = 0.75f;
    public const float DefaultMaxShadowDistance = 100f;
    public const float DefaultCascadeBlend = 0.1f;

    /// <summary>Default <see cref="ContactShadowLength"/> (metres).</summary>
    public const float DefaultContactShadowLength = 0.5f;

    /// <summary>Largest <see cref="AngularDistance"/> (degrees); PCSS clamps its penumbra long before it.</summary>
    public const float MaxAngularDistance = 90f;

    private int _cascadeCount = DefaultCascadeCount;
    private float _splitLambda = DefaultCascadeSplitLambda;
    private float _maxShadowDistance = DefaultMaxShadowDistance;
    private float _cascadeBlend = DefaultCascadeBlend;
    private float _angularDistance;
    private float _contactShadowLength = DefaultContactShadowLength;
    private int _coarseCascades;
    private float _farShadowDistance;

    public DirectionalLight() : base(DefaultShadowResolution)
    {
    }

    public Vector3 Direction { get; set; } = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f));

    /// <summary>Cascades of the primary (first shadow-casting) directional light, 1–4 (default 4).</summary>
    public int CascadeCount
    {
        get => _cascadeCount;
        set => _cascadeCount = Math.Clamp(value, 1, ShaderLimits.MaxShadowCascades);
    }

    /// <summary>
    /// Practical split scheme blend (default 0.75): 0 splits the shadow distance uniformly, 1 logarithmically (more
    /// resolution near the camera).
    /// </summary>
    public float CascadeSplitLambda
    {
        get => _splitLambda;
        set => _splitLambda = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// View distance the shadows reach (default 100, limited by the camera's far plane). The last cascade fades out
    /// over its blend band before it. A secondary directional light covers this distance with a single atlas tile.
    /// </summary>
    public float MaxShadowDistance
    {
        get => _maxShadowDistance;
        set => _maxShadowDistance = Math.Max(0.01f, value);
    }

    /// <summary>Fraction of each cascade (default 0.1, at most 0.5) over which it blends into the next, hiding seams.</summary>
    public float CascadeBlend
    {
        get => _cascadeBlend;
        set => _cascadeBlend = Math.Clamp(value, 0f, 0.5f);
    }

    /// <summary>
    /// How the primary light's cascades are re-rendered (G8e.2, ADR 0167): every frame (<see cref="ShadowCacheMode.Off"/>,
    /// the default) or on a staggered schedule (<see cref="ShadowCacheMode.Staggered"/>: cascade 0 every frame, the others
    /// every second or fourth frame, at most two cascade passes per frame).
    /// </summary>
    public ShadowCacheMode CacheMode { get; set; }

    /// <summary>
    /// The light's angular diameter in degrees (Godot's <c>light_angular_distance</c>; the sun is about 0.5): above 0, the
    /// primary light's cascades use percentage-closer soft shadows when the filter is <see cref="ShadowFilter.Pcss"/>
    /// (<see cref="ShadowQuality.High"/>): the penumbra widens with the distance between caster and receiver. 0 (the
    /// default) keeps the fixed-radius filter. Clamped to [0, <see cref="MaxAngularDistance"/>].
    /// </summary>
    public float AngularDistance
    {
        get => _angularDistance;
        set => _angularDistance = Math.Clamp(value, 0f, MaxAngularDistance);
    }

    /// <summary>
    /// Screen-space contact shadows for the primary light (G8e.2): a short march towards the light in the depth prepass
    /// catches the small shadows the cascades miss (pebbles, grass, the gap under a log). Default false; needs
    /// <see cref="ShadowQuality.High"/> (<see cref="ShadowSystem.ContactShadows"/>).
    /// </summary>
    public bool ContactShadows { get; set; }

    /// <summary>How far the contact-shadow march reaches towards the light, in metres (default 0.5; 0.01–10).</summary>
    public float ContactShadowLength
    {
        get => _contactShadowLength;
        set => _contactShadowLength = Math.Clamp(value, 0.01f, 10f);
    }

    /// <summary>
    /// How many of the last cascades draw coarse casters (0–3, default 0): in those passes, instances whose
    /// <see cref="GeometryInstance3D.ShadowCasterLod"/> is <see cref="ShadowCasterLod.Fine"/> do not cast, and
    /// <see cref="ShadowCasterLod.Coarse"/> ones cast from any distance (a <see cref="TreeScatter"/>'s
    /// <see cref="TreeScatter.ShadowCoarseLod"/>; later the tree impostors).
    /// </summary>
    public int CoarseCascades
    {
        get => _coarseCascades;
        set => _coarseCascades = Math.Clamp(value, 0, ShaderLimits.MaxShadowCascades - 1);
    }

    /// <summary>
    /// A static far shadow map past the cascades (G8e.2): the casters (coarse ones, the terrain) rendered once,
    /// orthographic along the light, and again only when the light turns or the covered bounds change. Default false.
    /// </summary>
    public bool FarShadowEnabled { get; set; }

    /// <summary>
    /// Half-size in metres of the box around the camera the far shadow covers; 0 (the default) covers the bounds of
    /// every shadow caster (in a valley, the terrain's).
    /// </summary>
    public float FarShadowDistance
    {
        get => _farShadowDistance;
        set => _farShadowDistance = Math.Max(0f, value);
    }
}

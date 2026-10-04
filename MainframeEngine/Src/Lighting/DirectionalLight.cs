using System.Numerics;

namespace MainframeEngine;

public class DirectionalLight : Light
{
    public const int DefaultShadowResolution = 2048;
    public const int DefaultCascadeCount = 4;
    public const float DefaultCascadeSplitLambda = 0.75f;
    public const float DefaultMaxShadowDistance = 100f;
    public const float DefaultCascadeBlend = 0.1f;

    private int _cascadeCount = DefaultCascadeCount;
    private float _splitLambda = DefaultCascadeSplitLambda;
    private float _maxShadowDistance = DefaultMaxShadowDistance;
    private float _cascadeBlend = DefaultCascadeBlend;

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
}

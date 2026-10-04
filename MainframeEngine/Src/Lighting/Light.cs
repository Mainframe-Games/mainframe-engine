using System.Numerics;

namespace MainframeEngine;

public abstract class Light
{
    public const int MinShadowResolution = 64;
    public const int MaxShadowResolution = 8192;

    /// <summary>Default <see cref="ShadowBias"/> (texels).</summary>
    public const float DefaultShadowBias = 0.5f;

    /// <summary>Default <see cref="ShadowNormalBias"/> (texels).</summary>
    public const float DefaultShadowNormalBias = 1.5f;

    private Vector3 _color = Vector3.One;
    private int _shadowResolution;
    private float _shadowBias = DefaultShadowBias;
    private float _shadowNormalBias = DefaultShadowNormalBias;

    private protected Light(int defaultShadowResolution)
    {
        _shadowResolution = defaultShadowResolution;
    }

    public Vector3 Position { get; set; }

    /// <summary>Light colour as authored (sRGB, like a colour picker); lighting uses <see cref="LinearColor"/>.</summary>
    public Vector3 Color
    {
        get => _color;
        set
        {
            _color = value;
            LinearColor = ColorSpace.SrgbToLinear(value);
        }
    }

    /// <summary><see cref="Color"/> converted to linear once, when set (what the lights UBO carries).</summary>
    public Vector3 LinearColor { get; private set; } = Vector3.One;

    public float Intensity { get; set; } = 1f;

    /// <summary>Whether the light gets a shadow map (default true). A light without one lights everything it reaches.</summary>
    public bool CastsShadows { get; set; } = true;

    /// <summary>
    /// Shadow map size in texels: per cascade for the primary directional light, the atlas tile for spot lights (half
    /// of it for a secondary directional light, which covers its whole shadow distance with one tile), one cube face for
    /// point lights. Clamped to
    /// [<see cref="MinShadowResolution"/>, <see cref="MaxShadowResolution"/>]; the shadow system rounds it to the
    /// nearest power of two (<see cref="ShadowResolutionPow2"/>) and limits atlas tiles to half the atlas.
    /// </summary>
    public int ShadowResolution
    {
        get => _shadowResolution;
        set => _shadowResolution = Math.Clamp(value, MinShadowResolution, MaxShadowResolution);
    }

    /// <summary><see cref="ShadowResolution"/> rounded to the nearest power of two.</summary>
    public int ShadowResolutionPow2 => RoundToPowerOfTwo(_shadowResolution);

    /// <summary>
    /// Receiver offset towards the light in shadow-map texels (scaled by a texel's world size where the surface is
    /// sampled): removes acne on surfaces facing the light. Clamped to [0, 16].
    /// </summary>
    public float ShadowBias
    {
        get => _shadowBias;
        set => _shadowBias = Math.Clamp(value, 0f, 16f);
    }

    /// <summary>
    /// Receiver offset along the surface normal in shadow-map texels, scaled by the sine of the angle to the light:
    /// removes acne at grazing angles without pulling the shadow away from its caster. Clamped to [0, 16].
    /// </summary>
    public float ShadowNormalBias
    {
        get => _shadowNormalBias;
        set => _shadowNormalBias = Math.Clamp(value, 0f, 16f);
    }

    /// <summary>The power of two nearest to <paramref name="value"/> (ties round up).</summary>
    internal static int RoundToPowerOfTwo(int value)
    {
        var up = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, value));
        var down = up >> 1;
        return down > 0 && value - down < up - value ? down : up;
    }
}

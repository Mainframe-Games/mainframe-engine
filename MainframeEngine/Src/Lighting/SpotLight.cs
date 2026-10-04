using System.Numerics;

namespace MainframeEngine;

public class SpotLight : Light
{
    /// <summary>Default <see cref="Light.ShadowResolution"/>: a 1024² atlas tile.</summary>
    public const int DefaultShadowResolution = 1024;

    public SpotLight() : base(DefaultShadowResolution)
    {
    }

    public Vector3 Direction { get; set; } = -Vector3.UnitY;
    public float Range { get; set; } = 20f;
    /// <summary>Half-angle of the fully lit inner cone, in degrees.</summary>
    public float InnerConeAngle { get; set; } = 15f;
    /// <summary>Half-angle of the outer falloff cone, in degrees.</summary>
    public float OuterConeAngle { get; set; } = 30f;
}

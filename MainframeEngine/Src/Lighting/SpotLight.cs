using System.Numerics;

namespace MainframeEngine;

public class SpotLight
{
    public Vector3 Position { get; set; }
    public Vector3 Direction { get; set; } = -Vector3.UnitY;
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1f;
    public float Range { get; set; } = 20f;
    /// <summary>Half-angle of the fully lit inner cone, in degrees.</summary>
    public float InnerConeAngle { get; set; } = 15f;
    /// <summary>Half-angle of the outer falloff cone, in degrees.</summary>
    public float OuterConeAngle { get; set; } = 30f;
}

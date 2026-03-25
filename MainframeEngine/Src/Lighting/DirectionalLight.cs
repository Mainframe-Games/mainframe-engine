using System.Numerics;

namespace MainframeEngine;

public class DirectionalLight
{
    public Vector3 Direction { get; set; } = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f));
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1f;
}

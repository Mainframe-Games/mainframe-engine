using System.Numerics;

namespace MainframeEngine;

public class DirectionalLight : Light
{
    public Vector3 Direction { get; set; } = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f));
}

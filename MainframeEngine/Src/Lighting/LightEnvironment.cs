using System.Numerics;

namespace MainframeEngine;

public class LightEnvironment
{
    public const int MaxDirectional = 4;
    public const int MaxPoint = 16;
    public const int MaxSpot = 8;

    public Vector3 AmbientColor { get; set; } = new(0.08f, 0.08f, 0.10f);
    public List<DirectionalLight> DirectionalLights { get; } = [];
    public List<PointLight> PointLights { get; } = [];
    public List<SpotLight> SpotLights { get; } = [];
}

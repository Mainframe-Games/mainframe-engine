namespace MainframeEngine;

public class PointLight : Light
{
    /// <summary>Default <see cref="Light.ShadowResolution"/>: one 512² cube face.</summary>
    public const int DefaultShadowResolution = 512;

    public PointLight() : base(DefaultShadowResolution)
    {
    }

    public float Range { get; set; } = 10f;
}

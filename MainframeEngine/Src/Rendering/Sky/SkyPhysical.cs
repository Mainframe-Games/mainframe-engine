namespace MainframeEngine;

/// <summary>
/// Creates a physical sky (ADR 0154): a Hillaire 2020 atmosphere, set by <see cref="SkyEnvironment.Physical"/> and lit
/// by the sun in <see cref="SkyEnvironment.SunDirection"/>, <see cref="SkyEnvironment.SunColor"/> and
/// <see cref="SkyEnvironment.SunIntensity"/> (here the sun's illuminance, default 1). Call
/// <see cref="SkyEnvironment.Prepare"/> each frame outside a render pass before <see cref="SkyEnvironment.Draw"/>.
/// </summary>
public class SkyPhysical : SkyEnvironment
{
    public SkyPhysical(IRenderer renderer) : base(renderer, SkyEnvironmentType.Physical)
    {
        SunIntensity = 1f;
        SunColor = System.Numerics.Vector3.One;
    }
}

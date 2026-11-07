namespace MainframeEngine;

/// <summary>
/// How finely <see cref="WorldEnvironment.VolumetricFogEnabled"/> fog is marched (G8e.3, ADR 0276;
/// <c>rendering.volumetricFogQuality</c>, <see cref="RenderServer.VolumetricFogQuality"/>): <see cref="High"/> (the
/// default) marches 24 steps at half resolution, <see cref="Medium"/> 12 at quarter resolution (about a quarter of the
/// cost; the shafts are softer and the step pattern shows more without TAA). High is 0, so a default value is High.
/// </summary>
public enum VolumetricFogQuality
{
    High,
    Medium,
}

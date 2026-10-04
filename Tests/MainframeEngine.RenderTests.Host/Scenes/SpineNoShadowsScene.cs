namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The Spine scene without a <see cref="ShadowSystem"/>: lit shapes and SpineLit bind the renderer's
/// "no shadows" fallback as set 2 (before M1 Spine's texture set landed on the wrong index and the
/// pipeline layout mismatched the shader).
/// </summary>
public sealed class SpineNoShadowsScene(HostOptions host) : SpineScene(host)
{
    protected override bool UseShadowSystem => false;
}

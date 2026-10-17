namespace MainframeEngine.Editor.Music;

public static partial class BuiltinEffects
{
    private static BuiltinEffectInfo[] EqFilterEffects() => [EqEffect.Definition];

    private static BuiltinEffectInfo[] DynamicsEffects() => [CompressorEffect.Definition];

    private static BuiltinEffectInfo[] DelayReverbEffects() => [];

    private static BuiltinEffectInfo[] ModulationEffects() => [];

    private static BuiltinEffectInfo[] DistortionEffects() => [];

    private static BuiltinEffectInfo[] UtilityEffects() => [UtilityEffect.Definition];
}

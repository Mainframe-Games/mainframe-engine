namespace MainframeEngine.Editor.Music;

public static partial class BuiltinEffects
{
    private static BuiltinEffectInfo[] EqFilterEffects() => [EqEffect.Definition, FilterEffect.Definition];

    private static BuiltinEffectInfo[] DynamicsEffects() => [CompressorEffect.Definition, LimiterEffect.Definition, GateEffect.Definition];

    private static BuiltinEffectInfo[] DelayReverbEffects() => [DelayEffect.Definition, ReverbEffect.Definition];

    private static BuiltinEffectInfo[] ModulationEffects() =>
        [ChorusEffect.Definition, FlangerEffect.Definition, PhaserEffect.Definition, TremoloEffect.Definition];

    private static BuiltinEffectInfo[] DistortionEffects() =>
        [SaturationEffect.Definition, DistortionEffect.Definition, BitcrusherEffect.Definition];

    private static BuiltinEffectInfo[] UtilityEffects() => [UtilityEffect.Definition];
}

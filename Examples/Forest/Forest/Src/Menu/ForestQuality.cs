namespace Forest;

/// <summary>
/// The Graphics page's Low / Medium / High preset (the <c>quality</c> option): sets the options that cost frame time, not
/// the ones that are a matter of taste (haze, sun, lens, tonemapper). Medium is every option's default, the Forest as it
/// is authored; High is every option at its maximum. The preset is not stored: the options it sets are, and the row
/// reads back whichever level they match (<see cref="Detect"/>), or Custom.
/// </summary>
public static class ForestQuality
{
    public const int Low = 0, Medium = 1, High = 2, Custom = 3;

    /// <summary>The <c>quality</c> row's choices.</summary>
    public static readonly string[] Names = ["Low", "Medium", "High", "Custom"];

    /// <summary>The options a preset sets and their Low value; Medium is the option's default and High its maximum.</summary>
    public static readonly (string Key, float Low)[] Options =
    [
        ("render_scale", 0.6f),
        ("shadow_res", 0f), // 512
        ("shadow_distance", 80f),
        ("shadow_cascades", 2f),
        ("pcss", 0f),
        ("contact_shadows", 0f),
        ("ssao", 0f),
        ("volumetric_fog", 0f),
        ("grass_density", 0.5f),
        ("grass_distance", 0.5f),
        ("tree_detail", 60f),
        ("tree_lod", 1f), // not lower: impostors would cast into the near cascades, striping the ground (ShadowHandOff)
        ("tree_distance", 300f),
        ("bush_distance", 45f),
        ("terrain_detail", 0.6f),
    ];

    /// <summary>Applies a level; <see cref="Custom"/> changes nothing.</summary>
    public static void Apply(ForestWorld world, int level)
    {
        if (level is < Low or > High)
            return;
        foreach (var (key, low) in Options)
        {
            var option = ForestOptions.Find(key)!;
            world.Apply(option, Target(world, option, low, level));
        }
    }

    /// <summary>The level every preset option currently matches, or <see cref="Custom"/>.</summary>
    public static int Detect(ForestWorld world)
    {
        for (var level = Low; level <= High; level++)
        {
            if (Matches(world, level))
                return level;
        }

        return Custom;
    }

    private static bool Matches(ForestWorld world, int level)
    {
        foreach (var (key, low) in Options)
        {
            var option = ForestOptions.Find(key)!;
            if (MathF.Abs(world.Value(option) - Target(world, option, low, level)) > 1e-3f)
                return false;
        }

        return true;
    }

    private static float Target(ForestWorld world, ForestOption option, float low, int level) => level switch
    {
        Low => option.Clamp(low),
        High => option.Kind == ForestOptionKind.Choice ? option.Choices.Length - 1 : option.Max,
        _ => world.DefaultOf(option),
    };
}

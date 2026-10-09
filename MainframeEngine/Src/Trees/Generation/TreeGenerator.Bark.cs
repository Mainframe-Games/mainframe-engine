namespace MainframeEngine.Trees;

/// <summary>Root flare and branch collars (ADR 0172): engine extensions of the port's bark, RNG-free.</summary>
public sealed partial class TreeGenerator
{
    // The trunk (the branch without a parent) flares at its base.
    private static bool IsFlared(in SkeletonBranch branch, TreeParams parameters) => branch.Parent < 0 && parameters.RootFlare > 1;

    // A side branch (not a terminal continuation) thickens into its parent.
    private static bool IsCollared(in SkeletonBranch branch, TreeParams parameters) =>
        branch.Parent >= 0 && !branch.Terminal && parameters.CollarScale > 1;

    /// <summary>Rings added in a branch's first section for its flare (3 at full detail) or collar (1).</summary>
    private static int ExtraRings(in SkeletonBranch branch, TreeParams parameters, int sectionStride)
    {
        if (IsFlared(branch, parameters))
            return sectionStride == 1 ? 3 : sectionStride <= 3 ? 1 : 0;
        return IsCollared(branch, parameters) && sectionStride == 1 ? 1 : 0;
    }

    /// <summary>
    /// Where extra ring <paramref name="ring"/> (1-based) of <paramref name="extras"/> sits in the first section (0..1):
    /// a flare's rings at 15 %, 40 % and 75 % of <see cref="TreeParams.RootFlareHeight"/> (one at 40 %), a collar's at
    /// <see cref="TreeParams.CollarLength"/> base radii.
    /// </summary>
    private static double ExtraRingFraction(in SkeletonBranch branch, TreeParams parameters, int ring, int extras)
    {
        var sectionLength = branch.Length / Math.Max(1, branch.SectionCount); // Ez Tree units
        double along;
        if (IsFlared(branch, parameters))
        {
            ReadOnlySpan<double> heights = extras == 3 ? [0.15, 0.4, 0.75] : [0.4];
            along = heights[Math.Clamp(ring - 1, 0, heights.Length - 1)] * parameters.RootFlareHeight / Math.Max(parameters.Scale, 1e-6);
        }
        else
        {
            along = parameters.CollarLength * branch.BaseRadius;
        }

        return Math.Clamp(along / Math.Max(sectionLength, 1e-9), 0.05, 0.9);
    }

    /// <summary>
    /// The bark's radius factor at a ring point and its slope along the branch (per Ez Tree unit): the root flare (the
    /// trunk's radius × up to <see cref="TreeParams.RootFlare"/> at the ground, fading out over
    /// <see cref="TreeParams.RootFlareHeight"/> metres, in <see cref="TreeParams.RootFlareLobes"/> buttress lobes), or a
    /// branch collar (× <see cref="TreeParams.CollarScale"/> at the base, easing back over
    /// <see cref="TreeParams.CollarLength"/> base radii).
    /// </summary>
    internal static (double Factor, double Slope) BarkProfile(in SkeletonBranch branch, TreeParams parameters, double originY, double along,
        double angle, int seed)
    {
        if (IsFlared(branch, parameters))
        {
            var scale = Math.Max(parameters.Scale, 1e-6);
            var height = Math.Max(parameters.RootFlareHeight, 1e-3);
            var y = originY * scale;
            if (y >= height)
                return (1, 0);
            var u = 1 - y / height;
            var lobe = 1 + 0.45 * Math.Cos(Math.Max(1, parameters.RootFlareLobes) * angle + 6.2831853 * Hash01((uint)seed * 2246822519u + 7u));
            var excess = (parameters.RootFlare - 1) * lobe;
            return (1 + excess * u * u, excess * -2 * u / height * scale);
        }

        if (IsCollared(branch, parameters))
        {
            var span = Math.Max(parameters.CollarLength * branch.BaseRadius, 1e-6);
            var s = Math.Clamp(along / span, 0, 1);
            var excess = parameters.CollarScale - 1;
            return (1 + excess * (1 - s * s * (3 - 2 * s)), excess * -(6 * s - 6 * s * s) / span);
        }

        return (1, 0);
    }
}

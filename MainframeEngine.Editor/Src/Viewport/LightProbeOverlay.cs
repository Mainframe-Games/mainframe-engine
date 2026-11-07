using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// View › Show Light Probes (G8e.1, ADR 0275): every baked <see cref="LightProbeVolume"/> in the edited scene draws its
/// probes near the camera as small crosses in the view's depth-tested lines. A cross's brightness is the probe's baked
/// sky visibility above it (the visible fraction of the upper hemisphere, cosine-weighted: black = the sky is hidden),
/// its tint the bounce light reaching it from above (× the volume's <see cref="LightProbeVolume.Energy"/>). Leaks show as
/// bright crosses inside rock or under a dense canopy, dark pockets as black ones in the open.
/// </summary>
/// <remarks>
/// Only probes within <see cref="Radius"/> metres of the camera are drawn, at most <see cref="MaxProbes"/> per frame (the
/// nearest columns first, by walking outwards from the camera's column), so a 133 000-probe forest bake stays cheap. Reads
/// the CPU coefficients (<see cref="LightProbeData.Coefficients"/>); allocates nothing per frame.
/// </remarks>
internal static class LightProbeOverlay
{
    /// <summary>Probes farther from the camera than this (metres) are not drawn.</summary>
    public const float Radius = 24f;

    /// <summary>At most this many probes per frame, over every volume.</summary>
    public const int MaxProbes = 8192;

    /// <summary>Draws the probes of every baked volume under <paramref name="root"/>; returns how many were drawn.</summary>
    public static int Draw(Node root, Vector3 camera, DebugLines lines)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(lines);
        var drawn = 0;
        DrawTree(root, camera, lines, ref drawn);
        return drawn;
    }

    private static void DrawTree(Node node, Vector3 camera, DebugLines lines, ref int drawn)
    {
        if (node is LightProbeVolume { Visible: true, Data: { HasData: true } data } volume)
            drawn += DrawVolume(data, volume.Energy, camera, lines, MaxProbes - drawn);
        var children = node.Children;
        for (var i = 0; i < children.Count && drawn < MaxProbes; i++)
            DrawTree(children[i], camera, lines, ref drawn);
    }

    /// <summary>Draws up to <paramref name="budget"/> probes of <paramref name="data"/> within <see cref="Radius"/> of <paramref name="camera"/>.</summary>
    internal static int DrawVolume(LightProbeData data, float energy, Vector3 camera, DebugLines lines, int budget)
    {
        var grid = data.Grid;
        if (grid.IsEmpty || budget <= 0)
            return 0;
        var coefficients = data.Coefficients;
        var ground = data.Ground;
        var half = MathF.Min(grid.Spacing.X, grid.Spacing.Z) * 0.12f;

        // The camera's column, clamped into the grid; square rings of columns outwards until past the radius or the budget.
        var cx = Math.Clamp((int)MathF.Round((camera.X - grid.Origin.X) / grid.Spacing.X), 0, grid.CountX - 1);
        var cz = Math.Clamp((int)MathF.Round((camera.Z - grid.Origin.Z) / grid.Spacing.Z), 0, grid.CountZ - 1);
        var rings = (int)MathF.Ceiling(Radius / MathF.Min(grid.Spacing.X, grid.Spacing.Z)) + 1;
        var radius2 = Radius * Radius;
        var drawn = 0;
        for (var ring = 0; ring <= rings; ring++)
        {
            for (var dz = -ring; dz <= ring; dz++)
            {
                var z = cz + dz;
                if ((uint)z >= (uint)grid.CountZ)
                    continue;
                // The ring's top and bottom rows take every column, the rows between only the two ends.
                var step = Math.Abs(dz) == ring ? 1 : Math.Max(2 * ring, 1);
                for (var dx = -ring; dx <= ring; dx += step)
                {
                    var x = cx + dx;
                    if ((uint)x >= (uint)grid.CountX)
                        continue;
                    var columnGround = grid.Layout == ProbeLayout.TerrainFollowing ? ground[grid.Column(x, z)] : 0f;
                    for (var y = 0; y < grid.CountY; y++)
                    {
                        var p = grid.Position(x, y, z, columnGround);
                        if (Vector3.DistanceSquared(p, camera) > radius2)
                            continue;
                        var color = ProbeColor(coefficients.Slice(grid.Index(x, y, z) * LightProbeData.CoefficientsPerProbe, LightProbeData.CoefficientsPerProbe), energy);
                        lines.AddLine(p - new Vector3(half, 0f, 0f), p + new Vector3(half, 0f, 0f), color);
                        lines.AddLine(p - new Vector3(0f, half, 0f), p + new Vector3(0f, half, 0f), color);
                        lines.AddLine(p - new Vector3(0f, 0f, half), p + new Vector3(0f, 0f, half), color);
                        if (++drawn >= budget)
                            return drawn;
                    }

                    if (ring == 0)
                        break;
                }
            }
        }

        return drawn;
    }

    /// <summary>
    /// A probe's cross colour (display values, alpha 1): its sky visibility toward up (cosine-weighted, 0–1) as grey,
    /// plus its bounce light toward up × <paramref name="energy"/>, clamped.
    /// </summary>
    internal static Vector4 ProbeColor(ReadOnlySpan<float> probe, float energy)
    {
        var visibility = Math.Clamp(SphericalHarmonics.L1Irradiance(probe[0], new Vector3(probe[1], probe[2], probe[3]), Vector3.UnitY), 0f, 1f);
        var r = Bounce(probe, 4) * energy;
        var g = Bounce(probe, 8) * energy;
        var b = Bounce(probe, 12) * energy;
        return new Vector4(Math.Clamp(visibility + r, 0f, 1f), Math.Clamp(visibility + g, 0f, 1f), Math.Clamp(visibility + b, 0f, 1f), 1f);

        static float Bounce(ReadOnlySpan<float> probe, int at) =>
            MathF.Max(0f, SphericalHarmonics.L1Irradiance(probe[at], new Vector3(probe[at + 1], probe[at + 2], probe[at + 3]), Vector3.UnitY));
    }
}

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace MainframeEngine;

/// <summary>What a light-probe bake does (ADR 0170); the <see cref="LightProbeVolume"/> exports fill it.</summary>
public sealed record ProbeBakeSettings
{
    /// <summary>Rays per probe for the sky visibility (and the validity test).</summary>
    public int RaysPerProbe { get; init; } = 256;

    /// <summary>Bounce passes (0: sky visibility only).</summary>
    public int Bounces { get; init; } = 3;

    /// <summary>Rays per probe in each bounce pass (0: a quarter of <see cref="RaysPerProbe"/>, at least 32).</summary>
    public int BounceRays { get; init; }

    /// <summary>Seeds every probe's ray rotation and leaf events: a bake is deterministic for a seed, whatever the thread count.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>A probe whose rays hit back faces more often than this is inside geometry: replaced by its valid neighbours.</summary>
    public float InvalidBackFaceFraction { get; init; } = 0.25f;

    /// <summary>Smooths the probes with a [1 2 1] filter per axis after the bake (removes ray noise).</summary>
    public bool Blur { get; init; } = true;

    /// <summary>Threads (0: every core).</summary>
    public int MaxThreads { get; init; }

    internal int ResolvedBounceRays => BounceRays > 0 ? BounceRays : Math.Max(32, RaysPerProbe / 4);
}

/// <summary>A finished bake: the data and what it cost.</summary>
public sealed record ProbeBakeResult(LightProbeData Data, TimeSpan Elapsed, int Probes, int InvalidProbes, long Rays)
{
    /// <summary>Seconds of each pass: the visibility pass, then each bounce pass.</summary>
    public IReadOnlyList<double> PassSeconds { get; init; } = [];
}

/// <summary>
/// The CPU light-probe bake (ADR 0170): for each probe of a <see cref="ProbeGrid"/>, Fibonacci rays with a per-probe
/// random rotation through a <see cref="ProbeBakeScene"/>.
/// <list type="number">
/// <item><b>Visibility.</b> Each ray's transmittance to the sky (0 at a solid hit, else e^−τ through the canopy) is
/// projected onto SH L1. Rays that hit back faces count against the probe; a probe over
/// <see cref="ProbeBakeSettings.InvalidBackFaceFraction"/> is inside geometry.</item>
/// <item><b>Fix-up.</b> Invalid probes take the mean of their valid neighbours (repeated until every probe has a value),
/// so the shaders' trilinear filtering needs no per-probe weights.</item>
/// <item><b>Bounce</b> passes (<see cref="ProbeBakeSettings.Bounces"/>): each ray finds its first solid hit and,
/// stochastically, a leaf that scatters it (free-path sampling through the canopy's optical depth); the hit's radiance
/// is albedo × (sun × N·L × the sun's transmittance + sky × the nearest probe's visibility + the nearest probe's bounce of
/// the previous pass), leaves also passing light through from behind. Projected onto SH L1 RGB.</item>
/// <item><b>Blur</b>: a [1 2 1] filter along each axis.</item>
/// </list>
/// Parallel over probes; every probe's rays depend only on the seed and its index, so the result does not depend on the
/// thread count.
/// </summary>
internal static class ProbeBaker
{
    private const int Per = LightProbeData.CoefficientsPerProbe;

    /// <summary>A grid's ground heights from the scene (terrain-following: the terrain under each column, else the origin's height).</summary>
    internal static float[] GroundHeights(ProbeGrid grid, ProbeBakeScene scene)
    {
        var ground = new float[grid.ColumnCount];
        for (var z = 0; z < grid.CountZ; z++)
            for (var x = 0; x < grid.CountX; x++)
                ground[grid.Column(x, z)] = grid.Layout == ProbeLayout.TerrainFollowing
                    ? scene.GroundAt(grid.Origin.X + x * grid.Spacing.X, grid.Origin.Z + z * grid.Spacing.Z) ?? grid.Origin.Y
                    : 0f;
        return ground;
    }

    /// <summary>The hash a bake of <paramref name="scene"/> on <paramref name="grid"/> with <paramref name="settings"/> stores (hex).</summary>
    internal static string Hash(ProbeBakeScene scene, ProbeGrid grid, ProbeBakeSettings settings) => Hash(scene.ContentHash, grid, settings);

    /// <summary>The bake hash from a scene's <see cref="ProbeBakeScene.ContentHash"/> (or a fingerprint of the same inputs).</summary>
    internal static string Hash(byte[] contentHash, ProbeGrid grid, ProbeBakeSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(contentHash);
        Span<float> values =
        [
            (float)grid.Layout, grid.Origin.X, grid.Origin.Y, grid.Origin.Z, grid.Spacing.X, grid.Spacing.Y, grid.Spacing.Z,
            grid.CountX, grid.CountY, grid.CountZ, settings.RaysPerProbe, settings.Bounces, settings.ResolvedBounceRays,
            settings.Seed, settings.InvalidBackFaceFraction, settings.Blur ? 1f : 0f,
        ];
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(grid.LayerHeights.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Bakes <paramref name="grid"/> in <paramref name="scene"/>; <paramref name="progress"/> gets 0 … 1.</summary>
    internal static ProbeBakeResult Bake(ProbeBakeScene scene, ProbeGrid grid, ProbeBakeSettings settings,
        IProgress<float>? progress = null, CancellationToken cancellation = default) =>
        Bake(scene, grid, settings, progress, cancellation, out _);

    internal static ProbeBakeResult Bake(ProbeBakeScene scene, ProbeGrid grid, ProbeBakeSettings settings,
        IProgress<float>? progress, CancellationToken cancellation, out bool[] valid)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        if (grid.IsEmpty)
            throw new ArgumentException("The probe grid is empty.", nameof(grid));
        if (grid.Layout == ProbeLayout.TerrainFollowing && (grid.LayerHeights.Length < grid.CountY || grid.CountY > ProbeGrid.MaxLayers))
            throw new ArgumentException($"A terrain-following grid needs 1–{ProbeGrid.MaxLayers} layer heights.", nameof(grid));
        var watch = Stopwatch.StartNew();
        var probes = grid.ProbeCount;
        var ground = GroundHeights(grid, scene);
        var positions = new Vector3[probes];
        for (var z = 0; z < grid.CountZ; z++)
            for (var y = 0; y < grid.CountY; y++)
                for (var x = 0; x < grid.CountX; x++)
                    positions[grid.Index(x, y, z)] = grid.Position(x, y, z, ground[grid.Column(x, z)]);

        var coefficients = new float[probes * Per];
        valid = new bool[probes];
        var validMask = valid;
        var passes = 1 + Math.Max(0, settings.Bounces);
        var done = 0L;
        long rays = 0;
        var totalWork = (long)probes * passes;
        var options = new ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = settings.MaxThreads > 0 ? settings.MaxThreads : -1,
        };
        const int block = 32;
        var blocks = (probes + block - 1) / block;

        // 1. Sky visibility and validity.
        var passSeconds = new List<double>();
        var passWatch = Stopwatch.StartNew();
        var visRays = Math.Max(16, settings.RaysPerProbe);
        Parallel.For(0, blocks, options, () => new ProbeTraceContext(scene.InstanceCount), (b, _, ctx) =>
        {
            var end = Math.Min(probes, (b + 1) * block);
            for (var p = b * block; p < end; p++)
                validMask[p] = Visibility(scene, positions[p], p, visRays, settings, coefficients.AsSpan(p * Per, 4), ctx);
            Interlocked.Add(ref rays, (long)(end - b * block) * visRays);
            progress?.Report((float)Interlocked.Add(ref done, end - b * block) / totalWork);
            return ctx;
        }, static _ => { });

        var invalid = 0;
        foreach (var v in validMask)
            if (!v)
                invalid++;
        Dilate(grid, coefficients, validMask, 0, 4, OpenSky);
        passSeconds.Add(passWatch.Elapsed.TotalSeconds);

        // 2. Bounces.
        var bounceRays = settings.ResolvedBounceRays;
        var previous = new float[probes * Per];
        for (var pass = 1; pass < passes; pass++)
        {
            Array.Copy(coefficients, previous, coefficients.Length);
            var source = previous;
            var passIndex = pass;
            Parallel.For(0, blocks, options, () => new ProbeTraceContext(scene.InstanceCount), (b, _, ctx) =>
            {
                var end = Math.Min(probes, (b + 1) * block);
                long traced = 0;
                for (var p = b * block; p < end; p++)
                    traced += Bounce(scene, grid, ground, source, positions[p], p, passIndex, bounceRays, settings, validMask[p],
                        coefficients.AsSpan(p * Per + 4, 12), ctx);
                Interlocked.Add(ref rays, traced);
                progress?.Report((float)Interlocked.Add(ref done, end - b * block) / totalWork);
                return ctx;
            }, static _ => { });
            Dilate(grid, coefficients, validMask, 4, 12, static _ => 0f);
            passSeconds.Add(passWatch.Elapsed.TotalSeconds - passSeconds.Sum());
        }

        // 3. Blur.
        if (settings.Blur)
            Blur(grid, coefficients);

        var data = new LightProbeData
        {
            BakeHash = Hash(scene, grid, settings),
            RaysPerProbe = settings.RaysPerProbe,
            Bounces = settings.Bounces,
        };
        data.SetData(grid, coefficients, ground);
        progress?.Report(1f);
        return new ProbeBakeResult(data, watch.Elapsed, probes, invalid, rays) { PassSeconds = passSeconds };
    }

    // The unoccluded visibility set: c0 = 4π·Y0 (the cosine-convolved value is 1 everywhere).
    private static float OpenSky(int k) => k == 0 ? 4f * MathF.PI * SphericalHarmonics.Y0 : 0f;

    // ── Passes ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Visibility(ProbeBakeScene scene, Vector3 position, int probe, int count, ProbeBakeSettings settings,
        Span<float> output, ProbeTraceContext ctx)
    {
        var rotation = RandomRotation(settings.Seed, probe, 0);
        float c0 = 0f;
        var c1 = Vector3.Zero;
        var back = 0;
        for (var r = 0; r < count; r++)
        {
            var dir = Vector3.Transform(SphericalHarmonics.FibonacciDirection(r, count), rotation);
            var far = scene.ExitDistance(position, dir);
            var hit = new ProbeHit { T = far };
            if (scene.TraceSolid(position, dir, ref hit, ctx))
            {
                if (hit.BackFace)
                    back++;
                continue;
            }

            var eventT = 0f; // no leaf events in this pass
            var tree = -1;
            var transmittance = MathF.Exp(-scene.Canopy(position, dir, far, float.PositiveInfinity, ref eventT, ref tree, ctx));
            c0 += transmittance * SphericalHarmonics.Y0;
            c1 += transmittance * SphericalHarmonics.Y1 * dir;
        }

        var weight = 4f * MathF.PI / count;
        output[0] = c0 * weight;
        output[1] = c1.X * weight;
        output[2] = c1.Y * weight;
        output[3] = c1.Z * weight;
        return back <= settings.InvalidBackFaceFraction * count;
    }

    private static long Bounce(ProbeBakeScene scene, ProbeGrid grid, float[] ground, float[] source, Vector3 position, int probe,
        int pass, int count, ProbeBakeSettings settings, bool probeValid, Span<float> output, ProbeTraceContext ctx)
    {
        output.Clear();
        if (!probeValid)
            return 0; // filled from its neighbours
        var rotation = RandomRotation(settings.Seed, probe, pass);
        var r0 = Vector3.Zero;
        var rx = Vector3.Zero;
        var ry = Vector3.Zero;
        var rz = Vector3.Zero;
        long traced = 0;
        for (var r = 0; r < count; r++)
        {
            var dir = Vector3.Transform(SphericalHarmonics.FibonacciDirection(r, count), rotation);
            var far = scene.ExitDistance(position, dir);
            var hit = new ProbeHit { T = far };
            var solid = scene.TraceSolid(position, dir, ref hit, ctx);
            traced++;
            var u = Random01(settings.Seed, probe, pass * 65536 + r);
            var eventTau = -MathF.Log(1f - u * 0.999999f);
            var eventT = -1f;
            var tree = -1;
            scene.Canopy(position, dir, solid ? hit.T : far, eventTau, ref eventT, ref tree, ctx, tauLimit: eventTau); // past the event the rest is unused
            var radiance = Vector3.Zero;
            if (eventT >= 0f && tree >= 0)
            {
                var (albedo, transmission) = scene.Leaf(tree);
                var p = position + dir * eventT;
                radiance += LeafRadiance(scene, grid, ground, source, p, -dir, albedo, transmission, ctx, ref traced);
            }
            else if (solid && !hit.BackFace)
            {
                var p = position + dir * hit.T + hit.Normal * 0.02f;
                // No leaf scattered the ray (probability e^−τ): the surface's light arrives whole.
                radiance += SurfaceRadiance(scene, grid, ground, source, p, hit.Normal, hit.Albedo, ctx, ref traced);
            }

            if (radiance == Vector3.Zero)
                continue;
            r0 += radiance * SphericalHarmonics.Y0;
            var b = SphericalHarmonics.Y1 * dir;
            rx += radiance * b.X;
            ry += radiance * b.Y;
            rz += radiance * b.Z;
        }

        var weight = 4f * MathF.PI / count;
        // Layout: red (c0, x, y, z), green, blue.
        output[0] = r0.X * weight;
        output[1] = rx.X * weight;
        output[2] = ry.X * weight;
        output[3] = rz.X * weight;
        output[4] = r0.Y * weight;
        output[5] = rx.Y * weight;
        output[6] = ry.Y * weight;
        output[7] = rz.Y * weight;
        output[8] = r0.Z * weight;
        output[9] = rx.Z * weight;
        output[10] = ry.Z * weight;
        output[11] = rz.Z * weight;
        return traced;
    }

    // Radiance leaving a diffuse surface towards the probe: sun (shadowed) + sky × visibility + last pass's bounce.
    private static Vector3 SurfaceRadiance(ProbeBakeScene scene, ProbeGrid grid, float[] ground, float[] source, Vector3 p,
        Vector3 n, Vector3 albedo, ProbeTraceContext ctx, ref long traced)
    {
        var light = Vector3.Zero;
        var cos = Vector3.Dot(n, scene.SunDirection);
        if (cos > 0f && scene.SunRadiance != Vector3.Zero)
        {
            traced++;
            light += scene.SunRadiance * (cos * scene.SunTransmittance(p, ctx));
        }

        light += Indirect(scene, grid, ground, source, p, n);
        return albedo * light;
    }

    // A leaf facing the probe (n): reflects what reaches its front, passes `transmission` of what reaches its back.
    private static Vector3 LeafRadiance(ProbeBakeScene scene, ProbeGrid grid, float[] ground, float[] source, Vector3 p,
        Vector3 n, Vector3 albedo, float transmission, ProbeTraceContext ctx, ref long traced)
    {
        var light = Vector3.Zero;
        var cos = Vector3.Dot(n, scene.SunDirection);
        if (scene.SunRadiance != Vector3.Zero)
        {
            traced++;
            var sun = scene.SunRadiance * scene.SunTransmittance(p, ctx);
            light += sun * (cos > 0f ? cos : -cos * transmission);
        }

        light += Indirect(scene, grid, ground, source, p, n) + Indirect(scene, grid, ground, source, p, -n) * transmission;
        return albedo * light;
    }

    // Sky × the nearest probe's visibility + the nearest probe's bounce, around n.
    private static Vector3 Indirect(ProbeBakeScene scene, ProbeGrid grid, float[] ground, float[] source, Vector3 p, Vector3 n)
    {
        var probe = NearestProbe(grid, ground, p) * Per;
        var vis = Math.Clamp(SphericalHarmonics.L1Irradiance(source[probe], new Vector3(source[probe + 1], source[probe + 2], source[probe + 3]), n), 0f, 1f);
        var bounce = new Vector3(
            SphericalHarmonics.L1Irradiance(source[probe + 4], new Vector3(source[probe + 5], source[probe + 6], source[probe + 7]), n),
            SphericalHarmonics.L1Irradiance(source[probe + 8], new Vector3(source[probe + 9], source[probe + 10], source[probe + 11]), n),
            SphericalHarmonics.L1Irradiance(source[probe + 12], new Vector3(source[probe + 13], source[probe + 14], source[probe + 15]), n));
        return scene.Sky.Irradiance(n) * vis + Vector3.Max(bounce, Vector3.Zero);
    }

    /// <summary>The index of the probe nearest <paramref name="p"/> (clamped to the grid).</summary>
    internal static int NearestProbe(ProbeGrid grid, float[] ground, Vector3 p)
    {
        var x = Math.Clamp((int)MathF.Round((p.X - grid.Origin.X) / grid.Spacing.X), 0, grid.CountX - 1);
        var z = Math.Clamp((int)MathF.Round((p.Z - grid.Origin.Z) / grid.Spacing.Z), 0, grid.CountZ - 1);
        var height = grid.Layout == ProbeLayout.TerrainFollowing ? p.Y - ground[grid.Column(x, z)] : p.Y - grid.Origin.Y;
        var y = Math.Clamp((int)MathF.Round(grid.LayerCoordinate(height)), 0, grid.CountY - 1);
        return grid.Index(x, y, z);
    }

    // ── Fix-up and filtering ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills coefficients [<paramref name="offset"/>, + <paramref name="count"/>) of every invalid probe with the mean of
    /// its valid six neighbours, growing inwards until all are filled; probes with no valid probe anywhere get
    /// <paramref name="fallback"/>(k).
    /// </summary>
    internal static void Dilate(ProbeGrid grid, float[] coefficients, bool[] valid, int offset, int count, Func<int, float> fallback)
    {
        var filled = (bool[])valid.Clone();
        var next = new List<(int Probe, float[] Values)>();
        Span<int> neighbours = stackalloc int[6];
        while (true)
        {
            next.Clear();
            for (var z = 0; z < grid.CountZ; z++)
            {
                for (var y = 0; y < grid.CountY; y++)
                {
                    for (var x = 0; x < grid.CountX; x++)
                    {
                        var p = grid.Index(x, y, z);
                        if (filled[p])
                            continue;
                        var n = 0;
                        if (x > 0) neighbours[n++] = grid.Index(x - 1, y, z);
                        if (x + 1 < grid.CountX) neighbours[n++] = grid.Index(x + 1, y, z);
                        if (y > 0) neighbours[n++] = grid.Index(x, y - 1, z);
                        if (y + 1 < grid.CountY) neighbours[n++] = grid.Index(x, y + 1, z);
                        if (z > 0) neighbours[n++] = grid.Index(x, y, z - 1);
                        if (z + 1 < grid.CountZ) neighbours[n++] = grid.Index(x, y, z + 1);
                        var sum = new float[count];
                        var used = 0;
                        for (var i = 0; i < n; i++)
                        {
                            var q = neighbours[i];
                            if (!filled[q])
                                continue;
                            used++;
                            for (var k = 0; k < count; k++)
                                sum[k] += coefficients[q * Per + offset + k];
                        }

                        if (used == 0)
                            continue;
                        for (var k = 0; k < count; k++)
                            sum[k] /= used;
                        next.Add((p, sum));
                    }
                }
            }

            if (next.Count == 0)
                break;
            foreach (var (p, values) in next)
            {
                values.CopyTo(coefficients, p * Per + offset);
                filled[p] = true;
            }
        }

        for (var p = 0; p < filled.Length; p++)
            if (!filled[p])
                for (var k = 0; k < count; k++)
                    coefficients[p * Per + offset + k] = fallback(k);
    }

    /// <summary>A [1 2 1] / 4 filter along x, z and y (edges renormalised) over every coefficient.</summary>
    internal static void Blur(ProbeGrid grid, float[] coefficients)
    {
        var temp = new float[coefficients.Length];
        BlurAxis(grid, coefficients, temp, 1, 0, 0);
        BlurAxis(grid, temp, coefficients, 0, 0, 1);
        BlurAxis(grid, coefficients, temp, 0, 1, 0);
        Array.Copy(temp, coefficients, coefficients.Length);
    }

    private static void BlurAxis(ProbeGrid grid, float[] from, float[] to, int dx, int dy, int dz)
    {
        for (var z = 0; z < grid.CountZ; z++)
        {
            for (var y = 0; y < grid.CountY; y++)
            {
                for (var x = 0; x < grid.CountX; x++)
                {
                    var p = grid.Index(x, y, z) * Per;
                    var weight = 2f;
                    for (var k = 0; k < Per; k++)
                        to[p + k] = from[p + k] * 2f;
                    for (var side = -1; side <= 1; side += 2)
                    {
                        int nx = x + dx * side, ny = y + dy * side, nz = z + dz * side;
                        if ((uint)nx >= (uint)grid.CountX || (uint)ny >= (uint)grid.CountY || (uint)nz >= (uint)grid.CountZ)
                            continue;
                        var q = grid.Index(nx, ny, nz) * Per;
                        weight += 1f;
                        for (var k = 0; k < Per; k++)
                            to[p + k] += from[q + k];
                    }

                    var inv = 1f / weight;
                    for (var k = 0; k < Per; k++)
                        to[p + k] *= inv;
                }
            }
        }
    }

    // ── Randomness (hashes of the seed, the probe and the sample: no shared state) ───────────────────────────────

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    private static float Random01(int seed, int probe, int sample) =>
        (Hash(Hash(Hash((uint)seed) ^ (uint)probe) ^ (uint)sample) >> 8) * (1f / 16777216f);

    // A uniformly random rotation (Shoemake): probe and pass decorrelate the Fibonacci sets of neighbours and passes.
    private static Quaternion RandomRotation(int seed, int probe, int pass)
    {
        var u1 = Random01(seed, probe, -1 - pass * 3);
        var u2 = Random01(seed, probe, -2 - pass * 3) * 2f * MathF.PI;
        var u3 = Random01(seed, probe, -3 - pass * 3) * 2f * MathF.PI;
        var a = MathF.Sqrt(1f - u1);
        var b = MathF.Sqrt(u1);
        return Quaternion.Normalize(new Quaternion(a * MathF.Sin(u2), a * MathF.Cos(u2), b * MathF.Sin(u3), b * MathF.Cos(u3)));
    }
}

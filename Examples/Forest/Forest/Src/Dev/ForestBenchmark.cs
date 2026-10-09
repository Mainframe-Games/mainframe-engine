using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MainframeEngine;

namespace Forest;

/// <summary>
/// <c>++ --benchmark</c>: after <see cref="WarmUpFrames"/> frames a camera flies <see cref="Spline"/> (low through the glade,
/// past the fall, up over the pine canopy, over the pond and back to the trailhead) for <see cref="Frames"/> frames;
/// wall-clock frame intervals are recorded (run it with <c>--fixed-fps 60 --no-vsync</c>: frame n shows the same pose on
/// every machine), then p50/p90/p99 are printed and written as JSON, and the game quits. Allocation-free while flying.
/// </summary>
public sealed class ForestBenchmark
{
    public const int WarmUpFrames = 240;

    /// <summary>The flight: world XZ and height above the ground (or the water).</summary>
    public static readonly Vector3[] Spline =
    [
        new(64f, 2.2f, 200f), new(58f, 2.5f, 168f), new(60f, 3f, 138f), new(70f, 3f, 104f), new(82f, 3f, 74f),
        new(88f, 4f, 58f), new(96f, 9f, 48f), new(120f, 26f, 70f), new(165f, 30f, 100f), new(200f, 34f, 140f),
        new(185f, 12f, 195f), new(150f, 2.5f, 212f), new(118f, 2.5f, 216f), new(96f, 3f, 228f), new(70f, 2.2f, 206f),
    ];

    private readonly float[] _frameMs;
    private readonly float[] _shadowMs;
    private readonly float[] _ssaoMs;
    private readonly float[] _fogMs;
    private readonly float[] _exposureMs;
    private readonly int[] _draws;
    private readonly long[] _allocations;
    private long _lastAllocated;
    private readonly Vector3[] _points;
    private readonly string? _outPath;
    private readonly long _processStart;
    private Camera3D? _camera;
    private int _frame;
    private long _last;
    private long _allocatedBefore;
    private double _loadMs;

    /// <summary>A baseline JSON to compare with (<c>--baseline</c>), and whether to overwrite it with this run instead (<c>--write-baseline</c>).</summary>
    public string? BaselinePath { get; init; }

    public bool WriteBaseline { get; init; }

    /// <summary>
    /// A CSV of every measured frame (<c>--frames-csv</c>: frame, ms, sun shadows' GPU ms, draws, the camera's x, y, z), to
    /// find where on the flight the slow frames are.
    /// </summary>
    public string? FramesCsvPath { get; init; }

    /// <summary>The run's exit code: 1 when it allocated, or when p50 or p99 is more than 10 % slower than the baseline.</summary>
    public int ExitCode { get; private set; }

    public ForestBenchmark(int frames, string? outPath)
    {
        Frames = frames;
        _frameMs = new float[frames];
        _shadowMs = new float[frames];
        _ssaoMs = new float[frames];
        _fogMs = new float[frames];
        _exposureMs = new float[frames];
        _draws = new int[frames];
        _allocations = new long[frames];
        _points = new Vector3[Spline.Length];
        _outPath = outPath;
        _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    }

    /// <summary>Measured frames (after the warm-up).</summary>
    public int Frames { get; }

    public bool Done { get; private set; }

    /// <summary>Called once per frame; returns true when the run is over (the caller quits).</summary>
    public bool Update(SceneTree tree, Node scene)
    {
        if (_camera is null)
        {
            _loadMs = (DateTime.UtcNow.Ticks - _processStart) / 10000.0;
            if (!Setup(tree, scene))
                return false;
        }

        var t = Math.Clamp((float)_frame / (WarmUpFrames + Frames), 0f, 1f) * (_points.Length - 1);
        var position = CatmullRom(_points, t);
        var ahead = CatmullRom(_points, MathF.Min(t + 0.25f, _points.Length - 1.001f));
        _camera!.Position = position;
        if (Vector3.DistanceSquared(ahead, position) > 1e-4f)
            _camera.LookAt(ahead + new Vector3(0f, -0.15f * Vector3.Distance(ahead, position), 0f));

        var now = Stopwatch.GetTimestamp();
        var measured = _frame - WarmUpFrames;
        if (measured == 0)
        {
            _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            _lastAllocated = _allocatedBefore;
        }

        if (measured > 0 && measured <= Frames)
        {
            var allocatedNow = GC.GetAllocatedBytesForCurrentThread();
            _allocations[measured - 1] = allocatedNow - _lastAllocated;
            _lastAllocated = allocatedNow;
            _frameMs[measured - 1] = (float)Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
            if (tree.Servers.Render is { } render)
            {
                _shadowMs[measured - 1] = (float)(render.ExistingShadows?.LastGpuMilliseconds ?? 0);
                _ssaoMs[measured - 1] = (float)render.SsaoGpuMilliseconds;
                _fogMs[measured - 1] = (float)render.VolumetricFogGpuMilliseconds;
                _exposureMs[measured - 1] = (float)render.AutoExposureGpuMilliseconds;
                _draws[measured - 1] = render.MeshStats.DrawCalls;
            }
        }

        _last = now;
        _frame++;
        if (measured < Frames)
            return false;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore;
        Report(tree, allocated);
        Done = true;
        return true;
    }

    private bool Setup(SceneTree tree, Node scene)
    {
        if (scene.FindChildren<ForestValley>(owned: false) is not [{ Terrain: { } terrain }, ..])
            return false;
        ResolveSpline(terrain.HeightAt, tree.Root.World3D.Water, _points);

        _camera = new Camera3D { Name = "BenchmarkCamera", Near = 0.05f, Far = 1200f, Fov = 55f };
        scene.AddChild(_camera);
        _camera.Current = true;
        Log.Info($"[Forest] Benchmark: {WarmUpFrames} warm-up frames, then {Frames} frames along the {Spline.Length}-point spline.");
        return true;
    }

    private void Report(SceneTree tree, long allocated)
    {
        if (FramesCsvPath is { } csv)
        {
            var lines = new List<string>(Frames + 1) { "frame,ms,shadow_ms,draws,x,y,z" };
            for (var i = 0; i < Frames; i++)
            {
                var p = PositionAt(i);
                lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{i},{_frameMs[i]:0.00},{_shadowMs[i]:0.00},{_draws[i]},{p.X:0.0},{p.Y:0.0},{p.Z:0.0}"));
            }

            File.WriteAllLines(csv, lines);
        }

        var sorted = (float[])_frameMs.Clone();
        Array.Sort(sorted);
        var shadows = (float[])_shadowMs.Clone();
        Array.Sort(shadows);
        var ssao = (float[])_ssaoMs.Clone();
        Array.Sort(ssao);
        var fog = (float[])_fogMs.Clone();
        Array.Sort(fog);
        var exposure = (float[])_exposureMs.Clone();
        Array.Sort(exposure);
        var mean = _frameMs.Average();
        var size = tree.Root.Size;
        // ADR 0174: the output (window) and the 3D render resolution.
        var vk = tree.Servers.Render?.Vulkan;
        var output = vk?.SwapchainExtent ?? new Silk.NET.Vulkan.Extent2D((uint)size.X, (uint)size.Y);
        var render = vk?.RenderExtent ?? output;
        var result = new BenchmarkResult(
            Machine: Environment.MachineName,
            Os: RuntimeInformation.OSDescription,
            Cpu: RuntimeInformation.ProcessArchitecture.ToString(),
            Width: (int)output.Width,
            Height: (int)output.Height,
            Frames: Frames,
            LoadMs: Math.Round(_loadMs),
            MeanMs: Round(mean),
            Fps: Round(1000f / mean),
            P50Ms: Round(Percentile(sorted, 0.5f)),
            P90Ms: Round(Percentile(sorted, 0.9f)),
            P99Ms: Round(Percentile(sorted, 0.99f)),
            P999Ms: Round(Percentile(sorted, 0.999f)),
            MaxMs: Round(sorted[^1]),
            ShadowGpuP50Ms: Round(Percentile(shadows, 0.5f)),
            ShadowGpuP90Ms: Round(Percentile(shadows, 0.9f)),
            MaxDrawCalls: _draws.Max(),
            AllocatedBytes: allocated,
            SsaoGpuP50Ms: Round(Percentile(ssao, 0.5f)),
            VolumetricFogGpuP50Ms: Round(Percentile(fog, 0.5f)),
            RenderWidth: (int)render.Width,
            RenderHeight: (int)render.Height,
            Scaling: vk is null ? null : $"{vk.Scaling3DMode} {vk.Scaling3DScale:0.##}",
            AutoExposureGpuP50Ms: Round(Percentile(exposure, 0.5f)));
        Log.Info($"[Forest] Benchmark {result.Width}x{result.Height} (3D at {result.RenderWidth}x{result.RenderHeight}, {result.Scaling}): p50 {result.P50Ms} ms, p90 {result.P90Ms} ms, p99 {result.P99Ms} ms, " +
                 $"max {result.MaxMs} ms (mean {result.MeanMs} ms, {result.Fps} fps); sun shadows GPU p50 {result.ShadowGpuP50Ms} ms; SSAO GPU p50 {result.SsaoGpuP50Ms} ms; " +
                 $"volumetric fog GPU p50 {result.VolumetricFogGpuP50Ms} ms; auto exposure GPU p50 {result.AutoExposureGpuP50Ms} ms; " +
                 $"≤ {result.MaxDrawCalls} draws; {allocated} B allocated; load {result.LoadMs} ms.");
        var path = _outPath ?? Path.Combine("artifacts", "forest-bench", $"forest-bench-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"[Forest] Benchmark written to {Path.GetFullPath(path)}");
        Compare(result);
        if (allocated > 0)
        {
            ExitCode = 1;
            var frames = string.Join(", ", _allocations.Select((b, i) => (b, i)).Where(p => p.b != 0).Take(30).Select(p => $"{p.i + 1}:{p.b}"));
            Log.Error($"[Forest] Benchmark: frames that allocated (frame:bytes) {frames}");
        }
    }

    private void Compare(BenchmarkResult result)
    {
        if (BaselinePath is not { } baseline)
            return;
        if (WriteBaseline)
        {
            File.WriteAllText(baseline, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Log.Info($"[Forest] Benchmark baseline written to {Path.GetFullPath(baseline)}");
            return;
        }

        if (!File.Exists(baseline))
        {
            Log.Warning($"[Forest] No benchmark baseline at {Path.GetFullPath(baseline)} (record one with --write-baseline).");
            return;
        }

        var reference = JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(baseline))!;
        if (reference.Width != result.Width || reference.Height != result.Height)
            Log.Warning($"[Forest] The baseline is {reference.Width}x{reference.Height}, this run {result.Width}x{result.Height}.");
        if (reference.RenderWidth != 0 && (reference.RenderWidth != result.RenderWidth || reference.RenderHeight != result.RenderHeight))
            Log.Warning($"[Forest] The baseline renders 3D at {reference.RenderWidth}x{reference.RenderHeight}, this run at {result.RenderWidth}x{result.RenderHeight}.");
        foreach (var (name, now, then) in (ReadOnlySpan<(string, double, double)>)[("p50", result.P50Ms, reference.P50Ms), ("p99", result.P99Ms, reference.P99Ms)])
        {
            var change = (now - then) / then;
            if (change > 0.10)
            {
                ExitCode = 1;
                Log.Error($"[Forest] Benchmark {name} {now} ms is {change:P0} slower than the baseline's {then} ms.");
            }
            else
            {
                Log.Info($"[Forest] Benchmark {name} {now} ms ({change:+0%;-0%} against the baseline's {then} ms).");
            }
        }
    }

    private static double Round(float value) => Math.Round(value, 2);

    /// <summary>The value at <paramref name="q"/> (0..1) of sorted <paramref name="sorted"/> (nearest rank).</summary>
    public static float Percentile(float[] sorted, float q)
    {
        if (sorted.Length == 0)
            return 0f;
        var rank = (int)MathF.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    // The camera's position at measured frame i (the frame after warm-up i + 1).
    private Vector3 PositionAt(int i) =>
        CatmullRom(_points, Math.Clamp((float)(WarmUpFrames + i + 1) / (WarmUpFrames + Frames), 0f, 1f) * (_points.Length - 1));

    /// <summary>
    /// <see cref="Spline"/> in world space: each point's height above the ground (<paramref name="groundHeight"/>) or the
    /// water (<paramref name="water"/>, when given), into <paramref name="points"/> (<see cref="Spline"/>'s length).
    /// </summary>
    public static void ResolveSpline(Func<float, float, float> groundHeight, WaterQueries? water, Vector3[] points)
    {
        for (var i = 0; i < Spline.Length; i++)
        {
            var s = Spline[i];
            var ground = groundHeight(s.X, s.Z);
            var surface = water?.SurfaceHeightAt(new Vector3(s.X, ground, s.Z)) ?? float.NaN;
            if (float.IsFinite(surface))
                ground = MathF.Max(ground, surface);
            points[i] = new Vector3(s.X, ground + s.Y, s.Z);
        }
    }

    /// <summary>A Catmull-Rom point on the open curve through <paramref name="points"/> at <paramref name="t"/> (0 … length − 1).</summary>
    public static Vector3 CatmullRom(Vector3[] points, float t)
    {
        var i = Math.Clamp((int)t, 0, points.Length - 2);
        var f = t - i;
        var p0 = points[Math.Max(i - 1, 0)];
        var p1 = points[i];
        var p2 = points[i + 1];
        var p3 = points[Math.Min(i + 2, points.Length - 1)];
        var f2 = f * f;
        var f3 = f2 * f;
        return 0.5f * (2f * p1 + (-p0 + p2) * f + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f2 + (-p0 + 3f * p1 - 3f * p2 + p3) * f3);
    }

    /// <summary>The JSON a run writes.</summary>
    public sealed record BenchmarkResult(string Machine, string Os, string Cpu, int Width, int Height, int Frames, double LoadMs, double MeanMs,
        double Fps, double P50Ms, double P90Ms, double P99Ms, double P999Ms, double MaxMs, double ShadowGpuP50Ms, double ShadowGpuP90Ms,
        int MaxDrawCalls, long AllocatedBytes, double SsaoGpuP50Ms = 0, double VolumetricFogGpuP50Ms = 0, int RenderWidth = 0,
        int RenderHeight = 0, string? Scaling = null, double AutoExposureGpuP50Ms = 0);
}

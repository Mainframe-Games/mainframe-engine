using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Runs BenchmarkDotNet, then optionally writes or checks <c>baseline.json</c>. A benchmark regresses
/// when its mean is more than <see cref="RegressionThreshold"/> slower than the baseline, or when it
/// allocates more per operation. Baselines are machine-specific: compare on the machine that wrote them.
/// </summary>
/// <remarks>
/// A run that produces no results (a filter that matches nothing, a benchmark that failed to build or run) fails the
/// comparison and never writes a baseline. Benchmarks build from this project's own <c>.csproj</c> (recorded at build
/// time), not from a search of the repository: BenchmarkDotNet's search also finds the copies in git worktrees
/// (<c>.claude/worktrees/</c>) and then refuses to build anything.
/// </remarks>
public static class BenchmarkHost
{
    public const double RegressionThreshold = 0.10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NewLine = "\n" }; // baseline.json is committed

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var bdnArgs = new List<string>();
        string? writePath = null, comparePath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--baseline-write" when i + 1 < args.Length:
                    writePath = args[++i];
                    break;
                case "--baseline-compare" when i + 1 < args.Length:
                    comparePath = args[++i];
                    break;
                default:
                    bdnArgs.Add(args[i]);
                    break;
            }
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkHost).Assembly).Run([.. bdnArgs], CreateConfig()).ToArray();
        var current = Collect(summaries, out var failed);

        if (current.Benchmarks.Count == 0 || failed.Count > 0)
        {
            Console.WriteLine(current.Benchmarks.Count == 0
                ? "No benchmark produced a result (does the filter match any? did the build fail?)."
                : $"{failed.Count} benchmark(s) produced no result: {string.Join(", ", failed)}.");
            if (writePath is not null)
                Console.WriteLine($"Baseline not written: {writePath}");
            return 1;
        }

        if (writePath is not null)
        {
            File.WriteAllText(writePath, JsonSerializer.Serialize(current, JsonOptions));
            Console.WriteLine($"Baseline written: {writePath} ({current.Benchmarks.Count} benchmarks)");
        }

        if (comparePath is null)
            return 0;

        var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(comparePath), JsonOptions)
                       ?? throw new InvalidDataException($"Empty baseline: {comparePath}");
        return Compare(baseline, current) ? 0 : 1;
    }

    // The default job, built by a toolchain that knows this project's file instead of searching for it.
    private static ManualConfig CreateConfig()
    {
        var config = ManualConfig.Create(DefaultConfig.Instance);
        var project = typeof(BenchmarkHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BenchmarkProjectFile")?.Value;
        if (project is null || !File.Exists(project))
            return config; // moved binaries: fall back to BenchmarkDotNet's own search

        var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var standard = CsProjCoreToolchain.From(new NetCoreAppSettings(tfm, runtimeFrameworkVersion: null!, name: tfm));
        var toolchain = new Toolchain($"{standard.Name} ({Path.GetFileName(project)})", new KnownProjectGenerator(project, tfm),
            standard.Builder, standard.Executor);
        return config.AddJob(Job.Default.WithToolchain(toolchain).AsDefault());
    }

    private sealed class KnownProjectGenerator(string projectFile, string tfm)
        // null: BenchmarkDotNet's own defaults (dotnet on PATH, default packages folder and runtime), as CsProjCoreToolchain passes.
        : CsProjGenerator(tfm, cliPath: null!, packagesPath: null!, runtimeFrameworkVersion: null!, isNetCore: true)
    {
        protected override FileInfo GetProjectFilePath(Type benchmarkTarget, ILogger logger) => new(projectFile);
    }

    private static Baseline Collect(IEnumerable<Summary> summaries, out List<string> failed)
    {
        var entries = new List<BaselineEntry>();
        failed = [];
        foreach (var summary in summaries)
            foreach (var report in summary.Reports)
            {
                // Type.Method[params], without the job name so short and default runs compare.
                var benchmark = report.BenchmarkCase;
                var name = $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}";
                if (benchmark.HasParameters)
                    name += $"({benchmark.Parameters.DisplayInfo})";

                if (report.ResultStatistics is not { } stats)
                {
                    failed.Add(name);
                    continue;
                }

                entries.Add(new BaselineEntry(
                    name,
                    Math.Round(stats.Mean, 3),
                    report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0));
            }

        return new Baseline(
            Machine: $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores",
            Runtime: RuntimeInformation.FrameworkDescription,
            Recorded: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Benchmarks: [.. entries.OrderBy(e => e.Name, StringComparer.Ordinal)]);
    }

    private static bool Compare(Baseline baseline, Baseline current)
    {
        var ok = true;
        var expected = baseline.Benchmarks.ToDictionary(b => b.Name, StringComparer.Ordinal);
        Console.WriteLine($"Comparing against baseline from {baseline.Recorded} ({baseline.Machine}):");
        foreach (var now in current.Benchmarks)
        {
            if (!expected.TryGetValue(now.Name, out var then))
            {
                Console.WriteLine($"  NEW   {now.Name}: {now.MeanNs:0.###} ns, {now.AllocatedBytes} B");
                continue;
            }

            var change = then.MeanNs > 0 ? (now.MeanNs - then.MeanNs) / then.MeanNs : 0;
            var slower = change > RegressionThreshold;
            var allocates = now.AllocatedBytes > then.AllocatedBytes;
            ok &= !slower && !allocates;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {(slower || allocates ? "FAIL" : "ok  ")}  {now.Name}: {then.MeanNs:0.###} → {now.MeanNs:0.###} ns ({change:+0.0%;-0.0%}), {then.AllocatedBytes} → {now.AllocatedBytes} B"));
        }

        Console.WriteLine(ok ? "No regressions." : $"Regressions found (> {RegressionThreshold:P0} slower or more allocation).");
        return ok;
    }

    public sealed record Baseline(string Machine, string Runtime, string Recorded, IReadOnlyList<BaselineEntry> Benchmarks);

    public sealed record BaselineEntry(string Name, double MeanNs, long AllocatedBytes);
}

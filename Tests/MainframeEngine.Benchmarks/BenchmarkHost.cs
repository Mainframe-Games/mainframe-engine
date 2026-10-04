using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Runs BenchmarkDotNet, then optionally writes or checks <c>baseline.json</c>. A benchmark regresses
/// when its mean is more than <see cref="RegressionThreshold"/> slower than the baseline, or when it
/// allocates more per operation. Baselines are machine-specific: compare on the machine that wrote them.
/// </summary>
public static class BenchmarkHost
{
    public const double RegressionThreshold = 0.10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkHost).Assembly).Run([.. bdnArgs]).ToArray();
        var current = Collect(summaries);

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

    private static Baseline Collect(IEnumerable<Summary> summaries)
    {
        var entries = new List<BaselineEntry>();
        foreach (var summary in summaries)
            foreach (var report in summary.Reports)
            {
                if (report.ResultStatistics is not { } stats)
                    continue;

                // Type.Method[params], without the job name so short and default runs compare.
                var benchmark = report.BenchmarkCase;
                var name = $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}";
                if (benchmark.HasParameters)
                    name += $"({benchmark.Parameters.DisplayInfo})";

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

using System.Text.Json;

namespace MainframeEngine.Tests.Crash;

[Collection(nameof(CrashReporterState))]
public sealed class CrashReporterTests
{
    private static InvalidOperationException Nested()
    {
        try
        {
            try
            {
                throw new FormatException("inner failure");
            }
            catch (FormatException inner)
            {
                throw new InvalidOperationException("outer failure", inner);
            }
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    [Fact]
    public void AFatalReportWritesTheBundleWithTheChainLogAndFacts()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        Log.Info("[Test] before the crash");
        CrashReporter.SetContext("level", () => "north-ridge");

        var folder = CrashReporter.WriteReport(Nested(), CrashKind.ManagedException);

        Assert.NotNull(folder);
        Assert.False(folder!.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(scratch.Crashes, "*.tmp"));
        var report = CrashReport.FromJson(File.ReadAllBytes(Path.Combine(folder, CrashReport.FileName)));
        Assert.Equal(CrashReport.CurrentFormat, report.Format);
        Assert.Equal(32, report.Id.Length);
        Assert.Equal(CrashKind.ManagedException, report.Kind);
        Assert.Equal("CrashTest", report.App.Name);
        Assert.Equal("1.2.3", report.App.Version);
        Assert.Equal(EngineInfo.Version, report.App.Engine);
        Assert.False(string.IsNullOrEmpty(report.App.Runtime));
        Assert.True(report.Os.Cpus > 0);
        Assert.Equal(Environment.ProcessId, report.Session.ProcessId);
        Assert.Equal("north-ridge", report.Context["level"]);

        // The chain, outermost first, with frames that symbolicate offline.
        Assert.Equal(["System.InvalidOperationException", "System.FormatException"], report.Exception.Select(e => e.Type));
        Assert.Equal("outer failure", report.Exception[0].Message);
        var frame = report.Exception[0].Frames[0];
        Assert.Contains(nameof(Nested), frame.Method, StringComparison.Ordinal);
        Assert.Equal(typeof(CrashReporterTests).Module.ModuleVersionId.ToString("D"), frame.Mvid);
        Assert.NotNull(frame.IlOffset);
        Assert.NotNull(frame.Token);
        Assert.Equal("MainframeEngine.Tests.dll", frame.Module);
        Assert.EndsWith("CrashReporterTests.cs", frame.File, StringComparison.Ordinal);
        Assert.True(frame.Line > 0);

        var log = File.ReadAllText(Path.Combine(folder, CrashReport.LogFileName));
        Assert.Contains("before the crash", log, StringComparison.Ordinal);
        Assert.Equal(CrashReport.LogFileName, report.Log);
    }

    [Fact]
    public void OnlyTheFirstFatalCrashWritesAndTheReserveIsReleased()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        Assert.True(CrashReporter.HoldsReserve);

        Assert.NotNull(CrashReporter.WriteReport(Nested(), CrashKind.ManagedException));
        Assert.False(CrashReporter.HoldsReserve);
        Assert.Null(CrashReporter.WriteReport(new InvalidOperationException("second"), CrashKind.ThreadException));
        Assert.Null(CrashReporter.ReportError(new TimeoutException("after the fatal one")));
        Assert.Single(scratch.Bundles());
    }

    [Fact]
    public void AThrowingCrashingHandlerAndSinksDoNotStopTheReport()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var sink = new RecordingSink();
        var early = new RecordingSink { Before = true };
        CrashReporter.AddSink(new ThrowingSink());
        CrashReporter.AddSink(sink);
        CrashReporter.AddSink(early);
        CrashReporter.Crashing += _ => throw new InvalidOperationException("handler bug");
        CrashReporter.Crashing += report => report.Context["handled"] = "yes";

        var folder = CrashReporter.WriteReport(Nested(), CrashKind.ManagedException);

        Assert.NotNull(folder);
        Assert.Equal("yes", CrashReporter.LoadFolder(folder!)!.Context["handled"]);
        Assert.Equal(folder, sink.Report!.Folder);   // after the write
        Assert.Null(early.FolderWhenCalled);           // RunsBeforeWrite: before it
    }

    [Fact]
    public void AStallingProviderCostsAtMostTheBudget()
    {
        using var scratch = new CrashScratch();
        using var release = new ManualResetEventSlim();
        CrashReporter.Install(scratch.Options(budget: TimeSpan.FromMilliseconds(200)));
        CrashReporter.SetContext("stalls", () =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return "late";
        });
        CrashReporter.SetContext("throws", () => throw new InvalidOperationException("provider bug"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var folder = CrashReporter.WriteReport(Nested(), CrashKind.ManagedException);
        watch.Stop();
        release.Set();

        Assert.NotNull(folder);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        var report = CrashReporter.LoadFolder(folder!)!;
        Assert.False(report.Context.ContainsKey("stalls"));
        Assert.False(report.Context.ContainsKey("throws"));
    }

    [Fact]
    public void NonFatalErrorsAreReportedOncePerType()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());

        Assert.NotNull(CrashReporter.ReportError(new TimeoutException("first"), "the note"));
        Assert.Null(CrashReporter.ReportError(new TimeoutException("again")));
        Assert.NotNull(CrashReporter.ReportError(new FormatException("other type")));

        var reports = scratch.Bundles().Select(CrashReporter.LoadFolder).ToList();
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(CrashKind.Error, r!.Kind));
        Assert.Contains(reports, r => r!.Note == "the note");
    }

    [Fact]
    public void OldBundlesArePrunedToMaxReports()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options(maxReports: 2));
        CrashReporter.ReportError(new TimeoutException());
        Thread.Sleep(1100); // bundle names carry the second
        CrashReporter.ReportError(new FormatException());
        Thread.Sleep(1100);
        CrashReporter.ReportError(new ArgumentException());

        var kept = scratch.Bundles().Select(b => CrashReporter.LoadFolder(b)!.Exception[0].Type).ToList();
        Assert.Equal(["System.FormatException", "System.ArgumentException"], kept);
    }

    [Fact]
    public void PendingReportsListEarlierRunsUntilHandled()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var folder = CrashReporter.WriteReport(Nested(), CrashKind.ManagedException)!;
        CrashReporter.Shutdown();

        CrashReporter.Install(scratch.Options());
        var pending = Assert.Single(CrashReporter.PendingReports);
        Assert.Equal(folder, pending.Folder);
        Assert.Equal(CrashKind.ManagedException, pending.Kind);
        Assert.StartsWith("InvalidOperationException in ", pending.Summary, StringComparison.Ordinal);
        Assert.NotNull(CrashReporter.Load(pending));

        CrashReporter.MarkHandled(pending, CrashDisposition.Copied);
        Assert.Empty(CrashReporter.PendingReports);
        Assert.True(File.Exists(Path.Combine(folder, CrashReporter.HandledFileName)));
        CrashReporter.Shutdown();

        CrashReporter.Install(scratch.Options());
        Assert.Empty(CrashReporter.PendingReports); // handled stays handled
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteRemovesTheBundle()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        CrashReporter.ReportError(new TimeoutException());
        CrashReporter.Shutdown();
        CrashReporter.Install(scratch.Options());
        var pending = Assert.Single(CrashReporter.PendingReports);

        CrashReporter.Delete(pending);

        Assert.False(Directory.Exists(pending.Folder));
        Assert.Empty(CrashReporter.PendingReports);
    }

    [Fact]
    public void ReportsScrubTheHomeFolderAndUserName()
    {
        var scrubber = new CrashScrubber("/Users/alice", "alice");

        Assert.Equal("~/game/Src/Player.cs", scrubber.Scrub("/Users/alice/game/Src/Player.cs"));
        Assert.Equal("saved by <user>", scrubber.Scrub("saved by alice"));
        Assert.Equal("~\\x", new CrashScrubber("C:\\Users\\bob", "bob").Scrub("C:\\Users\\bob\\x"));
        Assert.Equal("~/y", new CrashScrubber("C:\\Users\\bob", "bob").Scrub("C:/Users/bob/y"));
        Assert.Equal("an ox ate", new CrashScrubber("/home/ox", "ox").Scrub("an ox ate")); // short names are left alone
    }

    [Fact]
    public void AggregateExceptionsAreFlattened()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var aggregate = new AggregateException(new TimeoutException("a"), new FormatException("b", new ArgumentException("c")));

        var report = CrashReporter.LoadFolder(CrashReporter.WriteReport(aggregate, CrashKind.ThreadException)!)!;

        Assert.Equal(["System.AggregateException", "System.TimeoutException", "System.FormatException", "System.ArgumentException"],
            report.Exception.Select(e => e.Type));
    }

    [Fact]
    public void ADeviceLossInTheChainMakesADeviceLostReport()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        CrashContext.DeviceLoss = () => new CrashGpuLoss { Frame = 41, InFlight = ["Scene"], BreadcrumbsAvailable = true };
        var lost = CrashScratch.Thrown(() => new VulkanDeviceLostException("[Vulkan] vkQueueSubmit (frame) failed: the device was lost."));
        VulkanDeviceLostException.ResetForTests();

        var report = CrashReporter.LoadFolder(CrashReporter.WriteReport(new InvalidOperationException("wrapper", lost), CrashKind.ManagedException)!)!;

        Assert.Equal(CrashKind.DeviceLost, report.Kind);
        Assert.Equal(["Scene"], report.GpuLoss!.InFlight);
        Assert.Equal(41, report.GpuLoss.Frame);
        Assert.Equal("The graphics driver stopped responding", report.Summary);
    }

    [Fact]
    public void ReportJsonRoundTripsWithCamelCaseKeys()
    {
        var report = new CrashReport
        {
            Id = "0123456789abcdef0123456789abcdef",
            Kind = CrashKind.UncleanExit,
            Timestamp = new DateTime(2026, 10, 11, 10, 42, 7, DateTimeKind.Utc),
            ExitCode = 139,
            Gpu = new CrashGpuInfo { Name = "Apple M3 Pro", VendorId = "0x106b" },
        };
        report.Session.Node = "/root/Player";

        var json = report.ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("UncleanExit", root.GetProperty("kind").GetString());
        Assert.Equal("/root/Player", root.GetProperty("session").GetProperty("node").GetString());
        Assert.Equal("0x106b", root.GetProperty("gpu").GetProperty("vendorId").GetString());
        Assert.True(root.TryGetProperty("gpuLoss", out _));
        Assert.True(root.GetProperty("device").TryGetProperty("tier", out _)); // mobile's fields, null on desktop

        var back = CrashReport.FromJson(json);
        Assert.Equal(report.Id, back.Id);
        Assert.Equal(CrashKind.UncleanExit, back.Kind);
        Assert.Equal(139, back.ExitCode);
        Assert.Equal(report.Timestamp, back.Timestamp);
        // 139 is SIGSEGV's shell code on Unix; Windows names exit codes by NTSTATUS, so it stays a number there.
        Assert.StartsWith(OperatingSystem.IsWindows() ? "The game stopped unexpectedly (exit code 139" : "The game stopped unexpectedly (SIG",
            back.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryNamesTheNodeTypeAndCallback()
    {
        var report = new CrashReport { Kind = CrashKind.ManagedException };
        report.Exception.Add(new CrashException { Type = "System.NullReferenceException" });
        report.Session.NodeType = "Game.PlayerController";
        report.Session.Callback = "OnPhysicsProcess";

        Assert.Equal("NullReferenceException in PlayerController.OnPhysicsProcess", report.Summary);

        report.Session.NodeType = null;
        report.Exception[0].Frames.Add(new CrashFrame { Method = "Game.Level.Load(String)" });
        Assert.Equal("NullReferenceException in Level.Load", report.Summary);
    }

    private sealed class RecordingSink : ICrashSink
    {
        public bool Before { get; init; }

        public bool RunsBeforeWrite => Before;

        public CrashReport? Report { get; private set; }

        public string? FolderWhenCalled { get; private set; }

        public void OnCrash(CrashReport report)
        {
            Report = report;
            FolderWhenCalled = report.Folder;
        }
    }

    private sealed class ThrowingSink : ICrashSink
    {
        public void OnCrash(CrashReport report) => throw new InvalidOperationException("sink bug");
    }
}

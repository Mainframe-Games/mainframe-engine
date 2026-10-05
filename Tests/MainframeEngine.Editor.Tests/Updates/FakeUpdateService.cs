namespace MainframeEngine.Editor.Tests.Updates;

/// <summary>An <see cref="IUpdateService"/> the tests drive by hand: downloads finish when the test completes them.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public string CurrentVersion { get; set; } = "1.0.0";
    public InstallLocation Install { get; set; } = new("/Applications/Mainframe Engine.app", InstallKind.Replaceable);
    public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckStatus.UpToDate);
    public TaskCompletionSource<UpdateCheckResult>? PendingCheck { get; set; }
    public UpdateResult? LastResult { get; set; }
    public Exception? StartError { get; set; }

    /// <summary>Like a real download past its last token check (verify, extract): Cancel does not stop it.</summary>
    public bool IgnoresCancellation { get; set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public TaskCompletionSource<StagedUpdate> Download { get; private set; } = new();
    public List<(StagedUpdate Staged, string? Project)> Started { get; } = [];
    public List<StagedUpdate> Revealed { get; } = [];

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        Checks++;
        return PendingCheck?.Task ?? Task.FromResult(CheckResult);
    }

    public Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct)
    {
        Downloads++;
        Download = new TaskCompletionSource<StagedUpdate>();
        if (!IgnoresCancellation)
            ct.Register(() => Download.TrySetCanceled(ct));
        progress?.Report(0.5);
        return Download.Task;
    }

    public void StartApplier(StagedUpdate staged, string? project)
    {
        if (StartError is not null)
            throw StartError;
        Started.Add((staged, project));
    }

    public void Reveal(StagedUpdate staged) => Revealed.Add(staged);

    public Task<UpdateResult?> CleanUpAsync() => Task.FromResult(LastResult);

    public static UpdateCheckResult Update(string version = "1.1.0", string notes = "Notes")
    {
        var json = ReleaseFixtures.Latest("v" + version).Replace($"Notes for v{version}", notes, StringComparison.Ordinal);
        var release = ReleaseFeed.Parse(ReleaseFixtures.Bytes(json))!;
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, release.Assets[0]);
    }

    public static StagedUpdate Staged() =>
        new(new ReleaseVersion(1, 1, 0), "/u/1.1.0", "/u/1.1.0/app/Mainframe Engine.app", "/u/1.1.0/app/Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor");
}

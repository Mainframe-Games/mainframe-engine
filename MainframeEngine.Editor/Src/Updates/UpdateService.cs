using System.ComponentModel;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>
/// Everything the editor needs to update itself (docs/design/editor-updates.md). The editor executable passes
/// <see cref="GitHubUpdateService"/> through <see cref="EditorWorkspaceOptions.Updates"/>; tests and QA pass fakes.
/// </summary>
public interface IUpdateService
{
    /// <summary>The running editor's version (<see cref="EngineInfo.Version"/>).</summary>
    string CurrentVersion { get; }

    /// <summary>Where the editor is installed and whether it can be replaced in place.</summary>
    InstallLocation Install { get; }

    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    /// <summary>Downloads, verifies and stages <paramref name="update"/> (an <see cref="UpdateCheckResult.IsUpdate"/> result).</summary>
    Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Starts the staged editor with <c>--apply-update</c> for this process; throws <see cref="UpdateException"/>.</summary>
    void StartApplier(StagedUpdate staged, string? project);

    /// <summary>Shows the staged install in the file manager (installs that cannot be replaced).</summary>
    void Reveal(StagedUpdate staged);

    /// <summary>Start-up: the last update's result (null: none), after deleting the backup and stale staging folders.</summary>
    Task<UpdateResult?> CleanUpAsync();
}

/// <summary>Updates from GitHub Releases into <c>~/.mainframe/updates</c>.</summary>
public sealed class GitHubUpdateService : IUpdateService
{
    // One client for the process (like NetworkUtils); the feed applies its own 10 s timeout, downloads have none.
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly string _updatesDirectory;
    private readonly string? _rid = UpdatePlatform.CurrentRid;
    private readonly Lazy<InstallLocation> _install;

    public GitHubUpdateService(string? updatesDirectory = null)
    {
        _updatesDirectory = updatesDirectory ?? UpdatePaths.DefaultDirectory;
        _install = new Lazy<InstallLocation>(() => _rid is null
            ? new InstallLocation(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), InstallKind.NotBundled)
            : InstallLocation.Inspect(AppContext.BaseDirectory, _rid));
    }

    public string CurrentVersion => EngineInfo.Version;

    public InstallLocation Install => _install.Value;

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) =>
        new UpdateChecker(new ReleaseFeed(Http.Value, CurrentVersion), CurrentVersion, _rid).CheckAsync(ct);

    public Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.IsUpdate || _rid is null)
            throw new InvalidOperationException("Only an available update can be downloaded.");
        return new UpdateDownloader(Http.Value, CurrentVersion).DownloadAsync(update.Release!, update.Asset!, _rid, _updatesDirectory, progress, ct);
    }

    public ProcessStartInfo ApplierStartInfo(StagedUpdate staged, string? project)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var request = new ApplyUpdateRequest(Path.GetFullPath(Install.Root), Environment.ProcessId, CurrentVersion, project);
        var info = new ProcessStartInfo(staged.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(staged.Executable) ?? staged.Root,
        };
        foreach (var argument in request.ToArguments())
            info.ArgumentList.Add(argument);
        return info;
    }

    public void StartApplier(StagedUpdate staged, string? project)
    {
        try
        {
            using var process = Process.Start(ApplierStartInfo(staged, project))
                                ?? throw new UpdateException("The new version could not be started.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new UpdateException($"The new version could not be started ({e.Message}).", e);
        }
    }

    public void Reveal(StagedUpdate staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        OsShell.Reveal(staged.Root);
    }

    public Task<UpdateResult?> CleanUpAsync()
    {
        if (!ReleaseVersion.TryParse(CurrentVersion, out var current))
            return Task.FromResult<UpdateResult?>(null); // development builds never touch ~/.mainframe/updates
        return Task.Run(() => UpdateCleanup.Run(Install.Kind == InstallKind.NotBundled ? null : Install.Root, _updatesDirectory, current));
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The hand-off from the running editor to the staged one:
/// <c>--apply-update &lt;install-root&gt; --wait-pid &lt;pid&gt; --from &lt;version&gt; [--project &lt;folder&gt;]</c>.
/// </summary>
public sealed record ApplyUpdateRequest(string InstallRoot, int WaitPid, string From, string? Project)
{
    public const string Flag = "--apply-update";

    public static bool IsApplyUpdate(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static ApplyUpdateRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsApplyUpdate(args) || args.Count < 2)
            throw new ArgumentException($"{Flag} needs the install folder.");
        string? from = null, project = null;
        int? pid = null;
        for (var i = 2; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--wait-pid":
                    pid = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        ? value
                        : throw new ArgumentException("--wait-pid needs a process id.");
                    break;
                case "--from":
                    from = Next();
                    break;
                case "--project":
                    project = Next();
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return new ApplyUpdateRequest(Path.GetFullPath(args[1]), pid ?? throw new ArgumentException("--wait-pid is required."),
            from ?? throw new ArgumentException("--from is required."), project);
    }

    public IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string> { Flag, InstallRoot, "--wait-pid", WaitPid.ToString(CultureInfo.InvariantCulture), "--from", From };
        if (Project is not null)
        {
            arguments.Add("--project");
            arguments.Add(Project);
        }

        return arguments;
    }
}

/// <summary>What the applier did (<c>~/.mainframe/updates/result.json</c>), reported by the next editor start.</summary>
public sealed record UpdateResult(string From, string To, bool Ok, string? Error)
{
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, UpdateJson.Default.UpdateResult));
    }

    /// <summary>The recorded result, or null when there is none or it cannot be read.</summary>
    public static UpdateResult? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateJson.Default.UpdateResult) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateResult))]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>
/// Installs the staged editor it runs from (docs/design/editor-updates.md, Applying): waits for the old editor, renames
/// the install to <c>&lt;root&gt;.old</c>, copies <see cref="StagedRoot"/> in, moves the user's own top-level entries
/// back in, records the result, relaunches. Any failure after the rename restores the old install and relaunches it (or
/// the backup, when it cannot be moved back). Runs before any window or engine exists.
/// </summary>
public sealed class UpdateApplier
{
    public required string StagedRoot { get; init; }
    public required string Rid { get; init; }
    public required string ToVersion { get; init; }
    public required string UpdatesDirectory { get; init; }
    public Func<int, TimeSpan, bool> WaitForExit { get; init; } = DefaultWaitForExit;
    public Action<ProcessStartInfo> Start { get; init; } = DefaultStart;
    public Action<string, string> CopyDirectory { get; init; } = CopyDirectoryRecursive;

    /// <summary>Renames a folder (the backup and roll-back moves); tests inject failures.</summary>
    public Action<string, string> MoveDirectory { get; init; } = Directory.Move;
    public Action<string> Log { get; init; } = static _ => { };
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Windows: antivirus and indexers hold files briefly after the old editor exits.</summary>
    public int RenameAttempts { get; init; } = OperatingSystem.IsWindows() ? 20 : 1;

    private string ResultPath => UpdatePaths.ResultFile(UpdatesDirectory);

    /// <summary>Applies the update; 0 when the new version was installed and started.</summary>
    public int Apply(ApplyUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.TrimEndingDirectorySeparator(request.InstallRoot);
        var backup = UpdatePaths.BackupOf(root);
        var launch = LaunchInfo(root, Rid, request.Project);
        Log($"Updating {root} from v{request.From} to v{ToVersion}.");

        if (!WaitForExit(request.WaitPid, ExitTimeout))
            return Fail(request, $"The old editor did not exit within {ExitTimeout.TotalSeconds:0} seconds; nothing was changed.", launch);
        try
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
            RenameWithRetry(root, backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(request, $"The editor's folder could not be moved aside ({e.Message}); nothing was changed.", launch);
        }

        var kept = new List<string>(); // the user's entries moved from the backup into the new install
        try
        {
            CopyDirectory(StagedRoot, root);
            KeepUserEntries(backup, root, kept);
            new UpdateResult(request.From, ToVersion, true, null).Save(ResultPath);
            Log("Installed; starting the new version.");
            Start(launch);
            return 0; // nothing may run after Start: a throw here would roll back under the already-running new editor
        }
        catch (Exception e) // Anything after the rename must roll back, whatever its type: a half-installed editor cannot start.
        {
            Log($"Installing failed ({e.Message}); restoring the previous version.");
            var restored = Restore(root, backup, kept, out var stranded);
            return restored
                ? Fail(request, stranded
                    ? $"The update could not be installed ({e.Message}); the previous version was restored, but some of your files could not be moved back and are in {UpdatePaths.FailedOf(root)}."
                    : $"The update could not be installed ({e.Message}); the previous version was restored.", launch)
                // The partial copy may still start: relaunch the intact backup instead (on macOS a folder named .app.old
                // is no bundle, so its executable is started directly).
                : Fail(request, $"The update could not be installed ({e.Message}) and the previous version could not be moved back; it is in {backup}.",
                    BackupLaunchInfo(backup, Rid, request.Project));
        }
    }

    /// <summary>
    /// Moves every top-level entry of <paramref name="backup"/> the new release does not have (files and projects the user
    /// keeps in the editor's folder) into <paramref name="root"/>, recording each in <paramref name="kept"/>. Throws on
    /// failure: the caller rolls back.
    /// </summary>
    private void KeepUserEntries(string backup, string root, List<string> kept)
    {
        foreach (var entry in Directory.GetFileSystemEntries(backup))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(root, name);
            if (Path.Exists(target))
                continue; // the release's version wins
            MoveEntry(entry, target);
            kept.Add(name);
            Log($"Kept {name} (not part of the release) in the new version's folder.");
        }
    }

    private void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from))
            MoveDirectory(from, to);
        else
            File.Move(from, to);
    }

    /// <summary>
    /// Puts <paramref name="backup"/> back at <paramref name="root"/>: the partial copy is renamed aside to
    /// <c>&lt;root&gt;.failed</c> first (a rename succeeds where deleting freshly written files may not), the user's
    /// <paramref name="kept"/> entries move from it into the restored root, then it is deleted.
    /// </summary>
    private bool Restore(string root, string backup, List<string> kept, out bool stranded)
    {
        var failed = UpdatePaths.FailedOf(root);
        stranded = false;
        try
        {
            if (Directory.Exists(root))
            {
                TryDeleteDirectory(failed);
                RenameWithRetry(root, failed);
            }

            RenameWithRetry(backup, root);
        }
        catch (Exception e) // never let the rollback itself crash the applier: the caller still relaunches
        {
            Log($"Could not restore {root} from {backup}: {e.Message}");
            return false;
        }

        foreach (var name in kept)
        {
            try
            {
                MoveEntry(Path.Combine(failed, name), Path.Combine(root, name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stranded = true;
                Log($"Could not move {name} back into {root} ({e.Message}); it stays in {failed}.");
            }
        }

        if (!stranded)
            TryDeleteDirectory(failed);
        return true;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not delete {path} ({e.Message}); a later editor start deletes it.");
        }
    }

    private int Fail(ApplyUpdateRequest request, string error, ProcessStartInfo launch)
    {
        Log(error);
        try
        {
            new UpdateResult(request.From, ToVersion, false, error).Save(ResultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not record the result: {e.Message}");
        }

        try
        {
            Start(launch);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Log($"Could not start the editor: {e.Message}");
        }

        return 1;
    }

    private void RenameWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                MoveDirectory(from, to);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < RenameAttempts)
            {
                Log($"Moving {from} to {to} failed ({e.Message}); retrying.");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>How to start the editor installed at <paramref name="root"/> (macOS through <c>open</c>, so it starts as the app).</summary>
    public static ProcessStartInfo LaunchInfo(string root, string rid, string? project)
    {
        ProcessStartInfo info;
        if (UpdatePlatform.IsMac(rid))
        {
            info = new ProcessStartInfo("open") { ArgumentList = { "-n", root } };
            if (project is not null)
                info.ArgumentList.Add("--args");
        }
        else
        {
            info = new ProcessStartInfo(UpdatePlatform.ExecutablePath(root, rid)) { WorkingDirectory = root };
        }

        if (project is not null)
        {
            info.ArgumentList.Add("--project");
            info.ArgumentList.Add(project);
        }

        info.UseShellExecute = false;
        return info;
    }

    /// <summary>How to start the editor left in <paramref name="backup"/> when it could not be moved back.</summary>
    public static ProcessStartInfo BackupLaunchInfo(string backup, string rid, string? project)
    {
        if (!UpdatePlatform.IsMac(rid))
            return LaunchInfo(backup, rid, project);
        var executable = UpdatePlatform.ExecutablePath(backup, rid);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false };
        if (project is not null)
        {
            info.ArgumentList.Add("--project");
            info.ArgumentList.Add(project);
        }

        return info;
    }

    /// <summary>True when process <paramref name="pid"/> exited (or never existed) within <paramref name="timeout"/>.</summary>
    public static bool DefaultWaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private static void DefaultStart(ProcessStartInfo info)
    {
        using var _ = Process.Start(info);
    }

    public static void CopyDirectoryRecursive(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
    }

    /// <summary><c>Program.cs</c>: the staged editor's whole run. Logs to <c>~/.mainframe/updates/update.log</c>.</summary>
    public static int RunFromCommandLine(IReadOnlyList<string> args)
    {
        var updates = UpdatePaths.DefaultDirectory;
        var logPath = UpdatePaths.LogFile(updates);
        void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(updates);
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(line);
            }
        }

        ApplyUpdateRequest request;
        try
        {
            request = ApplyUpdateRequest.Parse(args);
        }
        catch (ArgumentException e)
        {
            Write($"Bad arguments: {e.Message}");
            return 2;
        }

        var rid = UpdatePlatform.CurrentRid;
        var staged = rid is null ? null : InstallLocation.FindRoot(AppContext.BaseDirectory, rid);
        if (rid is null || staged is null)
        {
            Write($"{AppContext.BaseDirectory} is not a released editor layout; nothing was changed.");
            return 2;
        }

        return new UpdateApplier { StagedRoot = staged, Rid = rid, ToVersion = EngineInfo.Version, UpdatesDirectory = updates, Log = Write }.Apply(request);
    }
}

/// <summary>Start-up after an update: report the result, delete the backup and stale staging folders. Never throws.</summary>
public static class UpdateCleanup
{
    /// <summary>
    /// Reads and deletes <c>result.json</c>; deletes <c>&lt;installRoot&gt;.old</c> and a leftover
    /// <c>&lt;installRoot&gt;.failed</c> unless the update failed (then the backup may be the only working install and the
    /// partial copy may hold the user's files); deletes every staging folder except those of versions newer than
    /// <paramref name="current"/> (another open editor may be about to install one).
    /// </summary>
    public static UpdateResult? Run(string? installRoot, string updatesDirectory, ReleaseVersion current)
    {
        var resultPath = UpdatePaths.ResultFile(updatesDirectory);
        var result = UpdateResult.Load(resultPath);
        TryDelete(resultPath, static p => File.Delete(p));
        if (installRoot is not null)
        {
            foreach (var leftover in (string[])[UpdatePaths.BackupOf(installRoot), UpdatePaths.FailedOf(installRoot)])
            {
                if (!Directory.Exists(leftover))
                    continue;
                if (result is { Ok: false })
                    Log.Info($"[Editor] Keeping {leftover} after the failed update.");
                else
                    TryDelete(leftover, static p => Directory.Delete(p, recursive: true));
            }
        }

        foreach (var folder in StagingFolders(updatesDirectory))
            if (!ReleaseVersion.TryParse(Path.GetFileName(folder), out var version) || version <= current)
                TryDelete(folder, static p => Directory.Delete(p, recursive: true));
        return result;
    }

    private static List<string> StagingFolders(string updatesDirectory)
    {
        try
        {
            return Directory.Exists(updatesDirectory) ? [.. Directory.EnumerateDirectories(updatesDirectory)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"[Editor] Could not list {updatesDirectory} ({e.Message}); stale downloads are retried at the next start.");
            return [];
        }
    }

    private static void TryDelete(string path, Action<string> delete)
    {
        try
        {
            delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"[Editor] Could not delete {path} ({e.Message}); it is retried at the next start.");
        }
    }
}

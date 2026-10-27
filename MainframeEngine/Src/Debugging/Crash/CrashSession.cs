using System.Globalization;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>The sentinel a running process keeps in its crash folder (<c>session-&lt;pid&gt;.json</c>).</summary>
internal sealed class CrashSessionFile
{
    public int Format { get; set; } = 1;
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
    public CrashAppInfo App { get; set; } = new();
    public CrashOsInfo Os { get; set; } = new();
    public CrashGpuInfo? Gpu { get; set; }
    public List<string> Args { get; set; } = [];

    /// <summary>The log file this run writes (its tail goes into an unclean exit's report).</summary>
    public string? LogFile { get; set; }

    /// <summary>Set once this run wrote a fatal report: its end is explained, no unclean exit follows.</summary>
    public string? ReportId { get; set; }
}

/// <summary>A bundle's <c>handled.json</c>: the player saw the report (<see cref="CrashReporter.MarkHandled"/>).</summary>
internal sealed class CrashHandledFile
{
    public string Disposition { get; set; } = "";
    public DateTime HandledUtc { get; set; }
}

/// <summary>
/// The unclean-exit sentinel (docs/design/crash-reporting.md#the-unclean-exit-sentinel): at start the process writes
/// <c>session-&lt;pid&gt;.json</c> and holds <c>session-&lt;pid&gt;.lock</c> open with no sharing; a clean shutdown
/// deletes both. On the next start a sentinel whose lock can be taken belongs to a process that is gone; unless it wrote
/// a report, it becomes an <see cref="CrashKind.UncleanExit"/> report with the tail of its log. It notices native faults,
/// stack overflows, <c>FailFast</c>, kills and power loss on every platform, at no cost, but has no stack. Sentinels of
/// instances still running (their lock is held) are left alone. No process APIs: the lock is the liveness test.
/// </summary>
internal sealed class CrashSession : IDisposable
{
    /// <summary>Bytes of the previous log kept in an unclean exit's bundle.</summary>
    public const int LogTailBytes = 256 * 1024;

    private const string Prefix = "session-";

    private readonly FileStream? _lock;
    private readonly CrashSessionFile _file;
    private bool _disposed;

    private CrashSession(string directory, CrashSessionFile file, FileStream? lockStream)
    {
        Directory = directory;
        _file = file;
        _lock = lockStream;
        Path = SessionPath(directory, file.ProcessId);
    }

    public string Directory { get; }

    /// <summary>The sentinel file.</summary>
    public string Path { get; }

    /// <summary>Writes the sentinel and takes its lock; null when the folder is not writable (no sentinel: nothing is noticed).</summary>
    public static CrashSession? Begin(string directory, CrashSessionFile file)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var lockPath = LockPath(directory, file.ProcessId);
            var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var session = new CrashSession(directory, file, lockStream);
            session.Write();
            return session;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Crash] No session sentinel in '{directory}': {e.Message}");
            return null;
        }
    }

    /// <summary>Records the GPU once the renderer knows it.</summary>
    public void SetGpu(CrashGpuInfo gpu)
    {
        _file.Gpu = gpu;
        Write();
    }

    /// <summary>Records that a fatal report explains this run's end.</summary>
    public void SetReport(string id)
    {
        _file.ReportId = id;
        Write();
    }

    /// <summary>A clean exit: deletes the sentinel and releases (deletes) its lock.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        _lock?.Dispose();
    }

    private void Write()
    {
        if (_disposed)
            return;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_file, CrashJsonContext.Default.CrashSessionFile);
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, json);
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A missing sentinel only loses the next start's unclean-exit report.
        }
    }

    internal static string SessionPath(string directory, int pid) =>
        System.IO.Path.Combine(directory, Prefix + pid.ToString(CultureInfo.InvariantCulture) + ".json");

    internal static string LockPath(string directory, int pid) =>
        System.IO.Path.Combine(directory, Prefix + pid.ToString(CultureInfo.InvariantCulture) + ".lock");

    /// <summary>A sentinel of a process that is gone (its lock was free), read and removed by <see cref="CollectDead"/>.</summary>
    internal readonly record struct DeadSession(CrashSessionFile File, string Path);

    /// <summary>
    /// The sentinels in <paramref name="directory"/> whose process is gone (other than <paramref name="ownPid"/>); each is
    /// deleted with its lock. Unreadable sentinels are deleted too.
    /// </summary>
    public static List<DeadSession> CollectDead(string directory, int ownPid)
    {
        var dead = new List<DeadSession>();
        string[] files;
        try
        {
            files = System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFiles(directory, Prefix + "*.json") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return dead;
        }

        foreach (var path in files)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == ownPid)
                continue;
            if (IsAlive(directory, pid))
                continue;
            CrashSessionFile? file = null;
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllBytes(path), CrashJsonContext.Default.CrashSessionFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            TryDelete(path);
            TryDelete(LockPath(directory, pid));
            if (file is not null)
                dead.Add(new DeadSession(file, path));
        }

        return dead;
    }

    /// <summary>Whether the process of <paramref name="pid"/>'s sentinel still holds its lock.</summary>
    internal static bool IsAlive(string directory, int pid)
    {
        var lockPath = LockPath(directory, pid);
        if (!File.Exists(lockPath))
            return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
            return false; // nobody holds it: the process is gone
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The last <paramref name="maxBytes"/> of <paramref name="path"/>, cut at a line start; null when it cannot be read.</summary>
    internal static byte[]? ReadTail(string path, int maxBytes = LogTailBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - maxBytes);
            stream.Position = start;
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);
            if (start > 0)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n');
                if (newline >= 0 && newline < buffer.Length - 1)
                    return buffer.AsSpan(newline + 1).ToArray();
            }

            return buffer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

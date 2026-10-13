namespace MainframeEngine.Video.Media;

/// <summary>
/// A per-process scratch directory under the OS temp path. Filter scripts, concat lists and
/// fast-export pieces go here and the whole thing is deleted on dispose (normally at app exit).
/// </summary>
public sealed class TempSession : IDisposable
{
    private bool _disposed;

    public TempSession()
    {
        Directory = Path.Combine(Path.GetTempPath(), "MainframeVideo", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    /// <summary>A unique path inside the session directory with the given extension (including the dot).</summary>
    public string NewFile(string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Path.Combine(Directory, Guid.NewGuid().ToString("N") + extension);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Another process may still hold a handle (an antivirus scan, say). The OS will reap it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

namespace MainframeEngine.Editor;

/// <summary>
/// Crash-safe file writes: the bytes go to a temporary file next to the target, are flushed to disk, then the temp
/// file replaces the target in one rename. A crash or a full disk leaves either the old file or the new one, never a
/// truncated mix. (Scenes go through <see cref="SceneSaver"/>, which does the same.)
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = $"{full}.{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
            // Best effort: a stray temp file is harmless.
        }
    }
}

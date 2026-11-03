namespace MainframeEngine;

/// <summary>
/// Read-only content mounted over a directory with <see cref="ContentFiles.Mount"/>: an export's pack, an in-memory
/// source in tests, a platform's asset store (docs/design/asset-pipeline.md#content-file-system).
/// </summary>
/// <remarks>
/// Every path a source receives is relative to <see cref="Root"/>, <c>'/'</c>-separated, without a leading separator,
/// and empty for the root itself; <see cref="ContentFiles"/> normalises them. Sources are read from any thread (the
/// loader worker, the audio streamer), so they must not change once mounted.
/// </remarks>
public interface IContentSource
{
    /// <summary>The absolute directory this source serves paths under (no trailing separator).</summary>
    string Root { get; }

    /// <summary>Names the source in errors (a pack's file name).</summary>
    string Name { get; }

    /// <summary>Whether a file exists. Must not allocate: the renderer asks while drawing.</summary>
    bool FileExists(ReadOnlySpan<char> path);

    /// <summary>Whether a directory exists (it holds at least one file, at any depth).</summary>
    bool DirectoryExists(ReadOnlySpan<char> path);

    /// <summary>A file's length and last write time, or <c>default</c> (not <see cref="ContentFileInfo.Exists"/>).</summary>
    ContentFileInfo GetInfo(ReadOnlySpan<char> path);

    /// <summary>A seekable read stream over a file, or null when the source has no such file.</summary>
    Stream? OpenRead(string path);

    /// <summary>The files in <paramref name="directory"/> (relative to <see cref="Root"/>, like the input).</summary>
    IEnumerable<string> EnumerateFiles(string directory, bool recursive);

    /// <summary>The directories in <paramref name="directory"/> (relative to <see cref="Root"/>, like the input).</summary>
    IEnumerable<string> EnumerateDirectories(string directory, bool recursive);
}

/// <summary>What <see cref="ContentFiles.GetInfo"/> knows about a file.</summary>
/// <param name="Exists">Whether the file exists; when false the other members are default.</param>
/// <param name="Length">Its length in bytes (the uncompressed length for a packed file).</param>
/// <param name="LastWriteUtc">Its last write time; a mounted source reports a fixed time (its content never changes).</param>
public readonly record struct ContentFileInfo(bool Exists, long Length, DateTime LastWriteUtc);

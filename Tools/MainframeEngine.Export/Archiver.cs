using System.Formats.Tar;
using System.IO.Compression;

namespace MainframeEngine.Export;

/// <summary>
/// Writes export archives in C#: zip (<see cref="ZipArchive"/>) and <c>.tar.gz</c> (<see cref="TarWriter"/> +
/// <see cref="GZipStream"/>) with explicit Unix modes, so the executable bit survives an export from Windows. Archives are
/// written as <c>&lt;name&gt;.partial</c> and moved over the old one at the end, so a cancelled or failed export leaves the
/// previous artifact intact.
/// </summary>
public static class Archiver
{
    /// <summary>The suffix of an archive (or folder) being written.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>The mode of executables and folders in archives.</summary>
    public const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                               UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                               UnixFileMode.OtherExecute;

    /// <summary>The mode of other files in archives.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// The archive mode of <paramref name="file"/>: <see cref="ExecutableMode"/> when it is in <paramref name="executables"/>
    /// (relative paths, '/') or has an execute bit on this host, else <see cref="FileMode"/>.
    /// </summary>
    public static UnixFileMode ModeOf(string file, string relative, IReadOnlySet<string> executables)
    {
        if (executables.Contains(relative))
            return ExecutableMode;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if ((File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return ExecutableMode;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return FileMode;
    }

    /// <summary>
    /// Zips <paramref name="sourceDirectory"/> as the top folder <paramref name="rootName"/> into <paramref name="archive"/>
    /// (through a <c>.partial</c> file). <paramref name="progress"/> gets 0–1.
    /// </summary>
    public static void Zip(string sourceDirectory, string rootName, string archive, IReadOnlySet<string> executables,
        Action<float>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        ArgumentNullException.ThrowIfNull(executables);
        var partial = archive + PartialSuffix;
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        try
        {
            using (var stream = File.Create(partial))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (var i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(sourceDirectory, files[i]).Replace('\\', '/');
                    var entry = zip.CreateEntryFromFile(files[i], $"{rootName}/{relative}", CompressionLevel.Optimal);
                    entry.ExternalAttributes = (int)((uint)(0x8000 | (int)ModeOf(files[i], relative, executables)) << 16); // S_IFREG | mode
                    progress?.Invoke((i + 1) / (float)files.Length);
                }
            }

            File.Move(partial, archive, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Writes <paramref name="sourceDirectory"/> as the top folder <paramref name="rootName"/> into a <c>.tar.gz</c>.</summary>
    public static void TarGz(string sourceDirectory, string rootName, string archive, IReadOnlySet<string> executables,
        Action<float>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        ArgumentNullException.ThrowIfNull(executables);
        var partial = archive + PartialSuffix;
        var directories = Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        try
        {
            using (var stream = File.Create(partial))
            using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rootName + "/") { Mode = ExecutableMode, ModificationTime = DateTimeOffset.UtcNow });
                foreach (var directory in directories)
                {
                    var relative = Path.GetRelativePath(sourceDirectory, directory).Replace('\\', '/');
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{rootName}/{relative}/")
                    {
                        Mode = ExecutableMode,
                        ModificationTime = Directory.GetLastWriteTimeUtc(directory),
                    });
                }

                for (var i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(sourceDirectory, files[i]).Replace('\\', '/');
                    using var data = File.OpenRead(files[i]);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{rootName}/{relative}")
                    {
                        Mode = ModeOf(files[i], relative, executables),
                        ModificationTime = File.GetLastWriteTimeUtc(files[i]),
                        DataStream = data,
                    });
                    progress?.Invoke((i + 1) / (float)files.Length);
                }
            }

            File.Move(partial, archive, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Moves <paramref name="sourceDirectory"/> to <paramref name="destination"/> (through a <c>.partial</c> folder, then
    /// over the old one): <c>archive: false</c>.
    /// </summary>
    public static void MoveFolder(string sourceDirectory, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var partial = destination + PartialSuffix;
        if (Directory.Exists(partial))
            Directory.Delete(partial, recursive: true);
        Directory.Move(sourceDirectory, partial);
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.Move(partial, destination);
    }

    /// <summary>Deletes a file or folder if it exists; never throws.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Collects the managed symbols of a publish folder (G15.8, docs/design/crash-reporting.md#symbols), as
/// <c>build/collect-symbols.sh</c> does for the editor's release: every portable PDB with the assembly it belongs to,
/// keeping the folder layout (<c>mf-symbolicate</c> matches frames to an assembly by its MVID, then reads its PDB).
/// </summary>
public static class SymbolCollector
{
    /// <summary>
    /// Copies each <c>x.pdb</c> that has an <c>x.dll</c> or <c>x.exe</c> beside it, with that assembly, from
    /// <paramref name="publishDirectory"/> into <paramref name="outputDirectory"/>; <paramref name="move"/> also deletes the
    /// PDBs from the publish ("symbols": "separate"). Returns how many were collected.
    /// </summary>
    public static int Collect(string publishDirectory, string outputDirectory, bool move)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var count = 0;
        foreach (var pdb in Directory.EnumerateFiles(publishDirectory, "*.pdb", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray())
        {
            var stem = pdb[..^4];
            var assembly = File.Exists(stem + ".dll") ? stem + ".dll" : File.Exists(stem + ".exe") ? stem + ".exe" : null;
            if (assembly is null)
                continue;
            var relative = Path.GetRelativePath(publishDirectory, pdb);
            var target = Path.Combine(outputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(pdb, target, overwrite: true);
            File.Copy(assembly, Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName(assembly)), overwrite: true);
            if (move)
                File.Delete(pdb);
            count++;
        }

        return count;
    }
}

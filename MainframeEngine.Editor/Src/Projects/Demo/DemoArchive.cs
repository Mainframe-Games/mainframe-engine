using System.IO.Compression;

namespace MainframeEngine.Editor;

/// <summary>Extracts a downloaded Demo zip into a work folder (no escapes, no links, size-capped) and validates it.</summary>
public static class DemoArchive
{
    public const long MaxUncompressedBytes = 1L << 30;

    public static string ExtractAndValidate(string zipPath, string workDirectory, long maxBytes = MaxUncompressedBytes)
    {
        var work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory)) + Path.DirectorySeparatorChar;
        var tops = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || Path.IsPathRooted(name))
                    throw new DemoDownloadException($"The demo archive has an absolute path ({entry.FullName}).");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new DemoDownloadException($"The demo archive contains a link ({entry.FullName}).");
                var target = Path.GetFullPath(Path.Combine(work, name));
                if (!target.StartsWith(work, StringComparison.Ordinal))
                    throw new DemoDownloadException($"The demo archive has a path outside its folder ({entry.FullName}).");
                if (entry.Length > maxBytes - total)
                    throw new DemoDownloadException("The demo archive is larger than expected.");
                total += entry.Length;
                tops.Add(Path.GetRelativePath(work, target).Split(Path.DirectorySeparatorChar, 2)[0]); // from the resolved path, so "Demo/../Other/x" counts as Other
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DemoDownloadException("The demo archive could not be unpacked: " + e.Message, e);
        }

        if (tops.Count != 1)
            throw new DemoDownloadException("The demo archive must contain exactly one project folder.");
        var root = Path.Combine(work, tops.Single());
        Validate(root);
        return root;
    }

    public static void Validate(string projectRoot)
    {
        var projectFile = GameProjectLayout.ProjectFileOf(projectRoot);
        if (!File.Exists(projectFile))
            throw new DemoDownloadException($"The demo has no {ProjectSettings.FileName}.");
        try
        {
            ProjectSettings.Load(projectFile);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            throw new DemoDownloadException($"The demo's {ProjectSettings.FileName} could not be read: {e.Message}", e);
        }

        if (GameProjectLayout.LauncherProjectOf(projectRoot) is null)
            throw new DemoDownloadException("The demo has no *.Launcher project.");
        if (!Directory.Exists(Path.Combine(projectRoot, "Content", "Scenes")))
            throw new DemoDownloadException("The demo has no Content/Scenes folder.");
        if (!File.Exists(Path.Combine(projectRoot, "Directory.Build.props")))
            throw new DemoDownloadException("The demo has no Directory.Build.props.");
    }
}

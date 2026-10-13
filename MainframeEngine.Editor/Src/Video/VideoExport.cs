using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Editor.Video;

/// <summary>Runs one export: what <see cref="Exporter.ExportAsync"/> does, behind a seam so tests never start ffmpeg.</summary>
public delegate Task VideoExportRunner(ExportRequest request, IProgress<ExportProgress> progress, CancellationToken cancellationToken);

/// <summary>
/// The pure parts of Export Video: where the file goes by default, which paths are refused, and the immutable request a
/// job runs on. The request is built on the main thread, before the job starts: <see cref="VideoDocument"/> is main-thread
/// state, so the job never reads it and edits made while an export runs cannot change that export.
/// </summary>
public static class VideoExport
{
    /// <summary>The extension a path without one gets (the picker and a typed name both may leave it off).</summary>
    public const string DefaultExtension = ".mp4";

    /// <summary>What Export says when every segment is cut.</summary>
    public const string NothingToExport = "Every segment is removed; there is nothing to export.";

    /// <summary>
    /// <c>&lt;name&gt;-cut.mp4</c> beside the recording, then <c>-cut-2</c>, <c>-cut-3</c> … until
    /// <paramref name="exists"/> says the name is free, so the default never overwrites anything.
    /// </summary>
    public static string DefaultPath(string sourcePath, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(exists);
        var directory = Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var path = Path.Combine(directory, name + "-cut" + DefaultExtension);
        for (var n = 2; exists(path); n++)
            path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{name}-cut-{n}{DefaultExtension}"));
        return path;
    }

    /// <summary>
    /// The path a typed or picked name means: trimmed, resolved against the recording's folder when relative, and
    /// <c>.mp4</c> added when it has no extension.
    /// </summary>
    public static string ResolveOutput(string typed, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var trimmed = (typed ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        var full = Path.GetFullPath(trimmed, Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory());
        if (Path.GetExtension(full).Length > 0)
            return full;
        // "mycut." names no extension; the dot goes so it does not become "mycut..mp4".
        return full.TrimEnd('.') + DefaultExtension;
    }

    /// <summary>
    /// Why <paramref name="outputPath"/> cannot be exported to, or null when it can: empty, a folder, an extension ffmpeg
    /// cannot write, or the recording itself (the source is opened read-only and never overwritten).
    /// </summary>
    public static string? Problem(string sourcePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (string.IsNullOrWhiteSpace(outputPath))
            return "Choose where to save the exported video.";
        if (VideoPaths.SameFile(outputPath, sourcePath))
            return "That is the recording itself. Choose a different file name; the original is never overwritten.";
        if (Directory.Exists(outputPath))
            return $"{outputPath} is a folder. Choose a file name.";
        var extension = Path.GetExtension(outputPath);
        if (!Exporter.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return $"\"{extension}\" is not a supported output format. Use {string.Join(", ", Exporter.SupportedExtensions)}.";
        return null;
    }

    /// <summary>Fast export copies the streams unchanged, which WebM cannot hold, so it is not offered for <c>.webm</c>.</summary>
    public static bool FastAllowed(string outputPath) =>
        !string.Equals(Path.GetExtension(outputPath ?? ""), ".webm", StringComparison.OrdinalIgnoreCase);

    /// <summary>The seconds the enabled segments add up to.</summary>
    public static double KeptSeconds(VideoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.Segments;
        var kept = 0.0;
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Enabled)
                kept += segments[i].Duration;
        return kept;
    }

    /// <summary>"Kept 4:12.3 of 6:00.0 · 23 cuts" for the dialog.</summary>
    public static string Summary(VideoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.Segments;
        var cuts = 0;
        for (var i = 0; i < segments.Count; i++)
            if (!segments[i].Enabled)
                cuts++;
        return $"Kept {VideoFormat.Clock(KeptSeconds(document))} of {VideoFormat.Clock(document.Duration)} · {cuts} {(cuts == 1 ? "cut" : "cuts")}";
    }

    /// <summary>
    /// The immutable request for exporting <paramref name="document"/> as it is now: a private copy of the partition, the
    /// source's size and frame rate for the encoder's bitrate. Main thread only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The output is refused (<see cref="Problem"/>) or nothing is kept.</exception>
    public static ExportRequest Request(VideoDocument document, string outputPath, ExportMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Problem(document.SourcePath, outputPath) is { } problem)
            throw new InvalidOperationException(problem);
        if (mode == ExportMode.Fast && !FastAllowed(outputPath))
            throw new InvalidOperationException("Fast export copies the source streams, which WebM cannot hold. Choose MP4, MOV or MKV, or use Precise.");
        if (KeptSeconds(document) <= 0)
            throw new InvalidOperationException(NothingToExport);
        var project = document.Project;
        var source = project.Source;
        return new ExportRequest(source.Path, [.. project.Segments], source.DurationSeconds, outputPath, mode,
            source.Width, source.Height, source.FrameRate);
    }
}

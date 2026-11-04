using System.Globalization;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using ExportProgress = MainframeEngine.Video.Media.Export.ExportProgress;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>Runs one timeline export (<see cref="TimelineExporter.ExportAsync"/>), behind a seam so tests never start ffmpeg.</summary>
public delegate Task TimelineExportRunner(Timeline timeline, TimelineExportPreset preset, double start, double end, string outputPath,
    Func<MediaRef, string> resolvePath, IProgress<ExportProgress> progress, CancellationToken cancellationToken);

/// <summary>
/// The pure parts of File › Export Video (docs/design/future/video-nle.md, Export): the default output, which outputs are
/// refused, and the range. The job gets the timeline snapshot taken when Export was pressed (an immutable record), so edits
/// made while it runs do not touch it.
/// </summary>
public static class TimelineExport
{
    /// <summary>What Export says when the range holds nothing.</summary>
    public const string NothingToExport = "The timeline is empty; there is nothing to export.";

    /// <summary>
    /// <c>&lt;project&gt;/Exports/&lt;timeline&gt;-&lt;preset&gt;.&lt;ext&gt;</c>, then <c>-2</c>, <c>-3</c> … until
    /// <paramref name="exists"/> says it is free: outside <c>Assets/</c>, so exports never mix with media.
    /// </summary>
    public static string DefaultPath(string projectRoot, string timelinePath, TimelineExportPreset preset, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(exists);
        var folder = Path.Combine(projectRoot, "Exports");
        var stem = $"{Path.GetFileNameWithoutExtension(timelinePath)}-{preset.Id}";
        var path = Path.Combine(folder, stem + preset.Extension);
        for (var n = 2; exists(path); n++)
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{preset.Extension}"));
        return path;
    }

    /// <summary>A typed or picked name: trimmed, relative to <c>Exports/</c>, the preset's extension added when it has none.</summary>
    public static string ResolveOutput(string typed, string projectRoot, TimelineExportPreset preset)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentNullException.ThrowIfNull(preset);
        var trimmed = (typed ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        var full = Path.GetFullPath(trimmed, Path.Combine(projectRoot, "Exports"));
        return Path.GetExtension(full).Length > 0 ? full : full.TrimEnd('.') + preset.Extension;
    }

    /// <summary>Why <paramref name="outputPath"/> cannot be written, or null: empty, a folder, not the preset's format, or a source.</summary>
    public static string? Problem(Timeline timeline, Func<MediaRef, string> resolve, string outputPath, TimelineExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(outputPath))
            return "Choose where to save the exported video.";
        if (Directory.Exists(outputPath))
            return $"{outputPath} is a folder. Choose a file name.";
        if (!Path.GetExtension(outputPath).Equals(preset.Extension, StringComparison.OrdinalIgnoreCase))
            return $"{preset.Name} writes {preset.Extension} files.";
        foreach (var clip in timeline.Clips)
            if (VideoPaths.SameFile(outputPath, resolve(clip.Media)))
                return "That is one of the timeline's media files. Choose a different file name; media is never overwritten.";
        return null;
    }

    /// <summary>The range to export: in to out when <paramref name="inToOut"/> and both are set, else the whole timeline.</summary>
    public static (double Start, double End) Range(Timeline timeline, bool inToOut)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return inToOut ? TimelineLayout.Range(timeline) : (0, TimelineLayout.Duration(timeline));
    }

    /// <summary>"0:42.3 · 1080×1920 · 30 fps" for the dialog.</summary>
    public static string Summary(Timeline timeline, TimelineExportPreset preset, bool inToOut)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(preset);
        var (start, end) = Range(timeline, inToOut);
        var (w, h) = preset.SizeFor(timeline.Settings);
        return $"{VideoFormat.Clock(Math.Max(0, end - start))} · {w}×{h} · {preset.RateFor(timeline.Settings).Fps:0.##} fps";
    }
}

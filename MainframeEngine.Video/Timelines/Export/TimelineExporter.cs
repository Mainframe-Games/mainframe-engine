using System.Text;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Timelines.Export;

/// <summary>
/// Renders a timeline range through a preset with the user's ffmpeg (ADR 0190): the graph goes in a temporary script, the
/// H.264 encoder is <see cref="DeliveryEncoder"/>'s choice, progress comes from <c>-progress pipe:1</c>, and a cancelled
/// or failed export deletes its partial file. Sources are never written.
/// </summary>
public sealed class TimelineExporter
{
    private readonly FfmpegLocation _ffmpeg;
    private readonly TempSession _temp;

    public TimelineExporter(FfmpegLocation ffmpeg, TempSession temp)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(temp);
        _ffmpeg = ffmpeg;
        _temp = temp;
    }

    /// <exception cref="InvalidOperationException">The range is empty, the extension is not the preset's, or the output is a source.</exception>
    /// <exception cref="FfmpegException">ffmpeg failed.</exception>
    public async Task ExportAsync(Timeline timeline, TimelineExportPreset preset, double start, double end, string outputPath,
        Func<MediaRef, string>? resolvePath, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Path.GetExtension(outputPath).Equals(preset.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"\"{preset.Name}\" writes {preset.Extension} files, not {Path.GetExtension(outputPath)}.");
        var output = Path.GetFullPath(outputPath);
        foreach (var clip in timeline.Clips)
            if (string.Equals(Path.GetFullPath(resolvePath?.Invoke(clip.Media) ?? clip.Media.Path), output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{outputPath} is a source of this timeline; export to another file.");

        var graph = TimelineGraphBuilder.Build(timeline, preset, start, end, resolvePath);
        var script = _temp.NewFile(".txt");
        await File.WriteAllTextAsync(script, graph.Graph, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var capabilities = await FfmpegCapabilities.DetectAsync(_ffmpeg, cancellationToken).ConfigureAwait(false);
        var encoder = preset.Format == TimelineExportFormat.Mp4
            ? await DeliveryEncoder.SelectAsync(_ffmpeg, cancellationToken).ConfigureAwait(false)
            : DeliveryEncoder.Software;
        var args = TimelineExportArguments.Build(graph, preset, encoder, capabilities, script, output);
        if (Path.GetDirectoryName(output) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);

        try
        {
            await RunAsync(args, graph.Duration, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or FfmpegException)
        {
            TryDelete(output);
            throw;
        }

        progress?.Report(new ExportProgress(1.0, "Done"));
    }

    private async Task RunAsync(IEnumerable<string> args, double totalSeconds, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var parser = new FfmpegProgressParser(totalSeconds);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                if (parser.Feed(line) is { } fraction)
                    progress?.Report(new ExportProgress(fraction, "Encoding"));
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Exporting", ct).ConfigureAwait(false);
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

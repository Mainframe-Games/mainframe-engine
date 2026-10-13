using System.Text;

namespace MainframeEngine.Video.Media.Export;

/// <summary>Renders the kept segments to a new file. Never touches the source.</summary>
public sealed class Exporter
{
    public static readonly string[] SupportedExtensions = [".mp4", ".mov", ".mkv", ".webm"];

    private readonly FfmpegLocation _ffmpeg;
    private readonly TempSession _temp;

    public Exporter(FfmpegLocation ffmpeg, TempSession temp)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(temp);
        _ffmpeg = ffmpeg;
        _temp = temp;
    }

    /// <exception cref="InvalidOperationException">Nothing is kept, or the container cannot take the requested mode.</exception>
    /// <exception cref="FfmpegException">ffmpeg failed.</exception>
    public async Task ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var extension = Path.GetExtension(request.OutputPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"\"{extension}\" is not a supported output format. Use {string.Join(", ", SupportedExtensions)}.");
        }

        if (FilterGraphBuilder.KeptDuration(request.Segments) <= 0)
        {
            throw new InvalidOperationException("Every segment is removed; there is nothing to export.");
        }

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        try
        {
            if (request.Mode == ExportMode.Precise)
            {
                await ExportPreciseAsync(request, extension, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ExportFastAsync(request, extension, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(request.OutputPath);
            throw;
        }
        catch (FfmpegException)
        {
            TryDelete(request.OutputPath);
            throw;
        }

        progress?.Report(new ExportProgress(1.0, "Done"));
    }

    // ---- precise ------------------------------------------------------------------------------

    private async Task ExportPreciseAsync(ExportRequest request, string extension, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var graph = FilterGraphBuilder.Build(request.Segments);
        var scriptPath = _temp.NewFile(".txt");
        await File.WriteAllTextAsync(scriptPath, graph, new UTF8Encoding(false), ct).ConfigureAwait(false);

        // ffmpeg 7 introduced "-/option file" and ffmpeg 8 removed -filter_complex_script, so ask
        // the binary which one it understands.
        var capabilities = await FfmpegCapabilities.DetectAsync(_ffmpeg, ct).ConfigureAwait(false);

        var encoder = extension == ".webm" ? null : await DeliveryEncoder.SelectAsync(_ffmpeg, ct).ConfigureAwait(false);
        var bitrate = DeliveryEncoder.TargetBitrate(request.Width, request.Height, request.FrameRate);

        var args = new List<string> { "-i", request.SourcePath };
        args.AddRange(capabilities.FilterComplexScriptArguments(scriptPath));
        args.AddRange(["-map", "[outv]", "-map", "[outa]"]);
        args.AddRange(CodecArguments(extension, encoder, bitrate));
        args.AddRange(["-progress", "pipe:1", "-y", request.OutputPath]);

        var total = FilterGraphBuilder.KeptDuration(request.Segments);
        await RunWithProgressAsync(args, total, "Encoding", f => progress?.Report(new ExportProgress(f, "Encoding")), ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> CodecArguments(string extension, string? encoder, long bitrate)
    {
        if (extension == ".webm")
        {
            return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"];
        }

        IEnumerable<string> video = DeliveryEncoder.VideoArguments(encoder ?? DeliveryEncoder.Software, bitrate);
        IEnumerable<string> audio = ["-c:a", "aac", "-b:a", "192k"];
        return extension is ".mp4" or ".mov" ? [.. video, .. audio, "-movflags", "+faststart"] : [.. video, .. audio];
    }

    // ---- fast ---------------------------------------------------------------------------------

    private async Task ExportFastAsync(ExportRequest request, string extension, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (extension == ".webm")
        {
            throw new InvalidOperationException("Fast export copies the source streams unchanged, and WebM cannot hold most recording codecs. Choose MP4, MOV or MKV, or use precise mode.");
        }

        progress?.Report(new ExportProgress(0, "Reading keyframes"));
        var keyframes = await ReadKeyframesAsync(request.SourcePath, ct).ConfigureAwait(false);
        var ranges = KeyframeSnapper.Snap(request.Segments, keyframes, request.DurationSeconds);
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("After snapping to keyframes nothing is left to export. The kept regions are shorter than the distance between keyframes; use precise mode.");
        }

        // Each range becomes a stream-copied piece; the pieces are then joined with the concat demuxer.
        var total = ranges.Sum(r => r.End - r.Start);
        var done = 0.0;
        var pieces = new List<string>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var piece = _temp.NewFile(extension);
            pieces.Add(piece);

            string[] args =
            [
                "-ss", FilterGraphBuilder.F(start),
                "-to", FilterGraphBuilder.F(end),
                "-i", request.SourcePath,
                "-map", "0:v:0", "-map", "0:a:0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-progress", "pipe:1",
                "-y", piece,
            ];

            var stage = $"Copying part {i + 1} of {ranges.Count}";
            var pieceLength = end - start;
            var doneSoFar = done;
            await RunWithProgressAsync(args, pieceLength, stage, f => progress?.Report(new ExportProgress((doneSoFar + f * pieceLength) / total, stage)), ct).ConfigureAwait(false);
            done += pieceLength;
        }

        progress?.Report(new ExportProgress(1.0, "Joining"));
        var listPath = _temp.NewFile(".txt");
        var list = new StringBuilder();
        foreach (var piece in pieces)
        {
            list.Append("file '").Append(piece.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        }

        await File.WriteAllTextAsync(listPath, list.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);

        string[] concatArgs =
        [
            "-f", "concat", "-safe", "0",
            "-i", listPath,
            "-c", "copy",
            "-y", request.OutputPath,
        ];
        await RunWithProgressAsync(concatArgs, total, "Joining", _ => { }, ct).ConfigureAwait(false);

        foreach (var piece in pieces)
        {
            TryDelete(piece);
        }
    }

    private async Task<IReadOnlyList<double>> ReadKeyframesAsync(string source, CancellationToken ct)
    {
        string[] args =
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
            source,
        ];

        var lines = new List<string>();
        using var process = FfmpegProcess.StartRaw(_ffmpeg.FfprobePath, args);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Reading keyframes", ct).ConfigureAwait(false);
        return KeyframeSnapper.ParseKeyframes(lines);
    }

    // ---- shared -------------------------------------------------------------------------------

    private async Task RunWithProgressAsync(IEnumerable<string> args, double totalSeconds, string what, Action<double> onFraction, CancellationToken ct)
    {
        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var parser = new FfmpegProgressParser(totalSeconds);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (parser.Feed(line) is { } fraction)
                {
                    onFraction(fraction);
                }
            }
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderr, what, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

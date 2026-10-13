namespace MainframeEngine.Video.Media;

/// <summary>Reads duration, dimensions, frame rate and stream presence from a source file via ffprobe.</summary>
public sealed class MediaProbe
{
    private readonly FfmpegLocation _ffmpeg;

    public MediaProbe(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="MediaProbeException">ffprobe failed or the file has no video stream.</exception>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The video file could not be found.", path);
        }

        string output;
        using (var process = FfmpegProcess.StartRaw(_ffmpeg.FfprobePath, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]))
        {
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Reading the file", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                FfmpegProcess.TryKill(process);
                throw;
            }
            catch (FfmpegException ex)
            {
                throw new MediaProbeException($"ffprobe could not read \"{Path.GetFileName(path)}\". It may not be a video file, or it may be damaged.\n\n{ex.Message}", ex);
            }
        }

        return FfprobeJson.Parse(output, path);
    }
}

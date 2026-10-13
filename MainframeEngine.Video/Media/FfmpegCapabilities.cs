using System.Text.RegularExpressions;

namespace MainframeEngine.Video.Media;

/// <summary>
/// What this particular ffmpeg build can do. Today that is one question: does it take option values
/// from files with the <c>-/option file</c> syntax (ffmpeg 7.0+), or does it still need the older
/// <c>-filter_complex_script</c> (removed in ffmpeg 8)?
/// </summary>
public sealed partial record FfmpegCapabilities(int? Major)
{
    /// <summary>
    /// True for ffmpeg 7 and later. Git snapshot builds report no version number and are assumed
    /// modern, since anyone running one has a recent ffmpeg.
    /// </summary>
    public bool SupportsOptionFiles => Major is null || Major >= 7;

    /// <summary>Arguments that load a filter graph from <paramref name="scriptPath"/>.</summary>
    public IReadOnlyList<string> FilterComplexScriptArguments(string scriptPath) => SupportsOptionFiles
        ? ["-/filter_complex", scriptPath]
        : ["-filter_complex_script", scriptPath];

    /// <summary>Parses the first line of <c>ffmpeg -version</c>.</summary>
    public static FfmpegCapabilities Parse(string bannerFirstLine)
    {
        ArgumentNullException.ThrowIfNull(bannerFirstLine);
        var match = VersionPattern().Match(bannerFirstLine);
        return new FfmpegCapabilities(match.Success ? int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public static async Task<FfmpegCapabilities> DetectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        using var process = FfmpegProcess.StartRaw(ffmpeg.FfmpegPath, ["-version"]);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var firstLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Reading the ffmpeg version", cancellationToken).ConfigureAwait(false);
        return Parse(firstLine);
    }

    // "ffmpeg version 9.0.1", "ffmpeg version n7.1-3", "ffmpeg version 6.1.1-3ubuntu5".
    // Not "ffmpeg version 2025-01-15-git-..." or "ffmpeg version N-118000-g...": those are dates / commit counts.
    [GeneratedRegex(@"^ffmpeg version n?(?<major>[1-9]\d?)(?:\.\d+)+(?:[-\s]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

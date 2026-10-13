using System.Globalization;
using System.Text.Json;

namespace MainframeEngine.Video.Capture;

/// <summary>Reads Mainframe Engine capture sidecars. The video editor never writes them.</summary>
public static class CaptureSidecarSerializer
{
    /// <exception cref="CaptureSidecarFormatException">Not a readable version-1 sidecar.</exception>
    public static CaptureSidecar Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        CaptureSidecarFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CaptureSidecarJsonContext.Default.CaptureSidecarFile);
        }
        catch (JsonException ex)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data next to this video is not valid JSON.", ex);
        }

        if (file is null || file.Format != CaptureSidecar.FormatName)
        {
            throw new CaptureSidecarFormatException("The .mfcap.json file next to this video is not Mainframe recording data.");
        }

        if (file.Version is not { } version || version < 1)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data next to this video has no valid version.");
        }

        if (version > CaptureSidecar.SupportedVersion)
        {
            throw new CaptureSidecarFormatException(string.Create(CultureInfo.InvariantCulture,
                $"This video was recorded by a newer Mainframe Engine (recording data version {version}, this build reads up to {CaptureSidecar.SupportedVersion}). Update the editor to use its gameplay data."));
        }

        var mode = file.Mode switch
        {
            "live" => CaptureMode.Live,
            "offline" => CaptureMode.Offline,
            null => throw new CaptureSidecarFormatException("The Mainframe recording data does not say whether it was live or offline."),
            _ => throw new CaptureSidecarFormatException($"The Mainframe recording data has an unknown mode \"{file.Mode}\"."),
        };

        if (file.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0)
        {
            throw new CaptureSidecarFormatException("The Mainframe recording data has no valid duration.");
        }

        return new CaptureSidecar(
            mode,
            duration,
            file.FrameRate ?? 0,
            file.Width ?? 0,
            file.Height ?? 0,
            file.Game,
            file.Scene,
            Spans(file.Input, "input"),
            Spans(file.Loading, "loading"),
            Spans(file.Paused, "paused"),
            Spans(file.Keep, "keep"),
            Spans(file.Skip, "skip"),
            Markers(file.Markers));
    }

    /// <summary>The sidecar next to <paramref name="videoPath"/>, or null when there is none.</summary>
    /// <exception cref="CaptureSidecarFormatException">A sidecar exists but cannot be read.</exception>
    /// <exception cref="IOException">The file exists but could not be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file exists but the caller may not read it.</exception>
    public static async Task<CaptureSidecar?> TryLoadForVideoAsync(string videoPath, CancellationToken ct)
    {
        var path = CaptureSidecar.PathFor(videoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Deserialize(json);
    }

    private static List<CaptureSpan> Spans(List<CaptureSpanFile>? spans, string name)
    {
        var result = new List<CaptureSpan>(spans?.Count ?? 0);
        foreach (var s in spans ?? [])
        {
            if (s is null || s.Start is not { } start || s.End is not { } end
                || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            {
                throw new CaptureSidecarFormatException($"The Mainframe recording data has an invalid \"{name}\" span.");
            }

            result.Add(new CaptureSpan(start, end, s.Label));
        }

        return result;
    }

    private static List<CaptureMarker> Markers(List<CaptureMarkerFile>? markers)
    {
        var result = new List<CaptureMarker>(markers?.Count ?? 0);
        foreach (var m in markers ?? [])
        {
            if (m is null || m.Time is not { } time || !double.IsFinite(time) || time < 0)
            {
                throw new CaptureSidecarFormatException("The Mainframe recording data has an invalid marker.");
            }

            result.Add(new CaptureMarker(time, m.Label ?? ""));
        }

        return result;
    }
}

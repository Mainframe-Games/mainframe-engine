using System.Globalization;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Timelines.Export;

/// <summary>The full ffmpeg command line of a timeline export (pure, so tests check it without running ffmpeg).</summary>
public static class TimelineExportArguments
{
    /// <summary>The arguments: inputs, the graph script, the maps, codecs, progress on stdout, the output.</summary>
    /// <param name="graph">The built graph.</param>
    /// <param name="preset">The preset it was built for.</param>
    /// <param name="encoder">The H.264 encoder (<see cref="DeliveryEncoder.SelectAsync"/>) for MP4; ignored otherwise.</param>
    /// <param name="capabilities">How this ffmpeg takes a filter script.</param>
    /// <param name="scriptPath">The file holding <see cref="TimelineGraph.Graph"/>.</param>
    /// <param name="outputPath">The file to write.</param>
    public static IReadOnlyList<string> Build(TimelineGraph graph, TimelineExportPreset preset, string encoder, FfmpegCapabilities capabilities, string scriptPath, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(capabilities);
        var args = new List<string>(graph.InputArguments);
        args.AddRange(capabilities.FilterComplexScriptArguments(scriptPath));
        args.AddRange(["-map", TimelineGraphBuilder.VideoOutput]);
        if (graph.HasAudio)
            args.AddRange(["-map", TimelineGraphBuilder.AudioOutput]);
        args.AddRange(Codecs(graph, preset, encoder));
        args.AddRange(["-progress", "pipe:1", "-y", outputPath]);
        return args;
    }

    private static IReadOnlyList<string> Codecs(TimelineGraph graph, TimelineExportPreset preset, string encoder)
    {
        var audioRate = preset.AudioKbps.ToString(CultureInfo.InvariantCulture) + "k";
        var crf = preset.Crf.ToString(CultureInfo.InvariantCulture);
        switch (preset.Format)
        {
            case TimelineExportFormat.Gif:
                return ["-loop", "0"];
            case TimelineExportFormat.WebM:
                return
                [
                    "-c:v", "libvpx-vp9", "-crf", crf, "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p",
                    .. graph.HasAudio ? (string[])["-c:a", "libopus", "-b:a", audioRate] : ["-an"],
                ];
            default:
                IReadOnlyList<string> video;
                if (preset.SizeCapBytes is { } cap)
                {
                    var bitrate = TimelineExportPresets.SizeCappedVideoBitrate(cap, graph.Duration, graph.HasAudio ? preset.AudioKbps : 0);
                    var b = bitrate.ToString(CultureInfo.InvariantCulture);
                    video =
                    [
                        "-c:v", encoder, "-b:v", b, "-maxrate", b, "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture),
                        .. encoder == DeliveryEncoder.Software ? (string[])["-preset", "medium"] : [], "-pix_fmt", "yuv420p",
                    ];
                }
                else if (encoder == DeliveryEncoder.Software)
                {
                    video = ["-c:v", DeliveryEncoder.Software, "-crf", crf, "-preset", "medium", "-pix_fmt", "yuv420p"];
                }
                else
                {
                    video = DeliveryEncoder.VideoArguments(encoder, DeliveryEncoder.TargetBitrate(graph.Width, graph.Height, graph.Rate.Fps));
                }

                return
                [
                    .. video,
                    .. graph.HasAudio ? (string[])["-c:a", "aac", "-b:a", audioRate] : ["-an"],
                    "-movflags", "+faststart",
                ];
        }
    }
}

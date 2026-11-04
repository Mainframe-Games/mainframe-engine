using System.Globalization;
using System.Text;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Timelines.Export;

/// <summary>An export's ffmpeg inputs and filter graph (outputs <c>[outv]</c> and, with audio, <c>[outa]</c>).</summary>
/// <param name="InputArguments">One <c>-ss In -t Len -i file</c> per clip window, in input-index order.</param>
/// <param name="Graph">The filter graph, written to a file (<c>-filter_complex_script</c> / <c>-/filter_complex</c>).</param>
/// <param name="HasAudio">Whether the graph has <c>[outa]</c>.</param>
/// <param name="Duration">Output seconds.</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="Rate">Output frame rate.</param>
public sealed record TimelineGraph(IReadOnlyList<string> InputArguments, string Graph, bool HasAudio, double Duration, int Width, int Height, FrameRate Rate);

/// <summary>
/// Builds the export filter graph of a timeline range (pure; docs/design/future/video-nle.md, Export).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each clip reads its own input, <c>-ss In -t Len</c> (frame-accurate input seeking, so a short clip of a long
/// recording decodes only its window). Its kept ranges are trimmed and concatenated, sped up or down, brought to the
/// timeline's rate, reframed (<see cref="ClipLayout.Visible"/>: crop, scale, pad onto a transparent frame), then padded
/// a frame and trimmed to its exact length, so rounding never drifts along a track.</item>
/// <item>Each enabled video track is one RGBA stream (its clips and transparent gaps, concatenated), overlaid in track
/// order on the background colour. One overlay per track, not per clip.</item>
/// <item>Each audible clip (enabled track, not muted, soloed when any track is) is spliced with Cutback's 8 ms fades,
/// tempo-changed, resampled to stereo, given its track's gain, delayed to its start, and mixed (<c>amix</c>, not
/// normalised) over a silent bed of exactly the output length.</item>
/// <item>The preset's size (letterboxed when its aspect differs) and rate come last; a GIF gets a generated palette.</item>
/// </list>
/// </remarks>
public static class TimelineGraphBuilder
{
    public const string VideoOutput = "[outv]";
    public const string AudioOutput = "[outa]";

    private const double Epsilon = 1e-6;

    /// <summary>The graph for <c>[start, end)</c> of <paramref name="timeline"/> through <paramref name="preset"/>.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <param name="preset">Size, rate, format, audio.</param>
    /// <param name="start">Range start (seconds; snapped to frames).</param>
    /// <param name="end">Range end.</param>
    /// <param name="resolvePath">Turns a clip's media path (project-relative) into the file ffmpeg reads; null: as stored.</param>
    /// <exception cref="InvalidOperationException">The range is empty.</exception>
    public static TimelineGraph Build(Timeline timeline, TimelineExportPreset preset, double start, double end, Func<MediaRef, string>? resolvePath = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(preset);
        var rate = timeline.Rate;
        var settings = timeline.Settings;
        start = rate.Snap(Math.Max(0, start));
        end = rate.Snap(end);
        var duration = end - start;
        if (duration < rate.FrameDuration - Epsilon)
            throw new InvalidOperationException("The range to export is empty.");

        var inputs = new List<string>();
        var inputCount = 0;
        var graph = new StringBuilder(4096);
        var n = 0;
        int AddInput(Clip clip)
        {
            inputs.AddRange(["-ss", F(clip.SourceIn), "-t", F(clip.SourceOut - clip.SourceIn), "-i", resolvePath?.Invoke(clip.Media) ?? clip.Media.Path]);
            return inputCount++;
        }

        // ── Video ──
        var size = $"{settings.Width}x{settings.Height}";
        graph.Append("color=c=").Append(settings.BackgroundForFfmpeg()).Append(":s=").Append(size).Append(":r=").Append(rate.ToFfmpeg())
             .Append(":d=").Append(F(duration)).Append(",format=rgba[base];\n");
        var below = "[base]";
        foreach (var track in timeline.Tracks)
        {
            if (track.Kind != TrackKind.Video || !track.Enabled)
                continue;
            var clips = InRange(timeline, track.Id, start, end);
            if (clips.Count == 0)
                continue;

            var parts = new List<string>();
            var cursor = 0.0;
            foreach (var (clip, at) in clips)
            {
                if (at > cursor + Epsilon)
                    parts.Add(Gap(graph, at - cursor, settings, ref n));
                var length = TimelineLayout.Length(clip, rate);
                parts.Add(ClipLayout.Visible(clip.Media.Width, clip.Media.Height, clip.Transform, settings.Width, settings.Height) is { } visible
                    ? VideoClip(graph, clip, AddInput(clip), visible, length, settings, ref n)
                    : Gap(graph, length, settings, ref n));
                cursor = at + length;
            }

            var trackLabel = $"[t{n++}]";
            foreach (var part in parts)
                graph.Append(part);
            graph.Append("concat=n=").Append(parts.Count).Append(":v=1:a=0").Append(trackLabel).Append(";\n");
            var over = $"[o{n++}]";
            graph.Append(below).Append(trackLabel).Append("overlay=eof_action=pass:format=auto").Append(over).Append(";\n");
            below = over;
        }

        var (width, height) = preset.SizeFor(settings);
        var outRate = preset.RateFor(settings);
        graph.Append(below);
        var scaled = width != settings.Width || height != settings.Height;
        if (scaled)
            graph.Append("scale=").Append(width).Append(':').Append(height).Append(":force_original_aspect_ratio=decrease:force_divisible_by=2,pad=")
                 .Append(width).Append(':').Append(height).Append(":(ow-iw)/2:(oh-ih)/2:color=").Append(settings.BackgroundForFfmpeg()).Append(',');
        if (outRate != rate)
            graph.Append("fps=").Append(outRate.ToFfmpeg()).Append(',');
        if (preset.Format == TimelineExportFormat.Gif)
            graph.Append("setsar=1,split[gifa][gifb];\n[gifa]palettegen=stats_mode=diff[gifp];\n[gifb][gifp]paletteuse=dither=bayer:bayer_scale=5").Append(VideoOutput);
        else
            graph.Append("setsar=1,format=yuv420p").Append(VideoOutput);

        // ── Audio ──
        var hasAudio = preset.HasAudio;
        if (hasAudio)
        {
            graph.Append(";\nanullsrc=r=").Append(settings.SampleRate).Append(":cl=stereo,atrim=duration=").Append(F(duration)).Append("[asil];\n");
            var anySolo = timeline.Tracks.Any(t => t.Kind == TrackKind.Audio && t.Solo);
            var mix = new List<string> { "[asil]" };
            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != TrackKind.Audio || !track.Enabled || track.Muted || (anySolo && !track.Solo))
                    continue;
                foreach (var (clip, at) in InRange(timeline, track.Id, start, end))
                    mix.Add(AudioClip(graph, clip, AddInput(clip), at, TimelineLayout.Length(clip, rate), track.GainDb, settings.SampleRate, ref n));
            }

            foreach (var label in mix)
                graph.Append(label);
            graph.Append(mix.Count == 1
                ? "anull"
                : $"amix=inputs={mix.Count.ToString(CultureInfo.InvariantCulture)}:normalize=0:duration=first:dropout_transition=0").Append(AudioOutput);
        }

        return new TimelineGraph(inputs, graph.ToString(), hasAudio, duration, width, height, outRate);
    }

    /// <summary>The <c>atempo</c> chain for <paramref name="speed"/> (each stage takes 0.5–2).</summary>
    public static string Atempo(double speed)
    {
        if (!(speed > 0))
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed is positive.");
        var stages = new List<string>();
        while (speed > 2 + Epsilon)
        {
            stages.Add("atempo=2");
            speed /= 2;
        }

        while (speed < 0.5 - Epsilon)
        {
            stages.Add("atempo=0.5");
            speed /= 0.5;
        }

        if (Math.Abs(speed - 1) > Epsilon)
            stages.Add("atempo=" + F(speed));
        return string.Join(',', stages);
    }

    // The enabled clips of a track inside [start, end), trimmed to it, with their start relative to the range.
    private static List<(Clip Clip, double At)> InRange(Timeline timeline, string trackId, double start, double end)
    {
        var rate = timeline.Rate;
        var result = new List<(Clip, double)>();
        foreach (var clip in timeline.ClipsOn(trackId))
        {
            if (!clip.Enabled)
                continue;
            var clipEnd = TimelineLayout.End(clip, rate);
            if (clipEnd <= start + Epsilon || clip.Start >= end - Epsilon)
                continue;
            var c = clip;
            if (c.Start < start - Epsilon)
                c = ClipOps.TrimStartTo(c, start, rate);
            if (TimelineLayout.End(c, rate) > end + Epsilon)
                c = ClipOps.TrimEndTo(c, end, rate);
            if (TimelineLayout.Length(c, rate) > Epsilon)
                result.Add((c, c.Start - start));
        }

        return result;
    }

    private static string Gap(StringBuilder graph, double seconds, TimelineSettings settings, ref int n)
    {
        var label = $"[g{n++}]";
        graph.Append("color=c=black@0:s=").Append(settings.Width).Append('x').Append(settings.Height).Append(":r=").Append(settings.FrameRate.ToFfmpeg())
             .Append(":d=").Append(F(seconds)).Append(",format=rgba").Append(label).Append(";\n");
        return label;
    }

    private static string VideoClip(StringBuilder graph, Clip clip, int input, VisiblePart visible, double length, TimelineSettings settings, ref int n)
    {
        var id = n++;
        Splice(graph, clip, $"[{input.ToString(CultureInfo.InvariantCulture)}:v]", "trim", "setpts", "", id, video: true);
        graph.Append(Math.Abs(clip.Speed - 1) > Epsilon ? "setpts=(PTS-STARTPTS)/" + F(clip.Speed) : "setpts=PTS-STARTPTS")
             .Append(",fps=").Append(settings.FrameRate.ToFfmpeg())
             .Append(",crop=").Append(visible.SourceWidth).Append(':').Append(visible.SourceHeight).Append(':').Append(visible.SourceX).Append(':').Append(visible.SourceY)
             .Append(",scale=").Append(visible.Width).Append(':').Append(visible.Height)
             .Append(",setsar=1,format=rgba,pad=").Append(settings.Width).Append(':').Append(settings.Height).Append(':').Append(visible.X).Append(':').Append(visible.Y)
             .Append(":color=black@0");
        if (clip.Transform.Opacity < 1 - Epsilon)
            graph.Append(",colorchannelmixer=aa=").Append(F(Math.Max(0, clip.Transform.Opacity)));
        var label = $"[v{id}]";
        graph.Append(",tpad=stop_mode=clone:stop_duration=").Append(F(settings.FrameRate.FrameDuration))
             .Append(",trim=duration=").Append(F(length)).Append(",setpts=PTS-STARTPTS").Append(label).Append(";\n");
        return label;
    }

    private static string AudioClip(StringBuilder graph, Clip clip, int input, double at, double length, double gainDb, int sampleRate, ref int n)
    {
        var id = n++;
        Splice(graph, clip, $"[{input.ToString(CultureInfo.InvariantCulture)}:a]", "atrim", "asetpts", ",afade", id, video: false);
        graph.Append("asetpts=PTS-STARTPTS");
        var tempo = Atempo(clip.Speed);
        if (tempo.Length > 0)
            graph.Append(',').Append(tempo);
        graph.Append(",aresample=").Append(sampleRate).Append(",aformat=sample_fmts=fltp:channel_layouts=stereo")
             .Append(",apad=whole_dur=").Append(F(length)).Append(",atrim=duration=").Append(F(length));
        if (Math.Abs(gainDb) > Epsilon)
            graph.Append(",volume=").Append(F(gainDb)).Append("dB");
        if (at > Epsilon)
            graph.Append(",adelay=delays=").Append(Math.Round(at * 1000).ToString(CultureInfo.InvariantCulture)).Append(":all=1");
        var label = $"[a{id}]";
        graph.Append(label).Append(";\n");
        return label;
    }

    // Trims each kept range of the window out of `source` and joins them, leaving the chain open after a comma:
    // "<source>trim=...,setpts=...," for one range; split + trims + "concat=n=K," for several.
    private static void Splice(StringBuilder graph, Clip clip, string source, string trim, string setpts, string fade, int id, bool video)
    {
        var ranges = TimelineLayout.KeptRanges(clip);
        if (ranges.Count == 1)
        {
            graph.Append(source);
            Range(graph, clip, ranges[0], trim, setpts, fade);
            graph.Append(',');
            return;
        }

        graph.Append(source).Append(video ? "split=" : "asplit=").Append(ranges.Count);
        for (var k = 0; k < ranges.Count; k++)
            graph.Append("[s").Append(id).Append('_').Append(k).Append(']');
        graph.Append(";\n");
        for (var k = 0; k < ranges.Count; k++)
        {
            graph.Append("[s").Append(id).Append('_').Append(k).Append(']');
            Range(graph, clip, ranges[k], trim, setpts, fade);
            graph.Append("[p").Append(id).Append('_').Append(k).Append("];\n");
        }

        for (var k = 0; k < ranges.Count; k++)
            graph.Append("[p").Append(id).Append('_').Append(k).Append(']');
        graph.Append("concat=n=").Append(ranges.Count).Append(video ? ":v=1:a=0," : ":v=0:a=1,");
    }

    private static void Range(StringBuilder graph, Clip clip, SourceRange range, string trim, string setpts, string fade)
    {
        // Window-relative: the input already starts at SourceIn.
        var from = range.Start - clip.SourceIn;
        var to = range.End - clip.SourceIn;
        graph.Append(trim).Append("=start=").Append(F(from)).Append(":end=").Append(F(to)).Append(',').Append(setpts).Append("=PTS-STARTPTS");
        if (fade.Length > 0)
        {
            var length = to - from;
            var d = Math.Min(FilterGraphBuilder.FadeSeconds, length / 2);
            graph.Append(fade).Append("=t=in:st=0:d=").Append(F(d)).Append(fade).Append("=t=out:st=").Append(F(length - d)).Append(":d=").Append(F(d));
        }
    }

    private static string F(double value) => FilterGraphBuilder.F(value);
}

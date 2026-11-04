using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// Reads and writes <c>.mftimeline</c> files: versioned JSON with the timeline's asset <c>uid</c> at the top, so the
/// engine's AssetDatabase reads it without a <c>.meta</c>. Loading validates everything the edits rely on.
/// </summary>
public static class TimelineSerializer
{
    /// <summary>Schema version written by this build. Bump alongside a new <see cref="TimelineMigrator"/> step.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The extension timelines are saved with.</summary>
    public const string FileExtension = ".mftimeline";

    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>True when <paramref name="path"/> ends in <see cref="FileExtension"/>, ignoring case.</summary>
    public static bool IsTimelinePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetExtension(path).Equals(FileExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static string Serialize(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var file = new TimelineFile(CurrentVersion, timeline.Uid, timeline.Settings, [.. timeline.Tracks],
            [.. timeline.Clips.OrderBy(c => c.Start).ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => new TimelineFileClip(
                c.Id, c.TrackId, c.Media, c.Start, c.SourceIn, c.SourceOut, c.Speed, c.LinkGroup, c.Enabled, c.Transform, [.. c.Segments],
                c.Kind, c.VolumeDb, c.FadeIn, c.FadeOut, c.Title))],
            [.. timeline.Markers], timeline.InPoint, timeline.OutPoint, timeline.Transitions.Length == 0 ? null : [.. timeline.Transitions]);
        return JsonSerializer.Serialize(file, TimelineJsonContext.Default.TimelineFile);
    }

    /// <exception cref="TimelineFormatException">The text is not a readable timeline.</exception>
    public static Timeline Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject
                   ?? throw new TimelineFormatException("The timeline file does not contain a JSON object.");
        }
        catch (JsonException e)
        {
            throw new TimelineFormatException("The timeline file is not valid JSON.", e);
        }

        TimelineMigrator.Default.Migrate(root);

        TimelineFile? file;
        try
        {
            file = root.Deserialize(TimelineJsonContext.Default.TimelineFile);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new TimelineFormatException($"The timeline file has an unexpected shape: {e.Message}", e);
        }

        if (file is null || file.Settings is null || file.Tracks is null || file.Clips is null || string.IsNullOrWhiteSpace(file.Uid))
            throw new TimelineFormatException("The timeline file is missing one of: uid, settings, tracks, clips.");

        var timeline = new Timeline(file.Uid, file.Settings, [.. file.Tracks],
            [.. file.Clips.Select(c => new Clip(c.Id, c.Track, c.Media, c.Start, c.In, c.Out, [.. c.Segments ?? []],
                c.Transform ?? ClipTransform.Default, c.Speed, c.Link, c.Enabled, c.Kind, c.VolumeDb, c.FadeIn, c.FadeOut, c.Title))],
            [.. file.Markers ?? []], file.In, file.Out, [.. file.Transitions ?? []]);
        Validate(timeline);
        // A transition whose clips no longer meet (a hand-edited file) is dropped, as an edit would drop it.
        return timeline.Transitions.All(t => TimelineEdits.StillFits(timeline, t))
            ? timeline
            : timeline with { Transitions = [.. timeline.Transitions.Where(t => TimelineEdits.StillFits(timeline, t))] };
    }

    /// <summary>Writes to a sibling <c>.tmp</c> file and moves it into place, so a crash never leaves half a timeline.</summary>
    public static async Task SaveAsync(Timeline timeline, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = Serialize(timeline);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <exception cref="TimelineFormatException">The file is not a readable timeline.</exception>
    public static async Task<Timeline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Throws <see cref="TimelineFormatException"/> when the timeline does not fit together.</summary>
    public static void Validate(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        try
        {
            timeline.Settings.Validate();
        }
        catch (ArgumentException e)
        {
            throw new TimelineFormatException(e.Message, e);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
            if (string.IsNullOrEmpty(track.Id) || !ids.Add(track.Id))
                throw new TimelineFormatException($"Track '{track.Name}' has a missing or repeated id.");
        foreach (var clip in timeline.Clips)
        {
            if (string.IsNullOrEmpty(clip.Id) || !ids.Add(clip.Id))
                throw new TimelineFormatException($"A clip of {clip.Media?.Path} has a missing or repeated id.");
            if (clip.Media is null)
                throw new TimelineFormatException($"Clip {clip.Id} has no media.");
            if (timeline.FindTrack(clip.TrackId) is not { } track)
                throw new TimelineFormatException($"Clip {clip.Id} is on track {clip.TrackId}, which does not exist.");
            if (track.Kind == TrackKind.Video ? !clip.Media.HasVideo : !clip.Media.HasAudio)
                throw new TimelineFormatException($"Clip {clip.Id} ({clip.Media.Path}) has no {track.Kind.ToString().ToLowerInvariant()} for track {track.Name}.");
            if (!(clip.Speed > 0) || !(clip.SourceIn >= 0) || !(clip.SourceOut > clip.SourceIn) || clip.SourceOut > clip.Media.Duration + 1e-6 || !(clip.Start >= 0))
                throw new TimelineFormatException($"Clip {clip.Id} has an invalid window, start or speed.");
            try
            {
                SegmentList.ValidatePartition(clip.Segments, clip.Media.Duration);
            }
            catch (InvalidPartitionException e)
            {
                throw new TimelineFormatException($"Clip {clip.Id}'s cuts do not cover its media: {e.Message}", e);
            }
        }
    }
}

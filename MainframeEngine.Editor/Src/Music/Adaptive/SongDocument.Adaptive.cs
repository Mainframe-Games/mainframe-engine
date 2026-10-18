using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

// Undoable edits of the song's adaptive block (docs/design/future/adaptive-music.md). Sections snap to bar lines; drags
// pass a mergeKey and become one history entry, like note drags. Objects keep their identity through undo and redo.
public sealed partial class SongDocument
{
    /// <summary>The song's adaptive block (throws when the song has none: call <see cref="EnableAdaptive"/> first).</summary>
    public SongAdaptive Adaptive => Song.Adaptive ?? throw new InvalidOperationException("The song has no adaptive music.");

    /// <summary>Gives the song an (empty) adaptive block; returns the existing one when it has it.</summary>
    public SongAdaptive EnableAdaptive()
    {
        if (Song.Adaptive is { } existing)
            return existing;
        var adaptive = new SongAdaptive();
        Commit("Enable Adaptive Music", () => Song.Adaptive = adaptive, () => Song.Adaptive = null);
        return adaptive;
    }

    /// <summary>Removes the adaptive block (tracks keep their layer names, undo brings everything back).</summary>
    public void DisableAdaptive()
    {
        if (Song.Adaptive is not { } adaptive)
            return;
        Commit("Disable Adaptive Music", () => Song.Adaptive = null, () => Song.Adaptive = adaptive);
    }

    /// <summary><paramref name="tick"/> rounded to the nearest bar line (not negative).</summary>
    public long SnapToBar(long tick)
    {
        var bar = Song.TicksPerBar;
        return Math.Max(0, (long)Math.Round(tick / (double)bar, MidpointRounding.AwayFromZero) * bar);
    }

    public void SetAdaptiveOutput(string? output) =>
        Set("Adaptive Output", Adaptive.Output, string.IsNullOrWhiteSpace(output) ? null : output.Replace('\\', '/'), v => Adaptive.Output = v, null);

    public void SetAdaptiveTailSeconds(double seconds, string? mergeKey = null)
    {
        var value = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 30) : 4;
        var adaptive = Adaptive;
        Set("Tail Length", adaptive.TailSeconds, value, v => adaptive.TailSeconds = v, mergeKey);
    }

    // --- sections ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds a section over [<paramref name="start"/>, <paramref name="end"/>) snapped to bars (at least one bar). The first
    /// playable section also becomes the start, in the same history entry.
    /// </summary>
    public SongSection AddSection(string name, MusicSectionKind kind, long start, long end)
    {
        var adaptive = Adaptive;
        var (s, e) = SnapRange(start, end);
        var section = new SongSection
        {
            Id = SongAdaptive.NextId("s", id => adaptive.FindSection(id) is not null),
            Name = string.IsNullOrWhiteSpace(name) ? "Section " + (adaptive.Sections.Count + 1) : name.Trim(),
            Kind = kind,
            Start = s,
            End = e,
            Loop = kind == MusicSectionKind.Section,
        };
        var makeStart = kind == MusicSectionKind.Section && adaptive.FindSection(adaptive.Start) is null;
        var oldStart = adaptive.Start;
        Commit("Add Section", () =>
        {
            adaptive.Sections.Add(section);
            if (makeStart)
                adaptive.Start = section.Id;
        }, () =>
        {
            adaptive.Sections.Remove(section);
            adaptive.Start = oldStart;
        });
        return section;
    }

    /// <summary>Removes a section (the start is cleared if it was this one; other references stay and are reported).</summary>
    public void RemoveSection(SongSection section)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Sections, section);
        var oldStart = adaptive.Start;
        Commit("Remove Section", () =>
        {
            adaptive.Sections.Remove(section);
            if (adaptive.Start == section.Id)
                adaptive.Start = null;
        }, () =>
        {
            adaptive.Sections.Insert(index, section);
            adaptive.Start = oldStart;
        });
    }

    /// <summary>Moves or resizes a section, snapped to bars (at least one bar).</summary>
    public void SetSectionRange(SongSection section, long start, long end, string? mergeKey = null)
    {
        Require(Adaptive.Sections, section);
        var range = SnapRange(start, end);
        Set("Move Section", (section.Start, section.End), range, v => (section.Start, section.End) = v, mergeKey);
    }

    public void RenameSection(SongSection section, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Require(Adaptive.Sections, section);
        Set("Rename Section", section.Name, name.Trim(), v => section.Name = v, null);
    }

    public void SetSectionLoop(SongSection section, bool loop)
    {
        Require(Adaptive.Sections, section);
        Set(loop ? "Loop Section" : "Don't Loop Section", section.Loop, loop, v => section.Loop = v, null);
    }

    /// <summary>What follows the section when it does not loop (null: the music ends).</summary>
    public void SetSectionThen(SongSection section, string? then)
    {
        Require(Adaptive.Sections, section);
        Set("Section Continuation", section.Then, then, v => section.Then = v, null);
    }

    public void SetSectionKind(SongSection section, MusicSectionKind kind)
    {
        Require(Adaptive.Sections, section);
        Set("Section Kind", section.Kind, kind, v => section.Kind = v, null);
    }

    public void SetSectionColor(SongSection section, string? color)
    {
        Require(Adaptive.Sections, section);
        Set("Section Color", section.Color, color, v => section.Color = v, null);
    }

    /// <summary>A stinger's quantize and pickup.</summary>
    public void SetStingerTiming(SongSection section, MusicQuantize quantize, float pickupBeats)
    {
        Require(Adaptive.Sections, section);
        var value = (quantize, float.IsFinite(pickupBeats) ? Math.Clamp(pickupBeats, 0, 64) : 0f);
        Set("Stinger Timing", (section.Quantize ?? MusicQuantize.Beat, section.PickupBeats), value,
            v => (section.Quantize, section.PickupBeats) = (v.Item1, v.Item2), null);
    }

    public void SetStartSection(string? id)
    {
        var adaptive = Adaptive;
        Set("Start Section", adaptive.Start, id, v => adaptive.Start = v, null);
    }

    // --- markers ----------------------------------------------------------------------------------

    public SongMarker AddMarker(string name, long tick, bool exit = false)
    {
        var adaptive = Adaptive;
        var marker = new SongMarker
        {
            Id = SongAdaptive.NextId("m", id => adaptive.Markers.Exists(m => m.Id == id)),
            Name = string.IsNullOrWhiteSpace(name) ? "Marker " + (adaptive.Markers.Count + 1) : name.Trim(),
            Tick = Math.Max(0, tick),
            Exit = exit,
        };
        Commit("Add Marker", () => adaptive.Markers.Add(marker), () => adaptive.Markers.Remove(marker));
        return marker;
    }

    public void RemoveMarker(SongMarker marker)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Markers, marker);
        Commit("Remove Marker", () => adaptive.Markers.Remove(marker), () => adaptive.Markers.Insert(index, marker));
    }

    public void MoveMarker(SongMarker marker, long tick, string? mergeKey = null)
    {
        Require(Adaptive.Markers, marker);
        Set("Move Marker", marker.Tick, Math.Max(0, tick), v => marker.Tick = v, mergeKey);
    }

    public void RenameMarker(SongMarker marker, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Require(Adaptive.Markers, marker);
        Set("Rename Marker", marker.Name, name.Trim(), v => marker.Name = v, null);
    }

    public void SetMarkerExit(SongMarker marker, bool exit)
    {
        Require(Adaptive.Markers, marker);
        Set(exit ? "Make Exit Marker" : "Make Plain Marker", marker.Exit, exit, v => marker.Exit = v, null);
    }

    // --- layers -----------------------------------------------------------------------------------

    public SongLayer AddLayer(string name, string? color = null)
    {
        var adaptive = Adaptive;
        var layer = new SongLayer
        {
            Id = SongAdaptive.NextId("l", id => id == SongLayer.MainId || adaptive.FindLayer(id) is not null),
            Name = string.IsNullOrWhiteSpace(name) ? "Layer " + (adaptive.Layers.Count + 1) : name.Trim(),
            Color = color,
        };
        Commit("Add Layer", () => adaptive.Layers.Add(layer), () => adaptive.Layers.Remove(layer));
        return layer;
    }

    /// <summary>Removes a layer, its curves and its tracks' assignments (they go back to Main), as one entry.</summary>
    public void RemoveLayer(SongLayer layer)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Layers, layer);
        var curves = adaptive.Curves.Select((c, i) => (c, i)).Where(x => x.c.Layer == layer.Id).ToArray();
        var tracks = Song.Tracks.Where(t => t.Layer == layer.Id).ToArray();
        Commit("Remove Layer", () =>
        {
            adaptive.Layers.Remove(layer);
            foreach (var (c, _) in curves)
                adaptive.Curves.Remove(c);
            foreach (var t in tracks)
                t.Layer = null;
        }, () =>
        {
            adaptive.Layers.Insert(index, layer);
            foreach (var (c, i) in curves)
                adaptive.Curves.Insert(i, c);
            foreach (var t in tracks)
                t.Layer = layer.Id;
        });
    }

    public void RenameLayer(SongLayer layer, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Require(Adaptive.Layers, layer);
        Set("Rename Layer", layer.Name, name.Trim(), v => layer.Name = v, null);
    }

    public void SetLayerColor(SongLayer layer, string? color)
    {
        Require(Adaptive.Layers, layer);
        Set("Layer Color", layer.Color, color, v => layer.Color = v, null);
    }

    public void SetLayerFilter(SongLayer layer, MusicLayerFilter filter)
    {
        Require(Adaptive.Layers, layer);
        Set("Layer Filter", layer.Filter, filter, v => layer.Filter = v, null);
    }

    /// <summary>Puts a track in a layer (null: the implicit Main layer).</summary>
    public void SetTrackLayer(SongTrack track, string? layerId)
    {
        IndexOf(track);
        if (layerId is not null && Adaptive.FindLayer(layerId) is null)
            throw new ArgumentException($"The song has no layer '{layerId}'.", nameof(layerId));
        Set("Track Layer", track.Layer, layerId, v => track.Layer = v, null);
    }

    // --- parameters -------------------------------------------------------------------------------

    /// <summary>Adds a parameter (a copy of <paramref name="parameter"/>; the name is made unique).</summary>
    public MusicParameterInfo AddParameter(MusicParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var adaptive = Adaptive;
        var added = parameter.Clone();
        var name = string.IsNullOrWhiteSpace(added.Name) ? "parameter" : added.Name.Trim();
        added.Name = adaptive.FindParameter(name) is null ? name : SongAdaptive.NextId(name, n => adaptive.FindParameter(n) is not null);
        Commit("Add Parameter", () => adaptive.Parameters.Add(added), () => adaptive.Parameters.Remove(added));
        return added;
    }

    public void RemoveParameter(MusicParameterInfo parameter)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Parameters, parameter);
        Commit("Remove Parameter", () => adaptive.Parameters.Remove(parameter), () => adaptive.Parameters.Insert(index, parameter));
    }

    /// <summary>Edits a parameter's settings (name, kind, range, labels, default, seek speed) as one entry.</summary>
    public void EditParameter(MusicParameterInfo parameter, Action<MusicParameterInfo> edit, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Require(Adaptive.Parameters, parameter);
        var old = parameter.Clone();
        var value = parameter.Clone();
        edit(value);
        value.Name = string.IsNullOrWhiteSpace(value.Name) ? old.Name : value.Name.Trim();
        value.SeekSpeed = float.IsFinite(value.SeekSpeed) ? Math.Max(0, value.SeekSpeed) : 0;
        Commit("Edit Parameter", () => parameter.CopyFrom(value), () => parameter.CopyFrom(old), mergeKey);
    }

    /// <summary>Makes <paramref name="value"/> the parameter's default (Set as Default in the Adaptive panel).</summary>
    public void SetParameterDefault(MusicParameterInfo parameter, float value) =>
        EditParameter(parameter, p => p.Default = float.IsFinite(value) ? value : p.Default);

    // --- curves -----------------------------------------------------------------------------------

    /// <summary>Adds a curve (a copy of <paramref name="curve"/>).</summary>
    public MusicCurveInfo AddCurve(MusicCurveInfo curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var adaptive = Adaptive;
        var added = curve.Clone();
        added.Points.Sort(static (a, b) => a.X.CompareTo(b.X));
        Commit("Add Curve", () => adaptive.Curves.Add(added), () => adaptive.Curves.Remove(added));
        return added;
    }

    public void RemoveCurve(MusicCurveInfo curve)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Curves, curve);
        Commit("Remove Curve", () => adaptive.Curves.Remove(curve), () => adaptive.Curves.Insert(index, curve));
    }

    /// <summary>Replaces a curve's points (sorted by x; non-finite points dropped). Point drags pass a mergeKey.</summary>
    public void SetCurvePoints(MusicCurveInfo curve, IEnumerable<MusicCurvePoint> points, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        Require(Adaptive.Curves, curve);
        var old = curve.Points.ToArray();
        var value = points.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).OrderBy(p => p.X).ToArray();
        if (old.AsSpan().SequenceEqual(value))
            return;
        Commit("Edit Curve", () => curve.Points = [.. value], () => curve.Points = [.. old], mergeKey);
    }

    public void SetCurveShape(MusicCurveInfo curve, MusicCurveShape shape)
    {
        Require(Adaptive.Curves, curve);
        Set("Curve Shape", curve.Shape, shape, v => curve.Shape = v, null);
    }

    // --- transitions ------------------------------------------------------------------------------

    /// <summary>Adds a transition (a copy of <paramref name="transition"/>) at <paramref name="index"/> (−1 = last, lowest priority).</summary>
    public MusicTransitionInfo AddTransition(MusicTransitionInfo transition, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var adaptive = Adaptive;
        var added = transition.Clone();
        var at = index < 0 || index > adaptive.Transitions.Count ? adaptive.Transitions.Count : index;
        Commit("Add Transition", () => adaptive.Transitions.Insert(at, added), () => adaptive.Transitions.Remove(added));
        return added;
    }

    public void RemoveTransition(MusicTransitionInfo transition)
    {
        var adaptive = Adaptive;
        var index = Require(adaptive.Transitions, transition);
        Commit("Remove Transition", () => adaptive.Transitions.Remove(transition), () => adaptive.Transitions.Insert(index, transition));
    }

    /// <summary>Moves a transition to <paramref name="newIndex"/> (its priority: earlier wins).</summary>
    public void MoveTransition(MusicTransitionInfo transition, int newIndex)
    {
        var list = Adaptive.Transitions;
        var old = Require(list, transition);
        newIndex = Math.Clamp(newIndex, 0, list.Count - 1);
        if (old == newIndex)
            return;
        Commit("Reorder Transition", () => Reinsert(list, transition, newIndex), () => Reinsert(list, transition, old));
    }

    /// <summary>Edits a transition's settings as one entry.</summary>
    public void EditTransition(MusicTransitionInfo transition, Action<MusicTransitionInfo> edit, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Require(Adaptive.Transitions, transition);
        var old = transition.Clone();
        var value = transition.Clone();
        edit(value);
        if (value.Every is { } every)
            value.Every = Math.Max(1, every);
        Commit("Edit Transition", () => transition.CopyFrom(value), () => transition.CopyFrom(old), mergeKey);
    }

    // --- helpers ----------------------------------------------------------------------------------

    private (long Start, long End) SnapRange(long start, long end)
    {
        if (end < start)
            (start, end) = (end, start);
        var s = SnapToBar(start);
        var e = Math.Max(s + Song.TicksPerBar, SnapToBar(end));
        return (s, e);
    }

    private static int Require<T>(List<T> list, T item)
    {
        var index = list.IndexOf(item);
        return index >= 0 ? index : throw new ArgumentException("It is not in this song.", nameof(item));
    }

    private static void Reinsert<T>(List<T> list, T item, int index)
    {
        list.Remove(item);
        list.Insert(index, item);
    }
}

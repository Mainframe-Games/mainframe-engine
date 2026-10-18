using System.Globalization;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>How bad an <see cref="AdaptiveProblem"/> is.</summary>
public enum AdaptiveProblemSeverity
{
    /// <summary>Plays, with the problem part ignored or approximated.</summary>
    Warning,

    /// <summary>The song cannot be baked until it is fixed.</summary>
    Error,
}

/// <summary>A problem in a song's adaptive block, with the id of the section, layer, parameter or transition it is about.</summary>
public readonly record struct AdaptiveProblem(AdaptiveProblemSeverity Severity, string Message, string? Subject = null);

/// <summary>
/// Checks a song's adaptive block (the Adaptive panel's problem list; errors refuse a bake). Errors: a section that is not
/// whole bars, overlapping sections, a missing or unplayable start, more layers than <see cref="MusicPlan.MaxLayers"/>.
/// Warnings: notes that cross a section boundary, unreachable sections, shadowed rules, long stingers, master inserts,
/// and references to ids or labels that do not exist (kept in the file, ignored when playing).
/// </summary>
public static class SongAdaptiveValidator
{
    /// <summary>Stingers longer than this many bars get a warning.</summary>
    public const int LongStingerBars = 4;

    public static IReadOnlyList<AdaptiveProblem> Validate(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var problems = new List<AdaptiveProblem>();
        if (song.Adaptive is not { } a)
            return problems;

        Sections(song, a, problems);
        Start(a, problems);
        Layers(song, a, problems);
        Parameters(a, problems);
        Curves(a, problems);
        Transitions(a, problems);
        Reachability(a, problems);
        CrossingNotes(song, a, problems);
        if (song.Master.Inserts.Exists(i => !i.Bypass))
            problems.Add(new(AdaptiveProblemSeverity.Warning, "Master inserts are not baked: adaptive stems are mixed in the game, so use the Music bus's effects."));
        return problems;
    }

    /// <summary>True when the song can be baked (no <see cref="AdaptiveProblemSeverity.Error"/>).</summary>
    public static bool CanBake(IReadOnlyList<AdaptiveProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        foreach (var p in problems)
        {
            if (p.Severity == AdaptiveProblemSeverity.Error)
                return false;
        }

        return true;
    }

    private static void Sections(Song song, SongAdaptive a, List<AdaptiveProblem> problems)
    {
        var bar = song.TicksPerBar;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in a.Sections)
        {
            if (string.IsNullOrEmpty(s.Id) || !ids.Add(s.Id))
                problems.Add(new(AdaptiveProblemSeverity.Error, $"Section '{s.Name}' needs a unique id.", s.Id));
            if (s.End <= s.Start || s.Start < 0)
                problems.Add(new(AdaptiveProblemSeverity.Error, $"Section '{s.Name}' is empty.", s.Id));
            else if (s.Start % bar != 0 || s.End % bar != 0)
                problems.Add(new(AdaptiveProblemSeverity.Error, $"Section '{s.Name}' does not start and end on bar lines.", s.Id));
            if (s.Kind == MusicSectionKind.Stinger && s.End > s.Start && (s.End - s.Start) / bar > LongStingerBars)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Stinger '{s.Name}' is longer than {LongStingerBars} bars.", s.Id));
            if (s.Then is { Length: > 0 } then && !s.Loop && a.FindSection(then) is not { Kind: not MusicSectionKind.Stinger })
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Section '{s.Name}' continues to '{then}', which is not a playable section; the music ends there.", s.Id));
        }

        var ordered = a.Sections.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
                problems.Add(new(AdaptiveProblemSeverity.Error, $"Sections '{ordered[i - 1].Name}' and '{ordered[i].Name}' overlap.", ordered[i].Id));
        }

        foreach (var m in a.Markers)
        {
            if (a.SectionAt(m.Tick) is null)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Marker '{m.Name}' is outside every section; it is ignored.", m.Id));
        }
    }

    private static void Start(SongAdaptive a, List<AdaptiveProblem> problems)
    {
        if (a.FindSection(a.Start) is not { Kind: MusicSectionKind.Section })
            problems.Add(new(AdaptiveProblemSeverity.Error, a.Start is null ? "No start section is set." : $"The start section '{a.Start}' is not a playable section.", a.Start));
    }

    private static void Layers(Song song, SongAdaptive a, List<AdaptiveProblem> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in a.Layers)
        {
            if (string.IsNullOrEmpty(l.Id) || l.Id == SongLayer.MainId || !ids.Add(l.Id))
                problems.Add(new(AdaptiveProblemSeverity.Error, $"Layer '{l.Name}' needs a unique id other than '{SongLayer.MainId}'.", l.Id));
        }

        var count = a.Layers.Count + (SongAdaptiveCompiler.NeedsMainLayer(song) ? 1 : 0);
        if (count > MusicPlan.MaxLayers)
            problems.Add(new(AdaptiveProblemSeverity.Error, $"{count} layers (with Main); the most is {MusicPlan.MaxLayers}."));
        foreach (var t in song.Tracks)
        {
            if (t.Layer is { } id && a.FindLayer(id) is null)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Track '{t.Name}' names layer '{id}', which does not exist; it bakes into Main.", t.Id));
        }
    }

    private static void Parameters(SongAdaptive a, List<AdaptiveProblem> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in a.Parameters)
        {
            if (string.IsNullOrEmpty(p.Name) || !names.Add(p.Name))
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Parameter '{p.Name}' needs a unique name; the first is used.", p.Name));
            if (p.Kind == MusicParameterKind.Labeled && p.Labels is not { Count: > 0 })
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Labeled parameter '{p.Name}' has no labels.", p.Name));
        }
    }

    private static void Curves(SongAdaptive a, List<AdaptiveProblem> problems)
    {
        foreach (var c in a.Curves)
        {
            var layer = a.FindLayer(c.Layer);
            if (layer is null)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"A curve names layer '{c.Layer}', which does not exist; it is ignored.", c.Layer));
            else if (c.Target == MusicCurveTarget.Cutoff && layer.Filter == MusicLayerFilter.None)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"A cutoff curve on layer '{layer.Name}', which has no filter; it is ignored.", c.Layer));
            if (a.FindParameter(c.Parameter) is null)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"A curve names parameter '{c.Parameter}', which does not exist; it is ignored.", c.Parameter));
            if (c.Points.Count == 0)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"A curve on layer '{c.Layer}' has no points; it is ignored.", c.Layer));
        }
    }

    private static void Transitions(SongAdaptive a, List<AdaptiveProblem> problems)
    {
        for (var i = 0; i < a.Transitions.Count; i++)
        {
            var t = a.Transitions[i];
            var label = $"Transition {(i + 1).ToString(CultureInfo.InvariantCulture)}";
            foreach (var from in t.From)
            {
                if (from != MusicPlan.AnySection && a.FindSection(from) is null)
                    problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label} comes from '{from}', which does not exist.", from));
            }

            if (a.FindSection(t.To) is not { Kind: MusicSectionKind.Section })
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label} goes to '{t.To}', which is not a playable section; it is ignored.", t.To));
            if (t.Segment is { } segment && a.FindSection(segment) is not { Kind: MusicSectionKind.Transition })
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label}: '{segment}' is not a transition section; the segment is ignored.", segment));
            if (t.Stinger is { } stinger && a.FindSection(stinger) is not { Kind: MusicSectionKind.Stinger })
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label}: '{stinger}' is not a stinger; it is ignored.", stinger));
            foreach (var c in Comparisons(t.When))
            {
                var p = a.FindParameter(c.Parameter);
                if (p is null)
                    problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label} tests parameter '{c.Parameter}', which does not exist; it never fires.", c.Parameter));
                else if (c.Value.Label is { } text && (p.Kind != MusicParameterKind.Labeled || p.Labels?.Contains(text) != true))
                    problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label} tests '{p.Name}' against '{text}', which is not one of its labels; it never fires.", p.Name));
            }

            if (t.When is not null && Shadowing(a, i) is { } earlier)
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"{label} never fires: transition {(earlier + 1).ToString(CultureInfo.InvariantCulture)} matches first.", t.To));
        }
    }

    // An earlier automatic rule with the same condition from every one of this rule's sources.
    private static int? Shadowing(SongAdaptive a, int index)
    {
        var t = a.Transitions[index];
        for (var i = 0; i < index; i++)
        {
            var e = a.Transitions[i];
            if (e.When is null || !SameCondition(e.When, t.When))
                continue;
            if (e.From.Contains(MusicPlan.AnySection) || (!t.From.Contains(MusicPlan.AnySection) && t.From.TrueForAll(e.From.Contains)))
                return i;
        }

        return null;
    }

    private static bool SameCondition(MusicConditionGroup a, MusicConditionGroup? b) =>
        b is not null && SameList(a.All, b.All) && SameList(a.Any, b.Any);

    private static bool SameList(List<MusicComparison>? a, List<MusicComparison>? b)
    {
        if ((a?.Count ?? 0) != (b?.Count ?? 0))
            return false;
        for (var i = 0; i < (a?.Count ?? 0); i++)
        {
            var (x, y) = (a![i], b![i]);
            if (!string.Equals(x.Parameter, y.Parameter, StringComparison.Ordinal) || x.Op != y.Op || x.Value != y.Value)
                return false;
        }

        return true;
    }

    private static IEnumerable<MusicComparison> Comparisons(MusicConditionGroup? when) =>
        when is null ? [] : (when.All ?? []).Concat(when.Any ?? []);

    private static void Reachability(SongAdaptive a, List<AdaptiveProblem> problems)
    {
        if (a.FindSection(a.Start) is not { } start)
            return;
        var reached = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var queue = new Queue<SongSection>([start]);
        while (queue.TryDequeue(out var s))
        {
            void Visit(string? id)
            {
                if (a.FindSection(id) is { Kind: not MusicSectionKind.Stinger } next && reached.Add(next.Id))
                    queue.Enqueue(next);
            }

            if (!s.Loop)
                Visit(s.Then);
            foreach (var t in a.Transitions)
            {
                if (t.From.Contains(MusicPlan.AnySection) || t.From.Contains(s.Id))
                {
                    Visit(t.To);
                    Visit(t.Segment);
                }
            }
        }

        foreach (var s in a.Sections)
        {
            if (s.Kind != MusicSectionKind.Stinger && !reached.Contains(s.Id))
                problems.Add(new(AdaptiveProblemSeverity.Warning, $"Section '{s.Name}' is never reached from the start.", s.Id));
        }
    }

    private static void CrossingNotes(Song song, SongAdaptive a, List<AdaptiveProblem> problems)
    {
        var reported = new HashSet<(string, string)>();
        foreach (var track in song.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip is not MidiClip midi)
                    continue;
                foreach (var note in midi.Notes)
                {
                    var start = clip.Start + note.Start;
                    var end = Math.Min(start + note.Length, clip.End);
                    if (a.SectionAt(start) is { } s && end > s.End && reported.Add((track.Id, s.Id)))
                        problems.Add(new(AdaptiveProblemSeverity.Warning, $"{track.Name}: a note crosses the end of '{s.Name}' (it is cut there in the bake).", s.Id));
                }
            }
        }
    }
}

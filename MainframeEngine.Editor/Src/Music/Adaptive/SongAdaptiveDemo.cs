using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// A small adaptive song built through <see cref="SongDocument"/> actions (QA's <c>song adaptive-demo</c>, the
/// <c>music-adaptive</c> golden): Pad in Main, Drums in a Perc layer and Brass in a Combat layer with a low-pass; Intro →
/// Explore (looping, with an exit marker) and Combat (looping), a Hit stinger; <c>intensity</c> (seeking) and <c>area</c>
/// (Calm / Combat); the layers' volume curves over intensity; Explore → Combat when area is Combat at the next bar, and
/// back at Combat's end when it is Calm.
/// </summary>
public static class SongAdaptiveDemo
{
    public static void Build(SongDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var song = document.Song;
        var bar = song.TicksPerBar;
        var beat = song.Ppq;
        document.EnableAdaptive();

        var pad = song.Tracks.Count > 0 && song.Tracks[0].Kind == SongTrackKind.Instrument ? song.Tracks[0] : document.AddTrack(SongTrackKind.Instrument, "Pad");
        document.RenameTrack(pad, "Pad");
        var drums = document.AddTrack(SongTrackKind.Instrument, "Drums");
        var brass = document.AddTrack(SongTrackKind.Instrument, "Brass");

        // Pad: a chord tone per bar through all of it; Drums: Explore and Combat; Brass: Combat; the stinger's notes on Brass.
        Notes(document, pad, 0, 10 * bar, beat, [(57, 0, 2), (60, 2, 2), (64, 4, 4), (62, 8, 4), (57, 12, 4), (60, 16, 4), (64, 20, 4), (65, 24, 8), (64, 32, 8)]);
        Notes(document, drums, 2 * bar, 8 * bar, beat, Pattern(8, [(36, 0), (38, 2), (36, 2.5f), (38, 3)]));
        Notes(document, brass, 6 * bar, 5 * bar, beat, [(67, 0, 1), (70, 1, 1), (72, 2, 2), (74, 4, 2), (72, 6, 2), (70, 8, 4), (67, 12, 4), (79, 16, 1), (77, 17, 1), (79, 18, 2)]);

        var perc = document.AddLayer("Perc", "#f59e0b");
        var combat = document.AddLayer("Combat", "#f472b6");
        document.SetLayerFilter(combat, MusicLayerFilter.LowPass);
        document.SetTrackLayer(drums, perc.Id);
        document.SetTrackLayer(brass, combat.Id);

        var intro = document.AddSection("Intro", MusicSectionKind.Section, 0, 2 * bar);
        var explore = document.AddSection("Explore", MusicSectionKind.Section, 2 * bar, 6 * bar);
        var fight = document.AddSection("Combat", MusicSectionKind.Section, 6 * bar, 10 * bar);
        var hit = document.AddSection("Hit", MusicSectionKind.Stinger, 10 * bar, 11 * bar);
        document.SetSectionLoop(intro, false);
        document.SetSectionThen(intro, explore.Id);
        document.SetStingerTiming(hit, MusicQuantize.Beat, 0);
        document.SetStartSection(intro.Id);
        document.AddMarker("drop", 4 * bar, exit: true);

        document.AddParameter(new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Min = 0, Max = 1, SeekSpeed = 0.5f });
        document.AddParameter(new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] });
        document.AddCurve(new MusicCurveInfo
        {
            Layer = perc.Id,
            Target = MusicCurveTarget.Volume,
            Parameter = "intensity",
            Shape = MusicCurveShape.Smooth,
            Points = [new(0, -80), new(0.3f, -80), new(0.5f, 0), new(1, 0)],
        });
        document.AddCurve(new MusicCurveInfo
        {
            Layer = combat.Id,
            Target = MusicCurveTarget.Volume,
            Parameter = "intensity",
            Shape = MusicCurveShape.Smooth,
            Points = [new(0, -80), new(0.5f, -80), new(0.8f, 0), new(1, 0)],
        });
        document.AddCurve(new MusicCurveInfo
        {
            Layer = combat.Id,
            Target = MusicCurveTarget.Cutoff,
            Parameter = "intensity",
            Points = [new(0.5f, 800), new(1, 20000)],
        });
        document.AddTransition(new MusicTransitionInfo
        {
            From = [explore.Id],
            To = fight.Id,
            Quantize = MusicQuantize.Bar,
            FadeOutBeats = 1,
            When = Is("area", "Combat"),
        });
        document.AddTransition(new MusicTransitionInfo
        {
            From = [fight.Id],
            To = explore.Id,
            Quantize = MusicQuantize.SectionEnd,
            Stinger = hit.Id,
            When = Is("area", "Calm"),
        });
    }

    private static MusicConditionGroup Is(string parameter, string label) =>
        new() { All = [new MusicComparison { Parameter = parameter, Op = MusicCompare.Equal, Value = new MusicValue(label) }] };

    // (pitch, start beat, length in beats) → notes of a clip at start..start+length.
    private static void Notes(SongDocument document, SongTrack track, long start, long length, long beat, (int Pitch, float Start, float Beats)[] notes)
    {
        var clip = document.AddMidiClip(track, start, length);
        var list = new List<MidiNote>(notes.Length);
        foreach (var (pitch, at, beats) in notes)
            list.Add(new MidiNote { Pitch = pitch, Start = (long)(at * beat), Length = Math.Max(1, (long)(beats * beat) - beat / 8), Velocity = 96 });
        document.AddNotes(clip, list);
    }

    // A drum pattern of (pitch, beat in the bar) repeated over `bars` bars, each hit a sixteenth long.
    private static (int, float, float)[] Pattern(int bars, (int Pitch, float Beat)[] hits)
    {
        var notes = new List<(int, float, float)>();
        for (var b = 0; b < bars; b++)
        {
            foreach (var (pitch, at) in hits)
                notes.Add((pitch, b * 4 + at, 0.25f));
        }

        return [.. notes];
    }
}

namespace MainframeEngine.Editor.Music;

/// <summary>The song tab's snap setting: off, straight divisions of the whole note, or triplets.</summary>
public enum SongSnap
{
    Off,
    Quarter,
    Eighth,
    Sixteenth,
    ThirtySecond,
    QuarterTriplet,
    EighthTriplet,
    SixteenthTriplet,
}

/// <summary>Snap, quantize and position math of the song tab (ticks at the song's PPQ).</summary>
public static class SongGrid
{
    /// <summary>The snap choices in menu order.</summary>
    public static readonly SongSnap[] All =
    [
        SongSnap.Off, SongSnap.Quarter, SongSnap.Eighth, SongSnap.Sixteenth, SongSnap.ThirtySecond,
        SongSnap.QuarterTriplet, SongSnap.EighthTriplet, SongSnap.SixteenthTriplet,
    ];

    public static string Label(SongSnap snap) => snap switch
    {
        SongSnap.Off => "Off",
        SongSnap.Quarter => "1/4",
        SongSnap.Eighth => "1/8",
        SongSnap.Sixteenth => "1/16",
        SongSnap.ThirtySecond => "1/32",
        SongSnap.QuarterTriplet => "1/4 T",
        SongSnap.EighthTriplet => "1/8 T",
        SongSnap.SixteenthTriplet => "1/16 T",
        _ => "?",
    };

    /// <summary>The grid step in ticks (1 when snapping is off).</summary>
    public static long Step(SongSnap snap, int ppq) => Math.Max(1, snap switch
    {
        SongSnap.Quarter => ppq,
        SongSnap.Eighth => ppq / 2,
        SongSnap.Sixteenth => ppq / 4,
        SongSnap.ThirtySecond => ppq / 8,
        SongSnap.QuarterTriplet => ppq * 2 / 3,
        SongSnap.EighthTriplet => ppq / 3,
        SongSnap.SixteenthTriplet => ppq / 6,
        _ => 1,
    });

    /// <summary>The step nudges and pastes use: the snap, or a sixteenth when snapping is off.</summary>
    public static long NudgeStep(SongSnap snap, int ppq) => snap == SongSnap.Off ? Math.Max(1, ppq / 4) : Step(snap, ppq);

    /// <summary><paramref name="tick"/> rounded to the nearest multiple of <paramref name="step"/>.</summary>
    public static long Round(long tick, long step)
    {
        if (step <= 1)
            return tick;
        var down = Floor(tick, step);
        return tick - down >= (step + 1) / 2 ? down + step : down;
    }

    /// <summary><paramref name="tick"/> rounded down to a multiple of <paramref name="step"/> (also for negative ticks).</summary>
    public static long Floor(long tick, long step)
    {
        if (step <= 1)
            return tick;
        var r = tick % step;
        return r < 0 ? tick - r - step : tick - r;
    }

    /// <summary><paramref name="tick"/> rounded up to a multiple of <paramref name="step"/>.</summary>
    public static long Ceiling(long tick, long step) => step <= 1 ? tick : -Floor(-tick, step);

    /// <summary>Ticks of one beat (the time signature's unit).</summary>
    public static long TicksPerBeat(Song song) => (long)song.Ppq * 4 / Math.Max(1, song.TimeSignature.ElementAtOrDefault(1));

    /// <summary>Writes <paramref name="ticks"/> as bars.beats.ticks (<c>003.1.000</c>, 1-based) into <paramref name="destination"/>.</summary>
    public static int FormatPosition(Span<char> destination, double ticks, Song song)
    {
        var t = (long)Math.Max(0, ticks);
        var bar = song.TicksPerBar;
        var beat = TicksPerBeat(song);
        var bars = t / bar + 1;
        var beats = t % bar / beat + 1;
        var rest = t % beat;
        var n = 0;
        n += Pad(destination[n..], bars, 3);
        destination[n++] = '.';
        n += Pad(destination[n..], beats, 1);
        destination[n++] = '.';
        n += Pad(destination[n..], rest, 3);
        return n;
    }

    private static int Pad(Span<char> destination, long value, int digits)
    {
        value.TryFormat(destination, out var written, digits == 1 ? "0" : "000", System.Globalization.CultureInfo.InvariantCulture);
        return written;
    }

    /// <summary>The note name of a MIDI pitch (60 = C4).</summary>
    public static string NoteName(int pitch) => NoteNames[Math.Clamp(pitch, 0, 127)];

    private static readonly string[] NoteNames = BuildNoteNames();

    private static string[] BuildNoteNames()
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        var result = new string[128];
        for (var p = 0; p < 128; p++)
            result[p] = names[p % 12] + (p / 12 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    /// <summary>True for the black keys of the piano.</summary>
    public static bool IsBlackKey(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;
}

namespace MainframeEngine.Audio.Music;

/// <summary>
/// The beat grid of adaptive music at a sample rate: one tempo, one time signature. Beat <c>n</c> of a section is frame
/// <see cref="FrameOfBeat(long)"/>, computed from the beat number in <c>double</c> and rounded once, never accumulated,
/// so the editor's preview, the bake and the game agree on every frame however many loops have passed.
/// </summary>
/// <remarks>A beat is the signature's beat unit (an eighth note in 6/8); frames are counted from a section's start.</remarks>
public readonly record struct MusicGrid
{
    public MusicGrid(int sampleRate, double tempo, int beatsPerBar, int beatUnit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (!(tempo > 0) || !double.IsFinite(tempo))
            throw new ArgumentOutOfRangeException(nameof(tempo), tempo, "The tempo must be positive.");
        ArgumentOutOfRangeException.ThrowIfLessThan(beatsPerBar, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(beatUnit, 1);
        SampleRate = sampleRate;
        Tempo = tempo;
        BeatsPerBar = beatsPerBar;
        BeatUnit = beatUnit;
    }

    public int SampleRate { get; }

    /// <summary>Quarter notes per minute.</summary>
    public double Tempo { get; }

    public int BeatsPerBar { get; }

    /// <summary>The signature's note value (4 = quarter, 8 = eighth).</summary>
    public int BeatUnit { get; }

    /// <summary>Frames per beat (not an integer in general; use <see cref="FrameOfBeat(long)"/> for positions).</summary>
    public double FramesPerBeat => SampleRate * 240.0 / (Tempo * BeatUnit);

    /// <summary>The first frame of beat <paramref name="beat"/> (0-based) from a section's start.</summary>
    public long FrameOfBeat(long beat) => (long)Math.Round(beat * (SampleRate * 240.0) / (Tempo * BeatUnit), MidpointRounding.AwayFromZero);

    /// <summary>The frame of a fractional beat position (markers, fades, pickups).</summary>
    public long FrameOfBeat(double beats) => (long)Math.Round(beats * (SampleRate * 240.0) / (Tempo * BeatUnit), MidpointRounding.AwayFromZero);

    /// <summary>Frames for a duration in beats (never negative).</summary>
    public long FramesForBeats(double beats) => beats > 0 && double.IsFinite(beats) ? FrameOfBeat(beats) : 0;

    /// <summary>The length of <paramref name="bars"/> whole bars.</summary>
    public long LengthFrames(int bars) => FrameOfBeat((long)Math.Max(0, bars) * BeatsPerBar);

    /// <summary>The smallest beat that starts at or after <paramref name="frame"/>.</summary>
    public long BeatAtOrAfter(long frame)
    {
        if (frame <= 0)
            return 0;
        var beat = (long)Math.Floor(frame / FramesPerBeat);
        while (beat > 0 && FrameOfBeat(beat - 1) >= frame)
            beat--;
        while (FrameOfBeat(beat) < frame)
            beat++;
        return beat;
    }

    /// <summary>The beat playing at <paramref name="frame"/> (the largest beat that starts at or before it).</summary>
    public long BeatAt(long frame)
    {
        if (frame <= 0)
            return 0;
        var beat = BeatAtOrAfter(frame);
        return FrameOfBeat(beat) == frame ? beat : beat - 1;
    }

    /// <summary>
    /// The first boundary of <paramref name="quantize"/> at or after <paramref name="frame"/> inside a section of
    /// <paramref name="lengthFrames"/> (frames from its start), or −1 when <paramref name="frame"/> is past its end. The
    /// section's end is always a boundary. <paramref name="exitMarkers"/> are the section's exit marker frames, sorted.
    /// </summary>
    public long NextQuantum(long frame, long lengthFrames, MusicQuantize quantize, int every = 1, ReadOnlySpan<long> exitMarkers = default)
    {
        if (frame > lengthFrames)
            return -1;
        frame = Math.Max(0, frame);
        long boundary;
        switch (quantize)
        {
            case MusicQuantize.Immediate:
                return frame;
            case MusicQuantize.Beat:
                boundary = FrameOfBeat(BeatAtOrAfter(frame));
                break;
            case MusicQuantize.Bar:
            case MusicQuantize.Bars:
                var step = (long)BeatsPerBar * (quantize == MusicQuantize.Bars ? Math.Max(1, every) : 1);
                var beat = BeatAtOrAfter(frame);
                boundary = FrameOfBeat((beat + step - 1) / step * step);
                break;
            case MusicQuantize.ExitMarker:
                boundary = lengthFrames;
                foreach (var marker in exitMarkers)
                {
                    if (marker >= frame)
                    {
                        boundary = marker;
                        break;
                    }
                }

                break;
            default:
                boundary = lengthFrames;
                break;
        }

        return Math.Min(boundary, lengthFrames);
    }
}

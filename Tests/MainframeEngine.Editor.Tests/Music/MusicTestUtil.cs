using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>An instrument that records every event (block, offset, pitch, on/off) and every all-notes-off.</summary>
internal sealed class RecordingInstrument : IInstrument
{
    public readonly List<(long Block, int Offset, int Pitch, bool On)> Events = [];
    public long Block;
    public int AllNotesOffCalls;

    public int LatencyFrames => 0;

    public void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo)
    {
        foreach (var e in events)
            Events.Add((Block, e.Offset, e.Pitch, e.On));
        Block++;
    }

    public void AllNotesOff() => AllNotesOffCalls++;
}

internal static class MusicTestUtil
{
    public const int Rate = 48000;

    /// <summary>120 BPM at 960 PPQ and 48 kHz: exactly 25 frames per tick.</summary>
    public const int FramesPerTick = 25;

    public static Song NewSong() => new() { Uid = "sng_000000000001", Tempo = 120 };

    public static SongTrack AddInstrumentTrack(Song song, string id, InstrumentDescriptor? instrument = null)
    {
        var track = new SongTrack { Id = id, Name = id, Kind = SongTrackKind.Instrument, Instrument = instrument };
        song.Tracks.Add(track);
        return track;
    }

    public static MidiClip AddClip(SongTrack track, long start, long length, params (int Pitch, long Start, long Length)[] notes)
    {
        var clip = new MidiClip { Start = start, Length = length };
        foreach (var (pitch, s, l) in notes)
            clip.Notes.Add(new MidiNote { Pitch = pitch, Start = s, Length = l, Velocity = 127 });
        track.Clips.Add(clip);
        return clip;
    }

    /// <summary>A constant (DC) mono sound as an in-memory stream, for exact mixer levels.</summary>
    public static AudioStream Constant(float value, float seconds = 2f)
    {
        var samples = new float[(int)(seconds * Rate)];
        Array.Fill(samples, value);
        return AudioStream.FromSamples(samples, 1, Rate, $"dc {value}");
    }

    /// <summary>Runs <paramref name="blocks"/> blocks and returns the concatenated output.</summary>
    public static float[] Run(SongEngine engine, int blocks)
    {
        var output = new float[blocks * SongEngine.BlockFrames * 2];
        for (var b = 0; b < blocks; b++)
            engine.Process(output.AsSpan(b * SongEngine.BlockFrames * 2, SongEngine.BlockFrames * 2));
        return output;
    }

    public static float Energy(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return (float)sum;
    }
}

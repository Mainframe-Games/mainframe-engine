using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// Turns a song's adaptive block into the engine's <see cref="MusicManifest"/> (frames at a sample rate, no stems): what
/// the live preview compiles into a <see cref="MusicPlan"/> and what the bake fills with stems. Ticks become frames
/// through <see cref="MusicGrid"/>, the same rounding the game uses.
/// </summary>
public static class SongAdaptiveCompiler
{
    /// <summary>The song's beat grid at <paramref name="sampleRate"/>.</summary>
    public static MusicGrid GridOf(Song song, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(song);
        var signature = song.TimeSignature is { Length: 2 } ts && ts[0] > 0 && ts[1] > 0 ? ts : [4, 4];
        return new MusicGrid(sampleRate, song.Tempo > 0 ? song.Tempo : 120, signature[0], signature[1]);
    }

    /// <summary>Ticks per beat of the time signature's beat unit.</summary>
    public static double TicksPerBeat(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var unit = song.TimeSignature is { Length: 2 } ts && ts[1] > 0 ? ts[1] : 4;
        return song.Ppq * 4.0 / unit;
    }

    /// <summary>
    /// The manifest for <paramref name="song"/>'s adaptive block at <paramref name="sampleRate"/> (empty when the song
    /// has none). Tracks without a known layer make the implicit Main layer (first). Sections that are not whole bars
    /// are rounded to the nearest bar (validation reports them).
    /// </summary>
    public static MusicManifest ToManifest(Song song, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(song);
        var grid = GridOf(song, sampleRate);
        var manifest = new MusicManifest
        {
            Song = string.IsNullOrEmpty(song.Uid) ? null : song.Uid,
            SampleRate = sampleRate,
            Tempo = grid.Tempo,
            TimeSignature = [grid.BeatsPerBar, grid.BeatUnit],
        };
        if (song.Adaptive is not { } adaptive)
            return manifest;

        manifest.Start = adaptive.Start;
        manifest.Parameters = adaptive.Parameters.ConvertAll(p => p.Clone());
        manifest.Curves = adaptive.Curves.ConvertAll(c => c.Clone());
        manifest.Transitions = adaptive.Transitions.ConvertAll(t => t.Clone());

        if (NeedsMainLayer(song))
            manifest.Layers.Add(new MusicManifestLayer { Id = SongLayer.MainId, Name = "Main" });
        foreach (var layer in adaptive.Layers)
            manifest.Layers.Add(new MusicManifestLayer { Id = layer.Id, Name = layer.Name, Filter = layer.Filter });

        var ticksPerBar = (double)song.TicksPerBar;
        var ticksPerBeat = TicksPerBeat(song);
        foreach (var s in adaptive.Sections)
        {
            var bars = (int)Math.Round((s.End - s.Start) / ticksPerBar);
            var section = new MusicManifestSection
            {
                Id = s.Id,
                Name = s.Name,
                Kind = s.Kind,
                Bars = bars,
                LengthFrames = grid.LengthFrames(bars),
                Loop = s.Loop,
                Then = s.Then,
                Quantize = s.Quantize,
                PickupBeats = s.PickupBeats,
            };
            foreach (var m in adaptive.Markers)
            {
                if (m.Tick >= s.Start && m.Tick < s.End)
                    section.Markers.Add(new MusicManifestMarker { Name = m.Name, Frame = grid.FrameOfBeat((m.Tick - s.Start) / ticksPerBeat), Exit = m.Exit });
            }

            section.Markers.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
            manifest.Sections.Add(section);
        }

        return manifest;
    }

    /// <summary>Compiles the song's adaptive block for playback at <paramref name="sampleRate"/>.</summary>
    public static MusicPlan Compile(Song song, int sampleRate) => MusicPlan.Compile(ToManifest(song, sampleRate));

    /// <summary>The layer a track bakes into: its own when the song has it, else the implicit Main layer.</summary>
    public static string LayerOf(Song song, SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(track);
        return track.Layer is { } id && song.Adaptive?.FindLayer(id) is not null ? id : SongLayer.MainId;
    }

    /// <summary>Some track bakes into the implicit Main layer.</summary>
    public static bool NeedsMainLayer(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return song.Tracks.Exists(t => LayerOf(song, t) == SongLayer.MainId);
    }
}

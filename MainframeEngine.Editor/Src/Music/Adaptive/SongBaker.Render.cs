using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>A stem rendered in memory (interleaved stereo at the bake's rate).</summary>
/// <param name="Body">Exactly the section's <see cref="MusicManifestSection.LengthFrames"/> (a stinger: its length plus its tail).</param>
/// <param name="Tail">What rings on past the section's end, trimmed at −90 dBFS with a 10 ms fade; null when silent.</param>
public sealed record SongStemAudio(float[] Body, float[]? Tail)
{
    public long BodyFrames => Body.Length / 2;

    public long TailFrames => Tail is null ? 0 : Tail.Length / 2;
}

public static partial class SongBaker
{
    /// <summary>Tail samples at or below this level (−90 dBFS) are trimmed.</summary>
    public const float SilenceThreshold = 3.1622776e-5f;

    /// <summary>The fade at the end of a trimmed tail.</summary>
    public const double TailFadeSeconds = 0.01;

    /// <summary>
    /// Renders one section of <paramref name="song"/> in isolation, the way the bake does: only the tracks of layer
    /// <paramref name="layerId"/> (null: every layer, the section's mix), from a clean start at the section's downbeat,
    /// notes starting inside the section only, held notes cut at its end, master volume without master inserts, plugin
    /// latency dropped. A stinger's body includes its tail. For tests and the bake; on the calling thread.
    /// </summary>
    public static SongStemAudio RenderStem(Song song, string sectionId, string? layerId, SongBakeOptions? options = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        var adaptive = song.Adaptive ?? throw new InvalidOperationException("The song has no adaptive block.");
        var section = adaptive.FindSection(sectionId) ?? throw new ArgumentException($"No section '{sectionId}'.", nameof(sectionId));
        options ??= new SongBakeOptions();
        var rate = RateOf(song);
        var grid = SongAdaptiveCompiler.GridOf(song, rate);
        var length = grid.LengthFrames((int)Math.Round(section.Length / (double)song.TicksPerBar));
        var isolated = Isolate(song, section.Start, section.End,
            t => layerId is null || string.Equals(SongAdaptiveCompiler.LayerOf(song, t), layerId, StringComparison.Ordinal));
        using var rack = options.Plugins?.Invoke(rate);
        var samples = RenderRange(isolated, rate, length + TailFramesOf(song, rate), options, rack, cancellation, out var problems);
        if (problems.Count > 0)
            throw new SongBakeException($"Section '{section.Name}' cannot be rendered.", problems);
        return Split(samples, length, rate, section.Kind == MusicSectionKind.Stinger);
    }

    internal static int RateOf(Song song) => Math.Clamp(song.Render.SampleRate, 8000, 192000);

    internal static long TailFramesOf(Song song, int rate) =>
        (long)Math.Round(Math.Clamp(song.Adaptive?.TailSeconds ?? 0, 0, MaxTailSeconds) * rate);

    /// <summary>The longest tail rendered past a section's end.</summary>
    public const double MaxTailSeconds = 60;

    // Body [0, cut) and the trimmed tail after it; a stinger keeps both in one stem.
    private static SongStemAudio Split(float[] samples, long cut, int rate, bool oneStem)
    {
        var tail = samples.AsSpan((int)(cut * 2));
        var tailFrames = TrimTail(tail, rate);
        if (oneStem)
            return new SongStemAudio(samples[..(int)((cut + tailFrames) * 2)], null);
        var body = samples[..(int)(cut * 2)];
        return new SongStemAudio(body, tailFrames == 0 ? null : tail[..(int)(tailFrames * 2)].ToArray());
    }

    /// <summary>
    /// The frames of <paramref name="tail"/> worth keeping: up to the last frame above <see cref="SilenceThreshold"/> plus
    /// <see cref="TailFadeSeconds"/>, the last of which fade linearly to silence (applied in place). 0 when all silent.
    /// </summary>
    internal static long TrimTail(Span<float> tail, int rate)
    {
        var frames = tail.Length / 2;
        var last = -1;
        for (var f = frames - 1; f >= 0; f--)
        {
            if (Math.Abs(tail[2 * f]) > SilenceThreshold || Math.Abs(tail[2 * f + 1]) > SilenceThreshold)
            {
                last = f;
                break;
            }
        }

        if (last < 0)
            return 0;
        var fade = Math.Max(1, (int)Math.Round(TailFadeSeconds * rate));
        var length = Math.Min(frames, last + 1 + fade);
        var fadeStart = Math.Max(0, length - fade);
        var fadeFrames = length - fadeStart;
        for (var f = fadeStart; f < length; f++)
        {
            var gain = (float)(length - f - 1) / fadeFrames;
            tail[2 * f] *= gain;
            tail[2 * f + 1] *= gain;
        }

        return length;
    }

    /// <summary>
    /// A copy of <paramref name="song"/> for one stem: the unmuted tracks <paramref name="include"/> accepts (solo
    /// ignored), with the notes that start in <c>[start, cut)</c> cut at <paramref name="cut"/> and audio clips sliced to
    /// that range, everything moved so <paramref name="start"/> is tick 0; master volume, no master inserts, no loop.
    /// Tracks with nothing in the range are left out (an empty result bakes nothing).
    /// </summary>
    internal static Song Isolate(Song song, long start, long cut, Func<SongTrack, bool> include)
    {
        var result = new Song
        {
            Uid = "",
            Tempo = song.Tempo,
            TimeSignature = [.. song.TimeSignature],
            Ppq = song.Ppq,
            Loop = new SongLoop { Enabled = false, Start = 0, End = 0 },
            Master = new SongMaster { VolumeDb = song.Master.VolumeDb },
        };
        foreach (var track in song.Tracks)
        {
            if (track.Mute || !include(track))
                continue;
            var copy = new SongTrack
            {
                Id = track.Id,
                Name = track.Name,
                Kind = track.Kind,
                VolumeDb = track.VolumeDb,
                Pan = track.Pan,
                Instrument = track.Instrument,
                Inserts = track.Inserts,
            };
            if (track.Kind == SongTrackKind.Instrument)
            {
                var notes = new MidiClip { Start = 0, Length = cut - start };
                foreach (var clip in track.Clips)
                {
                    if (clip is not MidiClip midi)
                        continue;
                    foreach (var note in midi.Notes)
                    {
                        // The engine's rule for notes outside their clip (SongSnapshotBuilder): not played.
                        if (note.Start < 0 || note.Start >= midi.Length || note.Length <= 0 || (uint)note.Pitch > 127)
                            continue;
                        var at = midi.Start + note.Start;
                        if (at < start || at >= cut)
                            continue;
                        var end = Math.Min(midi.Start + Math.Min(note.Start + note.Length, midi.Length), cut);
                        notes.Notes.Add(new MidiNote { Pitch = note.Pitch, Velocity = note.Velocity, Start = at - start, Length = end - at });
                    }
                }

                if (notes.Notes.Count > 0)
                    copy.Clips.Add(notes);
            }
            else
            {
                foreach (var clip in track.Clips)
                {
                    if (clip is not AudioClip audio)
                        continue;
                    var from = Math.Max(audio.Start, start);
                    var to = Math.Min(audio.End, cut);
                    if (to <= from)
                        continue;
                    copy.Clips.Add(new AudioClip
                    {
                        File = audio.File,
                        GainDb = audio.GainDb,
                        Start = from - start,
                        Length = to - from,
                        Offset = audio.Offset + (from - audio.Start),
                    });
                }
            }

            if (copy.Clips.Count > 0)
                result.Tracks.Add(copy);
        }

        return result;
    }

    /// <summary>A track that needs the plugin helper (a plugin instrument, or an active plugin insert).</summary>
    internal static bool UsesPlugins(SongTrack track) =>
        track.Instrument?.Plugin is not null || track.Inserts.Exists(i => i.Builtin is null && i.Plugin is not null && !i.Bypass);

    /// <summary>
    /// Renders <paramref name="frames"/> frames of an isolated song from its tick 0 with a fresh engine and fresh
    /// instruments. The chain's plugin-delay-compensation latency is rendered first and dropped, so frame 0 is the
    /// downbeat. <paramref name="problems"/> lists tracks whose plugins could not play (the result is then unusable).
    /// </summary>
    internal static float[] RenderRange(Song isolated, int rate, long frames, SongBakeOptions options, PluginRack? rack,
        CancellationToken cancellation, out List<string> problems)
    {
        problems = [];
        if (frames * 2 > Array.MaxLength)
            throw new InvalidOperationException("The section is too long to bake.");
        var builder = new SongSnapshotBuilder(rate, options.InstrumentFactory, options.ClipLoader, rack);
        try
        {
            // Built-in synths start building their wavetables on the first build: wait for them, then build again.
            var snapshot = builder.Build(isolated);
            WavetableCache.WaitForPending();
            snapshot = builder.Build(isolated);
            foreach (var track in snapshot.TrackList)
            {
                if (track.Status is not { } status || isolated.FindTrack(track.Id) is not { } source)
                    continue;
                if (UsesPlugins(source))
                    problems.Add($"{source.Name}: {status}");
                else
                    Log.Warning($"[Music] Bake: track {source.Name}: {status}");
            }

            if (problems.Count > 0)
                return [];

            var latency = Math.Min(snapshot.MaxTrackLatency, SongEngine.MaxCompensationFrames);
            var engine = new SongEngine(rate);
            engine.SetSnapshot(snapshot);
            engine.SeekFrames(0);
            engine.Play();
            var result = new float[frames * 2];
            var block = new float[SongEngine.BlockFrames * 2];
            var total = latency + frames;
            for (long done = 0; done < total;)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = (int)Math.Min(SongEngine.BlockFrames, total - done);
                var span = block.AsSpan(0, count * 2);
                engine.Process(span);
                if (rack is { Xruns: > 0 })
                    throw new PluginHostException($"A plugin did not finish a block within {PluginRack.OfflineBlockTimeout.TotalSeconds:0} s; the bake stopped.");
                // Keep the frames from `latency` on: the compensated chain's downbeat.
                var keepFrom = Math.Max(done, latency);
                if (done + count > keepFrom)
                    span[(int)((keepFrom - done) * 2)..].CopyTo(result.AsSpan((int)((keepFrom - latency) * 2)));
                done += count;
            }

            return result;
        }
        finally
        {
            builder.ReleasePlugins();
        }
    }
}

using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The adaptive part of a <see cref="SongSnapshot"/> (songs with sections): the compiled <see cref="MusicPlan"/> at the
/// engine's rate, each section's range in song frames, each track slot's layer, and a fresh <see cref="MusicDirector"/>
/// for the render thread. Built on the UI thread with the snapshot; when it replaces the playing one, the engine moves the
/// playing state (section, pass, parameters) to the new director, so edits apply without stopping the music.
/// </summary>
public sealed class AdaptiveSnapshot
{
    internal AdaptiveSnapshot(MusicPlan plan, long[] sectionStarts, long[] sectionEnds, int[] slotLayers)
    {
        Plan = plan;
        SectionStarts = sectionStarts;
        SectionEnds = sectionEnds;
        SlotLayers = slotLayers;
        Director = new MusicDirector(plan);
    }

    public MusicPlan Plan { get; }

    /// <summary>The render thread's director (it plays this snapshot's plan; nothing else touches it).</summary>
    internal MusicDirector Director { get; }

    /// <summary>Each plan section's first song frame (by plan index).</summary>
    public long[] SectionStarts { get; }

    /// <summary>Each plan section's end in song frames (notes starting there belong to the next section).</summary>
    public long[] SectionEnds { get; }

    /// <summary>The plan layer of each engine track slot (by <see cref="TrackSnapshot.Slot"/>).</summary>
    public int[] SlotLayers { get; }

    /// <summary>Some section can be played (Shift+Space has something to start).</summary>
    public bool CanPlay => Array.Exists(Plan.Sections, s => s.Kind == MusicSectionKind.Section);

    /// <summary>Builds the adaptive part of <paramref name="song"/> at <paramref name="sampleRate"/>, or null without sections.</summary>
    internal static AdaptiveSnapshot? Build(Song song, int sampleRate, Func<double, long> toFrames, Func<SongTrack, int> slotOf)
    {
        if (song.Adaptive is not { Sections.Count: > 0 } adaptive)
            return null;
        var plan = SongAdaptiveCompiler.Compile(song, sampleRate);
        var starts = new long[plan.Sections.Length];
        var ends = new long[plan.Sections.Length];
        foreach (var section in adaptive.Sections)
        {
            var index = plan.FindSection(section.Id);
            if (index < 0)
                continue;
            starts[index] = toFrames(section.Start);
            ends[index] = toFrames(section.End);
        }

        var layers = new int[SongSnapshotBuilder.MaxTracks];
        var last = Math.Max(0, plan.Layers.Length - 1);
        foreach (var track in song.Tracks)
        {
            var slot = slotOf(track);
            if ((uint)slot < (uint)layers.Length)
                layers[slot] = Math.Clamp(plan.FindLayer(SongAdaptiveCompiler.LayerOf(song, track)), 0, last);
        }

        return new AdaptiveSnapshot(plan, starts, ends, layers);
    }
}

/// <summary>What adaptive playback is doing, as the render thread last published it (UI thread: the Adaptive panel, the lane).</summary>
/// <param name="Playing">Adaptive playback runs.</param>
/// <param name="Section">The plan section playing, or −1.</param>
/// <param name="Pending">The destination of a scheduled transition, or −1.</param>
/// <param name="PendingFrames">Music frames until the scheduled transition (or stop), or −1.</param>
/// <param name="LocalFrame">Frames into the current pass of <see cref="Section"/>.</param>
/// <param name="Stinger">The stinger playing over the music, or −1.</param>
public readonly record struct AdaptiveState(bool Playing, int Section, int Pending, long PendingFrames, long LocalFrame, int Stinger);

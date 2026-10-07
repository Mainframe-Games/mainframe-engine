namespace MainframeEngine.Editor.Music;

/// <summary>
/// An open song: the <see cref="Music.Song"/>, its file and its own undo history (like <see cref="EditedResource"/>).
/// Every edit goes through a method here and is one undoable action; objects (tracks, clips, notes) keep their identity
/// through undo and redo, so the UI may hold references. Continuous edits (drags, sliders) pass the same
/// <c>mergeKey</c> for every step and become one history entry (call <see cref="UndoRedo.EndMerge"/> on release, as the
/// inspector does).
/// </summary>
public sealed class SongDocument : IDisposable
{
    public const int MinPitch = 0;
    public const int MaxPitch = 127;

    private bool _disposed;

    public SongDocument(Song song, string filePath, bool readOnly = false, string? readOnlyNotice = null)
    {
        Song = song ?? throw new ArgumentNullException(nameof(song));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        IsReadOnly = readOnly;
        ReadOnlyNotice = readOnlyNotice;
        History.Changed += OnHistoryChanged;
    }

    public Song Song { get; }

    public string FilePath { get; private set; }

    /// <summary>The file name without extension (the default render name).</summary>
    public string Name => Path.GetFileNameWithoutExtension(FilePath);

    public UndoRedo History { get; } = new();

    public bool IsDirty => History.IsDirty;

    /// <summary>True for files from a newer editor: edits are refused and Save throws.</summary>
    public bool IsReadOnly { get; }

    public string? ReadOnlyNotice { get; }

    /// <summary>Bumped on every change (edit, undo, redo): the engine rebuilds its snapshot when it differs.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every edit, undo and redo.</summary>
    public event Action? Changed;

    /// <summary>Opens <paramref name="path"/> (read-only when it is from a newer format).</summary>
    public static SongDocument Load(string path)
    {
        var result = SongFormat.Load(path);
        if (result.Notice is not null)
            Log.Warning($"[Music] {result.Notice}");
        return new SongDocument(result.Song, path, result.ReadOnly, result.Notice);
    }

    /// <summary>Writes the song to <see cref="FilePath"/> and marks the history saved.</summary>
    public void Save()
    {
        if (IsReadOnly)
            throw new InvalidOperationException(ReadOnlyNotice ?? "The song is read-only.");
        SongFormat.Save(Song, FilePath);
        AssetDatabase.Current.Register(Song.Uid, FilePath);
        History.MarkSaved();
    }

    /// <summary>The file moved (FileSystem panel): follow it.</summary>
    public void Rename(string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        FilePath = Path.GetFullPath(newPath);
    }

    // --- tracks ---------------------------------------------------------------------------------

    /// <summary>Adds a track (instrument tracks default to the built-in ZzFX instrument) at <paramref name="index"/> (−1 = end).</summary>
    public SongTrack AddTrack(SongTrackKind kind, string? name = null, InstrumentDescriptor? instrument = null, int index = -1)
    {
        var track = new SongTrack
        {
            Id = Song.NextTrackId(),
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(name) ? (kind == SongTrackKind.Audio ? "Audio" : "Instrument") + " " + (Song.Tracks.Count + 1) : name,
            Instrument = kind == SongTrackKind.Instrument ? instrument ?? InstrumentDescriptor.Zzfx(DefaultZzfxLine) : null,
        };
        var at = index < 0 || index > Song.Tracks.Count ? Song.Tracks.Count : index;
        Commit("Add Track", () => Song.Tracks.Insert(at, track), () => Song.Tracks.Remove(track));
        return track;
    }

    /// <summary>The built-in instrument's default sound: a short square-ish pluck.</summary>
    public const string DefaultZzfxLine = "zzfx(...[,0,220,.01,.1,.2,2])";

    public void RemoveTrack(SongTrack track)
    {
        var index = IndexOf(track);
        Commit("Remove Track", () => Song.Tracks.Remove(track), () => Song.Tracks.Insert(index, track));
    }

    /// <summary>Moves a track to <paramref name="newIndex"/> in the track list.</summary>
    public void MoveTrack(SongTrack track, int newIndex)
    {
        var old = IndexOf(track);
        newIndex = Math.Clamp(newIndex, 0, Song.Tracks.Count - 1);
        if (old == newIndex)
            return;
        Commit("Move Track", () => Reinsert(track, newIndex), () => Reinsert(track, old));
    }

    public void RenameTrack(SongTrack track, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Set("Rename Track", track.Name, name.Trim(), v => track.Name = v, null);
    }

    public void SetTrackColor(SongTrack track, string? color) => Set("Track Color", track.Color, color, v => track.Color = v, null);

    public void SetTrackVolume(SongTrack track, float volumeDb, string? mergeKey = null) =>
        Set("Track Volume", track.VolumeDb, Math.Clamp(volumeDb, AudioMath.SilenceDb, 24f), v => track.VolumeDb = v, mergeKey);

    public void SetTrackPan(SongTrack track, float pan, string? mergeKey = null) =>
        Set("Track Pan", track.Pan, Math.Clamp(pan, -1f, 1f), v => track.Pan = v, mergeKey);

    public void SetTrackMute(SongTrack track, bool mute) => Set(mute ? "Mute Track" : "Unmute Track", track.Mute, mute, v => track.Mute = v, null);

    public void SetTrackSolo(SongTrack track, bool solo) => Set(solo ? "Solo Track" : "Unsolo Track", track.Solo, solo, v => track.Solo = v, null);

    /// <summary>Replaces an instrument track's instrument (null: none). ZzFX line edits pass a mergeKey while typing.</summary>
    public void SetTrackInstrument(SongTrack track, InstrumentDescriptor? instrument, string? mergeKey = null) =>
        Set("Track Instrument", track.Instrument, instrument, v => track.Instrument = v, mergeKey, alwaysCommit: true);

    // --- clips ----------------------------------------------------------------------------------

    public MidiClip AddMidiClip(SongTrack track, long start, long length)
    {
        RequireKind(track, SongTrackKind.Instrument);
        var clip = new MidiClip { Start = Math.Max(0, start), Length = Math.Max(1, length) };
        AddClip(track, clip);
        return clip;
    }

    /// <summary>Places <paramref name="file"/> (asset UID or project path) on an audio track.</summary>
    public AudioClip AddAudioClip(SongTrack track, string file, long start, long length, long offset = 0, float gainDb = 0)
    {
        RequireKind(track, SongTrackKind.Audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var clip = new AudioClip { File = file, Start = Math.Max(0, start), Length = Math.Max(1, length), Offset = Math.Max(0, offset), GainDb = gainDb };
        AddClip(track, clip);
        return clip;
    }

    /// <summary>Adds an existing clip object (paste, duplicate) to a track of the matching kind.</summary>
    public void AddClip(SongTrack track, SongClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        RequireKind(track, clip is AudioClip ? SongTrackKind.Audio : SongTrackKind.Instrument);
        Commit("Add Clip", () => track.Clips.Add(clip), () => track.Clips.Remove(clip));
    }

    public void RemoveClip(SongTrack track, SongClip clip)
    {
        var index = track.Clips.IndexOf(clip);
        if (index < 0)
            throw new ArgumentException("The clip is not on the track.", nameof(clip));
        Commit("Remove Clip", () => track.Clips.Remove(clip), () => track.Clips.Insert(index, clip));
    }

    /// <summary>Moves a clip in time and optionally to another track of the same kind (drags: pass a mergeKey).</summary>
    public void MoveClip(SongTrack track, SongClip clip, long newStart, SongTrack? toTrack = null, string? mergeKey = null)
    {
        var target = toTrack ?? track;
        RequireKind(target, track.Kind);
        var oldStart = clip.Start;
        var oldIndex = track.Clips.IndexOf(clip);
        if (oldIndex < 0)
            throw new ArgumentException("The clip is not on the track.", nameof(clip));
        newStart = Math.Max(0, newStart);
        if (oldStart == newStart && ReferenceEquals(target, track))
            return;
        Commit("Move Clip",
            () =>
            {
                clip.Start = newStart;
                if (!ReferenceEquals(target, track) && track.Clips.Remove(clip))
                    target.Clips.Add(clip);
            },
            () =>
            {
                clip.Start = oldStart;
                if (!ReferenceEquals(target, track) && target.Clips.Remove(clip))
                    track.Clips.Insert(Math.Min(oldIndex, track.Clips.Count), clip);
            }, mergeKey);
    }

    /// <summary>
    /// Sets a clip's start and length (edge drags). Trimming an audio clip's left edge moves its <c>offset</c> by the same
    /// amount so the audio stays in place.
    /// </summary>
    public void ResizeClip(SongClip clip, long newStart, long newLength, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        newStart = Math.Max(0, newStart);
        newLength = Math.Max(1, newLength);
        var (oldStart, oldLength) = (clip.Start, clip.Length);
        var audio = clip as AudioClip;
        var oldOffset = audio?.Offset ?? 0;
        var newOffset = audio is null ? 0 : Math.Max(0, oldOffset + newStart - oldStart);
        if (oldStart == newStart && oldLength == newLength)
            return;
        Commit("Resize Clip",
            () =>
            {
                clip.Start = newStart;
                clip.Length = newLength;
                if (audio is not null)
                    audio.Offset = newOffset;
            },
            () =>
            {
                clip.Start = oldStart;
                clip.Length = oldLength;
                if (audio is not null)
                    audio.Offset = oldOffset;
            }, mergeKey);
    }

    public void SetClipGain(AudioClip clip, float gainDb, string? mergeKey = null) =>
        Set("Clip Gain", clip.GainDb, Math.Clamp(gainDb, AudioMath.SilenceDb, 24f), v => clip.GainDb = v, mergeKey);

    // --- notes ----------------------------------------------------------------------------------

    /// <summary>Adds notes to a clip (one history entry).</summary>
    public void AddNotes(MidiClip clip, IReadOnlyList<MidiNote> notes)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (notes.Count == 0)
            return;
        var added = notes.ToArray();
        foreach (var note in added)
            Sanitize(note);
        Commit(added.Length == 1 ? "Add Note" : "Add Notes", () => clip.Notes.AddRange(added), () =>
        {
            foreach (var note in added)
                clip.Notes.Remove(note);
        });
    }

    public MidiNote AddNote(MidiClip clip, int pitch, long start, long length, int velocity = 100)
    {
        var note = new MidiNote { Pitch = pitch, Start = start, Length = length, Velocity = velocity };
        AddNotes(clip, [note]);
        return note;
    }

    public void RemoveNotes(MidiClip clip, IReadOnlyList<MidiNote> notes)
    {
        var removed = notes.Where(clip.Notes.Contains).Distinct().Select(n => (Note: n, Index: clip.Notes.IndexOf(n))).OrderBy(p => p.Index).ToArray();
        if (removed.Length == 0)
            return;
        Commit(removed.Length == 1 ? "Remove Note" : "Remove Notes", () =>
        {
            foreach (var (note, _) in removed)
                clip.Notes.Remove(note);
        }, () =>
        {
            foreach (var (note, index) in removed)
                clip.Notes.Insert(Math.Min(index, clip.Notes.Count), note);
        });
    }

    /// <summary>Moves notes by <paramref name="deltaTicks"/> and <paramref name="deltaPitch"/> from where they are now (drags: mergeKey).</summary>
    public void MoveNotes(IReadOnlyList<MidiNote> notes, long deltaTicks, int deltaPitch, string? mergeKey = null)
    {
        if (notes.Count == 0 || (deltaTicks == 0 && deltaPitch == 0))
            return;
        var minStart = notes.Min(n => n.Start);
        var minPitch = notes.Min(n => n.Pitch);
        var maxPitch = notes.Max(n => n.Pitch);
        deltaTicks = Math.Max(deltaTicks, -minStart);
        deltaPitch = Math.Clamp(deltaPitch, MinPitch - minPitch, MaxPitch - maxPitch);
        SetNotes("Move Notes", notes, n => (n.Pitch + deltaPitch, n.Start + deltaTicks, n.Length, n.Velocity), mergeKey);
    }

    /// <summary>Changes the notes' lengths by <paramref name="deltaTicks"/> (minimum 1 tick).</summary>
    public void ResizeNotes(IReadOnlyList<MidiNote> notes, long deltaTicks, string? mergeKey = null)
    {
        if (notes.Count == 0 || deltaTicks == 0)
            return;
        SetNotes("Resize Notes", notes, n => (n.Pitch, n.Start, Math.Max(1, n.Length + deltaTicks), n.Velocity), mergeKey);
    }

    public void SetNoteVelocity(IReadOnlyList<MidiNote> notes, int velocity, string? mergeKey = null)
    {
        velocity = Math.Clamp(velocity, 1, 127);
        SetNotes("Note Velocity", notes, n => (n.Pitch, n.Start, n.Length, velocity), mergeKey);
    }

    // --- song settings --------------------------------------------------------------------------

    public void SetTempo(double bpm, string? mergeKey = null) => Set("Tempo", Song.Tempo, Math.Clamp(bpm, 20, 999), v => Song.Tempo = v, mergeKey);

    public void SetTimeSignature(int beats, int unit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beats, 1);
        if (unit is not (1 or 2 or 4 or 8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(unit), "The beat unit is a power of two up to 32.");
        var old = Song.TimeSignature;
        if (old.Length == 2 && old[0] == beats && old[1] == unit)
            return;
        int[] value = [beats, unit];
        Commit("Time Signature", () => Song.TimeSignature = value, () => Song.TimeSignature = old);
    }

    public void SetMasterVolume(float volumeDb, string? mergeKey = null) =>
        Set("Master Volume", Song.Master.VolumeDb, Math.Clamp(volumeDb, AudioMath.SilenceDb, 24f), v => Song.Master.VolumeDb = v, mergeKey);

    /// <summary>Sets the loop toggle and region (ticks; <paramref name="end"/> &gt; <paramref name="start"/>).</summary>
    public void SetLoop(bool enabled, long start, long end, string? mergeKey = null)
    {
        start = Math.Max(0, start);
        end = Math.Max(start + 1, end);
        var old = Song.Loop.Clone();
        if (old.Enabled == enabled && old.Start == start && old.End == end)
            return;
        Commit("Loop", () => Assign(enabled, start, end), () => Assign(old.Enabled, old.Start, old.End), mergeKey);

        void Assign(bool e, long s, long en) => (Song.Loop.Enabled, Song.Loop.Start, Song.Loop.End) = (e, s, en);
    }

    /// <summary>Edits the render settings through <paramref name="edit"/> on a copy (one history entry).</summary>
    public void SetRenderSettings(Action<SongRenderSettings> edit, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var old = Song.Render.Clone();
        var value = old.Clone();
        edit(value);
        value.SampleRate = Math.Clamp(value.SampleRate, 8000, 192000);
        value.Quality = Math.Clamp(value.Quality, 0, 10);
        value.TailSeconds = double.IsFinite(value.TailSeconds) ? Math.Clamp(value.TailSeconds, 0, 60) : 0;
        Commit("Render Settings", () => Song.Render = value.Clone(), () => Song.Render = old.Clone(), mergeKey);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        History.Changed -= OnHistoryChanged;
        History.Clear();
    }

    // --- plumbing -------------------------------------------------------------------------------

    private void OnHistoryChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    private void Commit(string name, Action apply, Action revert, string? mergeKey = null)
    {
        if (IsReadOnly)
            throw new InvalidOperationException(ReadOnlyNotice ?? "The song is read-only.");
        History.Commit(new SongAction(name, apply, revert), mergeKey: mergeKey);
    }

    private void Set<T>(string name, T old, T value, Action<T> assign, string? mergeKey, bool alwaysCommit = false)
    {
        if (!alwaysCommit && EqualityComparer<T>.Default.Equals(old, value))
            return;
        Commit(name, () => assign(value), () => assign(old), mergeKey);
    }

    private void SetNotes(string name, IReadOnlyList<MidiNote> notes, Func<MidiNote, (int Pitch, long Start, long Length, int Velocity)> next, string? mergeKey)
    {
        var targets = notes.Distinct().ToArray();
        if (targets.Length == 0)
            return;
        var before = targets.Select(n => (n.Pitch, n.Start, n.Length, n.Velocity)).ToArray();
        var after = targets.Select(next).ToArray();
        if (before.AsSpan().SequenceEqual(after))
            return;
        Commit(name, () => Apply(targets, after), () => Apply(targets, before), mergeKey);

        static void Apply(MidiNote[] targets, (int Pitch, long Start, long Length, int Velocity)[] values)
        {
            for (var i = 0; i < targets.Length; i++)
                (targets[i].Pitch, targets[i].Start, targets[i].Length, targets[i].Velocity) = values[i];
        }
    }

    private int IndexOf(SongTrack track)
    {
        var index = Song.Tracks.IndexOf(track);
        return index >= 0 ? index : throw new ArgumentException("The track is not in this song.", nameof(track));
    }

    private void Reinsert(SongTrack track, int index)
    {
        Song.Tracks.Remove(track);
        Song.Tracks.Insert(index, track);
    }

    private void RequireKind(SongTrack track, SongTrackKind kind)
    {
        IndexOf(track);
        if (track.Kind != kind)
            throw new ArgumentException($"'{track.Name}' is an {track.Kind.ToString().ToLowerInvariant()} track.", nameof(track));
    }

    private static void Sanitize(MidiNote note)
    {
        note.Pitch = Math.Clamp(note.Pitch, MinPitch, MaxPitch);
        note.Start = Math.Max(0, note.Start);
        note.Length = Math.Max(1, note.Length);
        note.Velocity = Math.Clamp(note.Velocity, 1, 127);
    }
}

/// <summary>
/// One song edit: <c>apply</c> sets the new state, <c>revert</c> the old (both absolute, so redo after undo is exact).
/// Merging (drags) keeps the first entry's revert and takes the latest apply.
/// </summary>
internal sealed class SongAction(string name, Action apply, Action revert) : IEditorAction
{
    private Action _apply = apply;

    public string Name { get; } = name;

    public void Do() => _apply();

    public void Undo() => revert();

    public bool TryMerge(IEditorAction next)
    {
        if (next is not SongAction other || !string.Equals(other.Name, Name, StringComparison.Ordinal))
            return false;
        _apply = other._apply;
        return true;
    }
}

namespace MainframeEngine.Editor.Music;

/// <summary>Which view the song tab's bottom panel shows.</summary>
public enum SongBottomPanel
{
    PianoRoll,
    Mixer,
}

/// <summary>The song tab's area that last took a click (Del, Cmd+D and the clipboard act on it).</summary>
public enum SongArea
{
    None,
    Arrangement,
    PianoRoll,
}

/// <summary>
/// A song tab (<c>.msong</c>, the music editor): the <see cref="SongDocument"/>, its own <see cref="SongPlayer"/> (stopped
/// when the tab is deactivated or closed) and the view state — snap, zoom and scroll of the arrangement and the piano
/// roll, the selected track, clip and notes. The arrangement and the piano roll are drawn by canvas nodes in two
/// <see cref="SubViewport"/>s (<see cref="ArrangeViewport"/>, <see cref="RollViewport"/>) that the
/// <see cref="SongView"/> publishes to the <see cref="SongPanel"/>; input on them goes through <see cref="Controller"/>.
/// </summary>
public sealed class SongTab : IEditorTab
{
    private bool _disposed;
    private bool _wasDirty;

    internal SongTab(SongDocument document, SongPlayer player, int number)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Player = player ?? throw new ArgumentNullException(nameof(player));
        Number = number;
        ArrangeViewport = NewViewport($"SongArrange{number}");
        RollViewport = NewViewport($"SongRoll{number}");
        Arrangement = new ArrangementCanvas(this) { Name = "Arrangement" };
        PianoRoll = new PianoRollCanvas(this) { Name = "PianoRoll" };
        ArrangeViewport.AddChild(Arrangement);
        RollViewport.AddChild(PianoRoll);
        Controller = new SongCanvasController(this);
        SelectedTrack = document.Song.Tracks.Count > 0 ? document.Song.Tracks[0] : null;
        SelectedClip = SelectedTrack?.Clips.Count > 0 ? SelectedTrack.Clips[0] : null;
        document.Changed += OnDocumentChanged;
    }

    private static SubViewport NewViewport(string name) => new()
    {
        Name = name,
        Disable3D = true,
        UpdateMode = SubViewportUpdateMode.Disabled,
        ClearColor = System.Drawing.Color.FromArgb(255, 27, 29, 35),
        Width = 16,
        Height = 16,
    };

    public SongDocument Document { get; }

    public SongPlayer Player { get; }

    public SongCanvasController Controller { get; }

    /// <summary>Unique per session (viewport names).</summary>
    public int Number { get; }

    public SubViewport ArrangeViewport { get; }

    public SubViewport RollViewport { get; }

    public ArrangementCanvas Arrangement { get; }

    public PianoRollCanvas PianoRoll { get; }

    public string? FilePath => Document.FilePath;

    public string DisplayName => Path.GetFileName(Document.FilePath);

    public string Title => IsDirty ? DisplayName + "*" : DisplayName;

    public bool IsDirty => Document.IsDirty;

    public string IconClasses => "icon icon-music icon-audio";

    public string Tooltip => DisplayName + " — song" + (Document.IsReadOnly ? " (read-only)" : IsDirty ? " (unsaved changes)" : "") + " — " + FilePath;

    // ── View state ───────────────────────────────────────────────────────────────────────────────────────────────

    public SongSnap Snap { get; set; } = SongSnap.Sixteenth;

    public SongBottomPanel Panel { get; set; } = SongBottomPanel.PianoRoll;

    public SongArea Focus { get; set; } = SongArea.Arrangement;

    /// <summary>Arrangement zoom: dp per tick (default: a 4/4 bar at 960 PPQ is 75 dp).</summary>
    public double ArrangePixelsPerTick { get; set; } = 75.0 / (Song.DefaultPpq * 4);

    /// <summary>The tick at the arrangement's left edge.</summary>
    public double ArrangeScrollTick { get; set; }

    /// <summary>The arrangement's vertical scroll (dp from the first lane).</summary>
    public float ArrangeScrollY { get; set; }

    /// <summary>Piano-roll zoom: dp per tick (default: a beat is 92 dp).</summary>
    public double RollPixelsPerTick { get; set; } = 92.0 / Song.DefaultPpq;

    /// <summary>The clip-relative tick at the piano roll's left edge (after the keyboard).</summary>
    public double RollScrollTick { get; set; }

    /// <summary>The piano roll's vertical scroll (dp from pitch 127's row); defaults around C4.</summary>
    public float RollScrollY { get; set; } = (127 - 84) * SongCanvasController.RowHeight;

    public SongTrack? SelectedTrack { get; private set; }

    public SongClip? SelectedClip { get; private set; }

    /// <summary>The selected notes of <see cref="SelectedClip"/>.</summary>
    public List<MidiNote> SelectedNotes { get; } = [];

    /// <summary>The length of a note added by a click: the last note clicked or resized (default a sixteenth).</summary>
    public long LastNoteLength { get; set; } = Song.DefaultPpq / 4;

    /// <summary>Bumped when the selection or the panel changes (the chrome refreshes).</summary>
    public int ViewVersion { get; private set; }

    /// <summary>The selected clip when it is a MIDI clip (what the piano roll shows).</summary>
    public MidiClip? RollClip => SelectedClip as MidiClip;

    public void SelectTrack(SongTrack? track)
    {
        if (ReferenceEquals(track, SelectedTrack))
            return;
        SelectedTrack = track;
        ViewVersion++;
    }

    /// <summary>Selects <paramref name="clip"/> on <paramref name="track"/> (null: none); a new clip clears the note selection.</summary>
    public void SelectClip(SongTrack? track, SongClip? clip)
    {
        if (track is not null)
            SelectedTrack = track;
        if (!ReferenceEquals(clip, SelectedClip))
        {
            SelectedClip = clip;
            SelectedNotes.Clear();
            RollScrollTick = 0;
        }

        ViewVersion++;
    }

    public void SetPanel(SongBottomPanel panel)
    {
        if (Panel == panel)
            return;
        Panel = panel;
        ViewVersion++;
    }

    public void Touch() => ViewVersion++;

    /// <summary>The track holding <paramref name="clip"/>, or null.</summary>
    public SongTrack? TrackOf(SongClip clip)
    {
        foreach (var track in Document.Song.Tracks)
            if (track.Clips.Contains(clip))
                return track;
        return null;
    }

    // Undo/redo can remove what is selected: keep only what still exists.
    private void OnDocumentChanged()
    {
        var song = Document.Song;
        if (SelectedTrack is not null && !song.Tracks.Contains(SelectedTrack))
        {
            SelectedTrack = song.Tracks.Count > 0 ? song.Tracks[0] : null;
            ViewVersion++;
        }

        if (SelectedClip is not null && TrackOf(SelectedClip) is null)
        {
            SelectedClip = null;
            SelectedNotes.Clear();
            ViewVersion++;
        }
        else if (SelectedClip is not null && TrackOf(SelectedClip) is { } track && !ReferenceEquals(track, SelectedTrack))
        {
            SelectedTrack = track;
            ViewVersion++;
        }

        if (RollClip is { } clip)
            SelectedNotes.RemoveAll(n => !clip.Notes.Contains(n));
        if (_wasDirty != IsDirty)
        {
            _wasDirty = IsDirty;
            DirtyChanged?.Invoke(this);
        }
    }

    /// <summary>The dirty state flipped (the tab strip and the FileSystem badge follow).</summary>
    internal event Action<SongTab>? DirtyChanged;

    /// <summary>Saved (or renamed): the dirty flag is re-read.</summary>
    internal void Saved()
    {
        _wasDirty = IsDirty;
    }

    /// <summary>The tab is no longer active: playback and previews stop, the views pause.</summary>
    internal void Deactivate()
    {
        Player.Stop();
        Controller.CancelGesture();
        ArrangeViewport.UpdateMode = SubViewportUpdateMode.Disabled;
        RollViewport.UpdateMode = SubViewportUpdateMode.Disabled;
    }

    // ── New songs ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a new song to <paramref name="path"/> (adding <c>.msong</c> when missing): 120 BPM, 4/4, one instrument track
    /// with the built-in ZzFX instrument. Throws when the file exists. Returns the full path.
    /// </summary>
    public static string CreateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".msong", StringComparison.OrdinalIgnoreCase))
            full += ".msong";
        if (File.Exists(full))
            throw new IOException($"'{Path.GetFileName(full)}' already exists.");
        var song = Song.CreateNew();
        song.Tracks.Add(new SongTrack
        {
            Id = song.NextTrackId(),
            Name = "Lead",
            Kind = SongTrackKind.Instrument,
            Color = "#22d3ee",
            Instrument = InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var document = new SongDocument(song, full);
        document.Save();
        return full;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Document.Changed -= OnDocumentChanged;
        Player.Dispose();
        if (!ArrangeViewport.IsFreed)
            ArrangeViewport.Free();
        if (!RollViewport.IsFreed)
            RollViewport.Free();
        Document.Dispose();
    }
}

namespace MainframeEngine.Editor.Music;

/// <summary>Which editor music mode's bottom pane shows (P / X, View › Curves), or none (the tracks fill the view).</summary>
public enum SongBottomPanel
{
    PianoRoll,
    Mixer,
    None,

    /// <summary>An adaptive song's curve editor (a layer's volume or cutoff over a parameter).</summary>
    Curves,
}

/// <summary>The song tab's area that last took a click (Del, Cmd+D and the clipboard act on it).</summary>
public enum SongArea
{
    None,
    Arrangement,
    PianoRoll,

    /// <summary>The Curves pane (an adaptive song's curve editor).</summary>
    Curves,
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
        Curves = new SongCurves(this);
        CurvesCanvas = new CurvesCanvas(this) { Name = "Curves", Visible = false };
        ArrangeViewport.AddChild(Arrangement);
        RollViewport.AddChild(PianoRoll);
        RollViewport.AddChild(CurvesCanvas); // the bottom pane's view shows the piano roll or the curves
        Controller = new SongCanvasController(this);
        Baked = new BakedAudition(() => Player.Server);
        Midi = new SongMidiInput(document, player, () => SelectedTrack);
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

    /// <summary>MIDI keyboard input: live play, record-arm, recording (ADR 0148).</summary>
    public SongMidiInput Midi { get; }

    /// <summary>Unique per session (viewport names).</summary>
    public int Number { get; }

    public SubViewport ArrangeViewport { get; }

    public SubViewport RollViewport { get; }

    public ArrangementCanvas Arrangement { get; }

    public PianoRollCanvas PianoRoll { get; }

    /// <summary>The Adaptive panel's Baked mode: the song's last bake through an <c>AdaptiveMusicInstance</c>.</summary>
    public BakedAudition Baked { get; }

    /// <summary>Adaptive playback plays the last bake (true) or the song model through the <see cref="SongEngine"/> (Live).</summary>
    public bool AuditionBaked { get; private set; }

    /// <summary>Switches Live / Baked (what plays stops); Baked only when a bake exists.</summary>
    public void SetAuditionBaked(bool baked)
    {
        baked &= Baked.Available(Document);
        if (baked == AuditionBaked)
            return;
        StopAdaptive();
        AuditionBaked = baked;
        ViewVersion++;
    }

    /// <summary>
    /// The playhead's tick: the baked music's (its section's start plus how far into it the instance is) while the bake
    /// plays, else the player's heard position.
    /// </summary>
    public double PlayheadTicks
    {
        get
        {
            if (AuditionBaked && Baked.Instance is { Playing: true } baked && baked.GetPosition() is { Section: { } id } position &&
                Document.Song.Adaptive?.FindSection(id) is { } section)
                return section.Start + Document.Song.SecondsToTicks(position.Seconds);
            return Player.PositionTicks;
        }
    }

    /// <summary>Adaptive playback runs, live or baked.</summary>
    public bool IsPlayingAdaptive => AuditionBaked ? Baked.Playing : Player.IsPlaying && Player.IsAdaptive;

    /// <summary>Sets an audition value: kept with the tab, and sent to what plays.</summary>
    public void SetAudition(string name, float value)
    {
        Auditions[name] = value;
        Player.SetParameter(name, value);
        Baked.SetParameter(name, value);
    }

    private void StopAdaptive()
    {
        Baked.Stop();
        if (Player.IsAdaptive)
            Player.Stop();
    }

    /// <summary>The Curves pane's state and gestures.</summary>
    public SongCurves Curves { get; }

    public CurvesCanvas CurvesCanvas { get; }

    /// <summary>The curve the Curves pane edits (null: none).</summary>
    public Audio.Music.MusicCurveInfo? SelectedCurve { get; private set; }

    public void SelectCurve(Audio.Music.MusicCurveInfo? curve)
    {
        if (ReferenceEquals(curve, SelectedCurve))
            return;
        SelectedCurve = curve;
        ViewVersion++;
    }

    /// <summary>
    /// A parameter's value now: the music's while it plays adaptively (live or baked), else the Adaptive panel's audition
    /// value, else its default. No allocation (the Curves pane draws it every frame).
    /// </summary>
    public float ParameterValue(string name)
    {
        if (Baked.Instance is { Playing: true } baked)
            return baked.GetParameter(name);
        if (Player.IsAdaptive && Player.Plan is { } plan)
        {
            for (var i = 0; i < plan.Parameters.Count && i < SongEngine.MaxPublishedParameters; i++)
            {
                if (plan.Parameters[i].Name == name)
                    return Player.Engine.ParameterValue(i);
            }
        }

        if (Auditions.TryGetValue(name, out var value))
            return value;
        return Document.Song.Adaptive?.FindParameter(name)?.Default ?? 0;
    }

    public string? FilePath => Document.FilePath;

    public string DisplayName => Path.GetFileName(Document.FilePath);

    public string Title => IsDirty ? DisplayName + "*" : DisplayName;

    public bool IsDirty => Document.IsDirty;

    public string IconClasses => "icon icon-music icon-audio";

    public EditorMode Mode => EditorMode.Music;

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

    /// <summary>The selected section of the Sections lane (adaptive songs), or null.</summary>
    public SongSection? SelectedSection { get; private set; }

    /// <summary>The adaptive parameters' audition values (by name): what the Adaptive panel's controls are set to, kept with
    /// the tab and not in the song.</summary>
    public Dictionary<string, float> Auditions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Shift+Space: plays the song adaptively from its start section (<paramref name="fromPlayhead"/>: from the section under
    /// the playhead) with the Adaptive panel's parameter values; while it plays, stops it. False when the song has no
    /// playable section.
    /// </summary>
    public bool PlayAdaptive(bool fromPlayhead)
    {
        if (IsPlayingAdaptive)
        {
            StopAdaptive();
            return true;
        }

        var section = fromPlayhead ? Document.Song.Adaptive?.SectionAt((long)Player.PositionTicks)?.Id : null;
        if (Player.IsPlaying)
            Player.Stop();
        if (AuditionBaked)
            return Baked.Play(Document, section, Auditions);
        return Player.PlayAdaptive(section, Auditions);
    }

    /// <summary>Fires stinger <paramref name="id"/> on what plays (live or baked).</summary>
    public void TriggerStinger(string id)
    {
        if (AuditionBaked)
            Baked.TriggerStinger(id);
        else
            Player.TriggerStinger(id);
    }

    /// <summary>T: fires the selected stinger (else the song's first) at its quantum while the song plays adaptively.</summary>
    public void FireStinger()
    {
        if (!IsPlayingAdaptive || Document.Song.Adaptive is not { } adaptive)
            return;
        var stinger = SelectedSection is { Kind: Audio.Music.MusicSectionKind.Stinger } selected
            ? selected
            : adaptive.Sections.Find(static s => s.Kind == Audio.Music.MusicSectionKind.Stinger);
        if (stinger is not null)
            TriggerStinger(stinger.Id);
    }

    public void SelectSection(SongSection? section)
    {
        if (ReferenceEquals(section, SelectedSection))
            return;
        SelectedSection = section;
        ViewVersion++;
    }

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

        if (SelectedSection is not null && song.Adaptive?.Sections.Contains(SelectedSection) != true)
        {
            SelectedSection = null;
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
        Midi.ReleaseAll();
        Player.Stop();
        Baked.Stop();
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
        Midi.ReleaseAll();
        Baked.Dispose();
        Player.Dispose();
        if (!ArrangeViewport.IsFreed)
            ArrangeViewport.Free();
        if (!RollViewport.IsFreed)
            RollViewport.Free();
        Document.Dispose();
    }
}

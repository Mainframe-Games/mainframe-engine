using System.Text;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

[Collection(nameof(SerialEditor))]
public sealed class SongModelTests : IDisposable
{
    private const string State = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNE+/8=";

    // The proposal's example, with a plugin instrument (missing here) and fields this editor does not know.
    private static readonly string Example = $$"""
        {
          "format": 1,
          "uid": "sng_0123456789ab",
          "tempo": 120, "timeSignature": [4, 4], "ppq": 960,
          "loop": { "enabled": true, "start": 0, "end": 61440 },
          "render": { "output": "Content/Music/Theme.ogg", "sampleRate": 48000, "quality": 6, "tailSeconds": 2, "seamlessLoop": true },
          "master": { "volumeDb": 0, "inserts": [] },
          "tracks": [
            {
              "id": "t1", "name": "Drums", "kind": "instrument", "color": "#f59e0b",
              "volumeDb": -3, "pan": 0, "mute": false, "solo": false,
              "instrument": { "plugin": { "format": "vst3", "classId": "ABCDEF0123456789ABCDEF0123456789", "name": "SSDSampler5", "vendor": "Steven Slate Drums", "x-future": { "bus": [1, 2] } },
                              "state": "{{State}}" },
              "inserts": [ { "plugin": { "classId": "0011", "name": "Saturation Knob" }, "state": "AQID", "bypass": false, "x-sidechain": null } ],
              "clips": [ { "start": 0, "length": 15360, "notes": [ { "pitch": 36, "start": 0, "length": 240, "velocity": 110 } ] } ]
            },
            { "id": "t2", "name": "Lead", "kind": "instrument", "instrument": { "builtin": "zzfx", "params": "zzfx(...[,,220,,.1,.2,2])" }, "clips": [] },
            { "id": "t3", "name": "Rain", "kind": "audio", "clips": [ { "start": 0, "length": 30720, "file": "aud_0123456789ab", "offset": 0, "gainDb": -6 } ] }
          ]
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("mf-song-").FullName;
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheProposalsExampleRoundTripsWithPluginDataPreserved()
    {
        var read = SongFormat.Parse(Encoding.UTF8.GetBytes(Example), "Theme.msong");
        Assert.False(read.ReadOnly);
        var song = read.Song;
        Assert.Equal("sng_0123456789ab", song.Uid);
        Assert.Equal([4, 4], song.TimeSignature);
        Assert.True(song.Loop.Enabled);
        Assert.Equal(61440, song.Loop.End);
        Assert.Equal(3, song.Tracks.Count);
        var drums = song.Tracks[0];
        Assert.Equal(SongTrackKind.Instrument, drums.Kind);
        Assert.Equal(State, drums.Instrument!.State);
        Assert.Equal("SSDSampler5 (Steven Slate Drums)", drums.Instrument.DisplayName);
        Assert.Equal(110, Assert.IsType<MidiClip>(drums.Clips[0]).Notes[0].Velocity);
        Assert.True(song.Tracks[1].Instrument!.IsZzfx);
        var rain = Assert.IsType<AudioClip>(song.Tracks[2].Clips[0]);
        Assert.Equal(("aud_0123456789ab", -6f), (rain.File, rain.GainDb));

        var written = SongFormat.Write(song);
        var text = Encoding.UTF8.GetString(written);
        Assert.Contains($"\"state\": \"{State}\"", text, StringComparison.Ordinal);
        Assert.Contains("\"x-future\"", text, StringComparison.Ordinal);
        Assert.Contains("\"x-sidechain\": null", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"audio\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
        Assert.Equal(written, SongFormat.Write(SongFormat.Parse(written, "again").Song)); // stable, byte for byte
        Assert.Equal(written, SongFormat.Write(song.Clone()));
    }

    [Fact]
    public void ANewerFormatOpensReadOnly()
    {
        var path = Path.Combine(_root, "Future.msong");
        File.WriteAllText(path, """{ "format": 99, "uid": "sng_0123456789ab", "tracks": [], "hologram": true }""");
        using var doc = SongDocument.Load(path);
        Assert.True(doc.IsReadOnly);
        Assert.Contains("newer", doc.ReadOnlyNotice, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.AddTrack(SongTrackKind.Instrument));
        Assert.Throws<InvalidOperationException>(doc.Save);
        Assert.Contains("hologram", Encoding.UTF8.GetString(SongFormat.Write(doc.Song)), StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsRunFromOlderFormats()
    {
        var migrations = new[] { new ProjectMigration(1, root => root["tempo"] = 90) };
        var read = SongFormat.Parse("""{ "format": 1, "uid": "sng_0123456789ab" }"""u8, "old.msong", 2, migrations);
        Assert.Equal(90, read.Song.Tempo);
        Assert.Equal(2, read.Song.Format);
    }

    [Fact]
    public void SongsAreSelfDescribingAssetsAndMovesRewriteTheirPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content", "Songs"));
        var song = Song.CreateNew();
        Assert.StartsWith("sng_", song.Uid, StringComparison.Ordinal);
        Assert.True(AssetUid.IsUid(song.Uid));
        song.Render.Output = "Content/Music/Theme.wav";
        var path = Path.Combine(_root, "Content", "Songs", "Theme.msong");
        SongFormat.Save(song, path);

        var db = new AssetDatabase(_root);
        db.Scan();
        Assert.Equal("Content/Songs/Theme.msong", db.GetPath(song.Uid));
        Assert.False(File.Exists(path + AssetDatabase.MetaExtension)); // the UID lives in the file

        var updated = ReferenceFixer.Apply(_root, db, new Dictionary<string, string> { ["Content/Music/Theme.wav"] = "Content/Audio/Theme.wav" });
        Assert.Contains(path, updated);
        Assert.Equal("Content/Audio/Theme.wav", SongFormat.Load(path).Song.Render.Output);
        Assert.Equal(SongFormat.Write(SongFormat.Load(path).Song), File.ReadAllBytes(path)); // rewritten in the song writer's style

        Assert.Equal(FileKind.Song, FileKinds.Of(path, isDirectory: false));
        Assert.Equal("music", EditorIcons.ForFile(path));
    }

    [Fact]
    public void UndoAndRedoRestoreEveryEditExactly()
    {
        var path = Path.Combine(_root, "Edit.msong");
        var song = Song.CreateNew();
        using var doc = new SongDocument(song, path);
        var states = new List<byte[]> { SongFormat.Write(song) };
        void Step(Action edit)
        {
            edit();
            states.Add(SongFormat.Write(song));
        }

        SongTrack lead = null!, drums = null!, rain = null!;
        MidiClip clip = null!;
        AudioClip audio = null!;
        Step(() => lead = doc.AddTrack(SongTrackKind.Instrument, "Lead"));
        Step(() => drums = doc.AddTrack(SongTrackKind.Instrument, "Drums", index: 0));
        Step(() => rain = doc.AddTrack(SongTrackKind.Audio));
        Step(() => doc.RenameTrack(lead, "Melody"));
        Step(() => doc.SetTrackVolume(lead, -6));
        Step(() => doc.SetTrackPan(lead, 0.25f));
        Step(() => doc.SetTrackMute(drums, true));
        Step(() => doc.SetTrackSolo(lead, true));
        Step(() => doc.SetTrackColor(lead, "#ff0000"));
        Step(() => doc.SetTrackInstrument(drums, InstrumentDescriptor.Zzfx("zzfx(...[,0,80,,.05,.1,4])")));
        Step(() => doc.MoveTrack(rain, 0));
        Step(() => clip = doc.AddMidiClip(lead, 960, 3840));
        Step(() => doc.AddNotes(clip, [new MidiNote { Pitch = 60, Start = 0, Length = 480 }, new MidiNote { Pitch = 64, Start = 480, Length = 480 }]));
        Step(() => doc.MoveNotes(clip.Notes, 240, 2));
        Step(() => doc.ResizeNotes([clip.Notes[0]], 120));
        Step(() => doc.SetNoteVelocity(clip.Notes, 64));
        Step(() => doc.RemoveNotes(clip, [clip.Notes[0]]));
        Step(() => doc.MoveClip(lead, clip, 1920, drums));
        Step(() => doc.ResizeClip(clip, 2000, 1000));
        Step(() => audio = doc.AddAudioClip(rain, "aud_0123456789ab", 0, 3840));
        Step(() => doc.ResizeClip(audio, 960, 2880)); // left trim moves the offset with it
        Step(() => doc.SetClipGain(audio, -3));
        Step(() => doc.RemoveClip(rain, audio));
        Step(() => doc.SetTempo(140));
        Step(() => doc.SetTimeSignature(3, 4));
        Step(() => doc.SetLoop(true, 0, 3840));
        Step(() => doc.SetMasterVolume(-2));
        Step(() => doc.SetRenderSettings(r => (r.TailSeconds, r.SeamlessLoop) = (4, false)));
        Step(() => doc.RemoveTrack(lead));
        Assert.Equal(states.Count - 1, doc.History.Actions.Count);
        Assert.True(doc.IsDirty);

        for (var i = states.Count - 1; i > 0; i--)
        {
            Assert.True(doc.History.Undo());
            Assert.Equal(Encoding.UTF8.GetString(states[i - 1]), Encoding.UTF8.GetString(SongFormat.Write(song)));
        }

        for (var i = 1; i < states.Count; i++)
        {
            Assert.True(doc.History.Redo());
            Assert.Equal(Encoding.UTF8.GetString(states[i]), Encoding.UTF8.GetString(SongFormat.Write(song)));
        }

        Assert.Equal(960, audio.Offset);
    }

    [Fact]
    public void DragsMergeIntoOneEntryAndSaveClearsDirty()
    {
        AssetDatabase.Current = new AssetDatabase(_root);
        var path = Path.Combine(_root, "Drag.msong");
        using var doc = new SongDocument(Song.CreateNew(), path);
        var track = doc.AddTrack(SongTrackKind.Instrument);
        var clip = doc.AddMidiClip(track, 0, 3840);
        var note = doc.AddNote(clip, 60, 0, 240);
        var changes = 0;
        doc.Changed += () => changes++;
        var version = doc.Version;
        for (var i = 1; i <= 5; i++)
            doc.MoveNotes([note], 120, 1, mergeKey: "drag");
        doc.History.EndMerge();
        Assert.Equal((65, 600L), (note.Pitch, note.Start));
        Assert.Equal(4, doc.History.Actions.Count);
        Assert.Equal(5, changes);
        Assert.True(doc.Version > version);

        for (var v = 0; v < 3; v++)
            doc.SetTrackVolume(track, -v, mergeKey: "fader");
        Assert.Equal(5, doc.History.Actions.Count);

        doc.Save();
        Assert.False(doc.IsDirty);
        Assert.Equal(doc.Song.Uid, AssetDatabase.Current.GetUid(path));
        doc.History.Undo();
        Assert.Equal(-0f, track.VolumeDb);
        doc.History.Undo();
        Assert.Equal((60, 0L), (note.Pitch, note.Start));
        Assert.True(doc.IsDirty);
    }
}

using MainframeEngine.Audio;
using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests.Audio;

/// <summary>Editor audio previews and the sound designer (ZzfxStream inspector), on the null audio device.</summary>
[Collection(nameof(SerialEditor))]
public sealed class SoundDesignerTests : IDisposable
{
    private readonly TestProject _project = new("Sounds");
    private readonly FakeGameBuilder _builder = new();
    private readonly FakeGameLauncher _launcher = new();
    private HeadlessEditor? _editor;
    private AudioServer? _audio;

    public void Dispose()
    {
        _editor?.Dispose();
        _audio?.Dispose();
        _project.Dispose();
    }

    private EditorWorkspace Open()
    {
        _editor = new HeadlessEditor(configure: o => o with { InitialProject = _project.Root, GameBuilder = _builder, GameLauncher = _launcher });
        _audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, _editor.Tree);
        _editor.Servers.Register(_audio);
        _editor.Tick(3);
        return _editor.Workspace;
    }

    private (EditorWorkspace Workspace, EditedResource Resource, ZzfxStream Stream) OpenSound()
    {
        var w = Open();
        var file = _project.Abs("Content/Coin.mres");
        ResourceSaver.Save(new ZzfxStream { ResourceName = "Coin" }, file);
        Assert.True(w.Inspector.InspectResourceFile(file));
        _editor!.Tick();
        var resource = w.Inspector.InspectedResource!;
        return (w, resource, (ZzfxStream)resource.Resource);
    }

    [Fact]
    public void ThePreviewPlaysOneVoiceAtATimeAndStopsWhenTheGameRuns()
    {
        var w = Open();
        var preview = w.AudioPreview;
        var a = new ZzfxStream { ResourceName = "A", Sustain = 1f };
        var b = new ZzfxStream { ResourceName = "B", Sustain = 1f };
        Assert.True(preview.Play(a));
        Assert.True(preview.IsPlaying(a));
        Assert.True(preview.Play(b));
        Assert.False(preview.IsPlaying(a));
        Assert.True(preview.IsPlaying(b));
        _editor!.Tick();
        Assert.Equal(1, _audio!.Stats.ActiveVoices);
        preview.Toggle(b);
        Assert.Null(preview.Stream);

        // An audio file loads through the resource loader; a voice that ends clears the preview.
        var wav = _project.Abs("Content/blip.wav");
        WavWriter.Write(wav, Zzfx.Generate(ZzfxParameters.Default), 1, Zzfx.SampleRate);
        w.AudioPreview.ToggleFile(wav);
        Assert.True(preview.IsPlayingFile(wav));
        _audio.RenderNullDevice(48000);
        _editor.Tick(2);
        Assert.False(preview.IsPlayingFile(wav));

        // Running the game stops the preview.
        Assert.True(preview.Play(a));
        w.Play.PlayMain();
        Assert.False(preview.IsPlaying(a));
    }

    [Fact]
    public void DesignerActionsAreOneUndoEntryEachAndRestoreExactly()
    {
        var (_, resource, stream) = OpenSound();
        var history = resource.History;
        var original = stream.Parameters;

        Assert.True(ZzfxStreamInspector.ApplyPreset(stream, "explosion", new Random(1), resource));
        Assert.Single(history.Actions);
        var exploded = stream.Parameters;
        Assert.NotEqual(original, exploded);

        Assert.True(ZzfxStreamInspector.Randomize(stream, new Random(2), resource));
        Assert.True(ZzfxStreamInspector.Mutate(stream, new Random(3), resource));
        Assert.True(ZzfxStreamInspector.Paste(stream, "zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])", resource, out _));
        Assert.Equal(4, history.Actions.Count);
        Assert.Equal(925f, stream.Frequency);
        Assert.Equal(ZzfxShape.Triangle, stream.Shape);

        // Invalid text changes nothing and explains why.
        Assert.False(ZzfxStreamInspector.Paste(stream, "zzfx(...[1,2,banana])", resource, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(4, history.Actions.Count);

        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Equal(exploded, stream.Parameters);
        history.Undo();
        Assert.Equal(original, stream.Parameters);
        history.Redo();
        Assert.Equal(exploded, stream.Parameters);
        Assert.Equal("Coin", stream.ResourceName);
    }

    [Fact]
    public void TheDesignerHidesFileRowsAndAutoPlaysOncePerMergedDrag()
    {
        var (w, resource, stream) = OpenSound();
        Assert.DoesNotContain(w.Inspector.Rows, r => r.Name is "File" or "LoadMode");
        Assert.Contains(w.Inspector.Rows, r => r.Name == "Frequency");
        var body = w.Inspector.Document.GetElementById("inspector-body").InnerRml;
        Assert.Contains("zzfx-wave", body, StringComparison.Ordinal);
        Assert.Contains("zzfx-preset:explosion", body, StringComparison.Ordinal);
        Assert.True(w.Layout.Settings.SoundDesignerAutoPlay);

        var plays = 0;
        w.AudioPreview.Changed += () =>
        {
            if (w.AudioPreview.IsPlaying(stream))
                plays++;
        };

        // A slider drag: several merged ticks, one history entry, one replay when the mouse is released.
        var row = w.Inspector.Rows.ToList().FindIndex(r => r.Name == "Frequency");
        var key = "inspector:Frequency:0";
        Assert.True(w.Inspector.Commit(row, 0, "300", key));
        Assert.True(w.Inspector.Commit(row, 0, "400", key));
        Assert.True(w.Inspector.Commit(row, 0, "500", key));
        Assert.Equal(0, plays);
        Assert.Single(resource.History.Actions);
        w.Inspector.EndDrag();
        Assert.Equal(1, plays);
        Assert.Equal(500f, stream.Frequency);

        // A field commit, an action, undo and redo each replay once.
        w.Inspector.Commit(row, 0, "600");
        Assert.Equal(2, plays);
        ZzfxStreamInspector.ApplyPreset(stream, "laser", new Random(4), resource);
        Assert.Equal(3, plays);
        resource.History.Undo();
        Assert.Equal(4, plays);
        resource.History.Redo();
        Assert.Equal(5, plays);

        // Auto-play off: changes stay silent.
        w.AudioPreview.Stop();
        w.Layout.SetSoundDesignerAutoPlay(false);
        resource.History.Undo();
        Assert.Equal(5, plays);
    }

    [Fact]
    public void ExportWritesA16BitWavThatDecodes()
    {
        var stream = new ZzfxStream { Frequency = 440f, Sustain = 0.2f };
        var path = _project.Abs("Content/Export.wav");
        ZzfxStreamInspector.ExportWav(stream, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
        Assert.Equal(Zzfx.SampleRate, BitConverter.ToInt32(bytes, 24));

        var peaks = new float[ZzfxStreamInspector.Columns * 2];
        ZzfxStreamInspector.ComputePeaks(Zzfx.Generate(stream.Parameters), peaks);
        Assert.Equal(1f, peaks.Max(MathF.Abs), 3);
    }
}

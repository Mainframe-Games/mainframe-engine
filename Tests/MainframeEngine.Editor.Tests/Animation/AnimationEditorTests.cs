using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>G1.6: the Animation panel's model, every edit one undo step, keys from the inspector, preview and save.</summary>
public sealed class AnimationEditorTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };
    private readonly Node3D _lift = new() { Name = "Lift", Position = new Vector3(1, 2, 3) };
    private readonly OmniLight3D _lamp = new() { Name = "Lamp", Energy = 1.5f };
    private readonly AnimationPlayer _player = new() { Name = "Player", CallbackModeProcess = AnimationCallbackMode.Manual };
    private readonly AnimationEditor _editor = new(null);
    private readonly UndoRedo _history = new();

    public AnimationEditorTests()
    {
        _tree.ChangeScene(_scene);
        _scene.AddChild(_lift);
        _scene.AddChild(_lamp);
        _scene.AddChild(_player);
        _editor.EditForTests(_player, _history);
    }

    public void Dispose() => _tree.Shutdown();

    private Animation Edited => _editor.Animation!;

    [Fact]
    public void NewAnimationCreatesTheDefaultLibraryInOneStep()
    {
        Assert.Null(_editor.Animation);
        var name = _editor.NewAnimation("lift_up");
        Assert.Equal("lift_up", name);
        Assert.NotNull(_player.GetAnimation("lift_up"));
        Assert.Equal("lift_up", _editor.AnimationName);
        Assert.True(_history.Undo());
        Assert.Empty(_player.Libraries);
        Assert.True(_history.Redo());
        Assert.NotNull(_player.GetAnimation("lift_up"));
        _editor.NewAnimation("lift_up");
        Assert.NotNull(_player.GetAnimation("lift_up_2"));
    }

    [Fact]
    public void RenameDuplicateAndDeleteAreUndoable()
    {
        _editor.NewAnimation("open");
        _editor.RenameAnimation("open_door");
        Assert.NotNull(_player.GetAnimation("open_door"));
        _editor.DuplicateAnimation();
        Assert.NotNull(_player.GetAnimation("open_door_copy"));
        Assert.Equal("open_door_copy", _editor.AnimationName);
        _editor.DeleteAnimation();
        Assert.Null(_player.GetAnimation("open_door_copy"));
        Assert.True(_history.Undo());
        Assert.NotNull(_player.GetAnimation("open_door_copy"));
        Assert.True(_history.Undo());
        Assert.True(_history.Undo());
        Assert.NotNull(_player.GetAnimation("open"));
        Assert.Null(_player.GetAnimation("open_door"));
    }

    [Fact]
    public void InspectorKeysCreateTheRightTrackKindAtThePlayhead()
    {
        _editor.NewAnimation("lift");
        Assert.True(_editor.InsertPropertyKey(_lift, "Position"));
        var position = Assert.IsType<Position3DTrack>(Assert.Single(Edited.Tracks));
        Assert.Equal(new Vector3(1, 2, 3), position.Values[0]);
        Assert.Equal("Lift", position.Path.ToString());

        _editor.Scrub(0.5);
        _lift.Position = new Vector3(1, 5, 3);
        Assert.True(_editor.InsertPropertyKey(_lift, "Position"));
        Assert.Equal([0f, 0.5f], position.Times.Length == 2 ? Edited.Tracks[0].Times : position.Times);
        Assert.True(_editor.InsertPropertyKey(_lamp, "Energy"));
        var energy = Assert.IsType<ValueTrack>(Edited.Tracks[1]);
        Assert.Equal("Energy", energy.Property);
        Assert.True(_editor.InsertPropertyKey(_lift, "RotationDegrees"));
        Assert.IsType<Rotation3DTrack>(Edited.Tracks[2]);
        Assert.False(_editor.InsertPropertyKey(_lift, "Name")); // strings cannot be animated

        // Each key is one undo step; the first undo removes the rotation track again.
        Assert.True(_history.Undo());
        Assert.Equal(2, Edited.Tracks.Count);
    }

    [Fact]
    public void KTransformsKeysPositionRotationAndScale()
    {
        _editor.NewAnimation("pose");
        _editor.KeyTransforms([_lift]);
        Assert.Equal(3, Edited.Tracks.Count);
        Assert.True(_history.Undo());
        Assert.Empty(Edited.Tracks);
    }

    [Fact]
    public void ScrubbingPreviewsAndStoppingOrSavingPutsTheSceneBack()
    {
        _editor.NewAnimation("glow");
        _editor.InsertPropertyKey(_lamp, "Energy");
        _editor.Scrub(1);
        _lamp.Energy = 9f;
        _editor.InsertPropertyKey(_lamp, "Energy");
        _lamp.Energy = 1.5f; // the authored value

        _editor.Scrub(0.5);
        Assert.Equal(5.25f, _lamp.Energy, 3); // previewed
        var scene = new EditedSceneProbe(_scene);
        Assert.True(_editor.BeforeSave(scene.Scene));
        Assert.Equal(1.5f, _lamp.Energy); // a save writes the authored value
        _editor.AfterSave();
        Assert.Equal(5.25f, _lamp.Energy, 3);

        _editor.Stop();
        Assert.Equal(1.5f, _lamp.Energy);
    }

    [Fact]
    public void PlayingAdvancesThePreviewAndLoops()
    {
        _editor.NewAnimation("spin");
        _editor.SetLength(2f);
        _editor.CycleLoop();
        Assert.Equal(AnimationLoopMode.Linear, Edited.LoopMode);
        _editor.InsertPropertyKey(_lamp, "Energy");
        _editor.Play();
        _editor.Tick(2.5);
        Assert.Equal(0.5, _editor.Playhead, 3);
        Assert.True(_editor.Playing);
        _editor.Pause();
        _editor.Tick(1);
        Assert.Equal(0.5, _editor.Playhead, 3);
    }

    [Fact]
    public void KeyDragsSnapAreOneStepAndCancelPutsThemBack()
    {
        _editor.NewAnimation("lift");
        _editor.InsertPropertyKey(_lift, "Position");
        _editor.Scrub(1);
        _lift.Position = Vector3.One;
        _editor.InsertPropertyKey(_lift, "Position");
        var track = Edited.Tracks[0];
        _editor.Select(new AnimationKeyRef(0, 0), add: false);
        var steps = _history.Position;

        _editor.BeginKeyDrag();
        _editor.MoveSelectedKeys(0.31);
        _editor.MoveSelectedKeys(1.42); // past the other key: they swap order
        _editor.EndGesture();
        Assert.Equal(steps + 1, _history.Position);
        Assert.Equal([1f, 1.4333334f], Edited.Tracks[0].Times, new FloatComparer(1e-4f)); // snapped to 1/30 s
        Assert.Equal(Vector3.Zero + new Vector3(1, 2, 3), ((Position3DTrack)Edited.Tracks[0]).Values[1]);
        Assert.Equal(new AnimationKeyRef(0, 1), Assert.Single(_editor.Selected));

        _editor.BeginKeyDrag();
        _editor.MoveSelectedKeys(-0.5);
        _editor.CancelGesture();
        Assert.Equal(1.4333334f, Edited.Tracks[0].Times[1], 4);
        Assert.True(_history.Undo());
        Assert.Equal([0f, 1f], Edited.Tracks[0].Times);
        Assert.NotNull(track);
    }

    [Fact]
    public void DeleteRubberBandAndHitTests()
    {
        _editor.NewAnimation("glow");
        foreach (var t in new[] { 0.0, 0.5, 1.0 })
        {
            _editor.Scrub(t);
            _editor.InsertPropertyKey(_lamp, "Energy");
        }

        var y = AnimationEditor.RulerHeight + AnimationEditor.RowHeight / 2;
        Assert.Equal(new AnimationKeyRef(0, 1), _editor.HitKey(_editor.View.X(0.5), y));
        Assert.Null(_editor.HitKey(_editor.View.X(0.25), y));
        Assert.Equal(0, _editor.RowAt(y));
        Assert.Equal(-1, _editor.RowAt(5));
        _editor.SelectRect(_editor.View.X(0.4), 0, _editor.View.X(1.1), 200, add: false);
        Assert.Equal(2, _editor.Selected.Count);
        _editor.DeleteSelectedKeys();
        Assert.Equal([0f], Edited.Tracks[0].Times);
        Assert.True(_history.Undo());
        Assert.Equal(3, Edited.Tracks[0].Times.Length);
    }

    [Fact]
    public void TrackSettingsAndMethodKeysAreUndoable()
    {
        _editor.NewAnimation("door");
        _editor.InsertPropertyKey(_lift, "Position");
        _editor.CycleInterpolation(0);
        Assert.Equal(AnimationInterpolation.Cubic, ((InterpolatedTrack)Edited.Tracks[0]).Interpolation);
        _editor.ToggleLoopWrap(0);
        _editor.ToggleEnabled(0);
        Assert.False(Edited.Tracks[0].Enabled);
        var method = _editor.AddCallTrack(_lift, audio: false);
        _editor.InsertMethodKey(method, 0.5, "Open", "[]");
        Assert.Equal(["Open"], ((MethodTrack)Edited.Tracks[method]).Methods);
        _editor.SetKeyTransition(new AnimationKeyRef(0, 0), 2f);
        Assert.Equal(2f, ((InterpolatedTrack)Edited.Tracks[0]).Transitions[0]);
        _editor.SetKeyValue(new AnimationKeyRef(0, 0), [4f, 5f, 6f]);
        Assert.Equal(new Vector3(4, 5, 6), ((Position3DTrack)Edited.Tracks[0]).Values[0]);
        _editor.RemoveTrack(method);
        Assert.Single(Edited.Tracks);
        for (var i = 0; i < 8; i++)
            _history.Undo();
        Assert.Single(Edited.Tracks);
        Assert.Equal(AnimationInterpolation.Linear, ((InterpolatedTrack)Edited.Tracks[0]).Interpolation);
    }

    [Fact]
    public void MethodTracksOfferTheNodesAnimationMethodsAndAudioKeysUseThePlayersStream()
    {
        var sound = new AudioPlayer3D { Name = "Chime" };
        _scene.AddChild(sound);
        _editor.NewAnimation("door");
        var method = _editor.AddCallTrack(sound, audio: false);
        Assert.Equal(["Play", "Stop"], _editor.MethodsFor(method).Select(m => m.Name).Order());
        _editor.InsertMethodKey(method, 0.5, "Stop");
        Assert.Equal(["Stop"], ((MethodTrack)Edited.Tracks[method]).Methods);

        var audio = _editor.AddCallTrack(sound, audio: true);
        Assert.False(_editor.InsertAudioKey(audio, 0.25)); // no stream yet
        sound.Stream = new ZzfxStream { ResourceName = "chime" };
        Assert.True(_editor.InsertAudioKey(audio, 0.25));
        var track = (AudioTrack)Edited.Tracks[audio];
        Assert.Same(sound.Stream, Assert.Single(track.Streams));
        Assert.Equal([0f], track.StartOffsets);
        Assert.True(_history.Undo());
        Assert.Empty(((AudioTrack)Edited.Tracks[audio]).Times);
    }

    private sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= tolerance;

        public int GetHashCode(float obj) => 0;
    }

    // An EditedScene over the test scene's root, for the save hooks.
    private sealed class EditedSceneProbe(Node3D root)
    {
        public EditedScene Scene { get; } = new(root, null, new SubViewport { Name = "Probe" }, null);
    }
}

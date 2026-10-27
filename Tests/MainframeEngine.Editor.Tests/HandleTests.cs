using System.Numerics;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// G7.3: range handles — the built-in providers' math, the [Export(Handle)] hint provider, one history entry per drag,
/// cancel, and hit priority over the transform gizmo.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class HandleTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public HandleTests()
    {
        // Looking along -Z from +Z: the camera's right is +X, its up +Y.
        var camera = _editor.Scene.Camera;
        camera.Pivot = Vector3.Zero;
        camera.Distance = 12f;
        camera.Yaw = 0f;
        camera.Pitch = 0f;
    }

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private T Select<T>(T node) where T : Node
    {
        _editor.Scene.AddNode(node, _editor.Scene.Root);
        _editor.Scene.Selection.Set(node);
        _editor.Tick();
        return node;
    }

    private static HandleDrag Along(float along, bool snap = false) => new()
    {
        Along = along,
        Axis = Vector3.UnitX,
        Snap = snap,
        SnapStep = 0.5f,
        AngleStep = 15f,
        Camera = new EditorCamera(),
    };

    private Vector2 WindowPoint(Vector3 world)
    {
        var view = W.Viewport.ViewPixels;
        Assert.True(_editor.Scene.Camera.Project(world, view.X, view.Y, out var pixel));
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    private void Drag(Vector2 from, Vector2 to, bool release = true)
    {
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = from });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = to });
        if (release)
            _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = to });
        _editor.Tick();
    }

    [Fact]
    public void TheOmniRangeHandleSitsOnTheCameraRightAndADragIsOneUndoEntry()
    {
        var lamp = Select(new OmniLight3D { Name = "Lamp", Range = 3f });
        W.Viewport.CollectHandles(_editor.Scene);
        var handle = Assert.Single(W.Viewport.Handles);
        Assert.True(Vector3.Distance(new Vector3(3, 0, 0), handle.Position) < 1e-3f);
        Assert.Same(lamp, W.Viewport.HandleNode(0));
        var history = _editor.Scene.History.Actions.Count;

        var start = WindowPoint(handle.Position);
        Drag(start, WindowPoint(new Vector3(4.2f, 0, 0)));
        Assert.Equal(4.2f, lamp.Range, 1);
        Assert.Equal(history + 1, _editor.Scene.History.Actions.Count);
        Assert.Equal("Set Range on Lamp", _editor.Scene.History.UndoAction!.Name);
        _editor.Scene.History.Undo();
        Assert.Equal(3f, lamp.Range);
    }

    [Fact]
    public void HoverShowsALabelAndHandlesWinOverTheGizmo()
    {
        W.Gizmo.Mode = GizmoMode.Translate;
        var lamp = Select(new OmniLight3D { Name = "Lamp", Range = 0.5f });
        W.Viewport.CollectHandles(_editor.Scene);
        var at = WindowPoint(W.Viewport.Handles[0].Position);
        // The handle lies on the gizmo's X arrow: the press grabs the handle.
        Assert.NotEqual(GizmoHandle.None, W.Gizmo.HitTest(_editor.Scene.Camera, W.Viewport.ViewPixels, lamp.GlobalPosition, Quaternion.Identity, W.Viewport.LocalPixel(at)));
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = at });
        _editor.Tick();
        Assert.Equal("Range 0.50 m", W.ViewportPanel.HandleLabel);
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = at });
        Assert.True(W.Viewport.IsDraggingHandle);
        Assert.False(W.Viewport.IsDraggingGizmo);
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = at });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = at + new Vector2(0, 200) });
        _editor.Tick();
        Assert.Null(W.ViewportPanel.HandleLabel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeOrARightClickRestoresTheValue(bool escape)
    {
        var lamp = Select(new OmniLight3D { Name = "Lamp", Range = 3f });
        W.Viewport.CollectHandles(_editor.Scene);
        var history = _editor.Scene.History.Actions.Count;
        Drag(WindowPoint(W.Viewport.Handles[0].Position), WindowPoint(new Vector3(5, 0, 0)), release: false);
        Assert.True(lamp.Range > 4f);
        if (escape)
            _editor.Tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = true });
        else
            _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Right, Pressed = true, Position = WindowPoint(new Vector3(5, 0, 0)) });
        _editor.Tick();
        Assert.Equal(3f, lamp.Range);
        Assert.False(W.Viewport.IsInteracting);
        Assert.Equal(history, _editor.Scene.History.Actions.Count);
    }

    [Fact]
    public void SpotAnglesComeFromTheRimOffsetAndInnerStaysUnderOuter()
    {
        var spot = new SpotLight3D { Range = 4f, OuterConeAngle = 40f, InnerConeAngle = 30f };
        var provider = Assert.IsType<SpotLight3DHandles>(HandleProviders.Find(typeof(SpotLight3D)));
        var handles = new List<EditorHandle>();
        provider.GetHandles(spot, new EditorCamera(), new Vector2(800, 600), handles);
        Assert.Equal(3, handles.Count);
        Assert.True(Vector3.Distance(spot.GlobalForward * 4f, handles[SpotLight3DHandles.RangeHandle].Position) < 1e-3f);

        provider.Drag(spot, SpotLight3DHandles.OuterHandle, Along(MathF.Tan(float.DegreesToRadians(20f)) * 4f));
        Assert.Equal(20f, spot.OuterConeAngle, 2);
        Assert.Equal(20f, spot.InnerConeAngle, 2); // pushed down with the outer angle
        provider.Drag(spot, SpotLight3DHandles.InnerHandle, Along(4f)); // 45°: clamped to the outer angle
        Assert.Equal(20f, spot.InnerConeAngle, 2);
        provider.Drag(spot, SpotLight3DHandles.OuterHandle, Along(MathF.Tan(float.DegreesToRadians(32f)) * 4f, snap: true));
        Assert.Equal(30f, spot.OuterConeAngle, 2); // snapped to the 15° rotate step
        Assert.Equal(["OuterConeAngle", "InnerConeAngle"], provider.Properties(spot, SpotLight3DHandles.OuterHandle).ToArray());
        provider.Drag(spot, SpotLight3DHandles.RangeHandle, Along(10_000f));
        Assert.Equal(4096f, spot.Range); // the [Export(Range = "0,4096,…")] clamp
    }

    [Fact]
    public void AudioUnitSizeStaysUnderTheMaxDistanceWhichIsHollowWhenUnlimited()
    {
        var audio = new AudioPlayer3D { UnitSize = 2f, MaxDistance = 0f };
        var provider = Assert.IsType<AudioPlayer3DHandles>(HandleProviders.Find(typeof(AudioPlayer3D)));
        var handles = new List<EditorHandle>();
        provider.GetHandles(audio, new EditorCamera(), new Vector2(800, 600), handles);
        Assert.Equal(HandleStyle.Hollow, handles[AudioPlayer3DHandles.MaxDistanceHandle].Style);
        Assert.Equal("Max Distance: unlimited — drag to set", provider.Describe(audio, AudioPlayer3DHandles.MaxDistanceHandle));

        provider.Drag(audio, AudioPlayer3DHandles.MaxDistanceHandle, Along(10.3f, snap: true));
        Assert.Equal(10.5f, audio.MaxDistance); // snapped to the 0.5 move step
        provider.Drag(audio, AudioPlayer3DHandles.UnitSizeHandle, Along(30f));
        Assert.Equal(10.5f, audio.UnitSize); // ≤ the max distance
        provider.Drag(audio, AudioPlayer3DHandles.MaxDistanceHandle, Along(1f));
        Assert.Equal(10.5f, audio.MaxDistance); // ≥ the unit size
    }

    [Fact]
    public void TwoDimensionalProvidersDragAlongX()
    {
        var audio = new AudioPlayer2D { MaxDistance = 300f, Position = new Vector2(10, 20) };
        var provider = HandleProviders.Find(typeof(AudioPlayer2D))!;
        var handles = new List<EditorHandle>();
        provider.GetHandles(audio, new EditorCamera { Is2D = true }, new Vector2(800, 600), handles);
        Assert.Equal(new Vector3(310, 20, 0), handles[0].Position);
        provider.Drag(audio, 0, Along(512f));
        Assert.Equal(512f, audio.MaxDistance);

        var light = new PointLight2D();
        handles.Clear();
        HandleProviders.Find(typeof(PointLight2D))!.GetHandles(light, new EditorCamera { Is2D = true }, new Vector2(800, 600), handles);
        Assert.Empty(handles); // no texture: no handle
    }

    [Fact]
    public void ExportHandleHintsGiveGameNodesStockHandles()
    {
        var info = TypeRegistry.GetRequired(typeof(HandleHintNode));
        Assert.Equal(HandleHint.Radius, info.FindProperty("AggroRadius")!.Hints.Handle);
        Assert.Equal(HandleHint.Length, info.FindProperty("Reach")!.Hints.Handle);
        Assert.Same(HintHandleProvider.Instance, HandleProviders.Find(typeof(HandleHintNode)));
        Assert.Null(HandleProviders.Find(typeof(Node3D)));
        Assert.IsType<OmniLight3DHandles>(HandleProviders.Find(typeof(OmniLight3D)));

        var node = Select(new HandleHintNode { Name = "Guard", Position = new Vector3(1, 0, 0) });
        W.Viewport.CollectHandles(_editor.Scene);
        Assert.Equal(2, W.Viewport.Handles.Count); // the int is ignored
        Assert.True(Vector3.Distance(new Vector3(9, 0, 0), W.Viewport.Handles[0].Position) < 1e-3f); // radius on the camera's right
        Assert.True(Vector3.Distance(new Vector3(1, 0, -2), W.Viewport.Handles[1].Position) < 1e-3f); // reach along -Z

        var provider = HintHandleProvider.Instance;
        provider.Drag(node, 0, Along(70f));
        Assert.Equal(50f, node.AggroRadius); // the Range clamp
        Assert.Equal("Aggro Radius 50.00 m", provider.Describe(node, 0));
        Drag(WindowPoint(W.Viewport.Handles[1].Position), WindowPoint(new Vector3(1, 0, -3.5f)));
        Assert.Equal("Set Reach on Guard", _editor.Scene.History.UndoAction!.Name);
    }

    [Fact]
    public void LockedNodesShowNoHandles()
    {
        var lamp = Select(new OmniLight3D { Name = "Lamp", Range = 3f });
        lamp.AddToGroup(EditorLock.Group, persistent: true);
        W.Viewport.CollectHandles(_editor.Scene);
        Assert.Empty(W.Viewport.Handles);
    }
}

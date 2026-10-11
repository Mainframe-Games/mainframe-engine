using System.Numerics;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Scene;

/// <summary><see cref="InputRebinder"/> with synthetic events: what it captures, ignores, passes on and handles.</summary>
public sealed class InputRebinderTests : IDisposable
{
    private readonly InputEventKey _key = new();
    private readonly InputEventMouseButton _mouse = new();
    private readonly InputEventGamepadButton _pad = new();
    private readonly InputEventGamepadAxis _axis = new();
    private readonly SceneTree _tree = new();
    private readonly InputRebinder _rebinder = new() { Name = "Rebinder" };
    private readonly RecordingUi _ui = new();
    private readonly Probe _probe = new() { Name = "Probe" };
    private readonly List<string> _signals = [];

    public InputRebinderTests()
    {
        _tree.Servers.Register(_ui);
        _tree.Root.AddChild(_probe);
        _tree.Root.AddChild(_rebinder);
        _rebinder.Captured += OnCaptured;
        _rebinder.Cleared += () => _signals.Add("cleared");
        _rebinder.Cancelled += () => _signals.Add("cancelled");
    }

    private void OnCaptured(InputBinding binding) => _signals.Add("captured " + binding);

    public void Dispose() => _tree.Shutdown();

    private bool Key(Key key, bool pressed)
    {
        _key.Key = key;
        _key.Pressed = pressed;
        return _tree.PushInput(_key);
    }

    private bool Mouse(MouseButton button, bool pressed)
    {
        _mouse.Button = button;
        _mouse.Pressed = pressed;
        return _tree.PushInput(_mouse);
    }

    private bool Pad(ButtonName button, bool pressed, int device = 0)
    {
        _pad.Device = device;
        _pad.Button = button;
        _pad.Pressed = pressed;
        return _tree.PushInput(_pad);
    }

    private bool Axis(GamepadAxis axis, Vector2 value, int device = 0)
    {
        _axis.Device = device;
        _axis.Axis = axis;
        _axis.Value = value;
        return _tree.PushInput(_axis);
    }

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    // Begin, then the frame that starts the capture.
    private void Start(InputDeviceClass deviceClass)
    {
        _rebinder.Begin(deviceClass);
        Tick();
    }

    [Fact]
    public void TheNodeIsCheapSeesInputBeforeTheUiAndIsRegistered()
    {
        var rebinder = new InputRebinder();
        Assert.True(rebinder.InputBeforeUi);
        Assert.Equal(ProcessMode.Always, rebinder.ProcessMode); // settings menus pause the game
        Assert.False(rebinder.IsCapturing);
        Assert.Equal(5f, rebinder.TimeoutSeconds);

        TypeRegistry.EnsureRegistered(typeof(InputRebinder).Assembly);
        var info = TypeRegistry.Get("InputRebinder");
        Assert.NotNull(info);
        Assert.IsType<InputRebinder>(TypeRegistry.CreateNode("InputRebinder"));
        Assert.NotNull(info.FindSignal("Captured"));
        Assert.NotNull(info.FindSignal("Cleared"));
        Assert.NotNull(info.FindSignal("Cancelled"));
        Assert.Throws<InvalidOperationException>(() => rebinder.Begin(InputDeviceClass.KeyboardMouse)); // not in a tree
    }

    [Fact]
    public void AKeyIsCapturedAndHandledSoTheUiAndTheGameNeverSeeIt()
    {
        Start(InputDeviceClass.KeyboardMouse);
        Assert.True(_rebinder.IsCapturing);

        Assert.True(Key(Silk.NET.Input.Key.F, true));
        Assert.Equal(["captured key:F"], _signals);
        Assert.False(_rebinder.IsCapturing);
        Assert.True(Key(Silk.NET.Input.Key.F, true));  // a repeat after the capture: still handled
        Assert.True(Key(Silk.NET.Input.Key.F, false)); // and the release
        Assert.Equal(0, _ui.Events);
        Assert.Equal(0, _probe.Events);

        Assert.False(Key(Silk.NET.Input.Key.F, true)); // idle again: everything goes on
        Assert.Equal(1, _ui.Events);
        Assert.Equal(1, _probe.Events);
    }

    [Fact]
    public void CaptureStartsOnTheNextFrameAndTheOpeningClickIsIgnored()
    {
        // The menu opens the capture on the press (the worst case): its release must not bind "Mouse Left".
        Assert.False(Mouse(MouseButton.Left, true));
        _rebinder.Begin(InputDeviceClass.KeyboardMouse);
        Assert.True(Key(Silk.NET.Input.Key.G, true)); // before the next frame: taken, not captured
        Assert.True(Key(Silk.NET.Input.Key.G, false));
        Tick();
        Assert.False(Mouse(MouseButton.Left, false)); // the release goes where the press went (the UI)
        Assert.Empty(_signals);
        Assert.True(_rebinder.IsCapturing);

        Assert.True(Mouse(MouseButton.Left, true)); // a new click binds
        Assert.Equal(["captured mouse:Left"], _signals);
    }

    [Fact]
    public void AClickThatOpenedTheCaptureOnReleaseIsNotCaptured()
    {
        Mouse(MouseButton.Left, true);
        Mouse(MouseButton.Left, false); // RmlUi's click fires here
        Start(InputDeviceClass.KeyboardMouse);
        Tick(10);

        Assert.Empty(_signals);
        Assert.True(Mouse(MouseButton.Right, true));
        Assert.Equal(["captured mouse:Right"], _signals);
    }

    [Fact]
    public void KeyRepeatsAreIgnored()
    {
        Assert.False(Key(Silk.NET.Input.Key.Enter, true)); // Enter on the focused button opens the capture…
        Start(InputDeviceClass.KeyboardMouse);

        Assert.True(Key(Silk.NET.Input.Key.Enter, true));  // …and keeps repeating: handled (no second click), not captured
        Assert.True(Key(Silk.NET.Input.Key.Enter, true));
        Assert.False(Key(Silk.NET.Input.Key.Enter, false));
        Assert.Empty(_signals);

        Assert.True(Key(Silk.NET.Input.Key.Enter, true)); // pressed again: a real press
        Assert.Equal(["captured key:Enter"], _signals);
    }

    [Fact]
    public void EscapeCancelsAndBackspaceOrDeleteClear()
    {
        Start(InputDeviceClass.KeyboardMouse);
        Assert.True(Key(Silk.NET.Input.Key.Escape, true));
        Assert.True(Key(Silk.NET.Input.Key.Escape, false)); // the menu does not close on the release either
        Start(InputDeviceClass.KeyboardMouse);
        Assert.True(Key(Silk.NET.Input.Key.Backspace, true));
        Start(InputDeviceClass.Gamepad);
        Assert.True(Key(Silk.NET.Input.Key.Delete, true));

        Assert.Equal(["cancelled", "cleared", "cleared"], _signals);
        Assert.Equal(0, _ui.Events);
    }

    [Fact]
    public void GamepadBackCancels()
    {
        Start(InputDeviceClass.Gamepad);
        Assert.True(Pad(ButtonName.Back, true, device: 3));

        Assert.Equal(["cancelled"], _signals);
    }

    [Fact]
    public void TheDebuggerAndOverlayKeysPassThroughAndNeverBind()
    {
        Start(InputDeviceClass.KeyboardMouse);

        Assert.False(Key(Silk.NET.Input.Key.F8, true));
        Assert.False(Key(Silk.NET.Input.Key.F8, false));
        Assert.False(Key(Silk.NET.Input.Key.F12, true));
        Assert.False(Key(Silk.NET.Input.Key.F12, false));
        Assert.Equal(4, _ui.Events);
        Assert.Empty(_signals);
        Assert.True(_rebinder.IsCapturing);
    }

    [Fact]
    public void GamesExtendOrShrinkTheReservedList()
    {
        _rebinder.ReservedInputs.Add(InputBinding.Key(Silk.NET.Input.Key.F1));
        _rebinder.ReservedInputs.Remove(InputBinding.Key(Silk.NET.Input.Key.Delete));
        Start(InputDeviceClass.KeyboardMouse);

        Assert.False(Key(Silk.NET.Input.Key.F1, true)); // the game's help key keeps working
        Assert.Empty(_signals);
        Assert.True(Key(Silk.NET.Input.Key.Delete, true)); // no longer reserved: binds
        Assert.Equal(["captured key:Delete"], _signals);
    }

    [Fact]
    public void EachCaptureTakesOnlyItsDeviceClass()
    {
        Start(InputDeviceClass.KeyboardMouse);
        Assert.True(Pad(ButtonName.A, true));                         // handled, ignored
        Assert.True(Axis(GamepadAxis.RightTrigger, new Vector2(1f, 0f)));
        Assert.Empty(_signals);
        Assert.True(Key(Silk.NET.Input.Key.Space, true));
        Assert.True(Pad(ButtonName.A, false));
        Key(Silk.NET.Input.Key.Space, false);
        Axis(GamepadAxis.RightTrigger, Vector2.Zero);

        Start(InputDeviceClass.Gamepad);
        Assert.True(Key(Silk.NET.Input.Key.Q, true));
        Assert.True(Mouse(MouseButton.Left, true));
        Assert.Single(_signals);
        Assert.True(Pad(ButtonName.Y, true, device: 2)); // any pad's press binds every pad

        Assert.Equal(["captured key:Space", "captured pad:Y"], _signals);
    }

    [Fact]
    public void AxesBindPastTheThresholdAndTriggersBecomePositiveAxes()
    {
        Start(InputDeviceClass.Gamepad);
        Assert.True(Axis(GamepadAxis.LeftStick, new Vector2(0.1f, -0.55f))); // under 0.6: nothing (still handled)
        Assert.Empty(_signals);
        Assert.True(Axis(GamepadAxis.LeftStick, new Vector2(0.65f, -0.8f))); // the stronger direction wins
        Assert.Equal(["captured axis:LeftY-"], _signals);
        Assert.True(Axis(GamepadAxis.LeftStick, new Vector2(0f, -0.9f)));     // still held: handled
        Assert.True(Axis(GamepadAxis.LeftStick, Vector2.Zero));               // back: handled once
        Assert.False(Axis(GamepadAxis.LeftStick, Vector2.Zero));

        Start(InputDeviceClass.Gamepad);
        Assert.True(Axis(GamepadAxis.RightTrigger, new Vector2(0.9f, 0f), device: 1));
        Assert.Equal(["captured axis:LeftY-", "captured axis:RightTrigger+"], _signals);
        Assert.Equal("RT", InputBinding.Parse("axis:RightTrigger+").DisplayName());
    }

    [Fact]
    public void AnAxisHeldWhenTheCaptureOpensIsIgnoredUntilItReturns()
    {
        Assert.False(Axis(GamepadAxis.LeftStick, new Vector2(0f, 1f))); // pushed down to reach the button
        Start(InputDeviceClass.Gamepad);

        Assert.True(Axis(GamepadAxis.LeftStick, new Vector2(0f, 0.95f)));
        Assert.Empty(_signals);
        Assert.True(Axis(GamepadAxis.LeftStick, Vector2.Zero));
        Assert.True(Axis(GamepadAxis.LeftStick, new Vector2(0f, 0.9f))); // pushed again: binds
        Assert.Equal(["captured axis:LeftY+"], _signals);
    }

    [Fact]
    public void TheCaptureTimesOut()
    {
        _rebinder.TimeoutSeconds = 0.5f;
        Start(InputDeviceClass.KeyboardMouse);

        Tick(29); // 0.48 s
        Assert.True(_rebinder.IsCapturing);
        Tick(2);
        Assert.False(_rebinder.IsCapturing);
        Assert.Equal(["cancelled"], _signals);

        _rebinder.TimeoutSeconds = 0f; // waits forever
        Start(InputDeviceClass.KeyboardMouse);
        Tick(600);
        Assert.True(_rebinder.IsCapturing);
    }

    [Fact]
    public void CancelStopsTheCaptureAndCapturingWorksWhilePaused()
    {
        _tree.Paused = true;
        Start(InputDeviceClass.KeyboardMouse);
        _rebinder.Cancel();
        _rebinder.Cancel(); // idle: nothing

        Start(InputDeviceClass.KeyboardMouse);
        Assert.True(Key(Silk.NET.Input.Key.P, true));
        Assert.Equal(["cancelled", "captured key:P"], _signals);
    }

    [Fact]
    public void TextAndWheelEventsAreHandledWhileCapturingButMouseMotionIsNot()
    {
        Start(InputDeviceClass.KeyboardMouse);

        Assert.True(_tree.PushInput(new InputEventText { Character = 'a' })); // a focused text field must not type
        Assert.True(_tree.PushInput(new InputEventMouseWheel { Delta = new Vector2(0, 1) }));
        Assert.False(_tree.PushInput(new InputEventMouseMotion { Position = new Vector2(3, 4) }));
    }

    [Fact]
    public void OnInputDoesNotAllocate()
    {
        var captured = 0;
        _rebinder.Captured -= OnCaptured; // (formats a string)
        _rebinder.Captured += _ => captured++;
        void Window()
        {
            for (var i = 0; i < 50; i++)
            {
                Start(InputDeviceClass.KeyboardMouse);
                Key(Silk.NET.Input.Key.F8, true);
                Key(Silk.NET.Input.Key.F8, false);
                Key(Silk.NET.Input.Key.Unknown, true);
                Pad(ButtonName.A, true);
                Pad(ButtonName.A, false);
                Key(Silk.NET.Input.Key.E, true);
                Key(Silk.NET.Input.Key.E, true);
                Key(Silk.NET.Input.Key.E, false);
                Start(InputDeviceClass.Gamepad);
                Axis(GamepadAxis.LeftStick, new Vector2(0.2f, 0.1f));
                Mouse(MouseButton.Left, true);
                Mouse(MouseButton.Left, false);
                Axis(GamepadAxis.RightTrigger, new Vector2(1f, 0f));
                Axis(GamepadAxis.RightTrigger, Vector2.Zero);
                Start(InputDeviceClass.KeyboardMouse);
                Key(Silk.NET.Input.Key.Escape, true);
                Key(Silk.NET.Input.Key.Escape, false);
            }
        }

        _signals.Capacity = 1024;
        Window();
        _signals.Clear();
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            Window();
            _signals.Clear();
        }));
        Assert.True(captured > 0);
    }

    private sealed class RecordingUi : IInputServer
    {
        public int Events { get; private set; }

        public bool HandleInput(InputEvent inputEvent)
        {
            Events++;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class Probe : Node
    {
        public int Events { get; private set; }

        protected override void OnInput(InputEvent inputEvent) => Events++;
    }
}

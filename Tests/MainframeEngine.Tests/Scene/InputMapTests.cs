using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Scene;

public sealed class InputMapTests
{
    private static readonly InputEventKey KeyEvent = new();
    private static readonly InputEventMouseButton MouseEvent = new();
    private static readonly InputEventGamepadButton PadEvent = new();
    private static readonly InputEventGamepadAxis AxisEvent = new();

    private static InputMap Map() => new InputMap()
        .Bind("jump", "key:Space", "pad:A")
        .Bind("fire", "mouse:Left", "axis:RightTrigger+")
        .Bind("left", "key:A", "axis:LeftX-")
        .Bind("right", "key:D", "axis:LeftX+")
        .Bind("up", "key:W", "axis:LeftY-")
        .Bind("down", "key:S", "axis:LeftY+")
        .Bind("p2_jump", "pad1:B");

    private static SceneTree Tree(InputMap map)
    {
        var tree = new SceneTree();
        tree.Input.Map = map;
        return tree;
    }

    private static void Key(SceneTree tree, Key key, bool pressed)
    {
        KeyEvent.Key = key;
        KeyEvent.Pressed = pressed;
        tree.PushInput(KeyEvent);
    }

    private static void Pad(SceneTree tree, int device, ButtonName button, bool pressed)
    {
        PadEvent.Device = device;
        PadEvent.Button = button;
        PadEvent.Pressed = pressed;
        tree.PushInput(PadEvent);
    }

    private static void Axis(SceneTree tree, int device, GamepadAxis axis, Vector2 value)
    {
        AxisEvent.Device = device;
        AxisEvent.Axis = axis;
        AxisEvent.Value = value;
        tree.PushInput(AxisEvent);
    }

    private static void Tick(SceneTree tree, float delta = 1f / 60f)
    {
        var time = new GameTime { DeltaTime = delta };
        tree.Tick(time);
    }

    [Theory]
    [InlineData("key:Space", InputBindingKind.Key, (int)Silk.NET.Input.Key.Space, 0, -1)]
    [InlineData("KEY: escape", InputBindingKind.Key, (int)Silk.NET.Input.Key.Escape, 0, -1)]
    [InlineData("mouse:Right", InputBindingKind.MouseButton, (int)MouseButton.Right, 0, -1)]
    [InlineData("pad:Start", InputBindingKind.GamepadButton, (int)ButtonName.Start, 0, -1)]
    [InlineData("pad3:DPadUp", InputBindingKind.GamepadButton, (int)ButtonName.DPadUp, 0, 3)]
    [InlineData("axis:LeftY-", InputBindingKind.GamepadAxis, (int)GamepadAxisCode.LeftY, -1, -1)]
    [InlineData("axis2:RightTrigger+", InputBindingKind.GamepadAxis, (int)GamepadAxisCode.RightTrigger, 1, 2)]
    public void BindingsParse(string text, InputBindingKind kind, int code, int direction, int device)
    {
        var binding = InputBinding.Parse(text);

        Assert.Equal(kind, binding.Kind);
        Assert.Equal(code, binding.Code);
        Assert.Equal(direction, binding.Direction);
        Assert.Equal(device, binding.Device);
        Assert.Equal(binding, InputBinding.Parse(binding.ToString())); // the text form round-trips
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("key:")]
    [InlineData("key:NotAKey")]
    [InlineData("key:32")]
    [InlineData("key:Unknown")]
    [InlineData("mouse:Middle2")]
    [InlineData("pad:Z")]
    [InlineData("padX:A")]
    [InlineData("pad99:A")]
    [InlineData("pad8:A")]
    [InlineData("axis8:LeftX+")]
    [InlineData("axis:LeftX")]
    [InlineData("axis:Sideways+")]
    [InlineData("joystick:A")]
    public void InvalidBindingsAreRejected(string text)
    {
        Assert.False(InputBinding.TryParse(text, out _));
        Assert.Throws<FormatException>(() => InputBinding.Parse(text));
    }

    [Fact]
    public void GamepadIndicesMatchTheTrackedPads()
    {
        Assert.Equal(InputState.MaxGamepads - 1, InputBinding.GamepadButton(ButtonName.A, InputState.MaxGamepads - 1).Device);
        Assert.Equal(-1, InputBinding.GamepadAxis(GamepadAxisCode.LeftX, 1, -5).Device);
        Assert.Throws<ArgumentOutOfRangeException>(() => InputBinding.GamepadButton(ButtonName.A, InputState.MaxGamepads));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputBinding.GamepadAxis(GamepadAxisCode.LeftX, 1, InputState.MaxGamepads));
    }

    [Fact]
    public void MapEditsBumpTheVersionAndKeepOrder()
    {
        var map = new InputMap();
        var v0 = map.Version;
        map.Bind("b", "key:B").Bind("a", "key:A");
        map.Bind("a", "key:A"); // duplicate: ignored
        Assert.Equal(["b", "a"], map.Actions.Select(a => a.Name));
        Assert.Single(map.GetAction("a")!.Bindings);
        Assert.True(map.Version > v0);

        Assert.True(map.RemoveAction("b"));
        Assert.Equal(0, map.IndexOf("a"));
        Assert.False(map.HasAction("b"));
        Assert.True(map.Unbind("a", InputBinding.Key(Silk.NET.Input.Key.A)));
        Assert.Empty(map.GetAction("a")!.Bindings);

        var clone = Map().Clone();
        Assert.Equal(Map().Actions.Select(a => (a.Name, a.Deadzone, string.Join(',', a.Bindings))),
            clone.Actions.Select(a => (a.Name, a.Deadzone, string.Join(',', a.Bindings))));
    }

    [Fact]
    public void KeysPressAndReleaseActionsWithJustEdges()
    {
        var tree = Tree(Map());
        var input = tree.Input;

        Key(tree, Silk.NET.Input.Key.Space, true);
        Assert.True(input.IsActionPressed("jump"));
        Assert.True(input.IsActionJustPressed("jump"));
        Assert.Equal(1f, input.GetActionStrength("jump"));
        Assert.True(input.IsKeyPressed(Silk.NET.Input.Key.Space));

        Tick(tree);
        Assert.True(input.IsActionPressed("jump"));
        Assert.False(input.IsActionJustPressed("jump")); // only for the frame after the press

        Key(tree, Silk.NET.Input.Key.Space, false);
        Assert.False(input.IsActionPressed("jump"));
        Assert.True(input.IsActionJustReleased("jump"));
        Tick(tree);
        Assert.False(input.IsActionJustReleased("jump"));
    }

    [Fact]
    public void TwoInputsOnOneActionStayPressedUntilBothRelease()
    {
        var tree = Tree(Map());
        Key(tree, Silk.NET.Input.Key.Space, true);
        Pad(tree, 0, ButtonName.A, true);
        Key(tree, Silk.NET.Input.Key.Space, false);

        Assert.True(tree.Input.IsActionPressed("jump"));
        Pad(tree, 0, ButtonName.A, false);
        Assert.False(tree.Input.IsActionPressed("jump"));
    }

    [Fact]
    public void AxesUseTheDeadzoneAndRescale()
    {
        var map = Map();
        map.GetAction("right")!.Deadzone = 0.2f;
        var tree = Tree(map);
        var input = tree.Input;

        Axis(tree, 0, GamepadAxis.LeftStick, new Vector2(0.1f, 0));
        Assert.False(input.IsActionPressed("right"));
        Assert.Equal(0f, input.GetActionStrength("right"));

        Axis(tree, 0, GamepadAxis.LeftStick, new Vector2(0.6f, 0));
        Assert.True(input.IsActionPressed("right"));
        Assert.Equal(0.5f, input.GetActionStrength("right"), 4);
        Assert.False(input.IsActionPressed("left"));
        Assert.Equal(0.5f, input.GetAxis("left", "right"), 4);

        Axis(tree, 0, GamepadAxis.LeftStick, new Vector2(-1f, 0));
        Assert.Equal(1f, input.GetActionStrength("left"));
        Assert.False(input.IsActionPressed("right"));
        Assert.Equal(-1f, input.GetAxis("left", "right"));

        Axis(tree, 0, GamepadAxis.RightTrigger, new Vector2(0.75f, 0));
        Assert.Equal(0.5f, input.GetActionStrength("fire"), 4); // default deadzone 0.5
        Assert.Equal(0.75f, input.GetGamepadAxis(0, GamepadAxisCode.RightTrigger));
    }

    [Fact]
    public void GetVectorCombinesFourActionsAndClampsTheLength()
    {
        var tree = Tree(Map());
        Key(tree, Silk.NET.Input.Key.D, true);
        Key(tree, Silk.NET.Input.Key.S, true);

        var v = tree.Input.GetVector("left", "right", "up", "down");
        Assert.Equal(1f, v.Length(), 4);
        Assert.Equal(v.X, v.Y, 4);
    }

    [Fact]
    public void DeviceSpecificBindingsIgnoreOtherPads()
    {
        var tree = Tree(Map());
        Pad(tree, 0, ButtonName.B, true);
        Assert.False(tree.Input.IsActionPressed("p2_jump"));
        Pad(tree, 1, ButtonName.B, true);
        Assert.True(tree.Input.IsActionPressed("p2_jump"));
        Assert.True(tree.Input.IsGamepadButtonPressed(1, ButtonName.B));

        Pad(tree, InputState.MaxGamepads + 3, ButtonName.A, true); // out of range: ignored, no crash
        Assert.False(tree.Input.IsActionPressed("jump"));
    }

    [Fact]
    public void JustPressedInPhysicsIsTrueForTheFirstStepOnly()
    {
        var tree = Tree(Map());
        var seen = new List<bool>();
        tree.Root.AddChild(new PhysicsProbe(seen));

        Key(tree, Silk.NET.Input.Key.Space, true);
        Tick(tree, 3f / 60f); // three physics steps in one frame

        Assert.Equal([true, false, false], seen);
        Tick(tree);
        Assert.Equal([true, false, false, false], seen);
    }

    [Fact]
    public void UiConsumedEventsStillUpdatePolledState()
    {
        var tree = Tree(Map());
        tree.Servers.Register(new ConsumingInputServer());

        Key(tree, Silk.NET.Input.Key.Space, true);
        Assert.True(tree.Input.IsActionPressed("jump"));
        Key(tree, Silk.NET.Input.Key.Space, false);
        Assert.False(tree.Input.IsActionPressed("jump")); // never stuck
    }

    [Fact]
    public void ActionPressAndReleaseSimulateInput()
    {
        var tree = Tree(Map());
        tree.Input.ActionPress("fire", 0.25f);
        Assert.True(tree.Input.IsActionJustPressed("fire"));
        Assert.Equal(0.25f, tree.Input.GetActionStrength("fire"));

        tree.Input.ActionRelease("fire");
        Assert.False(tree.Input.IsActionPressed("fire"));
        Assert.True(tree.Input.IsActionJustReleased("fire"));

        tree.Input.ActionPress("missing"); // unknown actions are ignored
        Assert.False(tree.Input.IsActionPressed("missing"));
    }

    [Fact]
    public void ReleaseAllClearsEverything()
    {
        var tree = Tree(Map());
        Key(tree, Silk.NET.Input.Key.Space, true);
        Axis(tree, 0, GamepadAxis.LeftStick, new Vector2(1, 0));
        tree.Input.ActionPress("fire");

        tree.Input.ReleaseAll();

        Assert.False(tree.Input.IsActionPressed("jump"));
        Assert.False(tree.Input.IsActionPressed("right"));
        Assert.False(tree.Input.IsActionPressed("fire"));
        Assert.False(tree.Input.IsKeyPressed(Silk.NET.Input.Key.Space));
    }

    [Fact]
    public void ChangingTheMapKeepsHeldInputsWithoutEdges()
    {
        var tree = Tree(new InputMap());
        Key(tree, Silk.NET.Input.Key.Space, true);
        Tick(tree);

        tree.Input.Map.Bind("jump", "key:Space"); // edited live
        Assert.True(tree.Input.IsActionPressed("jump"));
        Assert.False(tree.Input.IsActionJustPressed("jump"));

        tree.Input.Map = Map(); // replaced
        Assert.True(tree.Input.IsActionPressed("jump"));
    }

    [Fact]
    public void EventsCanBeTestedAgainstActions()
    {
        var map = Map();
        KeyEvent.Key = Silk.NET.Input.Key.Space;
        KeyEvent.Pressed = true;
        Assert.True(KeyEvent.IsAction("jump", map));
        Assert.True(KeyEvent.IsActionPressed("jump", map));
        Assert.False(KeyEvent.IsActionReleased("jump", map));
        Assert.False(KeyEvent.IsAction("fire", map));

        MouseEvent.Button = MouseButton.Left;
        MouseEvent.Pressed = false;
        Assert.True(MouseEvent.IsActionReleased("fire", map));

        AxisEvent.Device = 0;
        AxisEvent.Axis = GamepadAxis.LeftStick;
        AxisEvent.Value = new Vector2(0, -1);
        Assert.True(AxisEvent.IsActionPressed("up", map));
        Assert.Equal(1f, AxisEvent.GetActionStrength("up", map));
        Assert.False(AxisEvent.IsAction("fire", map)); // a trigger binding ignores stick events
    }

    [Fact]
    public void TheMouseMapsThroughTheStretchAndCanvasTransformsAndAxesReadRaw()
    {
        var tree = Tree(Map());
        var previous = Input.Current;
        try
        {
            Input.Current = tree.Input;
            var root = tree.Root;
            root.ContentScaleMode = ContentScaleMode.CanvasItems;
            root.ContentScaleAspect = ContentScaleAspect.Expand;
            root.ContentScaleSize = new Vector2(480, 270);
            root.SetSize(new Vector2(1920, 1080));   // ×4 stretch
            root.PointScale = 2;                     // a HiDPI window: 960×540 points
            root.CanvasTransform = Transform2D.FromTrs(new Vector2(-100, 0), 0, Vector2.One); // a camera offset
            var item = new Node2D { Name = "Item", Position = new Vector2(10, 0) };
            tree.ChangeScene(item);

            tree.PushInput(new InputEventMouseMotion { Position = new Vector2(480, 270) }); // window centre (points)
            Assert.Equal(new Vector2(480, 270), Input.MousePosition);
            Assert.Equal(new Vector2(240, 135), root.GetMousePosition());
            Assert.Equal(new Vector2(340, 135), item.GetGlobalMousePosition());
            Assert.Equal(new Vector2(330, 135), item.GetLocalMousePosition());

            tree.PushInput(new InputEventGamepadAxis { Device = 0, Axis = GamepadAxis.RightStick, Value = new Vector2(0.5f, -0.25f) });
            Assert.Equal(0.5f, Input.GetJoyAxis(0, GamepadAxisCode.RightX));
            Assert.Equal(-0.25f, Input.GetJoyAxis(0, GamepadAxisCode.RightY));
        }
        finally
        {
            Input.Current = previous;
        }
    }

    [Fact]
    public void StaticInputForwardsToTheCurrentState()
    {
        var tree = Tree(Map());
        var previous = Input.Current;
        try
        {
            Input.Current = null;
            Assert.False(Input.IsActionPressed("jump"));
            Assert.Equal(Vector2.Zero, Input.GetVector("left", "right", "up", "down"));

            Input.Current = tree.Input;
            Key(tree, Silk.NET.Input.Key.Space, true);
            Assert.True(Input.IsActionPressed("jump"));
            Assert.True(Input.IsActionJustPressed("jump"));
            Assert.Same(tree.Input.Map, Input.Map);
        }
        finally
        {
            Input.Current = previous;
        }
    }

    [Fact]
    public void PollingAndEventsDoNotAllocate()
    {
        var tree = Tree(Map());
        void Window()
        {
            for (var i = 0; i < 200; i++)
            {
                Key(tree, Silk.NET.Input.Key.Space, (i & 1) == 0);
                Axis(tree, 0, GamepadAxis.LeftStick, new Vector2(i % 3 - 1, 0));
                _ = tree.Input.IsActionPressed("jump");
                _ = tree.Input.IsActionJustPressed("jump");
                _ = tree.Input.GetVector("left", "right", "up", "down");
                Tick(tree);
            }
        }

        Window();
        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
    }

    private sealed class PhysicsProbe(List<bool> seen) : Node
    {
        protected override void OnPhysicsProcess(float delta) => seen.Add(Tree!.Input.IsActionJustPressed("jump"));
    }

    private sealed class ConsumingInputServer : IInputServer
    {
        public bool HandleInput(InputEvent inputEvent) => true;

        public void Dispose()
        {
        }
    }
}

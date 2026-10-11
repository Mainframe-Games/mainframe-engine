using Silk.NET.Input;

namespace MainframeEngine.Tests.Scene;

/// <summary><see cref="InputMap"/> rebinding: slots per device class, conflict policies, groups, locks and resets.</summary>
public sealed class InputRebindingTests
{
    private static readonly InputBinding Space = InputBinding.Key(Key.Space);
    private static readonly InputBinding E = InputBinding.Key(Key.E);
    private static readonly InputBinding F = InputBinding.Key(Key.F);
    private static readonly InputBinding Escape = InputBinding.Key(Key.Escape);
    private static readonly InputBinding PadA = InputBinding.GamepadButton(ButtonName.A);
    private static readonly InputBinding PadX = InputBinding.GamepadButton(ButtonName.X);

    private static InputMap Map()
    {
        var map = new InputMap()
            .Bind("jump", "key:Space", "pad:A")
            .Bind("interact", "key:E", "pad:X")
            .Bind("pause", "key:Escape", "pad:Start")
            .Bind("menu_confirm", "key:Space", "key:Enter", "pad:A");
        map.GetAction("pause")!.Rebindable = false;
        map.GetAction("menu_confirm")!.Group = "menu";
        return map;
    }

    private static string Bindings(InputMap map, string action) => string.Join(' ', map.GetAction(action)!.Bindings);

    [Fact]
    public void BindingsBelongToADeviceClass()
    {
        Assert.Equal(InputDeviceClass.KeyboardMouse, E.DeviceClass);
        Assert.Equal(InputDeviceClass.KeyboardMouse, InputBinding.Mouse(MouseButton.Right).DeviceClass);
        Assert.Equal(InputDeviceClass.Gamepad, PadA.DeviceClass);
        Assert.Equal(InputDeviceClass.Gamepad, InputBinding.Parse("axis:RightTrigger+").DeviceClass);
    }

    [Fact]
    public void SlotsAreTheNthBindingOfEachDeviceClassInListOrder()
    {
        var map = new InputMap().Bind("fire", "pad:RightBumper", "key:F", "axis:RightTrigger+", "mouse:Left");

        Assert.Equal(F, map.GetBinding("fire", InputDeviceClass.KeyboardMouse, 0));
        Assert.Equal(InputBinding.Mouse(MouseButton.Left), map.GetBinding("fire", InputDeviceClass.KeyboardMouse, 1));
        Assert.Equal(default, map.GetBinding("fire", InputDeviceClass.KeyboardMouse, 2));
        Assert.Equal(InputBinding.GamepadButton(ButtonName.RightBumper), map.GetBinding("fire", InputDeviceClass.Gamepad, 0));
        Assert.Equal(InputBinding.Parse("axis:RightTrigger+"), map.GetBinding("fire", InputDeviceClass.Gamepad, 1));
        Assert.Equal(default, map.GetBinding("missing", InputDeviceClass.Gamepad, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetBinding("fire", InputDeviceClass.Gamepad, -1));
    }

    [Fact]
    public void RebindReplacesASlotInPlaceAppendsToAnEmptyOneAndClears()
    {
        var map = Map();

        var result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 1, F);
        Assert.Equal(new RebindResult(true, null, default), result);
        Assert.Equal("key:E pad:X key:F", Bindings(map, "interact"));

        result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, InputBinding.Key(Key.Q));
        Assert.Equal(new RebindResult(true, null, E), result);
        Assert.Equal("key:Q pad:X key:F", Bindings(map, "interact")); // the slot keeps its place in the list

        result = map.Rebind("interact", InputDeviceClass.Gamepad, 0, default);
        Assert.Equal(new RebindResult(true, null, PadX), result);
        Assert.Equal("key:Q key:F", Bindings(map, "interact"));

        Assert.Throws<ArgumentException>(() => map.Rebind("interact", InputDeviceClass.Gamepad, 0, E)); // wrong class
        Assert.Throws<ArgumentException>(() => map.Rebind("missing", InputDeviceClass.Gamepad, 0, PadA));
    }

    [Fact]
    public void SwapGivesTheOtherActionThePreviousBinding()
    {
        var map = Map();

        var result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, Space);

        Assert.True(result.Applied);
        Assert.Same(map.GetAction("jump"), result.ConflictWith);
        Assert.Equal(E, result.Previous);
        Assert.Equal("key:Space pad:X", Bindings(map, "interact"));
        Assert.Equal("key:E pad:A", Bindings(map, "jump")); // in Space's place
        Assert.Equal("key:Space key:Enter pad:A", Bindings(map, "menu_confirm")); // another group: untouched
    }

    [Fact]
    public void SwapIntoAnEmptySlotTakesTheBindingAway()
    {
        var map = Map();

        var result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 1, Space);

        Assert.Equal(new RebindResult(true, map.GetAction("jump"), default), result);
        Assert.Equal("key:E pad:X key:Space", Bindings(map, "interact"));
        Assert.Equal("pad:A", Bindings(map, "jump"));
    }

    [Fact]
    public void UnbindRemovesTheBindingFromTheOtherAction()
    {
        var map = Map();

        var result = map.Rebind("interact", InputDeviceClass.Gamepad, 0, PadA, RebindConflictPolicy.Unbind);

        Assert.Equal(new RebindResult(true, map.GetAction("jump"), PadX), result);
        Assert.Equal("key:E pad:A", Bindings(map, "interact"));
        Assert.Equal("key:Space", Bindings(map, "jump"));
    }

    [Fact]
    public void AllowKeepsBothAndRejectChangesNothing()
    {
        var map = Map();

        var allowed = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, Space, RebindConflictPolicy.Allow);
        Assert.Equal(new RebindResult(true, map.GetAction("jump"), E), allowed);
        Assert.Equal("key:Space pad:X", Bindings(map, "interact"));
        Assert.Equal("key:Space pad:A", Bindings(map, "jump"));

        var version = map.Version;
        var rejected = map.Rebind("interact", InputDeviceClass.Gamepad, 0, PadA, RebindConflictPolicy.Reject);
        Assert.Equal(new RebindResult(false, map.GetAction("jump"), PadX), rejected);
        Assert.Equal("key:Space pad:X", Bindings(map, "interact"));
        Assert.Equal("key:Space pad:A", Bindings(map, "jump"));
        Assert.Equal(version, map.Version);
    }

    [Fact]
    public void ActionsInDifferentGroupsMayShareAnInput()
    {
        var map = Map();
        var conflicts = new List<InputAction>();

        Assert.Equal(1, map.FindConflicts("interact", Space, conflicts));
        Assert.Same(map.GetAction("jump"), Assert.Single(conflicts)); // menu_confirm also has Space, in "menu"
        Assert.Equal(0, map.FindConflicts("menu_confirm", InputBinding.Key(Key.E), conflicts));
        Assert.Empty(conflicts);

        var result = map.Rebind("menu_confirm", InputDeviceClass.KeyboardMouse, 1, E, RebindConflictPolicy.Reject);
        Assert.Equal(new RebindResult(true, null, InputBinding.Key(Key.Enter)), result);
        Assert.Equal("key:E pad:X", Bindings(map, "interact"));
    }

    [Theory]
    [InlineData(RebindConflictPolicy.Swap)]
    [InlineData(RebindConflictPolicy.Unbind)]
    [InlineData(RebindConflictPolicy.Allow)]
    [InlineData(RebindConflictPolicy.Reject)]
    public void ALockedActionNeverLosesABinding(RebindConflictPolicy policy)
    {
        var map = Map();
        var version = map.Version;

        var result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, Escape, policy);

        Assert.Equal(new RebindResult(false, map.GetAction("pause"), E), result); // "Esc is used by Pause"
        Assert.Equal("key:Escape pad:Start", Bindings(map, "pause"));
        Assert.Equal("key:E pad:X", Bindings(map, "interact"));
        Assert.Equal(version, map.Version);
    }

    [Fact]
    public void ALockedActionCannotBeRebound()
    {
        var map = Map();

        var result = map.Rebind("pause", InputDeviceClass.KeyboardMouse, 0, InputBinding.Key(Key.P));

        Assert.Equal(new RebindResult(false, null, Escape), result);
        Assert.Equal("key:Escape pad:Start", Bindings(map, "pause"));
    }

    [Fact]
    public void AnyPadOverlapsEveryPadIndexAndAxisDirectionsAreSeparateInputs()
    {
        var map = new InputMap()
            .Bind("p2_jump", "pad1:A")
            .Bind("p3_jump", "pad2:A")
            .Bind("left", "axis:LeftX-")
            .Bind("jump", "key:Space");
        var conflicts = new List<InputAction>();

        Assert.Equal(2, map.FindConflicts("jump", PadA, conflicts)); // pad:A overlaps pad1:A and pad2:A
        Assert.Equal(1, map.FindConflicts("jump", InputBinding.Parse("pad1:A"), conflicts));
        Assert.Same(map.GetAction("p2_jump"), conflicts[0]);
        Assert.Equal(0, map.FindConflicts("jump", InputBinding.Parse("axis:LeftX+"), conflicts));
        Assert.Equal(1, map.FindConflicts("jump", InputBinding.Parse("axis1:LeftX-"), conflicts)); // axis:LeftX- is any pad

        var result = map.Rebind("jump", InputDeviceClass.Gamepad, 0, PadA, RebindConflictPolicy.Unbind);
        Assert.True(result.Applied);
        Assert.Same(map.GetAction("p2_jump"), result.ConflictWith);
        Assert.Empty(map.GetAction("p2_jump")!.Bindings);
        Assert.Empty(map.GetAction("p3_jump")!.Bindings);
    }

    [Fact]
    public void RebindingToAnInputTheActionHasInAnotherSlotSwapsTheSlots()
    {
        var map = new InputMap().Bind("interact", "key:E", "key:F");

        var result = map.Rebind("interact", InputDeviceClass.KeyboardMouse, 1, E);

        Assert.Equal(new RebindResult(true, null, F), result);
        Assert.Equal("key:F key:E", Bindings(map, "interact"));
    }

    [Fact]
    public void SetBindingsReplacesTheListDropsDuplicatesAndCreatesTheAction()
    {
        var map = Map();

        map.SetBindings("interact", [F, PadA, F]);
        Assert.Equal("key:F pad:A", Bindings(map, "interact"));

        map.SetBindings("new", [Space]);
        Assert.Equal("key:Space", Bindings(map, "new"));

        Assert.Throws<ArgumentException>(() => map.SetBindings("interact", [default]));
    }

    [Fact]
    public void EveryChangeBumpsTheVersionAndNoOpsDoNot()
    {
        var map = Map();
        var defaults = map.Clone();

        var version = map.Version;
        map.SetBindings("interact", [E, PadX]); // the same list
        Assert.Equal(version, map.Version);
        map.SetBindings("interact", [F, PadX]);
        Assert.NotEqual(version, version = map.Version);

        map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, F); // already there
        Assert.Equal(version, map.Version);
        map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, E);
        Assert.NotEqual(version, version = map.Version);
        map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, default);
        Assert.NotEqual(version, version = map.Version);

        map.ResetAction("interact", defaults);
        Assert.NotEqual(version, version = map.Version);
        map.ResetAll(defaults); // nothing differs any more
        Assert.Equal(version, map.Version);
    }

    [Fact]
    public void RebindsRefreshTheInputState()
    {
        var map = Map();
        var tree = new SceneTree();
        tree.Input.Map = map;
        var key = new InputEventKey { Key = Key.F, Pressed = true };

        map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, F);
        tree.PushInput(key);

        Assert.True(tree.Input.IsActionPressed("interact"));
        tree.Shutdown();
    }

    [Fact]
    public void ResetsRestoreTheDefaultsAndDiffersFromComparesInOrder()
    {
        var map = Map();
        var defaults = map.Clone();
        map.AddAction("game_only").Bindings.Add(F);

        map.Rebind("interact", InputDeviceClass.KeyboardMouse, 0, Space);
        map.Rebind("pause", InputDeviceClass.Gamepad, 0, PadA); // locked: rejected
        Assert.True(map.DiffersFrom("interact", defaults));
        Assert.True(map.DiffersFrom("jump", defaults)); // swapped
        Assert.False(map.DiffersFrom("pause", defaults));
        Assert.True(map.DiffersFrom("game_only", defaults)); // only one map has it
        Assert.False(map.DiffersFrom("nowhere", defaults));

        map.ResetAction("interact", defaults);
        Assert.False(map.DiffersFrom("interact", defaults));
        Assert.True(map.DiffersFrom("jump", defaults));

        map.SetBindings("menu_confirm", [InputBinding.Key(Key.Enter), Space, PadA]);
        Assert.True(map.DiffersFrom("menu_confirm", defaults)); // same inputs, another order (slots move)

        map.RemoveAction("jump");
        map.ResetAll(defaults);
        foreach (var action in defaults.Actions)
            Assert.False(map.DiffersFrom(action.Name, defaults));
        Assert.Equal("key:F", Bindings(map, "game_only")); // actions without defaults keep their bindings
        Assert.Equal(defaults.GetAction("jump")!.Deadzone, map.GetAction("jump")!.Deadzone);
    }

    [Fact]
    public void CloneCopiesLabelsGroupsAndLocks()
    {
        var map = Map();
        map.GetAction("interact")!.Label = "Interact";

        var copy = map.Clone();

        Assert.Equal("Interact", copy.GetAction("interact")!.Label);
        Assert.Equal("menu", copy.GetAction("menu_confirm")!.Group);
        Assert.False(copy.GetAction("pause")!.Rebindable);
        Assert.Null(copy.GetAction("jump")!.Label);
        Assert.Equal("", copy.GetAction("jump")!.Group);
        copy.GetAction("jump")!.Group = null!;
        Assert.Equal("", copy.GetAction("jump")!.Group);
    }
}

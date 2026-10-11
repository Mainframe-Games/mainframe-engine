using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// "Press a key" capture for a rebinding menu: <see cref="Begin"/> waits for the next key or mouse button
/// (<see cref="InputDeviceClass.KeyboardMouse"/>) or gamepad button or axis direction (<see cref="InputDeviceClass.Gamepad"/>)
/// and reports it through <see cref="Captured"/>; the menu then calls <see cref="InputMap.Rebind"/>. The node sees input
/// before the game UI (<see cref="Node.InputBeforeUi"/>, ADR 0138) and handles every event it takes, so neither the UI nor
/// the game reacts to the key being bound.
/// </summary>
/// <remarks>
/// <para>Capture rules:</para>
/// <list type="bullet">
/// <item>Capture starts on the next frame. Inputs held when <see cref="Begin"/> runs (the click or button that opened it) are
/// ignored until released, and their release goes on to the UI, which saw their press. Key repeats are ignored.</item>
/// <item>Gamepad axes count past <see cref="AxisThreshold"/> (the UI's own press threshold); a trigger becomes
/// <c>axis:RightTrigger+</c>. Captured gamepad bindings match any pad (<see cref="InputBinding.Device"/> -1).</item>
/// <item><see cref="ReservedInputs"/> never bind. Escape and gamepad Back cancel (<see cref="Cancelled"/>), Backspace and
/// Delete clear (<see cref="Cleared"/>); the others (F8, the RmlUi debugger; F12, the dev overlay; whatever a game adds)
/// pass through untouched.</item>
/// <item>No input for <see cref="TimeoutSeconds"/> cancels.</item>
/// <item>While capturing, every key, button, axis, wheel and text event is handled (mouse motion is not); afterwards the
/// captured input's repeats and release are handled too.</item>
/// </list>
/// <para>The node processes while the tree is paused (<see cref="ProcessMode.Always"/>), since settings menus usually pause
/// the game. <see cref="Node.OnInput"/> allocates nothing.</para>
/// </remarks>
[EditorIcon("keyboard", Family = EditorIconFamily.Logic)]
public sealed class InputRebinder : Node
{
    /// <summary>How far (0..1) an axis must move along a direction to be captured.</summary>
    public const float AxisThreshold = 0.6f;

    // What happens to an input's next release: nothing tracked, let it through (its press reached the UI or game before the
    // capture), or handle it (this node took its press).
    private const byte Untracked = 0;
    private const byte PassRelease = 1;
    private const byte EatRelease = 2;

    private static readonly int MouseBase = InputState.KeyCount;
    private static readonly int PadButtonBase = MouseBase + InputState.MouseButtonCount;
    private static readonly int AxisBase = PadButtonBase + InputState.MaxGamepads * InputState.GamepadButtonCount;
    private static readonly int InputCount = AxisBase + InputState.MaxGamepads * InputState.GamepadAxisCount * 2;

    private byte[]? _inputs;
    private Phase _phase;
    private float _elapsed;

    private enum Phase : byte
    {
        Idle,
        Starting,
        Capturing,
    }

    /// <summary>A node that captures input before the UI and keeps working while the tree is paused.</summary>
    public InputRebinder()
    {
        InputBeforeUi = true;
        ProcessMode = ProcessMode.Always;
    }

    /// <summary>True from <see cref="Begin"/> until a capture, clear, cancel or timeout.</summary>
    public bool IsCapturing => _phase != Phase.Idle;

    /// <summary>The device class the current (or last) capture listens to.</summary>
    public InputDeviceClass DeviceClass { get; private set; }

    /// <summary>Seconds without a captured input before the capture cancels itself; 0 or less waits forever.</summary>
    [Export(Range = "0,60,0.5")]
    public float TimeoutSeconds { get; set; } = 5f;

    /// <summary>
    /// Inputs that never bind (matched like <see cref="InputMap.FindConflicts"/>: <c>pad:Back</c> covers every pad). By
    /// default Escape and gamepad Back (cancel), Backspace and Delete (clear), F8 and F12. Games add their own (they pass
    /// through like F8) or remove one to make it bindable.
    /// </summary>
    public List<InputBinding> ReservedInputs { get; } =
    [
        InputBinding.Key(Key.Escape),
        InputBinding.Key(Key.Backspace),
        InputBinding.Key(Key.Delete),
        InputBinding.Key(Key.F8),
        InputBinding.Key(Key.F12),
        InputBinding.GamepadButton(ButtonName.Back),
    ];

    /// <summary>The input pressed during a capture, as a binding of the capture's <see cref="DeviceClass"/>.</summary>
    [Signal]
    public event Action<InputBinding>? Captured;

    /// <summary>Backspace or Delete was pressed: the menu clears the slot.</summary>
    [Signal]
    public event Action? Cleared;

    /// <summary>Escape, gamepad Back, <see cref="Cancel"/> or the timeout ended the capture.</summary>
    [Signal]
    public event Action? Cancelled;

    /// <summary>
    /// Starts capturing an input of <paramref name="deviceClass"/> (restarting a capture in progress). Inputs held now are
    /// ignored until released, and capture begins on the next frame.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node is not in a tree.</exception>
    public void Begin(InputDeviceClass deviceClass)
    {
        var input = (Tree ?? throw new InvalidOperationException($"'{Name}' must be inside the tree to capture input.")).Input;
        DeviceClass = deviceClass;
        _phase = Phase.Starting;
        _elapsed = 0f;
        Snapshot(input);
        SetProcess(true);
    }

    /// <summary>Stops a capture in progress and raises <see cref="Cancelled"/>; does nothing when idle.</summary>
    public void Cancel()
    {
        if (!IsCapturing)
            return;
        Stop();
        Cancelled?.Invoke();
    }

    // Marks every input held right now: ignored until released, and that release goes on to whoever saw the press. Inputs
    // whose press this node took keep their state, so their release is still handled.
    private void Snapshot(InputState input)
    {
        var inputs = _inputs ??= new byte[InputCount];
        for (var i = 0; i < InputState.KeyCount; i++)
            Mark(inputs, i, input.IsKeyPressed((Key)i));
        for (var i = 0; i < InputState.MouseButtonCount; i++)
            Mark(inputs, MouseBase + i, input.IsMouseButtonPressed((MouseButton)i));
        for (var device = 0; device < InputState.MaxGamepads; device++)
        {
            for (var i = 0; i < InputState.GamepadButtonCount; i++)
                Mark(inputs, PadButtonIndex(device, i), input.IsGamepadButtonPressed(device, (ButtonName)i));
            for (var i = 0; i < InputState.GamepadAxisCount; i++)
            {
                var value = input.GetGamepadAxis(device, (GamepadAxisCode)i);
                Mark(inputs, AxisIndex(device, i, -1), -value > AxisThreshold);
                Mark(inputs, AxisIndex(device, i, 1), value > AxisThreshold);
            }
        }

        static void Mark(byte[] inputs, int index, bool held)
        {
            if (inputs[index] != EatRelease)
                inputs[index] = held ? PassRelease : Untracked;
        }
    }

    private void Stop()
    {
        _phase = Phase.Idle;
        SetProcess(false);
    }

    protected override void OnReady()
    {
        if (_phase == Phase.Idle)
            SetProcess(false);
    }

    protected override void OnExitTree()
    {
        // A capture does not survive leaving the tree (nothing would see the inputs' releases).
        Stop();
        if (_inputs is not null)
            Array.Clear(_inputs);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        switch (_phase)
        {
            case Phase.Starting:
                _phase = Phase.Capturing; // events of the frame that called Begin are behind us
                break;
            case Phase.Capturing:
                _elapsed += gameTime.DeltaTime;
                if (TimeoutSeconds > 0f && _elapsed >= TimeoutSeconds)
                    Cancel();
                break;
            default:
                SetProcess(false);
                break;
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        if (_inputs is null)
            return; // never captured
        var handled = inputEvent switch
        {
            InputEventKey key => key.Key == Key.Unknown ? IsCapturing : OnButton(InputBinding.Key(key.Key), (int)key.Key, key.Pressed),
            InputEventMouseButton mouse => mouse.Button == MouseButton.Unknown
                ? IsCapturing
                : OnButton(InputBinding.Mouse(mouse.Button), MouseBase + (int)mouse.Button, mouse.Pressed),
            InputEventGamepadButton pad => (uint)pad.Device >= InputState.MaxGamepads || pad.Button == ButtonName.Unknown
                ? IsCapturing
                : OnButton(InputBinding.GamepadButton(pad.Button), PadButtonIndex(pad.Device, (int)pad.Button), pad.Pressed),
            InputEventGamepadAxis axis => (uint)axis.Device >= InputState.MaxGamepads ? IsCapturing : OnAxis(axis),
            InputEventMouseWheel or InputEventText => IsCapturing,
            _ => false, // mouse motion and actions pressed by code
        };
        if (handled)
            Tree!.Root.SetInputAsHandled();
    }

    // A key or button event; true when it must be handled.
    private bool OnButton(InputBinding binding, int index, bool pressed)
    {
        ref var state = ref _inputs![index];
        if (!pressed)
        {
            var was = state;
            state = Untracked;
            return was == EatRelease || (was == Untracked && IsCapturing && !PassesThrough(binding));
        }

        if (state == EatRelease)
            return true; // a repeat of a press this node took
        if (!IsCapturing)
            return false;
        if (state == PassRelease)
            return true; // a repeat of an input held since before the capture
        if (PassesThrough(binding))
            return false;
        state = EatRelease;
        if (_phase == Phase.Capturing)
            Take(binding);
        return true;
    }

    // A stick or trigger moved; true when the event must be handled. The strongest direction past the threshold that was
    // not already held is captured.
    private bool OnAxis(InputEventGamepadAxis axis)
    {
        var handled = IsCapturing;
        var best = default(InputBinding);
        var bestValue = 0f;
        var bestIndex = -1;
        switch (axis.Axis)
        {
            case GamepadAxis.LeftStick:
                Track(axis.Device, GamepadAxisCode.LeftX, axis.Value.X);
                Track(axis.Device, GamepadAxisCode.LeftY, axis.Value.Y);
                break;
            case GamepadAxis.RightStick:
                Track(axis.Device, GamepadAxisCode.RightX, axis.Value.X);
                Track(axis.Device, GamepadAxisCode.RightY, axis.Value.Y);
                break;
            case GamepadAxis.LeftTrigger:
                Track(axis.Device, GamepadAxisCode.LeftTrigger, axis.Value.X);
                break;
            case GamepadAxis.RightTrigger:
                Track(axis.Device, GamepadAxisCode.RightTrigger, axis.Value.X);
                break;
        }

        if (bestIndex >= 0)
        {
            _inputs![bestIndex] = EatRelease;
            handled = true;
            if (_phase == Phase.Capturing)
                Take(best);
        }

        return handled;

        void Track(int device, GamepadAxisCode code, float value)
        {
            for (var direction = -1; direction <= 1; direction += 2)
            {
                var index = AxisIndex(device, (int)code, direction);
                ref var state = ref _inputs![index];
                var along = direction * value;
                if (along <= AxisThreshold)
                {
                    handled |= state == EatRelease;
                    state = Untracked;
                    continue;
                }

                if (state == EatRelease)
                {
                    handled = true;
                    continue;
                }

                var binding = InputBinding.GamepadAxis(code, direction);
                if (state != Untracked || !IsCapturing || PassesThrough(binding) || along <= bestValue)
                    continue;
                best = binding;
                bestValue = along;
                bestIndex = index;
            }
        }
    }

    // A fresh press during the capture: cancel, clear, ignore (the other device class) or capture.
    private void Take(InputBinding binding)
    {
        if (binding == InputBinding.Key(Key.Escape) || binding == InputBinding.GamepadButton(ButtonName.Back))
        {
            if (IsReserved(binding))
            {
                Cancel();
                return;
            }
        }
        else if (binding == InputBinding.Key(Key.Backspace) || binding == InputBinding.Key(Key.Delete))
        {
            if (IsReserved(binding))
            {
                Stop();
                Cleared?.Invoke();
                return;
            }
        }

        if (binding.DeviceClass != DeviceClass)
            return;
        Stop();
        Captured?.Invoke(binding);
    }

    // Reserved inputs other than the cancel and clear keys never bind and are not touched (F8, F12, the game's own).
    private bool PassesThrough(InputBinding binding) =>
        IsReserved(binding)
        && binding != InputBinding.Key(Key.Escape) && binding != InputBinding.GamepadButton(ButtonName.Back)
        && binding != InputBinding.Key(Key.Backspace) && binding != InputBinding.Key(Key.Delete);

    private bool IsReserved(InputBinding binding)
    {
        var reserved = ReservedInputs;
        for (var i = 0; i < reserved.Count; i++)
        {
            if (InputMap.Conflicts(reserved[i], binding))
                return true;
        }

        return false;
    }

    private static int PadButtonIndex(int device, int button) => PadButtonBase + device * InputState.GamepadButtonCount + button;

    private static int AxisIndex(int device, int axis, int direction) =>
        AxisBase + (device * InputState.GamepadAxisCount + axis) * 2 + (direction < 0 ? 0 : 1);
}

using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>The two kinds of rebinding slots an action has: keyboard and mouse, or gamepad.</summary>
public enum InputDeviceClass : byte
{
    /// <summary>Keys and mouse buttons.</summary>
    KeyboardMouse,

    /// <summary>Gamepad buttons and axis directions.</summary>
    Gamepad,
}

/// <summary>What <see cref="InputMap.Rebind"/> does when another action in the same group already uses the input.</summary>
public enum RebindConflictPolicy
{
    /// <summary>The other action gets the slot's previous binding in place of the taken one, or loses it when the slot was empty.</summary>
    Swap,

    /// <summary>The other action loses the binding.</summary>
    Unbind,

    /// <summary>Both actions keep the input.</summary>
    Allow,

    /// <summary>Nothing changes.</summary>
    Reject,
}

/// <summary>
/// The outcome of <see cref="InputMap.Rebind"/>: whether the binding was applied, the action that already used the input
/// (the first one when several did; for a rejection, the one that blocked it), and the binding the slot held before
/// (empty when the slot was empty).
/// </summary>
public readonly record struct RebindResult(bool Applied, InputAction? ConflictWith, InputBinding Previous);

public sealed partial class InputMap
{
    private List<InputAction>? _conflicts;

    /// <summary>
    /// Replaces <paramref name="action"/>'s bindings (the action is created when missing); duplicates are dropped, list
    /// order is kept. <see cref="Version"/> changes when the list does.
    /// </summary>
    public void SetBindings(string action, ReadOnlySpan<InputBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (binding.Kind == InputBindingKind.None)
                throw new ArgumentException("Cannot bind an empty InputBinding.", nameof(bindings));
        }

        var target = AddAction(action);
        var list = target.Bindings;
        if (SameDistinct(list, bindings))
            return;
        list.Clear();
        foreach (var binding in bindings)
        {
            if (!list.Contains(binding))
                list.Add(binding);
        }

        Version++;
    }

    // True when the duplicate-free form of `bindings` is exactly `list`.
    private static bool SameDistinct(List<InputBinding> list, ReadOnlySpan<InputBinding> bindings)
    {
        var n = 0;
        for (var i = 0; i < bindings.Length; i++)
        {
            if (bindings[..i].Contains(bindings[i]))
                continue;
            if (n >= list.Count || list[n] != bindings[i])
                return false;
            n++;
        }

        return n == list.Count;
    }

    /// <summary>
    /// The <paramref name="slot"/>-th binding of <paramref name="deviceClass"/> in <paramref name="action"/>'s list order
    /// (slot 0 is the first key or mouse button, or the first gamepad input). Empty (<see cref="InputBindingKind.None"/>)
    /// when the slot is empty or the action does not exist.
    /// </summary>
    public InputBinding GetBinding(string action, InputDeviceClass deviceClass, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        if (GetAction(action) is not { } target)
            return default;
        var index = SlotIndex(target.Bindings, deviceClass, slot);
        return index >= 0 ? target.Bindings[index] : default;
    }

    // Index in `bindings` of the slot-th binding of `deviceClass`, or -1.
    private static int SlotIndex(List<InputBinding> bindings, InputDeviceClass deviceClass, int slot)
    {
        for (var i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].DeviceClass == deviceClass && slot-- == 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Puts <paramref name="binding"/> in <paramref name="action"/>'s <paramref name="slot"/>-th
    /// <paramref name="deviceClass"/> binding (<see cref="GetBinding"/>): the binding there is replaced in place, and an
    /// empty slot appends. An empty <paramref name="binding"/> clears the slot. Another action in the same
    /// <see cref="InputAction.Group"/> that uses the same input (<see cref="FindConflicts"/>) is handled by
    /// <paramref name="policy"/>; with <see cref="RebindConflictPolicy.Swap"/> and several such actions, the first gets
    /// the previous binding and the others lose theirs. A locked action (<see cref="InputAction.Rebindable"/> false) is
    /// never changed: rebinding it, or taking one of its inputs, is rejected whatever the policy. When the action already
    /// has the input in another slot, the two slots swap. <see cref="Version"/> changes when anything changes.
    /// </summary>
    /// <exception cref="ArgumentException">The action does not exist, or the binding is not of <paramref name="deviceClass"/>.</exception>
    public RebindResult Rebind(string action, InputDeviceClass deviceClass, int slot, InputBinding binding,
        RebindConflictPolicy policy = RebindConflictPolicy.Swap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        var target = GetAction(action) ?? throw new ArgumentException($"There is no input action '{action}'.", nameof(action));
        if (binding.Kind != InputBindingKind.None && binding.DeviceClass != deviceClass)
            throw new ArgumentException($"'{binding}' is not a {deviceClass} binding.", nameof(binding));

        var bindings = target.Bindings;
        var index = SlotIndex(bindings, deviceClass, slot);
        var previous = index >= 0 ? bindings[index] : default;
        if (!target.Rebindable)
            return new RebindResult(false, null, previous);
        if (binding == previous)
            return new RebindResult(true, null, previous);

        if (binding.Kind == InputBindingKind.None)
        {
            bindings.RemoveAt(index); // (index >= 0: an empty slot equals the empty binding, handled above)
            Version++;
            return new RebindResult(true, null, previous);
        }

        var conflicts = _conflicts ??= [];
        FindConflicts(action, binding, conflicts);
        var first = conflicts.Count > 0 ? conflicts[0] : null;
        foreach (var other in conflicts)
        {
            if (!other.Rebindable)
                return Done(new RebindResult(false, other, previous));
        }

        if (first is not null && policy == RebindConflictPolicy.Reject)
            return Done(new RebindResult(false, first, previous));

        // The action's own copy of the input in another slot takes this slot's previous binding (or goes).
        var own = bindings.IndexOf(binding);
        if (own >= 0)
        {
            if (previous.Kind != InputBindingKind.None)
            {
                bindings[own] = previous;
            }
            else
            {
                bindings.RemoveAt(own);
                if (own < index)
                    index--;
            }
        }

        if (index >= 0)
            bindings[index] = binding;
        else
            bindings.Add(binding);

        if (policy != RebindConflictPolicy.Allow)
        {
            for (var i = 0; i < conflicts.Count; i++)
            {
                var swapIn = policy == RebindConflictPolicy.Swap && i == 0 ? previous : default;
                TakeFrom(conflicts[i].Bindings, binding, swapIn);
            }
        }

        Version++;
        return Done(new RebindResult(true, first, previous));

        RebindResult Done(RebindResult result)
        {
            conflicts.Clear();
            return result;
        }
    }

    // Removes every binding in `bindings` that conflicts with `taken`; the first one is replaced by `swapIn` instead when
    // that is a binding the list does not already have.
    private static void TakeFrom(List<InputBinding> bindings, InputBinding taken, InputBinding swapIn)
    {
        var replace = swapIn.Kind != InputBindingKind.None && !bindings.Contains(swapIn);
        for (var i = 0; i < bindings.Count; i++)
        {
            if (!Conflicts(bindings[i], taken))
                continue;
            if (replace)
            {
                bindings[i] = swapIn;
                replace = false;
            }
            else
            {
                bindings.RemoveAt(i--);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="results"/> (cleared first) with the other actions in <paramref name="action"/>'s
    /// <see cref="InputAction.Group"/> that use <paramref name="binding"/>'s input: the same kind and code, the same axis
    /// direction, and overlapping pads (<see cref="InputBinding.Device"/> -1 overlaps every pad). Returns how many.
    /// </summary>
    public int FindConflicts(string action, InputBinding binding, List<InputAction> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        if (binding.Kind == InputBindingKind.None)
            return 0;
        var group = GetAction(action)?.Group ?? "";
        foreach (var other in _actions)
        {
            if (string.Equals(other.Name, action, StringComparison.Ordinal) || !string.Equals(other.Group, group, StringComparison.Ordinal))
                continue;
            foreach (var existing in other.Bindings)
            {
                if (Conflicts(existing, binding))
                {
                    results.Add(other);
                    break;
                }
            }
        }

        return results.Count;
    }

    /// <summary>Whether two bindings listen to the same input (pad indices overlap when either is -1).</summary>
    internal static bool Conflicts(InputBinding a, InputBinding b) =>
        a.Kind == b.Kind && a.Kind != InputBindingKind.None && a.Code == b.Code && a.Direction == b.Direction
        && (a.Device < 0 || b.Device < 0 || a.Device == b.Device);

    /// <summary>
    /// Gives <paramref name="action"/> the bindings it has in <paramref name="defaults"/> (usually a <see cref="Clone"/> of
    /// the project's map taken before any rebinding). An action missing here is added with the default's deadzone, label,
    /// group and lock; one missing from <paramref name="defaults"/> is left alone.
    /// </summary>
    public void ResetAction(string action, InputMap defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (defaults.GetAction(action) is not { } source)
            return;
        if (!HasAction(action))
            CopyInfo(source, AddAction(action, source.Deadzone));
        SetBindings(action, CollectionsMarshal.AsSpan(source.Bindings));
    }

    /// <summary>
    /// <see cref="ResetAction"/> for every action of <paramref name="defaults"/>; actions only this map has keep their
    /// bindings.
    /// </summary>
    public void ResetAll(InputMap defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        foreach (var action in defaults.Actions)
            ResetAction(action.Name, defaults);
    }

    /// <summary>
    /// True when <paramref name="action"/>'s bindings (in order) are not the ones it has in <paramref name="defaults"/>, or
    /// when only one of the two maps has the action.
    /// </summary>
    public bool DiffersFrom(string action, InputMap defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var mine = GetAction(action);
        var theirs = defaults.GetAction(action);
        if (mine is null || theirs is null)
            return mine is not null || theirs is not null;
        return !CollectionsMarshal.AsSpan(mine.Bindings).SequenceEqual(CollectionsMarshal.AsSpan(theirs.Bindings));
    }
}

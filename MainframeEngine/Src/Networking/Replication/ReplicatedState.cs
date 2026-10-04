using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>What a client knows when it reads replicated values.</summary>
/// <param name="Tick">Server tick the values belong to.</param>
/// <param name="PreviousTick">
/// The previous snapshot the client applied: every member absent from this snapshot was unchanged through it, so an
/// interpolation buffer can hold its last value up to that tick before blending to the new one.
/// </param>
/// <param name="ApplyDirectly">Set interpolated members immediately too (spawns, or interpolation disabled).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ReplicationReadContext(uint Tick, uint PreviousTick, bool ApplyDirectly);

/// <summary>The client's interpolation clock for one frame, in server ticks.</summary>
/// <param name="RenderTick">The (fractional) server tick being shown: the estimated server tick minus the interpolation delay.</param>
/// <param name="LatestTick">The newest snapshot applied; members are known unchanged up to it.</param>
/// <param name="MaxExtrapolationTicks">How far past the newest sample a moving value may be extrapolated.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct InterpolationTime(double RenderTick, uint LatestTick, double MaxExtrapolationTicks);

/// <summary>
/// Replication state of one node for one declaring type, generated per type by <c>MainframeEngine.Generators</c>:
/// typed shadow copies of its <see cref="ReplicatedAttribute"/> members. On the server, <see cref="Capture"/> compares
/// them with the node and records the tick of every change, so a snapshot can send exactly the members changed since
/// a client's last acknowledged tick. On a client, <see cref="Read"/> applies received values (or buffers them for
/// <see cref="Interpolate"/>).
/// </summary>
public abstract class ReplicatedState
{
    private readonly uint[] _changedTicks;

    protected ReplicatedState(int propertyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(propertyCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(propertyCount, 64);
        PropertyCount = propertyCount;
        _changedTicks = new uint[propertyCount];
    }

    /// <summary>Number of members (bits in the change masks).</summary>
    public int PropertyCount { get; }

    /// <summary>Every member's bit.</summary>
    public ulong FullMask => PropertyCount == 64 ? ulong.MaxValue : (1UL << PropertyCount) - 1;

    /// <summary>True when some member is interpolated.</summary>
    public abstract bool HasInterpolation { get; }

    /// <summary>Server: compares the members of <paramref name="node"/> with the shadow copies; changed ones are stamped with <paramref name="tick"/>.</summary>
    public abstract void Capture(Node node, uint tick);

    /// <summary>Server: writes the captured values of the members in <paramref name="mask"/>.</summary>
    public abstract void Write(NetBufferWriter writer, ulong mask);

    /// <summary>Client: reads the members in <paramref name="mask"/> and applies (or buffers) them.</summary>
    public abstract void Read(NetBufferReader reader, Node node, ulong mask, in ReplicationReadContext context);

    /// <summary>Client: sets every interpolated member to its value at <paramref name="time"/>.</summary>
    public abstract void Interpolate(Node node, in InterpolationTime time);

    /// <summary>Client: forgets buffered samples (e.g. after a long stall).</summary>
    public abstract void ClearInterpolation();

    /// <summary>The tick at which member <paramref name="index"/> last changed (0: never since the node was registered).</summary>
    public uint GetChangedTick(int index) => _changedTicks[index];

    /// <summary>Bits of the members changed after <paramref name="tick"/>.</summary>
    public ulong ChangedSince(uint tick)
    {
        var mask = 0UL;
        var ticks = _changedTicks;
        for (var i = 0; i < ticks.Length; i++)
        {
            if (ticks[i] > tick)
                mask |= 1UL << i;
        }

        return mask;
    }

    protected void MarkChanged(int index, uint tick) => _changedTicks[index] = tick;
}

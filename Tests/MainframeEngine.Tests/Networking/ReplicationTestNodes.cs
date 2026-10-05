using System.Drawing;
using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

// Networked node types for the replication tests. Internal (not private nested) so the source generator registers
// them and emits their replication state and RPC senders, exactly like a game's types.

internal enum NetTeam : byte
{
    None,
    Red,
    Blue,
}

/// <summary>A custom transferable value (must be IEquatable to be [Replicated]).</summary>
internal struct NetLoadout : INetworkTransferable, IEquatable<NetLoadout>
{
    public int Weapon;
    public short Ammo;

    public readonly void NetworkWrite(NetBufferWriter writer)
    {
        writer.Write(Weapon);
        writer.Write(Ammo);
    }

    public void NetworkRead(NetBufferReader reader)
    {
        Weapon = reader.ReadInt32();
        Ammo = reader.ReadInt16();
    }

    public readonly bool Equals(NetLoadout other) => Weapon == other.Weapon && Ammo == other.Ammo;

    public override readonly bool Equals(object? obj) => obj is NetLoadout other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Weapon, Ammo);
}

/// <summary>A player: interpolated transform, plain state, and one RPC of each mode.</summary>
internal class NetPlayer : Node3D
{
    [Replicated(Interpolate = true)]
    public Vector3 NetPosition
    {
        get => Position;
        set => Position = value;
    }

    [Replicated(Interpolate = true)]
    public Quaternion NetRotation
    {
        get => Rotation;
        set => Rotation = value;
    }

    [Replicated]
    public int Health { get; set; } = 100;

    [Replicated]
    public string PlayerName { get; set; } = "";

    [Replicated]
    public NetTeam Team;

    [Replicated]
    public NetLoadout Loadout { get; set; }

    [Replicated]
    public PeerId Owner2 { get; set; }

    /// <summary>Not replicated: stays local.</summary>
    public int LocalOnly { get; set; }

    public List<(string Rpc, PeerId Sender, string Argument)> Calls { get; } = [];

    /// <summary>Client → server input (only the authority may call it).</summary>
    [Rpc(RpcMode.Authority)]
    public void Move(Vector3 delta)
    {
        Calls.Add((nameof(Move), Multiplayer?.RemoteSender ?? default, delta.ToString()));
        NetPosition += delta;
    }

    /// <summary>Server → clients effect.</summary>
    [Rpc(RpcMode.Server)]
    public void Flash(int times, NetTeam team)
    {
        Calls.Add((nameof(Flash), Multiplayer?.RemoteSender ?? default, $"{times}:{team}"));
    }

    /// <summary>Anyone, unreliable.</summary>
    [Rpc(RpcMode.AnyPeer, Reliable = false)]
    public void Ping(uint value) => Calls.Add((nameof(Ping), Multiplayer?.RemoteSender ?? default, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Also runs on the caller.</summary>
    [Rpc(RpcMode.Authority, CallLocal = true)]
    public void Emote(string name, NetLoadout loadout) =>
        Calls.Add((nameof(Emote), Multiplayer?.RemoteSender ?? default, $"{name}:{loadout.Weapon}"));
}

/// <summary>Derived type: base members and RPCs come first on the wire, then these.</summary>
internal sealed class NetBoss : NetPlayer
{
    [Replicated]
    public float Rage { get; set; }

    [Rpc(RpcMode.Server)]
    public void Roar(float volume) => Calls.Add((nameof(Roar), Multiplayer?.RemoteSender ?? default, volume.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>A networked descendant inside a spawned scene.</summary>
internal sealed class NetPart : Node3D
{
    [Replicated]
    public Color Tint { get; set; } = Color.White;

    [Replicated(Interpolate = true)]
    public float Spin { get; set; }

    [Replicated]
    public Transform3D Pose { get; set; } = Transform3D.Identity;

    [Rpc(RpcMode.Server)]
    public void Pulse(byte strength) => Pulses.Add(strength);

    public List<byte> Pulses { get; } = [];
}

/// <summary>A networked node whose members are fields of many types (codec coverage).</summary>
internal sealed class NetAllTypes : Node
{
    [Replicated] public bool Bool;
    [Replicated] public byte Byte;
    [Replicated] public sbyte SByte;
    [Replicated] public short Short;
    [Replicated] public ushort UShort;
    [Replicated] public int Int;
    [Replicated] public uint UInt;
    [Replicated] public long Long;
    [Replicated] public ulong ULong;
    [Replicated] public float Float;
    [Replicated] public double Double;
    [Replicated] public decimal Decimal;
    [Replicated] public char Char;
    [Replicated] public string? Text;
    [Replicated] public Vector2 V2;
    [Replicated] public Vector4 V4;
    [Replicated] public Transform2D T2 = Transform2D.Identity;
    [Replicated(Interpolate = true)] public double Smooth;
    [Replicated(Interpolate = true)] public Vector2 SmoothV2;
    [Replicated(Interpolate = true)] public Vector4 SmoothV4;
}

/// <summary>A movable box for the allocation test and benchmarks (game-like).</summary>
internal sealed class NetBox : Node3D
{
    [Replicated(Interpolate = true)]
    public Vector3 NetPosition
    {
        get => Position;
        set => Position = value;
    }

    [Replicated(Interpolate = true)]
    public Quaternion NetRotation
    {
        get => Rotation;
        set => Rotation = value;
    }

    [Replicated]
    public int Score { get; set; }

    /// <summary>Received RPC totals (allocation-free bodies, for the allocation gate).</summary>
    public int Bumps { get; private set; }

    [Rpc(RpcMode.Authority)]
    public void Nudge(Vector3 delta) => NetPosition += delta;

    [Rpc(RpcMode.Server)]
    public void Bump(int amount) => Bumps += amount;

    /// <summary>An RPC whose body throws (a client must not be able to crash the server with it).</summary>
    [Rpc(RpcMode.AnyPeer)]
    public void Crash(int code) => throw new InvalidOperationException($"boom {code} on {Name}");
}

using System.Numerics;

namespace MainframeEngine.Networking;

/// <summary>
/// A spawn's data for <see cref="MultiplayerSpawner.SpawnFunction"/> (Godot's spawn-function dictionary): string keys,
/// values of bool, int, long, float, double, string, <see cref="Vector2"/>, int[] or string[] — the types travel with the
/// values, so a client reads back exactly what the server put in.
/// </summary>
public class SpawnData : Dictionary<string, object>
{
    public SpawnData()
        : base(StringComparer.Ordinal)
    {
    }

    private enum Tag : byte { Null, Bool, Int, Long, Float, Double, String, Vector2, IntArray, StringArray }

    public byte[] Serialize()
    {
        var w = new NetBufferWriter(64);
        w.WriteVarUInt32((uint)Count);
        foreach (var (key, value) in this.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            w.Write(key.AsSpan());
            switch (value)
            {
                case null: w.Write((byte)Tag.Null); break;
                case bool b: w.Write((byte)Tag.Bool); w.Write(b); break;
                case int i: w.Write((byte)Tag.Int); w.Write(i); break;
                case long l: w.Write((byte)Tag.Long); w.Write(l); break;
                case float f: w.Write((byte)Tag.Float); w.Write(f); break;
                case double d: w.Write((byte)Tag.Double); w.Write(d); break;
                case string s: w.Write((byte)Tag.String); w.Write(s.AsSpan()); break;
                case Vector2 v: w.Write((byte)Tag.Vector2); w.Write(in v); break;
                case int[] a: w.Write((byte)Tag.IntArray); NetCodec.Write(w, a); break;
                case string[] a: w.Write((byte)Tag.StringArray); NetCodec.Write(w, a); break;
                default: throw new NotSupportedException($"Spawn data '{key}' has type {value.GetType().Name}, which cannot be sent.");
            }
        }

        return w.GetData();
    }

    public static SpawnData Deserialize(ReadOnlySpan<byte> bytes)
    {
        var r = new NetBufferReader(bytes);
        var data = new SpawnData();
        var count = r.ReadVarUInt32();
        for (var i = 0u; i < count; i++)
        {
            var key = r.ReadString();
            data[key] = (Tag)r.ReadByte() switch
            {
                Tag.Null => null!,
                Tag.Bool => r.ReadBoolean(),
                Tag.Int => r.ReadInt32(),
                Tag.Long => r.ReadInt64(),
                Tag.Float => r.ReadSingle(),
                Tag.Double => r.ReadDouble(),
                Tag.String => r.ReadString(),
                Tag.Vector2 => r.ReadVector2(),
                Tag.IntArray => ReadInts(r),
                Tag.StringArray => ReadStrings(r),
                var tag => throw new InvalidDataException($"Unknown spawn data tag {(byte)tag}."),
            };
        }

        return data;
    }

    private static int[] ReadInts(NetBufferReader r)
    {
        NetCodec.Read(r, out int[] v);
        return v;
    }

    private static string[] ReadStrings(NetBufferReader r)
    {
        NetCodec.Read(r, out string[] v);
        return v;
    }
}

/// <summary>
/// Spawns nodes from data on every peer (Godot's <c>MultiplayerSpawner</c> with a spawn function): the server calls
/// <see cref="Spawn"/>, which runs <see cref="SpawnFunction"/> with the data and adds the node under <see cref="SpawnPath"/>;
/// the data travels in the spawn message (late joiners too) and every client runs its own spawner's function with it
/// before the node's replicated state is applied. The function must build the same networked nodes everywhere.
/// </summary>
[EditorIcon("arrows-right-left", Family = EditorIconFamily.Logic)]
public class MultiplayerSpawner : Node
{
    private MultiplayerApi? _api;

    /// <summary>Where spawned nodes go, relative to this node (Godot's <c>spawn_path</c>).</summary>
    [Export]
    public NodePath SpawnPath { get; set; } = "";

    /// <summary>Builds the node from the spawn's data, on the server and on every client (Godot's <c>spawn_function</c>).</summary>
    public Func<SpawnData, Node>? SpawnFunction { get; set; }

    /// <summary>Clients: a node arrived from the server (Godot's <c>spawned</c>).</summary>
    [Signal] public event Action<Node>? Spawned;

    /// <summary>Clients: a spawned node was removed by the server (Godot's <c>despawned</c>).</summary>
    [Signal] public event Action<Node>? Despawned;

    /// <summary>The node spawned nodes are added to.</summary>
    public Node? SpawnParent => SpawnPath.IsEmpty ? null : GetNodeOrNull(SpawnPath);

    /// <summary>Server: spawns a node from <paramref name="data"/> on every peer and returns the server's.</summary>
    public Node Spawn(SpawnData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var api = Multiplayer ?? throw new InvalidOperationException("No MultiplayerApi in the tree.");
        return api.SpawnCustom(this, data);
    }

    protected override void OnReady()
    {
        base.OnReady();
        _api = Multiplayer;
        if (_api is not null)
            _api.NodeDespawned += OnDespawned;
    }

    protected override void OnExitTree()
    {
        if (_api is not null)
            _api.NodeDespawned -= OnDespawned;
        _api = null;
        base.OnExitTree();
    }

    internal void RaiseSpawned(Node node) => Spawned?.Invoke(node);

    private void OnDespawned(Node node)
    {
        if (_api is { IsClient: true } && ReferenceEquals(node.Parent, SpawnParent))
            Despawned?.Invoke(node);
    }
}

using System.Buffers.Binary;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using MainframeEngine.Networking;

namespace MainframeEngine.Benchmarks;

/// <summary>A replicated box: interpolated position and rotation plus an integer (a typical moving prop).</summary>
public sealed class BenchNetBox : Node3D
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
}

/// <summary>
/// Replication (M5) at 100 and 1000 nodes, every node changed: server change detection (capture), encoding one
/// client's snapshot, and a client decoding and applying it (including interpolation buffering). All allocation-free.
/// </summary>
[MemoryDiagnoser]
public class ReplicationBenchmarks : IDisposable
{
    private const string BoxUid = "scn_00000000b001";

    private SceneTree _serverTree = null!;
    private SceneTree _clientTree = null!;
    private MultiplayerApi _server = null!;
    private MultiplayerApi _client = null!;
    private BenchNetBox[] _boxes = [];
    private PeerState _peer = null!;
    private NetBufferWriter _writer = null!;
    private byte[] _packet = [];
    private ITransportListener _clientListener = null!;
    private PeerId _serverPeer;
    private uint _tick;
    private uint _clientTick;

    [Params(100, 1000)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var scene = PackedScene.Pack(new BenchNetBox { Name = "Box" });
        _serverTree = CreateTree();
        _clientTree = CreateTree();
        _server = MultiplayerApi.Attach(_serverTree);
        _client = MultiplayerApi.Attach(_clientTree);
        _server.RegisterScene(scene, BoxUid);
        _client.RegisterScene(scene, BoxUid);
        var endpoint = LoopbackTransport.CreateServer();
        _server.StartServer(endpoint);
        _client.StartClient(endpoint.ConnectClient(_client.Messages.Fingerprint));

        _boxes = new BenchNetBox[NodeCount];
        for (var i = 0; i < NodeCount; i++)
            _boxes[i] = _server.Spawn<BenchNetBox>(scene);
        var time = new GameTime { DeltaTime = 1f / 60f };
        for (var i = 0; i < 20; i++)
        {
            _serverTree.Tick(time);
            _clientTree.Tick(time);
        }

        if (!_client.IsConnected || _client.Stats.NetworkedNodes != NodeCount)
            throw new InvalidOperationException("Replication setup failed.");

        _peer = _server.PeerStateFor(_client.LocalPeerId)!;
        _serverPeer = LoopbackTransport.RemotePeerId;
        _clientListener = _client.Bus!;
        _writer = new NetBufferWriter(64 * 1024);
        _tick = _server.Tick + 1000;
        _clientTick = _tick;

        // A snapshot in which every node changed, as the client receives it.
        Move(_tick);
        _peer.AckedTick = _tick - 1;
        _server.Bus!.Tick = _tick;
        _packet = _server.Bus.Encode(new SnapshotMessage { Api = _server, Peer = _peer }, out _).ToArray();
    }

    private static SceneTree CreateTree()
    {
        var tree = new SceneTree();
        tree.ChangeScene(new Node3D { Name = "Level" });
        return tree;
    }

    // Moves every box and captures the change at `tick`.
    private void Move(uint tick)
    {
        for (var i = 0; i < _boxes.Length; i++)
        {
            var box = _boxes[i];
            box.NetPosition += new Vector3(0.01f, 0, 0);
            box.NetRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, tick * 0.01f + i);
            box.Score = (int)tick;
        }

        _server.CaptureAllAt(tick);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _client?.Dispose();
        _server?.Dispose();
        _writer?.ReleaseStorage();
    }

    /// <summary>Server: detect the changes of every replicated member after all nodes moved.</summary>
    [Benchmark]
    public void CaptureChanges() => Move(++_tick);

    /// <summary>Server: one client's snapshot with every node changed.</summary>
    [Benchmark]
    public int EncodeSnapshot()
    {
        _writer.Reset();
        _server.WriteSnapshot(_writer, _peer);
        return _writer.Length;
    }

    /// <summary>Client: decode and apply a snapshot with every node changed (interpolated members buffered).</summary>
    [Benchmark]
    public uint DecodeSnapshot()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_packet.AsSpan(3), ++_clientTick); // a newer tick each time
        _clientListener.OnReceive(_serverPeer, NetChannel.Unreliable, _packet);
        return _client.Tick;
    }
}

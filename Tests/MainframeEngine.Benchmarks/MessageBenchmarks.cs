using System.Numerics;
using BenchmarkDotNet.Attributes;
using MainframeEngine.Networking;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Message layer cost without a socket: encoding a snapshot-style message (registry lookup, header, payload) and
/// decoding + dispatching one received packet to a typed handler. Both must stay allocation-free.
/// </summary>
[MemoryDiagnoser]
public class MessageBenchmarks : IDisposable
{
    private MessageBus _bus = null!;
    private LoopbackTransport _peer = null!;
    private ITransportListener _listener = null!;
    private byte[] _packet = [];
    private TransformSnapshot _snapshot;
    private float _sink;

    public struct TransformSnapshot : INetworkTransferable
    {
        public uint NodeId;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;

        public readonly void NetworkWrite(NetBufferWriter writer)
        {
            writer.Write(NodeId);
            writer.Write(Position);
            writer.Write(Rotation);
            writer.Write(Velocity);
        }

        public void NetworkRead(NetBufferReader reader)
        {
            NodeId   = reader.ReadUInt32();
            Position = reader.ReadVector3();
            Rotation = reader.ReadQuaternion();
            Velocity = reader.ReadVector3();
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        var registry = new MessageRegistry();
        registry.Register<TransformSnapshot>();
        var (server, client) = LoopbackTransport.CreatePair(registry.Fingerprint);
        _peer     = client;
        _bus      = new MessageBus(server, registry);
        _listener = _bus;
        _bus.Subscribe((in MessageContext _, in TransformSnapshot message) => _sink += message.Position.X);
        _snapshot = new TransformSnapshot
        {
            NodeId   = 12,
            Position = new Vector3(1.5f, -2f, 30f),
            Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0.1f, 0f),
            Velocity = new Vector3(0f, 0.5f, 4f),
        };
        _packet = _bus.Encode(in _snapshot, out _).ToArray();
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _bus?.Dispose();
        _peer?.Dispose();
    }

    [Benchmark]
    public int EncodeSnapshot()
    {
        _bus.Tick++;
        return _bus.Encode(in _snapshot, out _).Length;
    }

    [Benchmark]
    public float DecodeAndDispatchSnapshot()
    {
        _listener.OnReceive(LoopbackTransport.RemotePeerId, NetChannel.Unreliable, _packet);
        return _sink;
    }
}

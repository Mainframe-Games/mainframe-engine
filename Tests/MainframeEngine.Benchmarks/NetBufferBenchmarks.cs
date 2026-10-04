using System.Numerics;
using BenchmarkDotNet.Attributes;
using MainframeEngine.Networking;

namespace MainframeEngine.Benchmarks;

/// <summary>A typical replication message: header, transform and a short string.</summary>
[MemoryDiagnoser]
public class NetBufferBenchmarks
{
    private byte[] _message = [];
    private Vector3 _position = new(1.5f, -2f, 30f);

    [GlobalSetup]
    public void Setup()
    {
        using var writer = NetBufferPool.GetWriter();
        WriteMessage(writer, _position);
        _message = writer.GetData();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NetBufferPool.Destroy();
        _message = [];
    }

    private static void WriteMessage(NetBufferWriter writer, in Vector3 position)
    {
        writer.Write((byte)7);
        writer.Write(123456u);
        writer.Write(position);
        writer.Write(0.25f);
        writer.Write(true);
        writer.Write("player-1");
    }

    [Benchmark]
    public int WritePooled()
    {
        using var writer = NetBufferPool.GetWriter();
        WriteMessage(writer, _position);
        return writer.GetDataSpan().Length;
    }

    [Benchmark]
    public float ReadPooled()
    {
        using var reader = NetBufferPool.GetReader(_message, _message.Length);
        reader.ReadByte();
        reader.ReadUInt32();
        var position = reader.ReadVector3();
        var value = reader.ReadSingle();
        reader.ReadBoolean();
        reader.ReadString();
        return position.X + value;
    }
}

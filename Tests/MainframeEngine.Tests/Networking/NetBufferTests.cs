using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

// NetBufferPool is process-wide static state, so these tests must not run in parallel with each other.
[Collection(nameof(SerialNetBufferPool))]
public sealed class NetBufferTests : IDisposable
{
    public NetBufferTests() => NetBufferPool.Destroy();

    public void Dispose() => NetBufferPool.Destroy();

    [Fact]
    public void PrimitivesRoundTrip()
    {
        using var writer = NetBufferPool.GetWriter();
        writer.Write(true);
        writer.Write((byte)200);
        writer.Write((sbyte)-100);
        writer.Write((short)-30000);
        writer.Write((ushort)60000);
        writer.Write(-123456789);
        writer.Write(3000000000u);
        writer.Write(-1234567890123L);
        writer.Write(12345678901234567890UL);
        writer.Write(1.5f);
        writer.Write(-2.25);
        writer.Write(79.99m);
        writer.Write('Ж');
        writer.Write("héllo wörld");
        writer.Write(new Vector3(1, -2, 3.5f));

        var data = writer.GetDataSpan();
        using var reader = NetBufferPool.GetReader(data, data.Length);

        Assert.True(reader.ReadBoolean());
        Assert.Equal(200, reader.ReadByte());
        Assert.Equal(-100, reader.ReadSByte());
        Assert.Equal(-30000, reader.ReadInt16());
        Assert.Equal(60000, reader.ReadUInt16());
        Assert.Equal(-123456789, reader.ReadInt32());
        Assert.Equal(3000000000u, reader.ReadUInt32());
        Assert.Equal(-1234567890123L, reader.ReadInt64());
        Assert.Equal(12345678901234567890UL, reader.ReadUInt64());
        Assert.Equal(1.5f, reader.ReadSingle());
        Assert.Equal(-2.25, reader.ReadDouble());
        Assert.Equal(79.99m, reader.ReadDecimal());
        Assert.Equal('Ж', reader.ReadChar());
        Assert.Equal("héllo wörld", reader.ReadString());
        Assert.Equal(new Vector3(1, -2, 3.5f), reader.ReadVector3());
        Assert.Equal(reader.Length, reader.Position);
    }

    [Fact]
    public void BytesAndTransferableArraysRoundTrip()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        Sample[] samples = [new() { Id = 1, Value = 0.5f }, new() { Id = 2, Value = -8f }];

        using var writer = NetBufferPool.GetWriter(16);
        writer.Write(payload.AsSpan());
        writer.Write(samples);

        var bytes = writer.GetData();
        using var reader = NetBufferPool.GetReader(bytes, bytes.Length);

        Assert.Equal(payload, reader.ReadBytes(payload.Length));
        var read = reader.ReadArray<Sample>();
        Assert.Equal(samples.Length, read.Length);
        Assert.Equal(samples[0].Id, read[0].Id);
        Assert.Equal(samples[1].Value, read[1].Value);
    }

    [Fact]
    public void GetDataSpanCoversOnlyWrittenBytes()
    {
        using var writer = NetBufferPool.GetWriter(1024);
        writer.Write(7);

        Assert.Equal(4, writer.GetDataSpan().Length);
        Assert.Equal(4, writer.Length);
    }

    [Fact]
    public void WritersAreReturnedToThePoolAndReusedEmpty()
    {
        var first = NetBufferPool.GetWriter();
        first.Write(123L);
        Assert.Equal(1, NetBufferPool.ActiveWriterPoolCount);

        first.Dispose();
        Assert.Equal(0, NetBufferPool.ActiveWriterPoolCount);
        Assert.Equal(1, NetBufferPool.AvailableWritersCount);

        var second = NetBufferPool.GetWriter();
        Assert.Same(first, second);
        Assert.Equal(0, second.Length);
        Assert.Equal(0, second.Position);
        second.Dispose();
    }

    [Fact]
    public void ReadersAreRepopulatedWhenReused()
    {
        var first = NetBufferPool.GetReader([1, 2, 3], 3);
        first.Dispose();

        using var second = NetBufferPool.GetReader([9, 8], 2);

        Assert.Same(first, second);
        Assert.Equal(2, second.Length);
        Assert.Equal(9, second.ReadByte());
        Assert.Equal(1, NetBufferPool.ActiveReaderPoolCount);
    }

    [Fact]
    public void ReaderHonoursTheLengthArgument()
    {
        using var reader = NetBufferPool.GetReader([1, 2, 3, 4], 2);

        Assert.Equal(2, reader.Length);
    }

    public sealed class Sample : INetworkTransferable
    {
        public int Id { get; set; }
        public float Value { get; set; }

        public void NetworkWrite(NetBufferWriter writer)
        {
            writer.Write(Id);
            writer.Write(Value);
        }

        public void NetworkRead(NetBufferReader reader)
        {
            Id = reader.ReadInt32();
            Value = reader.ReadSingle();
        }
    }
}

[CollectionDefinition(nameof(SerialNetBufferPool), DisableParallelization = true)]
public sealed class SerialNetBufferPool;

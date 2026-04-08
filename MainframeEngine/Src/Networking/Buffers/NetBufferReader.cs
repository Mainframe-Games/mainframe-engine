using System.Numerics;
using System.Text;

namespace MainframeEngine.Networking;

/// <summary>
/// Represents a specialized buffer reader used for reading data from a network buffer.
/// Provides functionality for reading primitive types, arrays, and custom network-transferable objects.
/// </summary>
public class NetBufferReader : NetBuffer
{
    private readonly BinaryReader _reader;
    
    public NetBufferReader(byte[] buffer, int length) 
        : base(buffer, length)
    {
        _reader = new BinaryReader(_memory, Encoding.UTF8, true);
    }
    
    public NetBufferReader(ReadOnlySpan<byte> buffer, int length) 
        : base(buffer, length)
    {
        _reader = new BinaryReader(_memory, Encoding.UTF8, true);
    }

    public override void Destroy()
    {
        _reader.Dispose();
        base.Destroy();
    }
    
    public bool ReadBoolean() => _reader.ReadBoolean();
    public byte ReadByte() => _reader.ReadByte();
    public byte[] ReadBytes(int count) => _reader.ReadBytes(count);
    public char ReadChar() => _reader.ReadChar();
    public char[] ReadChars(int count) => _reader.ReadChars(count);
    public decimal ReadDecimal() => _reader.ReadDecimal();
    public double ReadDouble() => _reader.ReadDouble();
    public float ReadSingle() => _reader.ReadSingle();
    public int ReadInt32() => _reader.ReadInt32();
    public long ReadInt64() => _reader.ReadInt64();
    public sbyte ReadSByte() => _reader.ReadSByte();
    public short ReadInt16() => _reader.ReadInt16();
    public string ReadString() => _reader.ReadString();
    public uint ReadUInt32() => _reader.ReadUInt32();
    public ulong ReadUInt64() => _reader.ReadUInt64();
    public ushort ReadUInt16() => _reader.ReadUInt16();

    public Vector3 ReadVector3()
    {
        return new Vector3
        {
            X = ReadSingle(),
            Y = ReadSingle(),
            Z = ReadSingle()
        };
    }
    
    public T[] ReadArray<T>() where T : INetworkTransferable, new()
    {
        var length = _reader.ReadInt32();
        var array = new T[length];
        
        for (int i = 0; i < length; i++)
        {
            array[i] = new T();
            array[i].NetworkRead(this);
        }

        return array;
    }
}
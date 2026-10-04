using System.Numerics;
using System.Text;

namespace MainframeEngine.Networking;

/// <summary>
/// Represents a network buffer writer responsible for serializing various data types into a memory stream
/// for network transmission. This class provides methods for writing primitive types, arrays, and
/// custom objects implementing the <see cref="INetworkTransferable"/> interface.
/// </summary>
public class NetBufferWriter : NetBuffer
{
    private readonly BinaryWriter _writer;

    public NetBufferWriter(int capacity) : base(capacity)
    {
        _writer = new BinaryWriter(_memory, Encoding.UTF8, true);
    }

    public override void Destroy()
    {
        _writer.Dispose();
        base.Destroy();
    }

    public void Write(bool value) => _writer.Write(value);
    public void Write(byte value) => _writer.Write(value);
    public void Write(byte[] buffer) => _writer.Write(buffer);
    public void Write(ReadOnlySpan<byte> buffer) => _writer.Write(buffer);
    public void Write(byte[] buffer, int index, int count) => _writer.Write(buffer, index, count);
    public void Write(char ch) => _writer.Write(ch);
    public void Write(char[] chars) => _writer.Write(chars);
    public void Write(char[] chars, int index, int count) => _writer.Write(chars, index, count);
    public void Write(decimal value) => _writer.Write(value);
    public void Write(double value) => _writer.Write(value);
    public void Write(float value) => _writer.Write(value);
    public void Write(int value) => _writer.Write(value);
    public void Write(long value) => _writer.Write(value);
    public void Write(sbyte value) => _writer.Write(value);
    public void Write(short value) => _writer.Write(value);
    public void Write(string value) => _writer.Write(value);
    public void Write(uint value) => _writer.Write(value);
    public void Write(ulong value) => _writer.Write(value);
    public void Write(ushort value) => _writer.Write(value);

    public void Write(in Vector3 position)
    {
        Write(position.X);
        Write(position.Y);
        Write(position.Z);
    }

    public void Write<T>(T[] array) where T : INetworkTransferable
    {
        Write(array.Length);
        for (int i = 0; i < array.Length; i++)
            array[i].NetworkWrite(this);
    }
}
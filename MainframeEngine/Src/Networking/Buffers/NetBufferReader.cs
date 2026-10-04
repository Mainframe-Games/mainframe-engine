using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MainframeEngine.Networking;

/// <summary>
/// Reads values written by <see cref="NetBufferWriter"/> from a copy of the received bytes, held in an array rented
/// from <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
/// <remarks>
/// <see cref="SetData"/> copies the input, so the reader stays valid after the source (e.g. a native packet) is
/// freed, and refilling a reader never allocates once its storage is large enough. Reads past the end throw
/// <see cref="EndOfStreamException"/>; malformed lengths throw <see cref="InvalidDataException"/>. Only
/// <see cref="ReadString"/>, <see cref="ReadBytes(int)"/> and <see cref="ReadArray{T}"/> allocate (their results).
/// <see cref="Dispose"/> returns the reader to <see cref="NetBufferPool"/>. Not thread-safe.
/// </remarks>
public sealed class NetBufferReader : IDisposable
{
    private byte[] _buffer = [];
    private int    _length;
    private int    _position;

    /// <summary>True while the reader sits in <see cref="NetBufferPool"/> (guards against double returns).</summary>
    internal bool InPool;

    /// <summary>True while rented from <see cref="NetBufferPool"/> (counted as active).</summary>
    internal bool Rented;

    /// <summary>Creates an empty reader; fill it with <see cref="SetData"/>.</summary>
    public NetBufferReader()
    {
    }

    /// <summary>Creates a reader over a copy of <paramref name="data"/>.</summary>
    public NetBufferReader(ReadOnlySpan<byte> data) => SetData(data);

    /// <summary>Creates a reader over a copy of the first <paramref name="length"/> bytes of <paramref name="data"/>.</summary>
    public NetBufferReader(byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        SetData(data.AsSpan(0, length));
    }

    /// <summary>Number of readable bytes.</summary>
    public int Length => _length;

    /// <summary>Read position, from 0 to <see cref="Length"/>.</summary>
    public int Position => _position;

    /// <summary>Bytes left to read.</summary>
    public int Remaining => _length - _position;

    /// <summary>The unread bytes. Valid until the next <see cref="SetData"/> or <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> RemainingSpan => new(_buffer, _position, _length - _position);

    /// <summary>Replaces the contents with a copy of <paramref name="data"/> and rewinds.</summary>
    public void SetData(ReadOnlySpan<byte> data)
    {
        if (data.Length > _buffer.Length)
        {
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 256));
            if (_buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        data.CopyTo(_buffer);
        _length   = data.Length;
        _position = 0;
    }

    /// <summary>Clears the contents, keeping the storage.</summary>
    public void Reset()
    {
        _length   = 0;
        _position = 0;
    }

    /// <summary>Returns the reader to <see cref="NetBufferPool"/>. Safe to call more than once.</summary>
    public void Dispose() => NetBufferPool.Return(this);

    /// <summary>Gives the storage back to the shared array pool (pool trimming / teardown).</summary>
    internal void ReleaseStorage()
    {
        var buffer = _buffer;
        _buffer = [];
        Reset();
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)(_length - _position))
            ThrowEndOfData(count);
        var span = new ReadOnlySpan<byte>(_buffer, _position, count);
        _position += count;
        return span;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowEndOfData(int count) =>
        throw new EndOfStreamException($"Tried to read {count} bytes with {Remaining} of {Length} left.");

    public bool ReadBoolean() => Take(1)[0] != 0;
    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(sizeof(short)));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(sizeof(double)));
    public char ReadChar() => (char)BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(char)));

    public decimal ReadDecimal()
    {
        var span = Take(16);
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4)..]);
        try
        {
            return new decimal(bits);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Invalid decimal encoding.", e);
        }
    }

    /// <summary>Reads a 7-bit encoded unsigned integer written by <see cref="NetBufferWriter.WriteVarUInt32"/>.</summary>
    public uint ReadVarUInt32()
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadByte();
            if (shift == 28 && b > 0x0F)
                break; // more than 32 bits
            result |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
                return result;
        }

        throw new InvalidDataException("Malformed 7-bit encoded integer.");
    }

    /// <summary>Reads a 7-bit encoded unsigned integer written by <see cref="NetBufferWriter.WriteVarUInt64"/>.</summary>
    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = ReadByte();
            if (shift == 63 && b > 0x01)
                break; // more than 64 bits
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
                return result;
        }

        throw new InvalidDataException("Malformed 7-bit encoded integer.");
    }

    /// <summary>Skips <paramref name="count"/> bytes.</summary>
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Take(count);
    }

    /// <summary>Reads a length-prefixed UTF-8 string. Allocates the string.</summary>
    public string ReadString()
    {
        var byteCount = ReadVarUInt32();
        if (byteCount > (uint)Remaining)
            throw new InvalidDataException($"String length {byteCount} exceeds the {Remaining} bytes left.");
        return byteCount == 0 ? string.Empty : Encoding.UTF8.GetString(Take((int)byteCount));
    }

    /// <summary>Returns the next <paramref name="count"/> bytes without copying. Valid until the reader is refilled.</summary>
    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Take(count);
    }

    /// <summary>Copies the next <c>destination.Length</c> bytes into <paramref name="destination"/>.</summary>
    public void ReadBytes(Span<byte> destination) => Take(destination.Length).CopyTo(destination);

    /// <summary>Reads <paramref name="count"/> bytes into a new array. Allocates: prefer <see cref="ReadSpan"/>.</summary>
    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Take(count).ToArray();
    }

    public Vector2 ReadVector2()
    {
        var x = ReadSingle();
        var y = ReadSingle();
        return new Vector2(x, y);
    }

    public Vector3 ReadVector3()
    {
        var x = ReadSingle();
        var y = ReadSingle();
        var z = ReadSingle();
        return new Vector3(x, y, z);
    }

    public Quaternion ReadQuaternion()
    {
        var x = ReadSingle();
        var y = ReadSingle();
        var z = ReadSingle();
        var w = ReadSingle();
        return new Quaternion(x, y, z, w);
    }

    /// <summary>Reads a value through its <see cref="INetworkTransferable.NetworkRead"/>. No allocation for structs.</summary>
    public T ReadValue<T>() where T : struct, INetworkTransferable
    {
        var value = default(T);
        value.NetworkRead(this);
        return value;
    }

    /// <summary>
    /// Reads an int32 count and that many elements (written by <see cref="NetBufferWriter.Write{T}(T[])"/>).
    /// Every element must encode to at least one byte; a larger count is rejected as malformed.
    /// </summary>
    public T[] ReadArray<T>() where T : INetworkTransferable, new()
    {
        var length = ReadInt32();
        if (length < 0 || length > Remaining)
            throw new InvalidDataException($"Array length {length} is invalid with {Remaining} bytes left.");

        var array = new T[length];
        for (var i = 0; i < length; i++)
        {
            array[i] = new T();
            array[i].NetworkRead(this);
        }

        return array;
    }
}

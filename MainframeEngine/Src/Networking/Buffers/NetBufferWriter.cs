using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MainframeEngine.Networking;

/// <summary>
/// Serializes values into a growable byte buffer rented from <see cref="ArrayPool{T}.Shared"/>, for network
/// transmission. All multi-byte values are little-endian; see <see cref="NetBufferReader"/> for the matching reads.
/// </summary>
/// <remarks>
/// Writing never allocates once the buffer is large enough: the array is kept across <see cref="Reset"/> and
/// across pool reuse. <see cref="Dispose"/> returns the writer to <see cref="NetBufferPool"/>; do not use it
/// afterwards. Not thread-safe: one writer belongs to one thread at a time.
/// </remarks>
public sealed class NetBufferWriter : IBufferWriter<byte>, IDisposable
{
    /// <summary>Capacity used when none is given (fits a typical MTU-sized packet).</summary>
    public const int DefaultCapacity = 1024;

    private byte[] _buffer;
    private int    _position;

    /// <summary>True while the writer sits in <see cref="NetBufferPool"/> (guards against double returns).</summary>
    internal bool InPool;

    /// <summary>True while rented from <see cref="NetBufferPool"/> (counted as active).</summary>
    internal bool Rented;

    /// <summary>Creates a writer with at least <paramref name="capacity"/> bytes of storage.</summary>
    public NetBufferWriter(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _buffer = capacity == 0 ? [] : ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <summary>Number of bytes written so far (also the write position).</summary>
    public int Length => _position;

    /// <summary>The write position; equal to <see cref="Length"/> (writes are append-only).</summary>
    public int Position => _position;

    /// <summary>Size of the underlying storage.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>The bytes written so far. Valid until the next write, <see cref="Reset"/> or <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, _position);

    /// <summary>Same as <see cref="WrittenSpan"/>; does not allocate.</summary>
    public ReadOnlySpan<byte> GetDataSpan() => WrittenSpan;

    /// <summary>Copies the written bytes to a new array. Allocates: prefer <see cref="GetDataSpan"/>.</summary>
    public byte[] GetData() => WrittenSpan.ToArray();

    /// <summary>Discards the written bytes, keeping the storage.</summary>
    public void Reset() => _position = 0;

    /// <summary>Returns the writer to <see cref="NetBufferPool"/>. Safe to call more than once.</summary>
    public void Dispose() => NetBufferPool.Return(this);

    /// <summary>Grows the storage so that at least <paramref name="capacity"/> bytes fit in total.</summary>
    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > _buffer.Length)
            Grow(capacity - _position);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var required = Math.Max(sizeHint, 1);
        if (_buffer.Length - _position < required)
            Grow(required);
        return _buffer.AsSpan(_position);
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        GetSpan(sizeHint);
        return _buffer.AsMemory(_position);
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _position);
        _position += count;
    }

    /// <summary>Gives the storage back to the shared array pool (pool trimming / teardown).</summary>
    internal void ReleaseStorage()
    {
        var buffer = _buffer;
        _buffer   = [];
        _position = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Reserve(int count)
    {
        if (_buffer.Length - _position < count)
            Grow(count);
        var span = _buffer.AsSpan(_position, count);
        _position += count;
        return span;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additional)
    {
        var required = checked(_position + additional);
        var size     = Math.Max(required, Math.Max(_buffer.Length * 2, 256));
        var next     = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _position).CopyTo(next);
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Write(bool value) => Reserve(1)[0] = value ? (byte)1 : (byte)0;
    public void Write(byte value) => Reserve(1)[0] = value;
    public void Write(sbyte value) => Reserve(1)[0] = unchecked((byte)value);
    public void Write(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(sizeof(short)), value);
    public void Write(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(sizeof(ushort)), value);
    public void Write(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);
    public void Write(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);
    public void Write(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);
    public void Write(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), value);
    public void Write(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(sizeof(float)), value);
    public void Write(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(sizeof(double)), value);

    /// <summary>Writes a UTF-16 code unit as 2 bytes.</summary>
    public void Write(char value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(sizeof(char)), value);

    /// <summary>Writes the four 32-bit parts of the decimal (lo, mid, hi, flags), 16 bytes.</summary>
    public void Write(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var span = Reserve(16);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt32LittleEndian(span[(i * 4)..], bits[i]);
    }

    /// <summary>Writes a 7-bit encoded unsigned integer (1 to 5 bytes; small values are short).</summary>
    public void WriteVarUInt32(uint value)
    {
        while (value >= 0x80)
        {
            Write((byte)(value | 0x80));
            value >>= 7;
        }
        Write((byte)value);
    }

    /// <summary>Writes a string as a 7-bit encoded UTF-8 byte count followed by the UTF-8 bytes. Does not allocate.</summary>
    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(value.AsSpan());
    }

    /// <summary>Writes characters as a 7-bit encoded UTF-8 byte count followed by the UTF-8 bytes.</summary>
    public void Write(ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt32((uint)byteCount);
        Encoding.UTF8.GetBytes(value, Reserve(byteCount));
    }

    /// <summary>Writes raw bytes (no length prefix).</summary>
    public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Writes raw bytes (no length prefix).</summary>
    public void Write(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(bytes.AsSpan());
    }

    /// <summary>Writes <paramref name="count"/> raw bytes from <paramref name="bytes"/> (no length prefix).</summary>
    public void Write(byte[] bytes, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(bytes.AsSpan(index, count));
    }

    /// <summary>Writes 2 × float32 (8 bytes).</summary>
    public void Write(in Vector2 value)
    {
        Write(value.X);
        Write(value.Y);
    }

    /// <summary>Writes 3 × float32 (12 bytes).</summary>
    public void Write(in Vector3 value)
    {
        Write(value.X);
        Write(value.Y);
        Write(value.Z);
    }

    /// <summary>Writes 4 × float32 (16 bytes), X Y Z W.</summary>
    public void Write(in Quaternion value)
    {
        Write(value.X);
        Write(value.Y);
        Write(value.Z);
        Write(value.W);
    }

    /// <summary>Writes a value through its <see cref="INetworkTransferable.NetworkWrite"/>. No boxing for structs.</summary>
    public void WriteValue<T>(in T value) where T : INetworkTransferable
    {
        // "is null" on an unconstrained T compiles away for structs (ThrowIfNull(object) would box them).
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        var copy = value;
        copy.NetworkWrite(this);
    }

    /// <summary>Writes an int32 count followed by each element's <see cref="INetworkTransferable.NetworkWrite"/>.</summary>
    public void Write<T>(T[] array) where T : INetworkTransferable
    {
        ArgumentNullException.ThrowIfNull(array);
        Write(array.Length);
        for (var i = 0; i < array.Length; i++)
            array[i].NetworkWrite(this);
    }
}

namespace MainframeEngine.Networking;

/// <summary>
/// Represents an abstract network buffer for reading and writing binary data.
/// Provides functionality to manage an in-memory stream for handling network data
/// with methods for populating, resetting, and retrieving data.
/// </summary>
public abstract class NetBuffer(int capacity) : IDisposable
{
    protected readonly MemoryStream _memory = new(capacity);
    public long Position => _memory.Position;
    public long Length => _memory.Length;

    protected NetBuffer(byte[] buffer, int length) : this(length)
    {
        _memory = new MemoryStream(length);
        _memory.Write(buffer, 0, length);
        _memory.Seek(0, SeekOrigin.Begin);
    }
    
    protected NetBuffer(ReadOnlySpan<byte> buffer, int length) : this(length)
    {
        _memory = new MemoryStream(length);
        _memory.Write(buffer);
        _memory.Seek(0, SeekOrigin.Begin);
    }

    public void Dispose()
    {
        NetBufferPool.ReturnToPool(this);
    }
    
    public void Populate(byte[] buffer, int length)
    {
        // Clear the existing memory stream
        _memory.SetLength(0);
        _memory.Seek(0, SeekOrigin.Begin);

        // Write the new data to the memory stream
        if (length > _memory.Capacity)
            _memory.Capacity = length;
        _memory.Write(buffer, 0, length);
        _memory.Seek(0, SeekOrigin.Begin);
    }
    
    public void Populate(ReadOnlySpan<byte> buffer, int length)
    {
        // Clear the existing memory stream
        _memory.SetLength(0);
        _memory.Seek(0, SeekOrigin.Begin);

        // Write the new data to the memory stream
        if (length > _memory.Capacity)
            _memory.Capacity = length;
        _memory.Write(buffer[..length]);
        _memory.Seek(0, SeekOrigin.Begin);
    }
    
    
    public void Populate(int capacity)
    {
        if (capacity > _memory.Capacity)
            _memory.Capacity = capacity;
        
        _memory.SetLength(0);
        _memory.Seek(0, SeekOrigin.Begin);
    }
    
    /// <summary>
    /// NOTE: This allocates, try to use <see cref="GetDataSpan"/>
    /// </summary>
    /// <returns></returns>
    public byte[] GetData()
    {
        return _memory.ToArray();
    }

    /// <summary>
    /// Retrieves a read-only span of the current data in the network buffer.
    /// This method avoids memory allocation by returning a span directly over the internal buffer.
    /// </summary>
    /// <returns>A read-only span of bytes representing the current data in the buffer.</returns>
    public ReadOnlySpan<byte> GetDataSpan()
    {
        var data = new ReadOnlySpan<byte>(_memory.GetBuffer(), 0, (int)Position);
        return data;
    }
    
    public void Reset()
    {
        _memory.SetLength(0);
        _memory.Seek(0 , SeekOrigin.Begin);
        _memory.Flush();
    }

    public virtual void Destroy()
    {
        _memory.Dispose();
    }
}
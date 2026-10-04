namespace MainframeEngine.Networking;

/// <summary>
/// Provides a pool for managing reusable instances of <see cref="NetBufferReader"/> and <see cref="NetBufferWriter"/>.
/// This pool minimizes allocations and improves performance for operations involving network buffers.
/// </summary>
internal static class NetBufferPool
{
    private static readonly Queue<NetBufferWriter> _availableWriterPool = [];
    private static readonly List<NetBufferWriter> _activeWriterPool = [];

    private static readonly Queue<NetBufferReader> _availableReaderPool = [];
    private static readonly List<NetBufferReader> _activeReaderPool = [];

    /// <summary>
    /// Gets the count of active writers currently in use from the pool.
    /// </summary>
    /// <remarks>
    /// This property indicates the number of <see cref="NetBufferWriter"/> instances that are currently
    /// being used and have been checked out from the pool. Monitoring this value can assist in analyzing
    /// resource usage and identifying potential issues, such as writer leaks.
    /// </remarks>
    public static int ActiveWriterPoolCount => _activeWriterPool.Count;

    /// <summary>
    /// Gets the count of active readers currently in use from the pool.
    /// </summary>
    /// <remarks>
    /// This property indicates the number of <see cref="NetBufferReader"/> instances that are currently
    /// being used and have been checked out from the pool. Monitoring this value can help assess
    /// resource utilization and detect potential issues such as reader leaks.
    /// </remarks>
    public static int ActiveReaderPoolCount => _activeReaderPool.Count;

    /// <summary>
    /// Gets the count of available <see cref="NetBufferReader"/> instances currently in the pool.
    /// </summary>
    /// <remarks>
    /// This property indicates the number of reusable <see cref="NetBufferReader"/> objects
    /// that are currently stored in the pool and ready for use. This count represents the pool's
    /// capacity to provide readers without needing to allocate new instances.
    /// </remarks>
    public static int AvailableReadersCount => _availableReaderPool.Count;

    /// <summary>
    /// Gets the count of available writers currently present in the pool.
    /// </summary>
    /// <remarks>
    /// This property indicates the number of reusable <see cref="NetBufferWriter"/> instances
    /// that are currently stored in the pool and available for use. It helps monitor the state
    /// of the writer pool and assess resource availability.
    /// </remarks>
    public static int AvailableWritersCount => _availableWriterPool.Count;

    /// <summary>
    /// Retrieves a reusable NetBufferReader from the pool or creates a new one if the pool is empty.
    /// </summary>
    /// <param name="data">The byte array that contains the data to initialize the reader with.</param>
    /// <param name="length">The length of the data in the byte array to be read by the reader.</param>
    /// <returns>A NetBufferReader instance initialized with the specified data and length.</returns>
    public static NetBufferReader GetReader(byte[] data, int length)
    {
        NetBufferReader buffer;

        if (_availableReaderPool.Count > 0)
        {
            buffer = _availableReaderPool.Dequeue();
            buffer.Populate(data, length);
        }
        else
        {
            buffer = new NetBufferReader(data, length);
        }

        _activeReaderPool.Add(buffer);
        return buffer;
    }

    public static NetBufferReader GetReader(ReadOnlySpan<byte> data, int length)
    {
        NetBufferReader buffer;

        if (_availableReaderPool.Count > 0)
        {
            buffer = _availableReaderPool.Dequeue();
            buffer.Populate(data, length);
        }
        else
        {
            buffer = new NetBufferReader(data, length);
        }

        _activeReaderPool.Add(buffer);
        return buffer;
    }

    /// <summary>
    /// Retrieves a reusable NetBufferWriter from the pool or creates a new one if the pool is empty.
    /// </summary>
    /// <param name="capacity">The capacity in bytes to initialize the writer with if a new instance is created.</param>
    /// <returns>A NetBufferWriter instance initialized with the specified capacity.</returns>
    public static NetBufferWriter GetWriter(int capacity = 1024)
    {
        NetBufferWriter buffer;

        if (_availableWriterPool.Count > 0)
        {
            buffer = _availableWriterPool.Dequeue();
            buffer.Populate(capacity);
        }
        else
        {
            buffer = new NetBufferWriter(capacity);
        }

        _activeWriterPool.Add(buffer);
        return buffer;
    }

    /// <summary>
    /// Returns the specified NetBuffer instance to the appropriate pool and resets its state for reuse.
    /// </summary>
    /// <param name="buffer">The NetBuffer instance to be returned to the pool, which can be a NetBufferReader or NetBufferWriter.</param>
    public static void ReturnToPool(NetBuffer buffer)
    {
        buffer.Reset();

        switch (buffer)
        {
            case NetBufferWriter writer:
                _activeWriterPool.Remove(writer);
                _availableWriterPool.Enqueue(writer);
                break;
            case NetBufferReader reader:
                _activeReaderPool.Remove(reader);
                _availableReaderPool.Enqueue(reader);
                break;
        }
    }

    /// <summary>
    /// Clears all active and available buffers from the pool and invokes their destruction logic to release any associated resources.
    /// </summary>
    public static void Destroy()
    {
        foreach (var buffer in _activeWriterPool)
            buffer.Destroy();
        foreach (var buffer in _activeReaderPool)
            buffer.Destroy();

        _activeWriterPool.Clear();
        _activeReaderPool.Clear();

        while (_availableWriterPool.Count > 0)
            _availableWriterPool.Dequeue().Destroy();
        while (_availableReaderPool.Count > 0)
            _availableReaderPool.Dequeue().Destroy();
    }
}
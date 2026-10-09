namespace MainframeEngine;

/// <summary>The state of a <see cref="ResourceLoadTask{T}"/> (Godot's <c>ResourceLoader.ThreadLoadStatus</c>).</summary>
public enum ResourceLoadStatus
{
    /// <summary>Still loading on a worker thread.</summary>
    InProgress,

    /// <summary>Loaded: <see cref="ResourceLoadTask{T}.Result"/> holds it.</summary>
    Loaded,

    /// <summary>The load threw: <see cref="ResourceLoadTask{T}.Error"/> holds the exception.</summary>
    Failed,
}

/// <summary>
/// A resource loading on a worker thread (<see cref="ResourceLoader.LoadThreaded{T}"/>, Godot's
/// <c>load_threaded_request</c>): poll <see cref="Status"/> from the game loop, then take <see cref="Result"/>. The load
/// takes a reference like <see cref="ResourceLoader.Load{T}"/>; release it when done (or call <see cref="Dispose"/>, which
/// releases it if it was never taken).
/// </summary>
public sealed class ResourceLoadTask<T> : IDisposable where T : Resource
{
    private readonly Task<T> _task;
    private int _taken;

    internal ResourceLoadTask(string pathOrUid)
    {
        Path = pathOrUid;
        _task = Task.Run(() => ResourceLoader.Load<T>(pathOrUid));
    }

    /// <summary>The path or UID being loaded.</summary>
    public string Path { get; }

    public ResourceLoadStatus Status => _task.Status switch
    {
        TaskStatus.RanToCompletion => ResourceLoadStatus.Loaded,
        TaskStatus.Faulted or TaskStatus.Canceled => ResourceLoadStatus.Failed,
        _ => ResourceLoadStatus.InProgress,
    };

    /// <summary>0 while loading, 1 once finished (loaded or failed).</summary>
    public float Progress => _task.IsCompleted ? 1f : 0f;

    /// <summary>True once the load finished, either way.</summary>
    public bool IsCompleted => _task.IsCompleted;

    /// <summary>The exception the load threw (null while loading or when it succeeded).</summary>
    public Exception? Error => _task.Exception?.InnerException ?? _task.Exception;

    /// <summary>
    /// The loaded resource; waits when it is still loading (Godot's <c>load_threaded_get</c>) and rethrows the load's
    /// exception when it failed. The caller owns the reference.
    /// </summary>
    public T Result
    {
        get
        {
            var result = _task.GetAwaiter().GetResult();
            Interlocked.Exchange(ref _taken, 1);
            return result;
        }
    }

    /// <summary>The load as a task (awaitable; it does not hand over the reference).</summary>
    public Task<T> AsTask() => _task;

    /// <summary>Releases the loaded resource's reference if <see cref="Result"/> was never read (waits for the load).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _taken, 1) != 0)
            return;
        try
        {
            _task.Wait();
        }
        catch (AggregateException)
        {
            return; // failed: nothing was loaded
        }

        _task.Result.Release();
    }
}

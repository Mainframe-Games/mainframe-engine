namespace MainframeEngine.Editor.Video;

/// <summary>
/// Starts <see cref="VideoJob{T}"/>s. The factory is not on the generic type itself: a static member there is CA1000, and
/// here the result type is inferred from the work (<c>VideoJob.Start("waveform", (p, ct) =&gt; …)</c>).
/// </summary>
public static class VideoJob
{
    /// <summary>Starts <paramref name="work"/> on the thread pool with a progress sink and a cancellation token of its own.</summary>
    public static VideoJob<T> Start<T>(string name, Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(work);
        var job = new VideoJob<T>(name);
        job.Run(work);
        return job;
    }
}

/// <summary>
/// One long video operation (waveform, silence detection, transcription, export) running on a worker thread, in the
/// pattern of <see cref="Music.SongRenderJob"/>: the main thread polls <see cref="Progress"/> and <see cref="IsDone"/>
/// each frame and reads <see cref="Task"/> once it is done, so no callback ever runs off the main thread.
/// </summary>
public sealed class VideoJob<T> : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private double _progress;
    private bool _disposed;

    internal VideoJob(string name)
    {
        Name = name;
        Task = System.Threading.Tasks.Task.FromResult(default(T)!);
    }

    /// <summary>What runs, for the status line and the log ("waveform", "silence", …).</summary>
    public string Name { get; }

    /// <summary>The work. Faulted with the work's exception, or cancelled after <see cref="Cancel"/>.</summary>
    public Task<T> Task { get; private set; }

    /// <summary>0…1, as the work last reported it.</summary>
    public double Progress => Volatile.Read(ref _progress);

    public bool IsDone => Task.IsCompleted;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    internal void Run(Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        var progress = new ProgressSink(this);
        var token = _cancel.Token;
        Task = System.Threading.Tasks.Task.Run(() => work(progress, token), token);
    }

    /// <summary>Asks the work to stop; it ends cancelled (ffmpeg is killed through the token).</summary>
    public void Cancel()
    {
        if (!_disposed)
            _cancel.Cancel();
    }

    /// <summary>
    /// Cancels work still running and releases the token source once the work has finished: the work may still register on
    /// the token while it unwinds, which a disposed source would turn into an <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Task.IsCompleted)
        {
            _ = Task.Exception; // a failure nobody read is observed here, not reported later by the finalizer
            _cancel.Dispose();
            return;
        }

        _cancel.Cancel();
        Task.ContinueWith(static (task, state) =>
        {
            _ = task.Exception;
            ((CancellationTokenSource)state!).Dispose();
        }, _cancel, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class ProgressSink(VideoJob<T> job) : IProgress<double>
    {
        public void Report(double value) => Volatile.Write(ref job._progress, Math.Clamp(value, 0, 1));
    }
}

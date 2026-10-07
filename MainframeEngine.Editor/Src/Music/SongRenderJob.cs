namespace MainframeEngine.Editor.Music;

/// <summary>
/// One song render (<see cref="SongRenderer.Render"/>) running on a worker thread: a copy of the song is rendered, so
/// the tab stays editable. The UI polls <see cref="Progress"/> and <see cref="IsDone"/> on the main thread.
/// </summary>
public sealed class SongRenderJob : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private double _progress;

    private SongRenderJob(string songPath, string name, Task<SongRenderResult> task)
    {
        SongPath = songPath;
        Name = name;
        Task = task;
    }

    /// <summary>The song file (absolute).</summary>
    public string SongPath { get; }

    /// <summary>The song's name (the default output name).</summary>
    public string Name { get; }

    public Task<SongRenderResult> Task { get; private set; }

    /// <summary>0…1.</summary>
    public double Progress => Volatile.Read(ref _progress);

    public bool IsDone => Task.IsCompleted;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    /// <summary>Starts rendering <paramref name="song"/> (copied first) into the project of <paramref name="database"/>.</summary>
    public static SongRenderJob Start(Song song, string songPath, AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(database);
        var copy = song.Clone();
        var name = Path.GetFileNameWithoutExtension(songPath);
        var job = new SongRenderJob(Path.GetFullPath(songPath), name, System.Threading.Tasks.Task.FromResult(default(SongRenderResult)));
        var progress = new ProgressSink(job);
        job.Task = System.Threading.Tasks.Task.Run(() => SongRenderer.Render(copy, name, database, progress: progress, cancellation: job._cancel.Token));
        return job;
    }

    public void Cancel() => _cancel.Cancel();

    /// <summary>Releases the cancellation source (after the task finished).</summary>
    public void Dispose() => _cancel.Dispose();

    private sealed class ProgressSink(SongRenderJob job) : IProgress<double>
    {
        public void Report(double value) => Volatile.Write(ref job._progress, Math.Clamp(value, 0, 1));
    }
}

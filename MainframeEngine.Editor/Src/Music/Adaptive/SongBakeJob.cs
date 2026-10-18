namespace MainframeEngine.Editor.Music;

/// <summary>
/// One bake (<see cref="SongBaker.Bake"/>) running on a worker thread, like <see cref="SongRenderJob"/>: a copy of the
/// song is baked, so the tab stays editable. The UI polls <see cref="Progress"/>, <see cref="Status"/> and
/// <see cref="IsDone"/> on the main thread; <see cref="Cancel"/> leaves the previous bake untouched.
/// </summary>
public sealed class SongBakeJob : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private double _progress;
    private string _status = "";

    private SongBakeJob(string songPath, string name)
    {
        SongPath = songPath;
        Name = name;
        Task = System.Threading.Tasks.Task.FromResult<SongBakeResult?>(null);
    }

    /// <summary>The song file (absolute).</summary>
    public string SongPath { get; }

    /// <summary>The song's name (the default manifest name).</summary>
    public string Name { get; }

    public Task<SongBakeResult?> Task { get; private set; }

    /// <summary>0…1.</summary>
    public double Progress => Volatile.Read(ref _progress);

    /// <summary>"Combat/Perc 7 of 18" (the last stem done), or "" before the first.</summary>
    public string Status => Volatile.Read(ref _status);

    public bool IsDone => Task.IsCompleted;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    /// <summary>
    /// Starts baking <paramref name="song"/> (copied first) into the project of <paramref name="database"/>;
    /// <paramref name="encoder"/> encodes the stems to <c>.ogg</c> (null: <c>.wav</c>).
    /// </summary>
    public static SongBakeJob Start(Song song, string songPath, AssetDatabase database, IPluginHost? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(database);
        var copy = song.Clone();
        var name = Path.GetFileNameWithoutExtension(songPath);
        var job = new SongBakeJob(Path.GetFullPath(songPath), name);
        var options = new SongBakeOptions
        {
            Encoder = encoder,
            Progress = new ProgressSink(job),
            // Plugin stems render in their own helper (offline mode), separate from the tab's live one.
            Plugins = SongRenderJob.UsesPlugins(copy)
                ? rate => new PluginRack(() => PluginHostClient.TryCreate(), rate, offline: true) { SongName = name }
                : null,
        };
        job.Task = System.Threading.Tasks.Task.Run<SongBakeResult?>(() => SongBaker.Bake(copy, name, database, options, job._cancel.Token));
        return job;
    }

    public void Cancel() => _cancel.Cancel();

    /// <summary>Releases the cancellation source (after the task finished).</summary>
    public void Dispose() => _cancel.Dispose();

    // Workers report concurrently: only a later count replaces the shown one.
    private sealed class ProgressSink(SongBakeJob job) : IProgress<SongBakeProgress>
    {
        private readonly Lock _gate = new();
        private int _done = -1;

        public void Report(SongBakeProgress value)
        {
            lock (_gate)
            {
                if (value.Done <= _done)
                    return;
                _done = value.Done;
                Volatile.Write(ref job._progress, Math.Clamp(value.Fraction, 0, 1));
                Volatile.Write(ref job._status, $"{value.Stem} {value.Done} of {value.Total}");
            }
        }
    }
}

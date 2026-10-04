namespace MainframeEngine.Editor;

/// <summary>Where a <see cref="PlayInstance"/> is in its life.</summary>
public enum PlayInstanceState
{
    /// <summary>Started; the game has not said hello on the editor link yet.</summary>
    Launching,

    /// <summary>Connected and running.</summary>
    Running,

    /// <summary>Connected; its scene tree is paused.</summary>
    Paused,

    /// <summary>Asked to stop (or saying goodbye); waiting for the process to exit.</summary>
    Stopping,

    /// <summary>The process exited with code 0, or after the editor stopped it.</summary>
    Exited,

    /// <summary>The process exited with a non-zero code the editor did not cause (or could not start).</summary>
    Crashed,
}

/// <summary>One launched game (see <see cref="PlayService"/>). Updated only by <see cref="PlayService.Update"/>.</summary>
public sealed class PlayInstance
{
    internal PlayInstance(int number, string label, string? scene, DateTime startedUtc, IReadOnlyList<string> arguments)
    {
        Number = number;
        Label = label;
        Scene = scene;
        StartedUtc = startedUtc;
        Arguments = arguments;
    }

    /// <summary>1, 2, … in launch order (per <see cref="PlayService"/>).</summary>
    public int Number { get; }

    /// <summary>The display name (<c>Game</c>, <c>Server</c>, <c>Client</c>).</summary>
    public string Label { get; }

    /// <summary>The <c>--scene</c> it was started with; null for the project's main scene.</summary>
    public string? Scene { get; }

    /// <summary>The full command-line arguments it was started with.</summary>
    public IReadOnlyList<string> Arguments { get; }

    public PlayInstanceState State { get; internal set; }

    /// <summary>The OS process id; null when it could not start.</summary>
    public int? ProcessId { get; internal set; }

    /// <summary>The editor-link connection id while connected.</summary>
    public int? GameId { get; internal set; }

    /// <summary>True once the game said hello (it stays true after it disconnects).</summary>
    public bool HasConnected { get; internal set; }

    /// <summary>The game's process frame (last status).</summary>
    public ulong Frame { get; internal set; }

    public float FramesPerSecond { get; internal set; }

    /// <summary>The scene the game reports running (last status); null before the first status.</summary>
    public string? CurrentScene { get; internal set; }

    /// <summary>The process exit code once it exited (the goodbye's code until then, when one arrived).</summary>
    public int? ExitCode { get; internal set; }

    public DateTime StartedUtc { get; }

    /// <summary>Launch → hello.</summary>
    public TimeSpan? ConnectTime { get; internal set; }

    /// <summary>Not <see cref="PlayInstanceState.Exited"/> or <see cref="PlayInstanceState.Crashed"/>.</summary>
    public bool IsAlive => State is not (PlayInstanceState.Exited or PlayInstanceState.Crashed);

    internal IGameProcess? Process { get; set; }

    /// <summary>When the editor asked it to stop (null: never).</summary>
    internal DateTime? StopRequestedUtc { get; set; }

    internal bool Killed { get; set; }

    internal bool GoodbyeReceived { get; set; }

    /// <summary>A Pause (true) or Resume (false) sent and not yet confirmed by a status.</summary>
    internal bool? PendingPause { get; set; }

    public override string ToString() => $"#{Number} {Label} ({State})";
}

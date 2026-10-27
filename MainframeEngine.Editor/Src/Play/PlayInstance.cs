using MainframeEngine.EditorLink;

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

/// <summary>Whether a <see cref="PlayInstance"/> is recording its gameplay.</summary>
public enum PlayRecordingState
{
    /// <summary>Not recording.</summary>
    None,

    /// <summary>The game confirmed it is recording.</summary>
    Recording,

    /// <summary>The game was asked to stop and is finalising the file.</summary>
    Saving,
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

    /// <summary>The game's scene tree from its last snapshot (<see cref="PlayService.RequestTree"/>), depth-first; null before one arrived.</summary>
    public EditorLinkTreeNode[]? RemoteTree { get; internal set; }

    /// <summary>The last snapshot stopped at <see cref="EditorLinkProtocol.MaxTreeNodes"/>.</summary>
    public bool RemoteTreeTruncated { get; internal set; }

    /// <summary>Bumped by every snapshot (the panel redraws only when it moves).</summary>
    public int RemoteTreeVersion { get; internal set; }

    /// <summary>Not <see cref="PlayInstanceState.Exited"/> or <see cref="PlayInstanceState.Crashed"/>.</summary>
    public bool IsAlive => State is not (PlayInstanceState.Exited or PlayInstanceState.Crashed);

    /// <summary>Whether the game is recording; <see cref="PlayRecordingState.Saving"/> until it reports the file saved.</summary>
    public PlayRecordingState Recording { get; internal set; }

    /// <summary>Where the current (or last started) recording is written; null before one started.</summary>
    public string? RecordingPath { get; internal set; }

    /// <summary>When the game confirmed the recording started; null while not recording.</summary>
    public DateTime? RecordingStartedUtc { get; internal set; }

    /// <summary>The editor-link protocol the game's hello reported; null before it connected.</summary>
    public int? ProtocolVersion { get; internal set; }

    /// <summary>
    /// The game's link can record from the editor (protocol <see cref="MinRecordingProtocolVersion"/> or later). Older
    /// games drop the recording commands, and the editor would wait for a Started that never comes.
    /// </summary>
    public bool CanRecord => ProtocolVersion >= MinRecordingProtocolVersion;

    /// <summary>The first editor-link protocol with <c>StartRecording</c>/<c>StopRecording</c> and the <c>Recording</c> message.</summary>
    public const int MinRecordingProtocolVersion = 4;

    /// <summary>
    /// The crash report the game sent on the editor link before it died (protocol 5), or null: a native crash, a kill, an
    /// older engine, or no crash at all.
    /// </summary>
    public CrashReport? Crash { get; internal set; }

    /// <summary>The game's report bundle folder (from its link report), or null.</summary>
    public string? CrashFolder { get; internal set; }

    /// <summary>The native dump the runtime wrote for this process (the editor launches games with dumps on), or null.</summary>
    public string? DumpPath { get; internal set; }

    /// <summary>The exit code in words (<see cref="ExitCodes.Describe(int)"/>: <c>SIGSEGV, a native crash (exit code 139)</c>), or null while running.</summary>
    public string? ExitDescription => ExitCode is { } code ? ExitCodes.Describe(code) : null;

    /// <summary>The crash has been reported (the Output row was added).</summary>
    internal bool CrashRaised { get; set; }

    /// <summary>Crashed with a link that may still deliver a report: wait until then before reporting without one.</summary>
    internal DateTime? CrashReportDeadlineUtc { get; set; }

    internal IGameProcess? Process { get; set; }

    /// <summary>When the editor asked it to stop (null: never).</summary>
    internal DateTime? StopRequestedUtc { get; set; }

    /// <summary>When a stopped instance still alive is killed (null: not stopping).</summary>
    internal DateTime? StopDeadlineUtc { get; set; }

    internal bool Killed { get; set; }

    internal bool GoodbyeReceived { get; set; }

    /// <summary>A Pause (true) or Resume (false) sent and not yet confirmed by a status.</summary>
    internal bool? PendingPause { get; set; }

    public override string ToString() => $"#{Number} {Label} ({State})";
}

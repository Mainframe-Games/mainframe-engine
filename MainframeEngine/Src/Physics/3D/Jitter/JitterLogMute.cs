namespace MainframeEngine;

/// <summary>Routes Jitter2's log to <see cref="Log"/> (<c>[Jitter2] …</c>), installed once per process.</summary>
internal static class JitterLog
{
    static JitterLog()
    {
        Jitter2.Logger.Listener = static (level, message) =>
        {
            switch (level)
            {
                case Jitter2.Logger.LogLevel.Error:
                    Log.Error($"[Jitter2] {message}");
                    break;
                case Jitter2.Logger.LogLevel.Warning:
                    Log.Warning($"[Jitter2] {message}");
                    break;
                default:
                    Log.Debug($"[Jitter2] {message}");
                    break;
            }
        };
    }

    /// <summary>Runs the static constructor (a no-op afterwards).</summary>
    public static void EnsureInstalled()
    {
    }
}

/// <summary>
/// Detaches Jitter2's log listener for the scope of a character query (cast, recovery, settle). Jitter2 formats a warning
/// string for every EPA that does not converge (<c>string.Format</c>, only while a listener is set); character queries
/// expect that on dense terrain (ADR 0128: the next query takes over), so warning about it would allocate on the main
/// thread every few seconds of walking and break the 0 B frame. Queries run on the main thread outside the world step.
/// </summary>
internal readonly ref struct JitterLogMute
{
    private readonly Action<Jitter2.Logger.LogLevel, string>? _listener;

    public JitterLogMute()
    {
        _listener = Jitter2.Logger.Listener;
        Jitter2.Logger.Listener = null;
    }

    public void Dispose() => Jitter2.Logger.Listener = _listener;
}

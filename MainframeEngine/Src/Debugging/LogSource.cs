using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>Who wrote a log entry: the engine (its runtime, editor and tools) or the game running on it.</summary>
public enum LogSource
{
    Engine,
    Game,
}

public static partial class Log
{
    // The engine checkout's root (the folder holding MainframeEngine/, MainframeEngine.Editor/, …), as the compiler wrote
    // this file's path: a local path, or "/_/" under deterministic builds. Taken from this file, so it matches how the
    // engine's own call sites were compiled.
    private static readonly string EngineRoot = RootOf();

    /// <summary>
    /// The source of a call site: <see cref="LogSource.Engine"/> when <paramref name="callerFile"/> is in one of the engine
    /// checkout's <c>MainframeEngine*</c> projects, else <see cref="LogSource.Game"/> (game code, or an unknown file).
    /// Needs no registration and works for any game; the check is a prefix compare.
    /// </summary>
    public static LogSource SourceOf(string? callerFile)
    {
        if (string.IsNullOrEmpty(callerFile) || EngineRoot.Length == 0 || !callerFile.StartsWith(EngineRoot, StringComparison.Ordinal))
            return LogSource.Game;
        return callerFile.AsSpan(EngineRoot.Length).StartsWith("MainframeEngine", StringComparison.Ordinal) ? LogSource.Engine : LogSource.Game;
    }

    /// <summary>The tag sinks print for <paramref name="source"/>: <c>engine</c> or <c>game</c>.</summary>
    public static string SourceTag(LogSource source) => source == LogSource.Engine ? "engine" : "game";

    private static string RootOf([CallerFilePath] string file = "")
    {
        // …/MainframeEngine/Src/Debugging/LogSource.cs → …/
        var marker = file.LastIndexOf("MainframeEngine", StringComparison.Ordinal);
        return marker <= 0 ? string.Empty : file[..marker];
    }
}

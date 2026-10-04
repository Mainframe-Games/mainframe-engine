using System.Globalization;

namespace MainframeEngine;

/// <summary>
/// <see cref="GameHost"/>'s command line:
/// <list type="table">
/// <item><term><c>--project &lt;path&gt;</c></term><description><c>project.mfproj</c> (or its folder); default: next to the app</description></item>
/// <item><term><c>--scene &lt;uid|path&gt;</c></term><description>start this scene instead of the project's main scene (editor "play scene")</description></item>
/// <item><term><c>--editor-port &lt;n&gt;</c></term><description>connect to the editor on <c>localhost:n</c> (logs, status, commands)</description></item>
/// <item><term><c>--max-frames &lt;n&gt;</c></term><description>quit after n rendered frames (CI smoke runs)</description></item>
/// <item><term><c>--fixed-fps &lt;n&gt;</c></term><description>every update gets a 1/n s delta (deterministic runs)</description></item>
/// <item><term><c>--hidden</c></term><description>create the window hidden</description></item>
/// <item><term><c>--no-vsync</c></term><description>start with VSync off</description></item>
/// <item><term><c>--no-log-file</c></term><description>do not write <c>{user data}/{game}/logs/{game}.log</c></description></item>
/// <item><term><c>--validation</c></term><description>enable the Vulkan validation layers</description></item>
/// </list>
/// Arguments the host does not know are kept in <see cref="Remaining"/> for the game.
/// </summary>
public sealed record GameHostOptions
{
    public string? ProjectPath { get; init; }

    public string? Scene { get; init; }

    public int? EditorPort { get; init; }

    public int MaxFrames { get; init; }

    public int FixedFps { get; init; }

    public bool Hidden { get; init; }

    public bool NoVSync { get; init; }

    public bool LogFile { get; init; } = true;

    public bool Validation { get; init; }

    /// <summary>Arguments not consumed by the host, in order.</summary>
    public IReadOnlyList<string> Remaining { get; init; } = [];

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on a missing or invalid value.</summary>
    public static GameHostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new GameHostOptions();
        var remaining = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--project":
                    options = options with { ProjectPath = Value(args, ref i) };
                    break;
                case "--scene":
                    options = options with { Scene = Value(args, ref i) };
                    break;
                case "--editor-port":
                    options = options with { EditorPort = Int(args, ref i, 1, 65535) };
                    break;
                case "--max-frames":
                    options = options with { MaxFrames = Int(args, ref i, 1, int.MaxValue) };
                    break;
                case "--fixed-fps":
                    options = options with { FixedFps = Int(args, ref i, 1, 1000) };
                    break;
                case "--hidden":
                    options = options with { Hidden = true };
                    break;
                case "--no-vsync":
                    options = options with { NoVSync = true };
                    break;
                case "--no-log-file":
                    options = options with { LogFile = false };
                    break;
                case "--validation":
                    options = options with { Validation = true };
                    break;
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        return options with { Remaining = remaining };
    }

    /// <summary><paramref name="options"/> with the command line overrides applied.</summary>
    public EngineOptions Apply(EngineOptions options)
    {
        if (MaxFrames > 0)
            options.MaxFrames = MaxFrames;
        if (FixedFps > 0)
            options.FixedDeltaTime = 1f / FixedFps;
        if (Hidden)
            options.WindowVisible = false;
        if (NoVSync)
            options.VSync = false;
        if (Validation)
            options.EnableValidation = true;
        return options;
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        var name = args[i];
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{name} needs a value.");
        return args[++i];
    }

    private static int Int(IReadOnlyList<string> args, ref int i, int min, int max)
    {
        var name = args[i];
        var text = Value(args, ref i);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{name} must be an integer between {min} and {max} (got '{text}').");
        return value;
    }
}

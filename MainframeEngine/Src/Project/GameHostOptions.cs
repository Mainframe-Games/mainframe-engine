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
/// <item><term><c>--locale &lt;name&gt;</c></term><description>start in this locale (the player's choice) instead of the project's <c>defaultLocale</c></description></item>
/// <item><term><c>--frame-capture</c></term><description>allow <see cref="SceneTree.CaptureFrame"/> (game screenshot harnesses)</description></item>
/// <item><term><c>--screenshot &lt;file.png&gt;</c></term><description>save the frame <c>--max-frames</c> ends on (frame 60 without it) as a PNG (smoke runs, CI)</description></item>
/// </list>
/// Arguments the host does not know are kept in <see cref="Remaining"/> for the game. Everything after <c>++</c> is the
/// game's own and is never parsed by the host (<see cref="UserArgs"/>, Godot's <c>OS.get_cmdline_user_args</c>), so a game
/// flag may share a name with a host flag (<c>-- --screenshot a.png ++ --screenshot b.png</c>).
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

    /// <summary><c>--frame-capture</c>: the game may capture frames (implied by <c>--screenshot</c>).</summary>
    public bool FrameCapture { get; init; }

    /// <summary>The starting locale, overriding <see cref="LocalizationProjectSettings.DefaultLocale"/>.</summary>
    public string? Locale { get; init; }

    /// <summary>PNG path for <c>--screenshot</c> (enables frame capture).</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>The update on which <c>--screenshot</c> is taken: the last of <see cref="MaxFrames"/>, else the 60th.</summary>
    public int ScreenshotFrame => MaxFrames > 0 ? MaxFrames : 60;

    /// <summary>Arguments not consumed by the host, in order.</summary>
    public IReadOnlyList<string> Remaining { get; init; } = [];

    /// <summary>The arguments after the first <c>++</c>, untouched (empty without one).</summary>
    public IReadOnlyList<string> UserArgs { get; init; } = [];

    /// <summary>The separator before the game's own arguments.</summary>
    public const string UserArgsSeparator = "++";

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on a missing or invalid value.</summary>
    public static GameHostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new GameHostOptions();
        var remaining = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == UserArgsSeparator)
                return options with { Remaining = remaining, UserArgs = [.. args.Skip(i + 1)] };
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
                case "--frame-capture":
                    options = options with { FrameCapture = true };
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
                case "--locale":
                    options = options with { Locale = Value(args, ref i) };
                    break;
                case "--screenshot":
                    options = options with { ScreenshotPath = Value(args, ref i) };
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
        if (Locale is not null)
            options.Locale = Locale;
        if (ScreenshotPath is not null || FrameCapture)
            options.EnableFrameCapture = true;
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

using System.Globalization;
using Silk.NET.Maths;

namespace MainframeEngine.Editor;

/// <summary>Parses the editor's command line into <see cref="EditorAppOptions"/>.</summary>
public static class EditorCommandLine
{
    public const string Usage =
        "Usage: MainframeEngine.Editor [scene.mscene] [--layout <file>] [--size WxH] [--hidden] [--no-vsync] " +
        "[--qa-script <file> --qa-out <dir>] [--smoke <dir> [--smoke-scene <file>] [--smoke-splash] [--no-validation]]";

    public static EditorAppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? scene = null, layout = EditorLayout.DefaultPath, qaScript = null, qaOut = null, smoke = null, smokeScene = null;
        Vector2D<int>? size = null;
        var hidden = false;
        var vsync = true;
        var captureFrame = 0u;
        var splashOnly = false;
        var validation = true;

        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--layout":
                    layout = Next();
                    break;
                case "--no-layout":
                    layout = null;
                    break;
                case "--size":
                    var parts = Next().Split('x');
                    if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var w) ||
                        !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) || w < 320 || h < 240)
                        throw new ArgumentException("--size needs WxH (at least 320x240).");
                    size = new Vector2D<int>(w, h);
                    break;
                case "--hidden":
                    hidden = true;
                    break;
                case "--no-vsync":
                    vsync = false;
                    break;
                case "--qa-script":
                    qaScript = Next();
                    break;
                case "--qa-out":
                    qaOut = Next();
                    break;
                case "--smoke":
                    smoke = Next();
                    break;
                case "--smoke-scene":
                    smokeScene = Next();
                    break;
                case "--smoke-splash":
                    splashOnly = true;
                    break;
                case "--no-validation":
                    validation = false;
                    break;
                case "--capture":
                    captureFrame = uint.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option {args[i]}.");
                    scene = Path.GetFullPath(args[i]);
                    break;
            }
        }

        if (smoke is not null)
        {
            // Deterministic and self-contained: fixed time step, no persisted layout, validation on.
            var smokeRun = new EditorSmokeRun(Path.GetFullPath(smoke), smokeScene is null ? scene : Path.GetFullPath(smokeScene), captureFrame)
            {
                SplashOnly = splashOnly,
            };
            return new EditorAppOptions
            {
                Workspace = new EditorWorkspaceOptions { LayoutPath = null, InitialScene = smokeRun.ScenePath, ShowSplash = false, OutputTimestamps = false, ShowFrameStats = false },
                WindowSize = size ?? new Vector2D<int>(1280, 720),
                Hidden = hidden,
                VSync = false,
                EnableValidation = validation,
                FixedDeltaTime = 1f / 60f,
                EnableFrameCapture = true,
                Automation = smokeRun,
            };
        }

        if (qaScript is not null)
        {
            var script = EditorQaScript.Load(Path.GetFullPath(qaScript), Path.GetFullPath(qaOut ?? "artifacts/qa-editor"));
            return new EditorAppOptions
            {
                Workspace = new EditorWorkspaceOptions { LayoutPath = null, InitialScene = scene },
                WindowSize = size ?? new Vector2D<int>(1600, 960),
                Hidden = hidden,
                VSync = vsync,
                FixedDeltaTime = 1f / 60f,
                EnableFrameCapture = true,
                Automation = script,
            };
        }

        return new EditorAppOptions
        {
            Workspace = new EditorWorkspaceOptions { LayoutPath = layout, InitialScene = scene },
            WindowSize = size,
            Hidden = hidden,
            VSync = vsync,
        };
    }
}

using System.Globalization;
using Silk.NET.Maths;

namespace MainframeEngine.Editor;

/// <summary>Parses the editor's command line into <see cref="EditorAppOptions"/>.</summary>
public static class EditorCommandLine
{
    public const string Usage =
        "Usage: MainframeEngine.Editor [scene.mscene | project folder | project.mfproj] [--project <folder>] [--project-manager] [--layout <file>] [--size WxH] [--scale S] [--hidden] [--no-vsync] " +
        "[--qa-script <file> --qa-out <dir>] [--smoke <dir> [--smoke-scene <file>] [--smoke-splash] [--smoke-golden project-manager|filesystem] [--no-validation]] | --validate-demo-zip <zip>";

    public static EditorAppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? scene = null, project = null, layout = EditorLayout.DefaultPath, qaScript = null, qaOut = null, smoke = null, smokeScene = null;
        var projectManager = false;
        string? golden = null;
        Vector2D<int>? size = null;
        var scale = 0f;
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
                case "--project":
                    project = ProjectFolderOf(Path.GetFullPath(Next())) ?? throw new ArgumentException("--project needs a folder holding project.mfproj (or the file).");
                    break;
                case "--project-manager":
                    projectManager = true;
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
                case "--scale":
                    // EngineOptions.ContentScale: the framebuffer is exactly --size × S pixels on any display.
                    if (!float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || !(scale > 0f) || !float.IsFinite(scale))
                        throw new ArgumentException("--scale needs a positive number (pixels per point).");
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
                case "--smoke-golden":
                    golden = Next() switch
                    {
                        "project-manager" => "project-manager",
                        "filesystem" => "filesystem",
                        var other => throw new ArgumentException($"Unknown --smoke-golden '{other}' (project-manager, filesystem)."),
                    };
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
                    var path = Path.GetFullPath(args[i]);
                    if (ProjectFolderOf(path) is { } folder)
                        project = folder;
                    else
                        scene = path;
                    break;
            }
        }

        if (smoke is not null)
        {
            // Deterministic and self-contained: fixed time step, no persisted layout, validation on.
            var smokeRun = new EditorSmokeRun(Path.GetFullPath(smoke), smokeScene is null ? scene : Path.GetFullPath(smokeScene), captureFrame)
            {
                SplashOnly = splashOnly,
                Golden = golden,
            };
            return new EditorAppOptions
            {
                Workspace = new EditorWorkspaceOptions
                {
                    LayoutPath = null,
                    InitialScene = golden is null ? smokeRun.ScenePath : null,
                    InitialProject = golden == "filesystem" ? project : null,
                    ShowProjectManager = golden == "project-manager",
                    ShowSplash = false,
                    OutputTimestamps = false,
                    ShowFrameStats = false,
                },
                WindowSize = size ?? new Vector2D<int>(1280, 720),
                ContentScale = scale,
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
                // Deterministic: no persisted layout, recents or settings; the Project Manager only when asked for.
                Workspace = new EditorWorkspaceOptions
                {
                    LayoutPath = null,
                    InitialScene = scene,
                    InitialProject = project,
                    ShowProjectManager = projectManager,
                },
                WindowSize = size ?? new Vector2D<int>(1600, 960),
                ContentScale = scale,
                Hidden = hidden,
                VSync = vsync,
                FixedDeltaTime = 1f / 60f,
                EnableFrameCapture = true,
                Automation = script,
            };
        }

        return new EditorAppOptions
        {
            Workspace = new EditorWorkspaceOptions
            {
                LayoutPath = layout,
                InitialScene = scene,
                InitialProject = project,
                ShowProjectManager = projectManager || (scene is null && project is null),
                RecentProjectsPath = RecentProjects.DefaultPath,
                EditorSettingsPath = EditorSettings.DefaultPath,
                ThemeOverlayDirectory = EditorTheme.DefaultOverlayDirectory,
                Updates = hidden ? null : new GitHubUpdateService(),
            },
            WindowSize = size,
            ContentScale = scale,
            Hidden = hidden,
            VSync = vsync,
        };
    }

    /// <summary>The project folder for <paramref name="path"/> (a folder holding <c>project.mfproj</c>, or the file), or null.</summary>
    public static string? ProjectFolderOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, ProjectSettings.FileName)))
            return path;
        if (File.Exists(path) && string.Equals(Path.GetFileName(path), ProjectSettings.FileName, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(path);
        return null;
    }
}

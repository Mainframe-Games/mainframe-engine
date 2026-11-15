using System.Text;

namespace MainframeEngine.Cli;

/// <summary>A problem with the command's input (a missing file, clip or skeleton, a refused replace): exit code 2.</summary>
internal sealed class InputException(string message) : Exception(message);

/// <summary>
/// <c>mfe</c>, the Mainframe engine/editor command-line tool: <c>mfe &lt;group&gt; &lt;command&gt; [args] [options]</c>.
/// <see cref="Run"/> is the whole program (tests call it in-process). Exit codes: 0 ok, 1 failed (checks found errors), 2
/// usage or input errors.
/// </summary>
internal static class MfeCli
{
    public const string Usage = """
        mfe — Mainframe Engine command-line tool

          mfe anim <command> …    Animation authoring on rigged humanoids (G1c): rig, poses, bake, describe, check, list, render
          mfe help                This text; `mfe anim help` lists the anim commands

        Options for every command: --project <dir> (default: the nearest project.mfproj above the working directory),
        --json (one JSON document instead of text).
        Exit codes: 0 ok, 1 checks found errors or the command failed, 2 usage or input errors (nothing written).
        """;

    internal static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--json", "--write-meta", "--replace", "--no-link", "--flat", "--help", "-h",
    };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "anim" => AnimCommands.Run(args[1..], output, error),
                _ => throw new UsageException($"unknown command group '{args[0]}'"),
            };
        }
        catch (UsageException e)
        {
            error.WriteLine($"mfe: {e.Message}");
            error.WriteLine("Run 'mfe help' for usage.");
            return 2;
        }
        catch (PoseScriptException e)
        {
            foreach (var problem in e.Errors)
                error.WriteLine($"mfe: {problem}");
            error.WriteLine("Nothing was written.");
            return 2;
        }
        catch (Exception e) when (e is InputException or PoseLibraryException or FileNotFoundException or DirectoryNotFoundException)
        {
            error.WriteLine($"mfe: {e.Message}");
            return 2;
        }
        catch (ContactSheet.RenderUnavailableException e)
        {
            error.WriteLine($"mfe: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                                      or System.Text.Json.JsonException)
        {
            error.WriteLine($"mfe: {e.Message}");
            return 1;
        }
    }

    public static int RunConsole(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        return Run(args, Console.Out, Console.Error);
    }
}

/// <summary>
/// The game project a command runs in: <c>--project</c>, or the nearest <c>project.mfproj</c> above the working directory.
/// Opening it points <see cref="ContentPaths.ProjectDirectory"/> and <see cref="AssetDatabase.Current"/> at it; disposing
/// restores both (tests run commands in-process).
/// </summary>
internal sealed class MfeProject : IDisposable
{
    private readonly AssetDatabase _previousAssets;
    private readonly string? _previousDirectory;

    private MfeProject(string root)
    {
        Root = root;
        _previousAssets = AssetDatabase.Current;
        _previousDirectory = ContentPaths.ProjectDirectory;
        Settings = ProjectSettings.Load(root);
        ContentPaths.ProjectDirectory = root;
        AssetDatabase.Current = new AssetDatabase(root);
        AssetDatabase.Current.Refresh(createMissingMeta: false);
        ResourceLoader.ClearCache();
    }

    public string Root { get; }

    public ProjectSettings Settings { get; }

    /// <summary>The project's 3D gravity (m/s²), for arcs and the physics check.</summary>
    public float Gravity => Settings.Physics.Physics3D.Gravity.Length() is var g and > 0f ? g : 9.8f;

    public static MfeProject Open(string? directory)
    {
        if (directory is not null)
        {
            var root = Path.GetFullPath(directory);
            if (!File.Exists(Path.Combine(root, ProjectSettings.FileName)))
                throw new InputException($"{root} has no {ProjectSettings.FileName}.");
            return new MfeProject(root);
        }

        for (var at = new DirectoryInfo(Directory.GetCurrentDirectory()); at is not null; at = at.Parent)
            if (File.Exists(Path.Combine(at.FullName, ProjectSettings.FileName)))
                return new MfeProject(at.FullName);
        throw new InputException($"no {ProjectSettings.FileName} here or above {Directory.GetCurrentDirectory()}; run inside a game project or pass --project <dir>.");
    }

    /// <summary>The absolute path of a project path (<c>Assets/…</c>); an absolute path stays as it is.</summary>
    public static string Absolute(string projectPath) => AssetDatabase.Current.ToAbsolutePath(projectPath);

    /// <summary>A path inside the project as a project path (<c>Assets/…</c>), whatever form it was given in.</summary>
    public static string ProjectPath(string path) => AssetDatabase.Current.ToProjectPath(Path.IsPathRooted(path) ? path : Absolute(path));

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        ContentPaths.ProjectDirectory = _previousDirectory;
    }
}

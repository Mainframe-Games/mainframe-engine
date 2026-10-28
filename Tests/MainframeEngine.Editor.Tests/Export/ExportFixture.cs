using System.IO.Compression;
using System.Text;

namespace MainframeEngine.Editor.Tests.Export;

/// <summary>
/// A small game project on disk for the export tests (no SDK needed): <c>project.mfproj</c>, a desktop project file, a few
/// <c>Assets/</c> files, and a fake <c>dotnet publish</c> (<see cref="Run"/>) that writes what a checkout publish writes: the
/// apphost, every platform's engine natives, both Steamworks shims, the content and the project file.
/// </summary>
public sealed class ExportFixture : IDisposable
{
    public ExportFixture(string name = "Tiny Game")
    {
        Root = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-export").FullName);
        Settings = new ProjectSettings { Name = name, MainScene = "Assets/Scenes/Main.mscene", Version = "1.2.0" };
        Settings.Window.Icon = "Assets/icon.png";
        Settings.Audio.BusLayout = "Assets/Settings/Bus.mres";
        Write("TinyGame.Desktop/TinyGame.Desktop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("Assets/Scenes/Main.mscene", """{ "uid": "scn_aaaaaaaaaaaa", "root": { "type": "Node", "name": "Main", "props": { "music": { "ref": "res_bbbbbbbbbbbb", "path": "Assets/Music/theme.mres" }, "tex": "Assets/Textures/a.png" } } }""");
        Write("Assets/Music/theme.mres", """{ "uid": "res_bbbbbbbbbbbb", "type": "AudioStream" }""");
        Write("Assets/Textures/a.png", "png");
        Write("Assets/Textures/a.png.meta", """{ "uid": "tex_cccccccccccc" }""");
        Write("Assets/Settings/Bus.mres", """{ "uid": "res_dddddddddddd", "type": "AudioBusLayout" }""");
        Write("Assets/locale/fr/LC_MESSAGES/messages.po", "msgid \"\"\nmsgstr \"\"\n");
        WriteIcon("Assets/icon.png", 256);
        Save();
    }

    public string Root { get; }

    public ProjectSettings Settings { get; }

    /// <summary>The <c>dotnet</c> calls the fake tool runner saw.</summary>
    public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    /// <summary>The fake publish fails with this many errors (0: it succeeds).</summary>
    public bool FailPublish { get; set; }

    /// <summary>Called during the fake publish (e.g. to cancel).</summary>
    public Action? DuringPublish { get; set; }

    /// <summary>Steam files are left out of the fake publish (a broken publish).</summary>
    public bool OmitSteam { get; set; }

    public void Save() => Settings.Save(Root);

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string text)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public void WriteIcon(string relative, int size)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 3] = 255;
        }

        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Png.WriteRgba8(path, size, size, rgba);
    }

    /// <summary>An environment on <paramref name="host"/> whose tools are this fixture's fakes.</summary>
    public ExportEnvironment Environment(ExportHost host = ExportHost.Linux, Dictionary<string, string>? variables = null) => new()
    {
        DotnetPath = "/fake/dotnet",
        Sdk = new DotnetSdkInfo("/fake/dotnet", ["10.0.100"], "10.0.100", true, null),
        Host = host,
        HostRid = host switch { ExportHost.Windows => "win-x64", ExportHost.MacOS => "osx-arm64", _ => "linux-x64" },
        GetVariable = name => variables is not null && variables.TryGetValue(name, out var value) ? value : null,
        RunTool = Run,
    };

    /// <summary>The fake tools: <c>dotnet publish</c> writes a publish folder; <c>codesign</c> succeeds; <c>ditto</c> zips.</summary>
    public Task<ProcessResult> Run(string file, IReadOnlyList<string> args, string? directory, Action<string>? onLine,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        Calls.Add((file, args));
        if (file == "ditto")
        {
            var app = args[^2];
            var zip = args[^1];
            ZipFile.CreateFromDirectory(app, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
            return Task.FromResult(Ok());
        }

        if (file != "/fake/dotnet")
            return Task.FromResult(Ok());

        DuringPublish?.Invoke();
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ProcessResult(-1, [], TimeSpan.Zero, false, true, null));
        if (FailPublish)
        {
            var line = $"{PathOf("TinyGame/Spinner.cs")}(3,5): error CS1002: ; expected [{PathOf("TinyGame/TinyGame.csproj")}]";
            onLine?.Invoke(line);
            return Task.FromResult(new ProcessResult(1, [line], TimeSpan.Zero, false, false, null));
        }

        var output = args[args.ToList().IndexOf("-o") + 1];
        var rid = args[args.ToList().IndexOf("-r") + 1];
        Directory.CreateDirectory(output);
        var apphost = rid.StartsWith("win", StringComparison.Ordinal) ? "TinyGame.Desktop.exe" : "TinyGame.Desktop";
        var apphostText = rid.StartsWith("osx", StringComparison.Ordinal) ? "apphost ../MonoBundle/TinyGame.Desktop.dll" : "apphost";
        File.WriteAllText(Path.Combine(output, apphost), apphostText);
        foreach (var f in (string[])["TinyGame.Desktop.dll", "TinyGame.Desktop.pdb", "TinyGame.dll", "TinyGame.pdb", "TinyGame.Desktop.runtimeconfig.json",
                     "enet.dll", "mfrmlui.dll", "libenet.so", "libmfrmlui.so", "libenet.dylib", "libmfrmlui.dylib"])
            File.WriteAllText(Path.Combine(output, f), f);
        if (!OmitSteam)
        {
            foreach (var f in (string[])["steam_api64.dll", "libsteam_api.so", "libsteam_api.dylib"])
                File.WriteAllText(Path.Combine(output, f), f);
            foreach (var variant in (string[])["win64", "posix"])
            {
                Directory.CreateDirectory(Path.Combine(output, "steamworks", variant));
                File.WriteAllText(Path.Combine(output, "steamworks", variant, "MainframeEngine.Steamworks.dll"), variant);
            }
        }

        // The project's content and file (as the desktop project copies them) plus engine content and a compiled catalog.
        CopyDirectory(PathOf("Assets"), Path.Combine(output, "Assets"));
        Directory.CreateDirectory(Path.Combine(output, "Assets", "Shaders"));
        File.WriteAllText(Path.Combine(output, "Assets", "Shaders", "Mesh.vk.vert.spv"), "spv");
        File.WriteAllText(Path.Combine(output, "Assets", "locale", "fr", "LC_MESSAGES", "messages.mo"), "mo");
        File.Copy(PathOf(ProjectSettings.FileName), Path.Combine(output, ProjectSettings.FileName));
        onLine?.Invoke("  TinyGame.Desktop -> " + output);
        return Task.FromResult(Ok());
    }

    private static ProcessResult Ok() => new(0, [], TimeSpan.Zero, false, false, null);

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

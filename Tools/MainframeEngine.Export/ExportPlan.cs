using System.Xml.Linq;

namespace MainframeEngine.Export;

/// <summary>
/// What an export of one preset will do, decided without running anything (pure, unit-tested): the effective values
/// (RID, version, executable, bundle id, icon, signing), the folders (artifacts cache, stage, output) and the
/// <c>dotnet publish</c> arguments.
/// </summary>
public sealed class ExportPlan
{
    /// <summary>The folder under the project for export intermediates (<c>.mainframe/export</c>; the template ignores <c>.mainframe/</c>).</summary>
    public const string WorkFolder = ".mainframe/export";

    private ExportPlan()
    {
    }

    public required string ProjectDirectory { get; init; }

    public required ProjectSettings Settings { get; init; }

    public required ExportPreset Preset { get; init; }

    /// <summary>The desktop project (<c>*.Desktop.csproj</c>), or null when there is none.</summary>
    public required string? DesktopProject { get; init; }

    /// <summary>The desktop project's assembly name (the published apphost before it is renamed).</summary>
    public required string AssemblyName { get; init; }

    public required string Rid { get; init; }

    public required string Configuration { get; init; }

    /// <summary>The preset's version, else the project's, else <c>0.0.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The player-facing executable name (no extension).</summary>
    public required string Exe { get; init; }

    /// <summary>The app's display name (the project name): the <c>.app</c> folder and <c>CFBundleName</c>.</summary>
    public required string AppName { get; init; }

    /// <summary>The PNG icon (absolute; null when neither the preset nor <c>window.icon</c> names one).</summary>
    public required string? IconPng { get; init; }

    /// <summary>The desktop project sets its own Windows icon (<c>ApplicationIcon</c>), which wins over the project's PNG.</summary>
    public required bool ProjectHasWindowsIcon { get; init; }

    /// <summary>The desktop project's own <c>MacAppIcon</c> when it is an <c>.icns</c> (ADR 0182), else null.</summary>
    public required string? ProjectMacIcns { get; init; }

    public required string BundleId { get; init; }

    public required string Copyright { get; init; }

    /// <summary>The signing identity: <c>MF_SIGN_IDENTITY</c>, else the preset's <c>sign</c>, else <c>-</c> (ad hoc).</summary>
    public required string SignIdentity { get; init; }

    /// <summary>The notary profile: <c>MF_NOTARY_PROFILE</c>, else the preset's; empty: no notarization.</summary>
    public required string NotaryProfile { get; init; }

    /// <summary>The output folder (absolute).</summary>
    public required string OutputDirectory { get; init; }

    /// <summary><c>--artifacts-path</c>: per RID and full/demo, kept for incremental exports.</summary>
    public required string ArtifactsPath { get; init; }

    /// <summary>The stage folder (emptied first, deleted at the end).</summary>
    public required string StageDirectory { get; init; }

    /// <summary><c>&lt;exe&gt;-&lt;version&gt;-&lt;rid&gt;</c>.</summary>
    public string BaseName => $"{Exe}-{Version}-{Rid}";

    /// <summary>The publish output inside the stage.</summary>
    public string PublishDirectory => Path.Combine(StageDirectory, "publish");

    public ExportPlatform Platform => Preset.Platform;

    public DesktopExportOptions Desktop => Preset.Desktop;

    /// <summary>The executable's file name in the publish (<c>.exe</c> on Windows).</summary>
    public string ExecutableFile => Platform == ExportPlatform.Windows ? Exe + ".exe" : Exe;

    /// <summary>The published apphost's file name before the rename.</summary>
    public string PublishedApphost => Platform == ExportPlatform.Windows ? AssemblyName + ".exe" : AssemblyName;

    /// <summary>What the export leaves: the archive, or the folder for <c>archive: false</c>.</summary>
    public string ArtifactPath => !Preset.Archive ? Path.Combine(OutputDirectory, BaseName)
        : Path.Combine(OutputDirectory, BaseName + (Platform == ExportPlatform.Linux ? ".tar.gz" : ".zip"));

    /// <summary>The symbols zip of <c>symbols: separate</c>.</summary>
    public string SymbolsArchivePath => Path.Combine(OutputDirectory, BaseName + "-symbols.zip");

    /// <summary>The <c>.ico</c> the publish embeds (written before it), in the stage's parent so emptying the stage keeps it.</summary>
    public string IcoPath => Path.Combine(StageDirectory + ".icon", "icon.ico");

    /// <summary>Plans <paramref name="preset"/> of the project at <paramref name="projectDirectory"/>.</summary>
    public static ExportPlan Create(string projectDirectory, ProjectSettings settings, ExportPreset preset, ExportEnvironment environment,
        string? outputOverride = null, string? appName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(environment);
        var root = Path.GetFullPath(projectDirectory);
        var desktop = Directory.Exists(root) ? GameProjectLayout.DesktopProjectOf(root) : null;
        var csproj = desktop is not null && File.Exists(desktop) ? CsprojProperties.Read(desktop) : CsprojProperties.Empty;
        var options = preset.Options as DesktopExportOptions ?? new DesktopExportOptions();
        var mac = options.MacOs ?? new MacOsExportOptions();
        var rid = ExportTargets.EffectiveRid(preset);
        var version = preset.Version.Length > 0 ? preset.Version : settings.Version.Length > 0 ? settings.Version : "0.0.0";
        var name = string.IsNullOrWhiteSpace(appName) ? settings.Name : appName;
        var exe = options.Exe.Length > 0 ? options.Exe : ExportTargets.DefaultExe(name);
        var iconSetting = preset.Icon.Length > 0 ? preset.Icon : settings.Window.Icon;
        var macIcns = csproj.MacAppIcon is { } m && m.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(m, Path.GetDirectoryName(desktop!)!)
            : null;
        var slug = ExportTargets.Slug(preset.Name);
        var output = outputOverride ?? preset.Output ?? $"export/{slug}";
        var work = Path.Combine(root, WorkFolder.Replace('/', Path.DirectorySeparatorChar));
        return new ExportPlan
        {
            ProjectDirectory = root,
            Settings = settings,
            Preset = preset,
            DesktopProject = desktop,
            AssemblyName = csproj.AssemblyName ?? (desktop is null ? exe : Path.GetFileNameWithoutExtension(desktop)),
            Rid = rid,
            Configuration = preset.Configuration,
            Version = version,
            Exe = exe,
            AppName = name,
            IconPng = string.IsNullOrWhiteSpace(iconSetting) ? null : Path.GetFullPath(iconSetting, root),
            // An explicit preset icon wins over the desktop project's own icons; window.icon does not.
            ProjectHasWindowsIcon = preset.Icon.Length == 0 && csproj.ApplicationIcon is { Length: > 0 },
            ProjectMacIcns = preset.Icon.Length == 0 ? macIcns : null,
            BundleId = mac.BundleId.Length > 0 ? mac.BundleId : "com.mainframegames." + exe.ToLowerInvariant(),
            Copyright = mac.Copyright,
            SignIdentity = Variable(environment, "MF_SIGN_IDENTITY") ?? (mac.Sign.Length > 0 ? mac.Sign : "-"),
            NotaryProfile = Variable(environment, "MF_NOTARY_PROFILE") ?? mac.NotaryProfile,
            OutputDirectory = Path.GetFullPath(output, root),
            ArtifactsPath = Path.Combine(work, "obj", $"{rid}-{(preset.Demo ? "demo" : "full")}"),
            StageDirectory = Path.Combine(work, "stage", slug),
        };
    }

    private static string? Variable(ExportEnvironment environment, string name) =>
        environment.GetVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>The <c>dotnet publish</c> arguments; <paramref name="ico"/> is the generated Windows icon (or null).</summary>
    public IReadOnlyList<string> PublishArguments(string? ico)
    {
        if (DesktopProject is null)
            throw new InvalidOperationException("The project has no desktop project to publish.");
        var o = Desktop;
        var args = new List<string>
        {
            "publish", DesktopProject, "-c", Configuration, "-r", Rid, "--self-contained", Bool(o.SelfContained),
            $"-p:MainframeDemo={Bool(Preset.Demo)}",
            $"-p:PublishReadyToRun={Bool(o.ReadyToRun)}",
            $"-p:PublishSingleFile={Bool(o.SingleFile)}",
            $"-p:PublishTrimmed={Bool(o.Trim)}",
        };
        if (o.SingleFile)
            args.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
        // Windows: a GUI-subsystem exe (no console window behind the game); macOS: an apphost that starts the app from
        // Contents/MonoBundle (build/MainframeGame.props, BundleAppHost.targets).
        if (Platform == ExportPlatform.Windows)
            args.Add("-p:MainframeWindowsGui=true");
        if (Platform == ExportPlatform.MacOS)
            args.Add($"-p:MacBundleAppDirectory={AppBundleWriter.AppDirectory}");
        if (ico is not null)
            args.Add($"-p:ApplicationIcon={ico}");
        args.AddRange(["--artifacts-path", ArtifactsPath, "-o", PublishDirectory, "-nologo", "-v:minimal", "-clp:NoSummary", "-p:GenerateFullPaths=true"]);
        return args;
    }

    /// <summary>The executables of the publish (relative, '/'), which archives mark executable.</summary>
    public IReadOnlySet<string> Executables() => new HashSet<string>(StringComparer.Ordinal) { ExecutableFile, "createdump" };

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>The few desktop project properties an export reads (unconditional ones; MSBuild conditions are not evaluated).</summary>
public sealed record CsprojProperties(string? AssemblyName, string? ApplicationIcon, string? MacAppIcon)
{
    public static readonly CsprojProperties Empty = new(null, null, null);

    /// <summary>Reads <paramref name="csproj"/>; <see cref="Empty"/> when it is not readable XML.</summary>
    public static CsprojProperties Read(string csproj)
    {
        try
        {
            var doc = XDocument.Load(csproj);
            string? Value(string name) => doc.Descendants()
                .Where(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")
                .Select(e => e.Value.Trim())
                .LastOrDefault(v => v.Length > 0 && !v.Contains("$(", StringComparison.Ordinal));
            return new CsprojProperties(Value("AssemblyName"), Value("ApplicationIcon"), Value("MacAppIcon"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Empty;
        }
    }
}

namespace MainframeEngine;

/// <summary>The platform an <see cref="ExportPreset"/> builds for (<c>platform</c> in <c>project.mfproj</c>).</summary>
/// <remarks>M12.7 adds <c>android</c> and <c>ios</c> with options of their own (<see cref="IExportPlatformOptions"/>).</remarks>
public enum ExportPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>What an export does with the managed symbols (PDBs) of the build (G15.8, docs/design/crash-reporting.md#symbols).</summary>
public enum ExportSymbols
{
    /// <summary>The PDBs ship next to their assemblies, so crash reports carry files and lines (default).</summary>
    Ship,

    /// <summary>The PDBs and their assemblies go into <c>&lt;exe&gt;-&lt;version&gt;-&lt;rid&gt;-symbols.zip</c> for <c>mf-symbolicate</c>.</summary>
    Separate,
}

/// <summary>A preset's platform-specific options: <see cref="DesktopExportOptions"/> now, mobile options in M12.7.</summary>
public interface IExportPlatformOptions
{
    /// <summary>A deep copy.</summary>
    IExportPlatformOptions Clone();
}

/// <summary>
/// One export preset: the <c>export</c> array of <c>project.mfproj</c>, edited by the editor's File › Export dialog and run
/// by <c>mf-export</c> (docs/design/game-export.md). Keys common to every platform are here; platform keys are in
/// <see cref="Options"/>. Games parse the section and never use it; exported builds carry no presets.
/// </summary>
public sealed class ExportPreset
{
    /// <summary>The default <see cref="Include"/> globs.</summary>
    public static readonly IReadOnlyList<string> DefaultInclude = ["Assets/**"];

    /// <summary>The default <see cref="Exclude"/> globs: translation sources (the build compiles them to <c>.mo</c>).</summary>
    public static readonly IReadOnlyList<string> DefaultExclude = ["Assets/**/*.po", "Assets/**/*.pot"];

    /// <summary>Shown in the dialog; the CLI selects presets by it. Unique within a project.</summary>
    public required string Name
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    public required ExportPlatform Platform { get; set; }

    /// <summary>The output folder, project-relative or absolute; null: <c>export/&lt;name as a slug&gt;</c>.</summary>
    public string? Output
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The build configuration, <c>Release</c> (default) or <c>Debug</c> (a development run).</summary>
    public string Configuration
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    } = "Release";

    /// <summary>Builds the demo (<c>-p:MainframeDemo=true</c>), whatever the project's <see cref="ProjectSettings.IsDemo"/> says.</summary>
    public bool Demo { get; set; }

    /// <summary>The version; empty: the project's <see cref="ProjectSettings.Version"/>, else <c>0.0.0</c>.</summary>
    public string Version
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>A PNG for the executable's and the bundle's icon; empty: <see cref="WindowSettings.Icon"/>.</summary>
    public string Icon
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>Zip (Windows, macOS) or <c>.tar.gz</c> (Linux); false leaves a folder (e.g. a Steam depot's content root).</summary>
    public bool Archive { get; set; } = true;

    /// <summary>Globs (project-relative, <c>/</c>) of the game's <c>Assets/</c> files that ship.</summary>
    public List<string> Include { get; } = [.. DefaultInclude];

    /// <summary>Globs applied after <see cref="Include"/>.</summary>
    public List<string> Exclude { get; } = [.. DefaultExclude];

    /// <summary>The platform's options; a <see cref="DesktopExportOptions"/> for Windows, macOS and Linux.</summary>
    public required IExportPlatformOptions Options { get; set; }

    /// <summary>The desktop options (every platform of this version is a desktop one).</summary>
    public DesktopExportOptions Desktop => Options as DesktopExportOptions
                                           ?? throw new InvalidOperationException($"Preset '{Name}' has no desktop options.");

    /// <summary>A new preset for <paramref name="platform"/> with every default.</summary>
    public static ExportPreset Create(string name, ExportPlatform platform) => new()
    {
        Name = name,
        Platform = platform,
        Options = new DesktopExportOptions { MacOs = platform == ExportPlatform.MacOS ? new MacOsExportOptions() : null },
    };

    /// <summary>A deep copy.</summary>
    public ExportPreset Clone()
    {
        var copy = new ExportPreset
        {
            Name = Name,
            Platform = Platform,
            Output = Output,
            Configuration = Configuration,
            Demo = Demo,
            Version = Version,
            Icon = Icon,
            Archive = Archive,
            Options = Options.Clone(),
        };
        copy.Include.Clear();
        copy.Include.AddRange(Include);
        copy.Exclude.Clear();
        copy.Exclude.AddRange(Exclude);
        return copy;
    }

    /// <summary>The platform's name in <c>project.mfproj</c>: <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
    public static string PlatformKey(ExportPlatform platform) => platform switch
    {
        ExportPlatform.Windows => "windows",
        ExportPlatform.MacOS => "macos",
        ExportPlatform.Linux => "linux",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>Parses a <see cref="PlatformKey"/>; false for anything else (M12.7's <c>android</c>/<c>ios</c> included).</summary>
    public static bool TryParsePlatform(string text, out ExportPlatform platform)
    {
        switch (text)
        {
            case "windows":
                platform = ExportPlatform.Windows;
                return true;
            case "macos":
                platform = ExportPlatform.MacOS;
                return true;
            case "linux":
                platform = ExportPlatform.Linux;
                return true;
            default:
                platform = default;
                return false;
        }
    }
}

/// <summary>The desktop keys of an <see cref="ExportPreset"/> (Windows, macOS, Linux).</summary>
public sealed class DesktopExportOptions : IExportPlatformOptions
{
    /// <summary>The runtime identifier (<c>win-x64</c>, <c>linux-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>); empty: the platform's first.</summary>
    public string Rid
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>Steam on: the app id is checked and the Steam files must ship. Off: the exported project file has app ids 0.</summary>
    public bool Steam { get; set; }

    /// <summary>The player-facing executable name; empty: the project name without spaces.</summary>
    public string Exe
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>Ship the .NET runtime (default); false makes players install .NET 10 (servers that already have it).</summary>
    public bool SelfContained { get; set; } = true;

    /// <summary><c>-p:PublishReadyToRun</c>: faster start-up, larger output (default on).</summary>
    public bool ReadyToRun { get; set; } = true;

    /// <summary><c>-p:PublishSingleFile</c> with the natives self-extracted; <c>Assets/</c> and <c>project.mfproj</c> stay beside the exe.</summary>
    public bool SingleFile { get; set; }

    /// <summary><c>-p:PublishTrimmed</c>: experimental until the engine is trim-clean (M12.6).</summary>
    public bool Trim { get; set; }

    /// <summary>Ship the PDBs (default) or put them in a separate symbols zip.</summary>
    public ExportSymbols Symbols { get; set; }

    /// <summary>
    /// The frame profiler can record the build (default). Off sets the <c>AppContext</c> switch
    /// <see cref="MainframeEngine.Profiler.DisabledSwitch"/> in its runtimeconfig, so profiler scopes cost nothing.
    /// </summary>
    public bool Profiler { get; set; } = true;

    /// <summary>The macOS keys (null on other platforms).</summary>
    public MacOsExportOptions? MacOs { get; set; }

    IExportPlatformOptions IExportPlatformOptions.Clone() => Clone();

    /// <summary>A deep copy.</summary>
    public DesktopExportOptions Clone() => new()
    {
        Rid = Rid,
        Steam = Steam,
        Exe = Exe,
        SelfContained = SelfContained,
        ReadyToRun = ReadyToRun,
        SingleFile = SingleFile,
        Trim = Trim,
        Symbols = Symbols,
        Profiler = Profiler,
        MacOs = MacOs?.Clone(),
    };
}

/// <summary>The macOS keys of a desktop preset: the bundle's identity, signing and notarization.</summary>
public sealed class MacOsExportOptions
{
    /// <summary><c>CFBundleIdentifier</c>; empty: <c>com.mainframegames.&lt;exe lowercased&gt;</c>.</summary>
    public string BundleId
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary><c>NSHumanReadableCopyright</c>.</summary>
    public string Copyright
    {
        get;
        set => field = value ?? "";
    } = "";

    /// <summary>A Developer ID identity name (not a secret); empty: ad-hoc. <c>MF_SIGN_IDENTITY</c> overrides it.</summary>
    public string Sign
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>A <c>notarytool</c> keychain profile name; empty: no notarization. <c>MF_NOTARY_PROFILE</c> overrides it.</summary>
    public string NotaryProfile
    {
        get;
        set => field = value?.Trim() ?? "";
    } = "";

    /// <summary>All keys at their defaults (not written).</summary>
    public bool IsDefault => BundleId.Length == 0 && Copyright.Length == 0 && Sign.Length == 0 && NotaryProfile.Length == 0;

    /// <summary>A copy.</summary>
    public MacOsExportOptions Clone() => new() { BundleId = BundleId, Copyright = Copyright, Sign = Sign, NotaryProfile = NotaryProfile };
}

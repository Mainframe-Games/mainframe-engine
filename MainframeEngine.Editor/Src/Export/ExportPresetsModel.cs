using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>One option of a preset in the Export dialog: its key, group, label and tooltip, and the platforms it applies to.</summary>
public sealed record ExportOption(string Key, string Group, string Label, ExportOptionKind Kind, string Tooltip, ExportPlatform[]? Platforms = null)
{
    public bool AppliesTo(ExportPlatform platform) => Platforms is null || Platforms.Contains(platform);
}

/// <summary>How an <see cref="ExportOption"/> is edited.</summary>
public enum ExportOptionKind
{
    Text,
    Bool,
    Choice,
    File,
}

/// <summary>
/// The Export dialog's model: the project's <c>export</c> presets edited through a <see cref="ProjectSettingsModel"/> (its
/// own undo/redo history, saved with <c>project.mfproj</c>, as in Project Settings). Every edit returns null or the reason it
/// was refused.
/// </summary>
public sealed class ExportPresetsModel
{
    public ExportPresetsModel(ProjectSettings settings, string filePath) => Settings = new ProjectSettingsModel(settings, filePath);

    /// <summary>The whole project file being edited (the presets live in its <see cref="ProjectSettings.ExportPresets"/>).</summary>
    public ProjectSettingsModel Settings { get; }

    public IReadOnlyList<ExportPreset> Presets => Settings.Current.ExportPresets;

    public UndoRedo History => Settings.History;

    public bool IsDirty => Settings.IsDirty;

    /// <summary>The options in the order of the schema, grouped (General, Build, Steam, Icon, macOS; Content is the globs).</summary>
    public static readonly IReadOnlyList<ExportOption> Options =
    [
        new("name", "General", "Name", ExportOptionKind.Text, "Shown here; mf-export --preset selects presets by it."),
        new("rid", "General", "Target", ExportOptionKind.Choice, "The runtime identifier: the engine ships natives for these."),
        new("output", "General", "Output Folder", ExportOptionKind.Text, "Project-relative or absolute; not inside Assets/. Empty: export/<name>."),
        new("configuration", "General", "Configuration", ExportOptionKind.Choice, "Release for players; Debug is a development run."),
        new("demo", "General", "Demo Build", ExportOptionKind.Bool, "Builds with DEMO defined (#if DEMO) and the Steam demo app id, whatever isDemo says."),
        new("version", "General", "Version", ExportOptionKind.Text, "Names the archive and the macOS bundle version. Empty: the project's version."),
        new("archive", "General", "Archive", ExportOptionKind.Bool, "Zip (Windows, macOS) or .tar.gz (Linux); off leaves a folder (a Steam depot's content root)."),
        new("exe", "Build", "Executable Name", ExportOptionKind.Text, "The player-facing executable. Empty: the project name without spaces."),
        new("selfContained", "Build", "Self-Contained", ExportOptionKind.Bool, "Ship the .NET runtime; off makes players install .NET 10 (servers that have it)."),
        new("readyToRun", "Build", "ReadyToRun", ExportOptionKind.Bool, "Precompiled code: faster start-up, larger output."),
        new("singleFile", "Build", "Single File", ExportOptionKind.Bool, "One executable (natives extracted on first start); Assets/ stays beside it. Experimental.",
            [ExportPlatform.Windows, ExportPlatform.Linux]),
        new("trim", "Build", "Trim", ExportOptionKind.Bool, "Remove unused code. Experimental until the engine is trim-clean."),
        new("profiler", "Build", "Profiler", ExportOptionKind.Bool, "The frame profiler (F12, the editor's Profiler tab) can record this build; off compiles its scopes out."),
        new("symbols", "Build", "Symbols", ExportOptionKind.Choice, "ship: PDBs beside the assemblies (crash reports get lines); separate: a -symbols.zip for mf-symbolicate."),
        new("steam", "Steam", "Steam", ExportOptionKind.Bool, "Validates the app id and the Steam files; off ships app id 0 (Steam stays inert)."),
        new("icon", "Icon", "Icon", ExportOptionKind.File, "A PNG (256 px or more; 512 for macOS) for the exe and the bundle. Empty: Window › Icon."),
        new("bundleId", "macOS", "Bundle ID", ExportOptionKind.Text, "CFBundleIdentifier. Empty: com.mainframegames.<exe>.", [ExportPlatform.MacOS]),
        new("copyright", "macOS", "Copyright", ExportOptionKind.Text, "NSHumanReadableCopyright.", [ExportPlatform.MacOS]),
        new("sign", "macOS", "Signing Identity", ExportOptionKind.Text, "A Developer ID Application identity from the keychain; empty signs ad hoc.", [ExportPlatform.MacOS]),
        new("notaryProfile", "macOS", "Notary Profile", ExportOptionKind.Text, "An xcrun notarytool keychain profile; empty skips notarization.", [ExportPlatform.MacOS]),
    ];

    /// <summary>The choices of a <see cref="ExportOptionKind.Choice"/> option for a preset.</summary>
    public static IReadOnlyList<string> Choices(string key, ExportPreset preset) => key switch
    {
        "rid" => ExportTargets.RidsOf(preset.Platform),
        "configuration" => ["Release", "Debug"],
        "symbols" => ["ship", "separate"],
        _ => [],
    };

    /// <summary>An option's text for a preset (booleans as <c>true</c>/<c>false</c>).</summary>
    public static string Get(ExportPreset preset, string key)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var d = preset.Desktop;
        var m = d.MacOs ?? new MacOsExportOptions();
        return key switch
        {
            "name" => preset.Name,
            "rid" => ExportTargets.EffectiveRid(preset),
            "output" => preset.Output ?? "",
            "configuration" => preset.Configuration,
            "demo" => B(preset.Demo),
            "version" => preset.Version,
            "archive" => B(preset.Archive),
            "exe" => d.Exe,
            "selfContained" => B(d.SelfContained),
            "readyToRun" => B(d.ReadyToRun),
            "singleFile" => B(d.SingleFile),
            "trim" => B(d.Trim),
            "symbols" => d.Symbols == ExportSymbols.Separate ? "separate" : "ship",
            "profiler" => B(d.Profiler),
            "steam" => B(d.Steam),
            "icon" => preset.Icon,
            "bundleId" => m.BundleId,
            "copyright" => m.Copyright,
            "sign" => m.Sign,
            "notaryProfile" => m.NotaryProfile,
            _ => throw new ArgumentException($"Unknown export option '{key}'.", nameof(key)),
        };
    }

    private static string B(bool value) => value ? "true" : "false";

    /// <summary>A name for a new preset of <paramref name="platform"/> that no preset uses yet.</summary>
    public string NewName(ExportPlatform platform)
    {
        var name = platform switch
        {
            ExportPlatform.Windows => "Windows",
            ExportPlatform.MacOS => "macOS",
            _ => "Linux",
        };
        return Unique(name);
    }

    private string Unique(string name)
    {
        if (Presets.All(p => p.Name != name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {i}");
            if (Presets.All(p => p.Name != candidate))
                return candidate;
        }
    }

    /// <summary>Adds a preset for <paramref name="platform"/> (the host's RID when it is that platform's); null or the error.</summary>
    public string? Add(ExportPlatform platform, ExportHost host, string hostRid)
    {
        var name = NewName(platform);
        return Settings.Edit($"Add Preset {name}", s =>
        {
            var preset = ExportPreset.Create(name, platform);
            if (ExportTargets.PlatformOf(host) == platform)
                preset.Desktop.Rid = ExportTargets.HostRid(host, hostRid);
            s.ExportPresets.Add(preset);
        });
    }

    /// <summary>Copies the preset at <paramref name="index"/> after it, with a new name.</summary>
    public string? Duplicate(int index)
    {
        if ((uint)index >= (uint)Presets.Count)
            return "No preset to duplicate.";
        var copy = Presets[index].Clone();
        copy.Name = Unique(copy.Name + " copy");
        return Settings.Edit($"Duplicate {Presets[index].Name}", s => s.ExportPresets.Insert(index + 1, copy));
    }

    public string? Remove(int index)
    {
        if ((uint)index >= (uint)Presets.Count)
            return "No preset to delete.";
        return Settings.Edit($"Delete {Presets[index].Name}", s => s.ExportPresets.RemoveAt(index));
    }

    /// <summary>Sets option <paramref name="key"/> of the preset at <paramref name="index"/> from text.</summary>
    public string? Set(int index, string key, string text)
    {
        ArgumentNullException.ThrowIfNull(key);
        text ??= "";
        if ((uint)index >= (uint)Presets.Count)
            return "No preset selected.";
        if (key == "name")
        {
            var name = text.Trim();
            if (name.Length == 0)
                return "A preset needs a name.";
            if (Presets.Where((_, i) => i != index).Any(p => p.Name == name))
                return $"Another preset is named '{name}'.";
        }

        if (key == "output" && text.Trim().Replace('\\', '/').StartsWith(ContentPaths.FolderName + "/", StringComparison.Ordinal))
            return "The output folder must not be inside Assets/.";
        return Settings.Edit($"Set {Presets[index].Name} {key}", s => Apply(s.ExportPresets[index], key, text));
    }

    private static void Apply(ExportPreset p, string key, string text)
    {
        var d = p.Desktop;
        bool Bool() => text is "true" ? true : text is "false" ? false : throw new FormatException($"'{text}' is not true or false.");
        MacOsExportOptions Mac() => d.MacOs ??= new MacOsExportOptions();
        switch (key)
        {
            case "name": p.Name = text.Trim(); break;
            case "rid": d.Rid = text; break;
            case "output": p.Output = text.Trim(); break;
            case "configuration": p.Configuration = text; break;
            case "demo": p.Demo = Bool(); break;
            case "version": p.Version = text; break;
            case "archive": p.Archive = Bool(); break;
            case "exe": d.Exe = text; break;
            case "selfContained": d.SelfContained = Bool(); break;
            case "readyToRun": d.ReadyToRun = Bool(); break;
            case "singleFile": d.SingleFile = Bool(); break;
            case "trim": d.Trim = Bool(); break;
            case "profiler": d.Profiler = Bool(); break;
            case "symbols":
                d.Symbols = text switch
                {
                    "ship" => ExportSymbols.Ship,
                    "separate" => ExportSymbols.Separate,
                    _ => throw new FormatException($"'{text}' is not ship or separate."),
                };
                break;
            case "steam": d.Steam = Bool(); break;
            case "icon": p.Icon = text; break;
            case "bundleId": Mac().BundleId = text; break;
            case "copyright": Mac().Copyright = text; break;
            case "sign": Mac().Sign = text; break;
            case "notaryProfile": Mac().NotaryProfile = text; break;
            default: throw new ArgumentException($"Unknown export option '{key}'.");
        }
    }

    /// <summary>Adds a glob to the include (<paramref name="include"/>) or exclude list.</summary>
    public string? AddGlob(int index, bool include, string glob)
    {
        glob = (glob ?? "").Trim();
        if ((uint)index >= (uint)Presets.Count)
            return "No preset selected.";
        if (Glob.Problem(glob) is { } problem)
            return $"The glob '{glob}' {problem}.";
        var list = include ? Presets[index].Include : Presets[index].Exclude;
        if (list.Contains(glob))
            return null;
        return Settings.Edit($"Add {(include ? "Include" : "Exclude")} {glob}", s => (include ? s.ExportPresets[index].Include : s.ExportPresets[index].Exclude).Add(glob));
    }

    public string? RemoveGlob(int index, bool include, int globIndex)
    {
        if ((uint)index >= (uint)Presets.Count)
            return "No preset selected.";
        var list = include ? Presets[index].Include : Presets[index].Exclude;
        if ((uint)globIndex >= (uint)list.Count)
            return "No such glob.";
        return Settings.Edit($"Remove {list[globIndex]}", s => (include ? s.ExportPresets[index].Include : s.ExportPresets[index].Exclude).RemoveAt(globIndex));
    }

    public bool Save() => Settings.Save();
}

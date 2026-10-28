namespace MainframeEngine.Export;

/// <summary>
/// The checks an export runs before anything slow (docs/design/game-export.md#validation). Starts no process: the SDK check
/// comes from <see cref="ExportEnvironment.Sdk"/>, detected once per session. Thread-safe.
/// </summary>
public static class ExportValidator
{
    /// <summary>Validates <paramref name="preset"/> of the project in <paramref name="projectDirectory"/>.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <param name="project">Its settings (the preset need not be one of its presets).</param>
    /// <param name="preset">The preset.</param>
    /// <param name="environment">The machine.</param>
    /// <param name="database">The project's asset database (null: scanned here).</param>
    /// <param name="outputOverride">The CLI's <c>--output</c>.</param>
    /// <param name="appName">The legacy <c>--name</c>.</param>
    public static IReadOnlyList<ExportIssue> Validate(string projectDirectory, ProjectSettings project, ExportPreset preset,
        ExportEnvironment environment, AssetDatabase? database = null, string? outputOverride = null, string? appName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(environment);
        var issues = new List<ExportIssue>();
        void Error(string code, string message, string? fix = null) => issues.Add(new ExportIssue(ExportIssueSeverity.Error, code, message, fix));
        void Warn(string code, string message, string? fix = null) => issues.Add(new ExportIssue(ExportIssueSeverity.Warning, code, message, fix));

        var plan = ExportPlan.Create(projectDirectory, project, preset, environment, outputOverride, appName);
        var root = plan.ProjectDirectory;

        // The machine and the project.
        if (environment.DotnetPath is null || environment.Sdk is { IsSupported: false })
            Error("sdk-missing", environment.Sdk?.Error ?? $"No .NET SDK was found. Install the .NET 10 SDK from {DotnetSdk.DownloadUrl}.");
        if (plan.DesktopProject is null)
            Error("no-desktop-project", "The project has no desktop project (*.Desktop/*.Desktop.csproj) to publish.",
                "Create the project from the mfgame template, or add a desktop project next to the game library.");
        if (project.ExportPresets.Count(p => p.Name == preset.Name) > 1)
            Error("duplicate-name", $"Another preset is also named '{preset.Name}'.", "Rename one of them.");

        // The target.
        var platform = ExportTargets.PlatformOf(plan.Rid);
        if (!EngineNatives.Rids.Contains(plan.Rid, StringComparer.Ordinal) || platform != preset.Platform)
            Error("rid-unsupported", $"'{plan.Rid}' is not a {ExportPreset.PlatformKey(preset.Platform)} target the engine ships natives for.",
                $"Use one of {string.Join(", ", ExportTargets.RidsOf(preset.Platform))}.");
        if (preset.Platform == ExportPlatform.MacOS && environment.Host != ExportHost.MacOS)
            Error("host-unsupported", "macOS exports need a Mac: the app is signed with codesign, and Apple Silicon refuses unsigned code.",
                "Export this preset on a Mac (CI: a macOS runner).");
        var configuration = preset.Configuration;
        if (configuration is not ("Release" or "Debug"))
            Warn("configuration", $"Configuration '{configuration}' is neither Release nor Debug.");

        // Where it goes.
        var assets = Path.Combine(root, ContentPaths.FolderName);
        if (IsInside(plan.OutputDirectory, assets))
            Error("output-in-content", "The output folder is inside Assets/, so the next export would ship this one.", "Choose a folder outside Assets/ (default: export/).");
        foreach (var glob in preset.Include.Concat(preset.Exclude))
            if (Glob.Problem(glob) is { } problem)
                Error("glob", $"The content glob '{glob}' {problem}.");
        if (preset.Include.Count == 0)
            Warn("include-empty", "No include globs: none of the game's Assets/ files ship.", "Add Assets/**.");

        // Content: the main scene, autoload scenes and the bus layout must ship.
        database ??= ScanDatabase(root);
        var shipped = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (path, ships) in ContentFilter.GameFiles(root, preset.Include, preset.Exclude))
            shipped[path] = ships;
        if (string.IsNullOrWhiteSpace(project.MainScene))
        {
            Error("main-scene", "The project has no main scene.", "Set it in Project Settings › Application.");
        }
        else if (Resolve(project.MainScene, root, database) is not { } main)
        {
            Error("main-scene", $"The main scene '{project.MainScene}' is not in the project.", "Set it in Project Settings › Application.");
        }
        else if (shipped.TryGetValue(main, out var ships) && !ships)
        {
            Error("main-scene-excluded", $"The main scene {main} is excluded from the export.", "Change the include/exclude globs.");
        }

        foreach (var autoload in project.Autoloads)
            if (autoload is { Enabled: true, Scene: { } scene } && Resolve(scene, root, database) is { } path && shipped.TryGetValue(path, out var s) && !s)
                Error("main-scene-excluded", $"The autoload {autoload.Name}'s scene {path} is excluded from the export.", "Change the include/exclude globs.");
        if (project.Audio.BusLayout is { Length: > 0 } busLayout && Resolve(busLayout, root, database) is { } bus && shipped.TryGetValue(bus, out var b) && !b)
            Error("main-scene-excluded", $"The audio bus layout {bus} is excluded from the export.", "Change the include/exclude globs.");

        CheckExcludedContent(shipped, root, database, Warn);

        // Steam.
        var desktop = preset.Options as DesktopExportOptions;
        if (desktop is { Steam: true })
        {
            var appId = preset.Demo ? project.Steam.DemoAppId : project.Steam.AppId;
            if (appId == 0)
                Error("steam-app-id", $"Steam is on, but the project has no {(preset.Demo ? "demo app id" : "app id")}: Steam would never start.",
                    "Set it in Project Settings › Steamworks, or turn Steam off for this preset.");
            if (configuration != "Release")
                Warn("steam-debug", "A Debug export is a development run: it writes steam_appid.txt on players' machines and never relaunches through Steam.",
                    "Use the Release configuration for Steam builds.");
        }

        // macOS signing.
        if (preset.Platform == ExportPlatform.MacOS && plan.NotaryProfile.Length > 0 && plan.SignIdentity == "-")
            Error("notarize-needs-sign", "Notarization needs a Developer ID signature.", "Set sign (a \"Developer ID Application: …\" identity), or clear notaryProfile.");

        // Options.
        if (desktop is { SingleFile: true } && preset.Platform == ExportPlatform.MacOS)
            Error("single-file", "Single-file publishing does not work inside a macOS .app bundle.", "Turn single file off for this preset.");
        else if (desktop is { SingleFile: true })
            Warn("single-file", "Single file is experimental: the natives are extracted on first start, which slows it.");
        if (desktop is { Trim: true })
            Warn("trim", "Trimming is experimental until the engine is trim-clean (M12.6): reflection-based code may break.");
        if (!ExportTargets.IsSemanticVersion(plan.Version))
            Warn("version", $"The version '{plan.Version}' is not MAJOR.MINOR.PATCH[-suffix] (macOS rejects other versions).",
                "Set the project's version (Project Settings) or the preset's.");

        CheckIcon(plan, Warn);
        return issues;
    }

    /// <summary>Whether a list of issues has an error.</summary>
    public static bool HasErrors(IReadOnlyList<ExportIssue> issues) => issues.Any(i => i.IsError);

    private static void CheckIcon(ExportPlan plan, Action<string, string, string?> warn)
    {
        var needsPng = plan.Platform switch
        {
            ExportPlatform.Windows => !plan.ProjectHasWindowsIcon,
            ExportPlatform.MacOS => plan.ProjectMacIcns is null,
            _ => false,
        };
        if (!needsPng)
            return;
        if (plan.IconPng is null)
        {
            warn("icon", "No icon: the executable keeps the default one.", "Set window.icon in Project Settings, or the preset's icon.");
            return;
        }

        if (!File.Exists(plan.IconPng))
        {
            warn("icon", $"The icon '{plan.IconPng}' does not exist.", "Pick a PNG of at least 256 px.");
            return;
        }

        if (!TryReadPngSize(plan.IconPng, out var width, out var height))
        {
            warn("icon", $"The icon '{Path.GetFileName(plan.IconPng)}' is not a PNG.", "Pick a PNG of at least 256 px.");
            return;
        }

        if (Math.Min(width, height) < 256)
            warn("icon", $"The icon is {width}×{height} px; it should be at least 256 px (512 recommended for macOS).", null);
    }

    /// <summary>Reads a PNG's size from its header; false when the file is not a PNG.</summary>
    public static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[24];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return false;
        width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        return width > 0 && height > 0;
    }

    // Excluded .meta sidecars of kept files, and kept scenes/resources that reference excluded files.
    private static void CheckExcludedContent(Dictionary<string, bool> shipped, string root, AssetDatabase database, Action<string, string, string?> warn)
    {
        var excluded = shipped.Where(p => !p.Value).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count == 0)
            return;
        var metas = excluded.Where(p => p.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase)
                                        && shipped.TryGetValue(p[..^AssetDatabase.MetaExtension.Length], out var kept) && kept).Order(StringComparer.Ordinal).ToArray();
        if (metas.Length > 0)
            warn("meta-excluded", $"{metas.Length} .meta sidecar(s) of shipped files are excluded (e.g. {metas[0]}): their import settings and UIDs are lost.",
                "Exclude the asset with its .meta, or neither.");

        // Translation sources are never referenced: skip the scan when nothing else is excluded.
        if (!excluded.Any(p => !p.EndsWith(".po", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".pot", StringComparison.OrdinalIgnoreCase)))
            return;
        var broken = new List<string>();
        foreach (var (path, ships) in shipped)
        {
            if (!ships || !ReferenceScanner.IsScanned(path))
                continue;
            foreach (var reference in ReferenceScanner.ReferencesOf(Path.Combine(root, path), database))
                if (excluded.Contains(reference))
                    broken.Add($"{path} uses {reference}");
        }

        broken.Sort(StringComparer.Ordinal);
        if (broken.Count > 0)
            warn("broken-reference", $"{broken.Count} reference(s) from shipped scenes or resources point at excluded files: {string.Join(", ", broken.Take(3))}" +
                                     (broken.Count > 3 ? ", …" : "") + ".", "Include those files, or remove the references.");
    }

    // A UID or an Assets/ path → the project path of an existing file, else null.
    private static string? Resolve(string reference, string root, AssetDatabase database)
    {
        if (AssetUid.IsUid(reference))
            return database.GetPath(reference);
        var path = ContentPaths.UpgradeLegacyPath(reference).Replace('\\', '/');
        return File.Exists(Path.Combine(root, path)) ? path : null;
    }

    private static AssetDatabase ScanDatabase(string root)
    {
        var database = new AssetDatabase(root);
        if (Directory.Exists(Path.Combine(root, ContentPaths.FolderName)))
            database.Scan();
        return database;
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}

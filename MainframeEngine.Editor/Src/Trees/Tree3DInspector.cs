using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.Trees;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="Tree3D"/> inspector (G8b.3, ADR 0271). Above the generated rows: the Preset dropdown (every
/// <see cref="TreePresets"/> name and Custom) with an Ez Tree <c>.json</c> import, the Seed field with a dice button,
/// the Style toggle (Realistic / LowPoly), the wind preview, the LOD table (triangles, vertices and distances per level;
/// an eye per row forces that level in the view, <see cref="Tree3D.ForceLod"/>) and Bake with the bake's status. Preset,
/// import, seed and style are undoable edits; the eye and wind toggles are view state and are not saved. The options'
/// own rows (levels, leaves, bark) stay the generated ones: <see cref="TreeOptionsInspector"/> names the levels and
/// <see cref="TreeLevelInspector"/> hides the fields that do not apply to a level.
/// </summary>
[CustomInspector(typeof(Tree3D))]
public sealed class Tree3DInspector : ICustomInspector, IWorkspaceInspector
{
    public const string PresetInput = "tree-preset";
    public const string SeedInput = "tree-seed";
    public const string ImportAction = "tree-import";
    public const string DiceAction = "tree-dice";
    public const string RealisticAction = "tree-style:realistic";
    public const string LowPolyAction = "tree-style:lowpoly";
    public const string WindAction = "tree-wind";
    public const string LodAction = "tree-lod:";
    public const string BakeAction = "tree-bake";

    /// <summary>The bake's folder in the project.</summary>
    public const string BakeFolder = "Assets/Trees";

    private static readonly Dictionary<string, string> PresetHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock PresetGate = new();

    public EditorWorkspace? Workspace { get; set; }

    /// <summary>Seeds for the dice; tests replace it for a known sequence.</summary>
    internal Random Dice { get; set; } = new();

    // ── Header ───────────────────────────────────────────────────────────────────────────────────────────────────

    public string? GetHeaderRml(object target)
    {
        var tree = (Tree3D)target;
        var options = tree.ResolveOptions();
        var rml = new StringBuilder(4096);
        rml.Append("<div class=\"tree-insp\">");

        // Preset + import.
        var preset = PresetOf(tree);
        rml.Append("<div class=\"tree-row\"><span class=\"tree-label\" data-tooltip=\"Preset — the Ez Tree preset or engine species the options were copied from\">")
            .Append("<span class=\"icon icon-sm icon-trees icon-muted\"></span><span>Preset</span></span>")
            .Append("<select class=\"tree-preset\" data-header-input=\"").Append(PresetInput).Append("\">");
        rml.Append("<option value=\"\"").Append(preset is null ? " selected=\"\"" : "").Append('>')
            .Append(RmlText.Escape(preset is null && tree.Preset.Length > 0 ? $"Custom (from {tree.Preset})" : "Custom")).Append("</option>");
        foreach (var name in TreePresets.Names)
            rml.Append("<option value=\"").Append(RmlText.Escape(name)).Append('"').Append(name == preset ? " selected=\"\"" : "").Append('>')
                .Append(RmlText.Escape(name)).Append("</option>");
        rml.Append("</select>");
        AppendButton(rml, ImportAction, "file-import", "Import Ez Tree JSON — replace the options with a preset or a file saved by Ez Tree's web app");
        rml.Append("</div>");

        // Seed + dice.
        var seed = options is null ? tree.Seed : tree.ResolveSeed(options);
        rml.Append("<div class=\"tree-row\"><span class=\"tree-label\" data-tooltip=\"Seed — the same options with another seed grow another tree of the same kind (Enter applies; −1: the options' own seed)\">")
            .Append("<span class=\"icon icon-sm icon-hash icon-muted\"></span><span>Seed</span></span>")
            .Append("<input type=\"text\" class=\"text tree-seed\" data-header-input=\"").Append(SeedInput).Append("\" value=\"")
            .Append(seed.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
        AppendButton(rml, DiceAction, "dice-5", "New Seed — a random seed (one undo step)");
        rml.Append("</div>");

        // Style + wind.
        var wind = tree.GetWorld3D()?.PreviewWind == true;
        rml.Append("<div class=\"tree-row\"><span class=\"tree-label\" data-tooltip=\"Style — how the skeleton is meshed\">")
            .Append("<span class=\"icon icon-sm icon-brush icon-muted\"></span><span>Style</span></span>");
        AppendButton(rml, RealisticAction, "photo", "Realistic — Ez Tree's meshes: round bark, leaf cards, PBR textures and wind", tree.Style == TreeStyle.Realistic);
        AppendButton(rml, LowPolyAction, "vector-triangle", "Low Poly — faceted bark and leaf blobs in palette colours", tree.Style == TreeStyle.LowPoly);
        rml.Append("<span class=\"tree-gap\"></span>");
        AppendButton(rml, WindAction, "wind", wind
            ? "Wind Preview — on: a steady breeze sways the foliage in this view (click to hold still)"
            : "Wind Preview — off: sway the foliage in this view with a steady breeze (only while the world has no wind of its own)", wind);
        rml.Append("</div>");

        AppendLodTable(rml, tree);

        // Bake + status.
        rml.Append("<div class=\"probe-bake\">");
        AppendButton(rml, BakeAction, "package", $"Bake meshes to .mfres — the levels of detail and trunk into {BakePath(tree)}; the tree then loads them instead of generating");
        rml.Append("<span class=\"icon icon-sm ").Append(tree.GetBakeStatus() switch
        {
            TreeBakeStatus.UpToDate => "icon-circle-check icon-ui",
            TreeBakeStatus.Stale => "icon-alert-triangle icon-warn",
            _ => "icon-circle-dot icon-muted",
        }).Append(" tree-status-icon\"></span><span class=\"probe-bake-status\">").Append(RmlText.Escape(Status(tree))).Append("</span></div>");
        return rml.Append("</div>").ToString();
    }

    private static void AppendLodTable(StringBuilder rml, Tree3D tree)
    {
        rml.Append("<div class=\"tree-lods\">");
        var mesh = tree.Mesh;
        var count = mesh?.Lods.Length ?? 0;
        if (count == 0)
            rml.Append("<div class=\"tree-lod muted\">No levels: choose a preset or give the tree options.</div>");
        else
            rml.Append("<div class=\"tree-lod tree-lod-head\"><span class=\"tree-lod-eye\"></span><span class=\"tree-lod-name\">Level</span>")
                .Append("<span class=\"tree-lod-num\">Triangles</span><span class=\"tree-lod-num\">Vertices</span><span class=\"tree-lod-range\">Distance</span></div>");
        for (var i = 0; i < count; i++)
        {
            var (triangles, vertices) = Counts(mesh!.Lods[i]);
            var forced = tree.ForceLod == i;
            rml.Append("<div class=\"tree-lod").Append(forced ? " forced" : "").Append("\">");
            AppendButton(rml, LodAction + i.ToString(CultureInfo.InvariantCulture), forced ? "eye" : "eye-off", forced
                ? $"Level {i} forced — the view draws only this level at any distance (click: by distance again)"
                : $"Force Level {i} — draw only this level in the view, at any distance", forced);
            rml.Append("<span class=\"tree-lod-name\">LOD ").Append(i).Append("</span><span class=\"tree-lod-num mono\">")
                .Append(triangles.ToString("N0", CultureInfo.InvariantCulture)).Append("</span><span class=\"tree-lod-num mono\">")
                .Append(vertices.ToString("N0", CultureInfo.InvariantCulture)).Append("</span><span class=\"tree-lod-range mono\">")
                .Append(RmlText.Escape(RangeText(i, count, tree))).Append("</span></div>");
        }

        if (count > 0)
            rml.Append("<div class=\"tree-lod muted\" data-tooltip=\"Impostor — one camera-facing quad of 8×8 baked views; TreeScatter draws it from its ImpostorDistance (Tree3D does not yet)\">")
                .Append("<span class=\"icon icon-sm icon-photo icon-muted tree-lod-icon\"></span><span class=\"tree-lod-name\">Impostor</span>")
                .Append("<span class=\"tree-lod-num mono\">2</span><span class=\"tree-lod-num mono\">4</span>")
                .Append("<span class=\"tree-lod-range\">scatter</span></div>");
        rml.Append("</div>");
    }

    /// <summary>Triangles and vertices of a level, all surfaces.</summary>
    internal static (int Triangles, int Vertices) Counts(Mesh lod)
    {
        ArgumentNullException.ThrowIfNull(lod);
        var indices = 0;
        for (var s = 0; s < lod.SurfaceCount; s++)
            indices += lod.GetSurface(s).IndexCount;
        return (indices / 3, lod.VertexCount);
    }

    private static string RangeText(int lod, int count, Tree3D tree)
    {
        var (begin, end) = TreeMesh.LodRange(lod, count, tree.Lod1Distance, tree.Lod2Distance, tree.MaxDistance);
        if (begin == float.MaxValue)
            return "never";
        return end > 0f
            ? string.Create(CultureInfo.InvariantCulture, $"{begin:0.#}–{end:0.#} m")
            : string.Create(CultureInfo.InvariantCulture, $"{begin:0.#} m+");
    }

    /// <summary>The status line: the bake's state and file, and the last generation's time.</summary>
    internal static string Status(Tree3D tree)
    {
        var generated = string.Create(CultureInfo.InvariantCulture, $"{tree.LastRegenerateMilliseconds:0.0} ms");
        var file = tree.BakedMesh?.ResourcePath is { } path ? Path.GetFileName(path) : "in the scene";
        return tree.GetBakeStatus() switch
        {
            TreeBakeStatus.UpToDate => $"Baked, up to date: {file}",
            TreeBakeStatus.Stale => $"Stale: changed since the bake ({file}); the view shows the live tree ({generated}). Bake again.",
            _ => tree.Mesh is null ? "Not baked" : $"Not baked: generated in {generated} at load",
        };
    }

    /// <summary>
    /// The preset the tree's options are: its <see cref="Tree3D.Preset"/> when <see cref="Tree3D.Options"/> is null or still
    /// equal to that preset; null (Custom) once edited, imported or without a preset.
    /// </summary>
    internal static string? PresetOf(Tree3D tree)
    {
        if (tree.Preset.Length == 0 || !TreePresets.Names.Contains(tree.Preset, StringComparer.OrdinalIgnoreCase))
            return null;
        var name = TreePresets.Names.First(n => string.Equals(n, tree.Preset, StringComparison.OrdinalIgnoreCase));
        if (tree.Options is not { } options)
            return name;
        return string.Equals(TreeMesh.ComputeBakeHash(options, 0, TreeStyle.Realistic), PresetHash(name), StringComparison.Ordinal) ? name : null;
    }

    private static string PresetHash(string name)
    {
        lock (PresetGate)
        {
            if (!PresetHashes.TryGetValue(name, out var hash))
            {
                hash = TreeMesh.ComputeBakeHash(TreePresets.Load(name), 0, TreeStyle.Realistic);
                PresetHashes.Add(name, hash);
            }

            return hash;
        }
    }

    /// <summary>
    /// Where Bake writes (project path): over the tree's bake file when it has one, else
    /// <c>Assets/Trees/&lt;preset&gt;_&lt;seed&gt;_&lt;style&gt;.mfres</c> (the preset the options came from, edited or not; the
    /// node's name without one).
    /// </summary>
    internal static string BakePath(Tree3D tree)
    {
        if (tree.BakedMesh?.ResourcePath is { Length: > 0 } existing)
            return AssetDatabase.Current.ToProjectPath(existing);
        var options = tree.ResolveOptions();
        var seed = options is null ? Math.Max(tree.Seed, 0) : tree.ResolveSeed(options);
        var preset = TreePresets.Names.FirstOrDefault(n => string.Equals(n, tree.Preset, StringComparison.OrdinalIgnoreCase));
        var name = preset is not null ? TreePresets.FileName(preset) : FileStem(tree.Name);
        var style = tree.Style == TreeStyle.LowPoly ? "lowpoly" : "realistic";
        return string.Create(CultureInfo.InvariantCulture, $"{BakeFolder}/{name}_{seed}_{style}.mfres");
    }

    // "Old Oak (2)" → "old_oak_2".
    private static string FileStem(string name)
    {
        var stem = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                stem.Append(char.ToLowerInvariant(c));
            else if (stem.Length > 0 && stem[^1] != '_')
                stem.Append('_');
        }

        var text = stem.ToString().Trim('_');
        return text.Length > 0 ? text : "tree";
    }

    private static void AppendButton(StringBuilder rml, string action, string icon, string tooltip, bool active = false) =>
        rml.Append("<button class=\"tool-button").Append(active ? " active" : "").Append("\" data-action=\"").Append(action)
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-sm icon-").Append(icon)
            .Append("\"></span></button>");

    // The header edits these.
    public bool ShowProperty(object target, ExportPropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Name is not (nameof(Tree3D.Preset) or nameof(Tree3D.Seed) or nameof(Tree3D.Style));
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var tree = (Tree3D)target;
        if (action.StartsWith(PresetInput + "=", StringComparison.Ordinal))
            ApplyPreset(tree, action[(PresetInput.Length + 1)..], context);
        else if (action.StartsWith(SeedInput + "=", StringComparison.Ordinal))
        {
            if (int.TryParse(action[(SeedInput.Length + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) && seed >= -1)
                SetSeed(tree, seed, context);
        }
        else if (action == DiceAction)
            SetSeed(tree, Dice.Next(0, 65536), context);
        else if (action == RealisticAction)
            Set(tree, nameof(Tree3D.Style), TreeStyle.Realistic, context);
        else if (action == LowPolyAction)
            Set(tree, nameof(Tree3D.Style), TreeStyle.LowPoly, context);
        else if (action == WindAction)
        {
            if (tree.GetWorld3D() is { } world)
                world.PreviewWind = !world.PreviewWind;
        }
        else if (action.StartsWith(LodAction, StringComparison.Ordinal))
        {
            if (int.TryParse(action[LodAction.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lod))
                tree.ForceLod = tree.ForceLod == lod ? -1 : lod;
        }
        else if (action == BakeAction)
            Bake(tree, context);
        else if (action == ImportAction)
            ShowImport(tree, context);
        else
            return;

        Refresh(tree);
    }

    /// <summary>After any committed change (rows, undo, redo): a changed tree regenerates now so the LOD table and status are current.</summary>
    public void OnPropertyChanged(object target, ExportPropertyInfo? property, IInspectorContext context) => Refresh((Tree3D)target);

    private void Refresh(Tree3D tree)
    {
        if (tree.IsInsideTree && tree.IsNodeReady && tree.NeedsRegenerate)
            tree.Regenerate();
        Workspace?.Inspector.Rebuild();
    }

    /// <summary>
    /// Copies preset <paramref name="name"/> into the tree's inline options and names it, as one undo step; "" (Custom)
    /// keeps the options and drops the name (an inline copy first when the tree only named a preset).
    /// </summary>
    internal static void ApplyPreset(Tree3D tree, string name, IInspectorContext context)
    {
        if (name.Length == 0)
        {
            if (tree.Preset.Length == 0)
                return;
            var copy = tree.Options ?? tree.ResolveOptions()?.Clone();
            context.History.Commit(new CompositeAction("Tree Preset Custom", Action(tree, nameof(Tree3D.Options), copy), Action(tree, nameof(Tree3D.Preset), "")));
            return;
        }

        if (!TreePresets.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Log.Warning($"[Editor] '{name}' is not a tree preset.");
            return;
        }

        var options = TreePresets.Load(name);
        context.History.Commit(new CompositeAction($"Tree Preset {name}", Action(tree, nameof(Tree3D.Options), options), Action(tree, nameof(Tree3D.Preset), options.ResourceName)));
    }

    /// <summary>Replaces the tree's options with Ez Tree <paramref name="json"/> (Custom), as one undo step; false with a message when it does not read.</summary>
    internal static bool ImportEzTree(Tree3D tree, string json, string name, IInspectorContext context, out string? error)
    {
        TreeOptions options;
        try
        {
            options = EzTreeJson.Read(json, name);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            error = e.Message;
            return false;
        }

        error = null;
        context.History.Commit(new CompositeAction($"Import Ez Tree {name}", Action(tree, nameof(Tree3D.Options), options), Action(tree, nameof(Tree3D.Preset), "")));
        return true;
    }

    private void ShowImport(Tree3D tree, IInspectorContext context)
    {
        if (Workspace is not { } workspace)
            return;
        var start = workspace.Session.ProjectRoot is { } root && Directory.Exists(root)
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, start, ["*.json"]);
        workspace.FilePicker.Show(model, "Import Ez Tree JSON", "Import", path =>
        {
            string json;
            try
            {
                json = File.ReadAllText(path); // disk-io: a file the user picked, outside the content
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                workspace.Message.Show(new MessageRequest { Title = "Import Ez Tree JSON", Message = e.Message });
                return;
            }

            if (!ImportEzTree(tree, json, Path.GetFileNameWithoutExtension(path), context, out var error))
                workspace.Message.Show(new MessageRequest { Title = "Import Ez Tree JSON", Message = $"{Path.GetFileName(path)} is not Ez Tree JSON: {error}" });
            Refresh(tree);
        });
    }

    private static void SetSeed(Tree3D tree, int seed, IInspectorContext context) => Set(tree, nameof(Tree3D.Seed), seed, context);

    private static void Set(Tree3D tree, string property, object value, IInspectorContext context)
    {
        var info = Property(property);
        if (Equals(info.GetValue(tree), value))
            return;
        context.SetProperty(tree, info, value);
    }

    /// <summary>
    /// Bakes the tree into <see cref="BakePath"/> (saved now; the file stays on undo) and sets <see cref="Tree3D.BakedMesh"/>
    /// as one undo step. Returns the project path, or null when it could not be written.
    /// </summary>
    internal static string? Bake(Tree3D tree, IInspectorContext context)
    {
        if (tree.ResolveOptions() is null || (tree.Options is null && tree.Preset.Length == 0))
        {
            Log.Warning($"[Editor] Tree '{tree.Name}' has no options or preset to bake.");
            return null;
        }

        var path = BakePath(tree);
        var previous = tree.BakedMesh;
        TreeMesh mesh;
        try
        {
            mesh = tree.GenerateBake();
            Directory.CreateDirectory(Path.GetDirectoryName(AssetDatabase.Current.ToAbsolutePath(path))!);
            ResourceSaver.Save(mesh, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error($"[Editor] Could not bake '{tree.Name}' to {path}: {e.Message}");
            return null;
        }

        Log.Info($"[Editor] Baked '{tree.Name}' to {path}.");
        context.History.Commit(new CompositeAction($"Bake {tree.Name}", new SetPropertyAction(tree, Property(nameof(Tree3D.BakedMesh)), previous, mesh)));
        return path;
    }

    private static SetPropertyAction Action(Tree3D tree, string property, object? value)
    {
        var info = Property(property);
        return new SetPropertyAction(tree, info, info.GetValue(tree), value);
    }

    private static ExportPropertyInfo Property(string name) =>
        TypeRegistry.GetRequired(typeof(Tree3D)).FindProperty(name) ?? throw new InvalidOperationException($"Tree3D.{name} is not exported.");
}

/// <summary>
/// Names the levels of a <see cref="TreeOptions"/> in the inspector ("Level 0 · trunk", "Level 3 · unused") and shows
/// their key values beside the title ("6 children · length 37.24", "4 children · 54° · length 9.6").
/// </summary>
[CustomInspector(typeof(TreeOptions))]
public sealed class TreeOptionsInspector : ICustomInspector
{
    public string? ElementLabel(object target, ExportPropertyInfo property, int index) =>
        IsLevel(target, property, index, out var options) ? LevelLabel(options, index) : null;

    public string? ElementSummary(object target, ExportPropertyInfo property, int index) =>
        IsLevel(target, property, index, out var options) ? LevelSummary(options, index) : null;

    private static bool IsLevel(object target, ExportPropertyInfo property, int index, out TreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(property);
        options = (target as TreeOptions)!;
        return property.Name == nameof(TreeOptions.Level) && options is not null && (uint)index < (uint)options.Level.Length;
    }

    /// <summary>"Level 0 · trunk", "Level 2"; levels past <see cref="TreeOptions.Levels"/> are "unused".</summary>
    internal static string LevelLabel(TreeOptions options, int index)
    {
        ArgumentNullException.ThrowIfNull(options);
        var label = string.Create(CultureInfo.InvariantCulture, $"Level {index}");
        return index == 0 ? label + " · trunk" : index > options.Levels ? label + " · unused" : label;
    }

    /// <summary>"6 children · length 37.24" (the trunk), "4 children · 54° · length 9.6"; empty for an unused level.</summary>
    internal static string LevelSummary(TreeOptions options, int index)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (index > options.Levels || options.Level[index] is not { } level)
            return string.Empty;
        var text = new StringBuilder(48);
        if (index < TreeLevelInspector.LastLevel && index < options.Levels)
            text.Append(CultureInfo.InvariantCulture, $"{level.Children} children · ");
        if (index > 0)
            text.Append(CultureInfo.InvariantCulture, $"{level.Angle:0.#}° · ");
        return text.Append(CultureInfo.InvariantCulture, $"length {level.Length:0.##}").ToString();
    }
}

/// <summary>Hides the <see cref="TreeLevel"/> fields that do not apply to its level: Angle and Start of the trunk, Children of level 3.</summary>
[CustomInspector(typeof(TreeLevel))]
public sealed class TreeLevelInspector : ICustomInspector
{
    /// <summary>The last branch level (Ez Tree's four levels, 0–3): its branches have no children.</summary>
    public const int LastLevel = 3;

    public bool ShowProperty(object target, ExportPropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var index = ((TreeLevel)target).LevelIndex;
        return property.Name switch
        {
            nameof(TreeLevel.Angle) or nameof(TreeLevel.Start) => index != 0,
            nameof(TreeLevel.Children) => index != LastLevel,
            _ => true,
        };
    }
}

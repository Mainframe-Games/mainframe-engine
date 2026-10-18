using System.Globalization;
using MainframeEngine.Editor.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

// The wavetable synth in the plugin window (until its own window): its presets and each oscillator's wavetable.
public sealed partial class PluginWindow
{
    private (string? A, string? B) _shownTables;
    private string? _shownPreset;

    private void TickSynth(RmlDocument document, InstrumentDescriptor synth)
    {
        var a = TableLabel(synth.Patch, "a");
        var b = TableLabel(synth.Patch, "b");
        if (a != _shownTables.A || b != _shownTables.B)
        {
            _shownTables = (a, b);
            document.GetElementById("pw-table-a").SetInnerRml(RmlText.Escape("A: " + a));
            document.GetElementById("pw-table-b").SetInnerRml(RmlText.Escape("B: " + b));
        }

        if (!string.Equals(synth.Preset, _shownPreset, StringComparison.Ordinal))
            _built = false; // the title names the preset: build again
    }

    private static SynthTableRef Table(SynthPatch? patch, string oscillator) =>
        patch?.Tables is { } tables && tables.TryGetValue(oscillator, out var t) ? t : new SynthTableRef();

    private static string TableLabel(SynthPatch? patch, string oscillator)
    {
        var source = Table(patch, oscillator).Source;
        if (source.StartsWith(FactoryWavetables.Prefix, StringComparison.Ordinal))
            return FactoryWavetables.DisplayName(source);
        var path = AssetUid.IsUid(source) ? AssetDatabase.Current.GetPath(source) : source;
        return path is null ? source : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>A preset id's name for the title ("Warm Pad"), or null.</summary>
    private static string? PresetName(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (id.StartsWith(SynthPresets.FactoryPrefix, StringComparison.Ordinal))
        {
            var slash = id.LastIndexOf('/');
            return slash < 0 ? id : id[(slash + 1)..];
        }

        var path = AssetUid.IsUid(id) ? AssetDatabase.Current.GetPath(id) : id;
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void SynthPresetMenu(SongTab tab)
    {
        if (Track is not { } track || Instrument is not { } synth)
            return;
        var presets = SynthPresets.All(Workspace.Session.ProjectRoot);
        var editable = !tab.Document.IsReadOnly;
        var items = new List<MenuItem> { new("Init", "init", Enabled: editable, Icon: "refresh") };
        foreach (var group in presets.GroupBy(static p => (p.Path is null ? "" : "Project · ") + p.Category))
        {
            items.Add(MenuItem.Header(group.Key));
            foreach (var p in group)
                items.Add(new MenuItem(p.Name, "preset:" + p.Id, Enabled: editable, Css: p.Id == synth.Preset ? "current" : null, Icon: "wave-square"));
        }

        items.Add(MenuItem.Separator);
        items.Add(new MenuItem("Save Preset…", "save", Enabled: Workspace.Session.ProjectRoot is not null, Icon: "device-floppy"));
        var shown = synth;
        Workspace.Popup.Show(items, _position.X + _width - 220, _position.Y + TitleHeight, command =>
        {
            if (command is null || !ReferenceEquals(Instrument, shown))
                return;
            if (command == "init")
                Edit(tab, "Could not reset the synth", () => tab.Document.SetSynthPatch(track, SynthPatch.Init(), "Init Patch"));
            else if (command == "save")
                SavePreset(tab, track, shown);
            else if (command.StartsWith("preset:", StringComparison.Ordinal) && presets.Find(p => p.Id == command[7..]) is { } entry)
                LoadPreset(tab, track, entry);
        });
    }

    /// <summary>Loads <paramref name="entry"/> on <paramref name="track"/>'s synth: one undo step ("Preset: Warm Pad").</summary>
    internal static void LoadPreset(SongTab tab, SongTrack track, SynthPresetEntry entry)
    {
        if (SynthPresets.Patch(entry) is not { } patch)
        {
            Log.Warning($"[Music] The preset {entry.Name} could not be read.");
            return;
        }

        Edit(tab, "Could not load the preset", () => tab.Document.SetSynthPatch(track, patch, "Preset: " + entry.Name, entry.Id));
    }

    // Next / previous preset in the current one's category (wrapping); with none, the first factory preset.
    internal void StepPreset(SongTab tab, int direction)
    {
        if (Track is not { } track || Instrument is not { } synth || tab.Document.IsReadOnly)
            return;
        var presets = SynthPresets.All(Workspace.Session.ProjectRoot);
        if (presets.Count == 0)
            return;
        var current = presets.FindIndex(p => p.Id == synth.Preset);
        if (current < 0)
        {
            LoadPreset(tab, track, presets[0]);
            return;
        }

        var category = presets.Where(p => p.Category == presets[current].Category && (p.Path is null) == (presets[current].Path is null)).ToList();
        var at = category.FindIndex(p => p.Id == synth.Preset);
        LoadPreset(tab, track, category[(at + direction + category.Count) % category.Count]);
    }

    private void SavePreset(SongTab tab, SongTrack track, InstrumentDescriptor synth)
    {
        if (Workspace.Session.ProjectRoot is not { } root)
            return;
        Workspace.Message.Show(new MessageRequest
        {
            Title = "Save Preset",
            Message = $"A name for the preset (saved in Content/{SynthPresets.Folder}/; \"Category / Name\" files it under a category):",
            Input = PresetName(synth.Preset) ?? track.Name,
            Buttons = ["Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Icon = "device-floppy",
            Callback = (button, text) =>
            {
                if (button != 0 || string.IsNullOrWhiteSpace(text) || synth.Patch is null)
                    return;
                var slash = text.IndexOf('/', StringComparison.Ordinal);
                var category = slash > 0 ? text[..slash].Trim() : "Project";
                var name = (slash > 0 ? text[(slash + 1)..] : text).Trim();
                var file = string.Concat(name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var path = Path.Combine(root, AssetDatabase.ContentFolder, SynthPresets.Folder, file + ".mres");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var uid = SynthPresets.Save(synth.Patch, path, name, category);
                    Log.Info($"[Music] Saved the preset {name} to {AssetDatabase.Current.ToProjectPath(path)}.");
                    if (ReferenceEquals(track.Instrument, synth) && !tab.Document.IsReadOnly)
                        tab.Document.SetSynthPatch(track, synth.Patch.Clone(), "Save Preset", uid);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Workspace.Commands.ReportError("Could not save the preset", e);
                }
            },
        });
    }

    // ── Wavetables ───────────────────────────────────────────────────────────────────────────────────────────────

    private void TableMenu(SongTab tab, string oscillator)
    {
        if (Track is not { } track || Instrument is not { } synth)
            return;
        var current = Table(synth.Patch, oscillator);
        var editable = !tab.Document.IsReadOnly;
        var items = new List<MenuItem>();
        foreach (var group in FactoryWavetables.Sources.GroupBy(static s => s[FactoryWavetables.Prefix.Length..].Split('/')[0]))
        {
            items.Add(MenuItem.Header(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(group.Key)));
            foreach (var source in group)
                items.Add(new MenuItem(FactoryWavetables.DisplayName(source), "table:" + source, Enabled: editable,
                    Css: source == current.Source ? "current" : null, Icon: "wave-sine"));
        }

        var project = ProjectTables();
        if (project.Count > 0)
        {
            items.Add(MenuItem.Header("Project"));
            foreach (var (source, name) in project)
                items.Add(new MenuItem(name, "table:" + source, Enabled: editable, Css: source == current.Source ? "current" : null, Icon: "file-music"));
        }

        items.Add(MenuItem.Separator);
        items.Add(new MenuItem("Import Wavetable…", "import", Enabled: editable && Workspace.Session.ProjectRoot is not null, Icon: "file-plus"));
        if (!current.Source.StartsWith(FactoryWavetables.Prefix, StringComparison.Ordinal))
        {
            items.Add(MenuItem.Header("Frame size"));
            foreach (var size in WavetableWav.FrameSizes)
            {
                var label = size switch { 0 => "Automatic", -1 => "Single cycle", _ => size.ToString(CultureInfo.InvariantCulture) };
                items.Add(new MenuItem(label, "frames:" + size.ToString(CultureInfo.InvariantCulture), Enabled: editable,
                    Css: size == current.FrameSize ? "current" : null));
            }
        }

        var x = _position.X + 220;
        Workspace.Popup.Show(items, x, _position.Y + TitleHeight, command =>
        {
            if (command is null || !ReferenceEquals(Instrument, synth))
                return;
            if (command == "import")
            {
                ImportTable(tab, track, oscillator);
                return;
            }

            var colon = command.IndexOf(':', StringComparison.Ordinal);
            var argument = command[(colon + 1)..];
            if (command.StartsWith("table:", StringComparison.Ordinal))
                Edit(tab, "Could not set the wavetable", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = argument }));
            else if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                Edit(tab, "Could not set the frame size", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = current.Source, FrameSize = size }));
        });
    }

    // The project's .wav files in Content/Wavetables/ (by UID when they have one).
    private List<(string Source, string Name)> ProjectTables()
    {
        var result = new List<(string, string)>();
        if (Workspace.Session.ProjectRoot is not { } root)
            return result;
        var folder = Path.Combine(root, AssetDatabase.ContentFolder, "Wavetables");
        if (!Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = AssetDatabase.Current.ToProjectPath(file);
            result.Add((AssetDatabase.Current.GetUid(path) ?? path, Path.GetFileNameWithoutExtension(file)));
        }

        return result;
    }

    private void ImportTable(SongTab tab, SongTrack track, string oscillator)
    {
        if (Workspace.Session.ProjectRoot is not { } root)
            return;
        var model = new FilePickerModel(FilePickerMode.Open, root, ["*.wav"]);
        Workspace.FilePicker.Show(model, "Import Wavetable", "Import", path => ImportTable(tab, track, oscillator, path));
    }

    /// <summary>Imports <paramref name="file"/> (copied into the project when outside it) as <paramref name="oscillator"/>'s table.</summary>
    internal void ImportTable(SongTab tab, SongTrack track, string oscillator, string file)
    {
        try
        {
            var uid = WavetableWav.Import(file, AssetDatabase.Current);
            Edit(tab, "Could not set the wavetable", () => tab.Document.SetSynthTable(track, oscillator, new SynthTableRef { Source = uid }));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Workspace.Message.Show(new MessageRequest { Title = "Import Wavetable", Message = $"{Path.GetFileName(file)} is not a wavetable: {e.Message}", Buttons = ["OK"] });
        }
    }
}

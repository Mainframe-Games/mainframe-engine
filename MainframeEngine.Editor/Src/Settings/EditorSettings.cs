using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's own preferences (Editor › Editor Settings), per user in <c>~/.mainframe/editor_settings.json</c>: the
/// theme accent colour, the autosave interval, the external code editor command (opens <c>.cs</c> files and
/// click-to-source lines), whether code reloads automatically after a build, whether the editor checks for updates and
/// the 3D view's resolution.
/// </summary>
public sealed class EditorSettings
{
    /// <summary>The theme's built-in accent (blue).</summary>
    public const string DefaultAccent = "#3b82f6";

    /// <summary>Placeholders of <see cref="CodeEditorCommand"/>.</summary>
    public const string FilePlaceholder = "{file}";
    public const string LinePlaceholder = "{line}";
    public const string ColumnPlaceholder = "{column}";
    public const string ProjectPlaceholder = "{project}";

    /// <summary>Known editors offered by the settings dialog (name, command template).</summary>
    public static readonly IReadOnlyList<(string Name, string Command)> CodeEditorPresets =
    [
        ("System default", ""),
        ("Visual Studio Code", "code --goto \"{file}:{line}:{column}\""),
        ("JetBrains Rider", "rider --line {line} \"{file}\""),
        ("Visual Studio (Windows)", "devenv /Edit \"{file}\""),
        ("Sublime Text", "subl \"{file}:{line}:{column}\""),
    ];

    /// <summary>Theme accent as <c>#rrggbb</c> (selection, active buttons, focus).</summary>
    public string Accent
    {
        get;
        set => field = ValueText.TryParseColorHex(value, out _) && value.Length == 7 ? value.ToLowerInvariant() : DefaultAccent;
    } = DefaultAccent;

    /// <summary>Minutes between autosaves of open scenes that have a file (0: off).</summary>
    public int AutosaveMinutes
    {
        get;
        set => field = Math.Clamp(value, 0, 120);
    }

    /// <summary>
    /// The command that opens a source file (<see cref="FilePlaceholder"/>, <see cref="LinePlaceholder"/>,
    /// <see cref="ColumnPlaceholder"/>, <see cref="ProjectPlaceholder"/>); empty uses the system's default application.
    /// </summary>
    public string CodeEditorCommand { get; set; } = "";

    /// <summary>Reload the game code automatically when its build output changes.</summary>
    public bool AutoReloadCode { get; set; } = true;

    /// <summary>Look for a newer editor on GitHub Releases at start-up (Help › Check for Updates… works either way).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Extra folders scanned for VST3 plugins (besides the OS's default ones).</summary>
    public List<string> PluginFolders { get; set; } = [];

    /// <summary>MIDI input devices (port names) the song editor listens to (Editor Settings › MIDI).</summary>
    public List<string> MidiInputs { get; set; } = [];

    /// <summary>
    /// How many pixels the 3D view renders (<see cref="ViewResolutionScale"/>); the UI scales the image to the view. Auto by
    /// default: one pixel per point, so a 2× display renders a quarter of its pixels.
    /// </summary>
    public ViewResolution ViewResolution { get; set; } = ViewResolution.Auto;

    /// <summary>
    /// The 3D view's pixels per display pixel for <paramref name="resolution"/> on a display of <paramref name="pixelScale"/>
    /// pixels per point: Auto renders one pixel per point (1 on a 1× display, 0.5 on a 2× one), Full every display pixel.
    /// </summary>
    public static float ViewResolutionScale(ViewResolution resolution, float pixelScale) => resolution switch
    {
        ViewResolution.Full => 1f,
        ViewResolution.ThreeQuarters => 0.75f,
        ViewResolution.Half => 0.5f,
        _ => pixelScale > 1f ? 1f / pixelScale : 1f,
    };

    /// <summary><c>~/.mainframe/editor_settings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "editor_settings.json");

    /// <summary>Reads <paramref name="path"/>; a missing or unreadable file gives the defaults (reported once).</summary>
    public static EditorSettings Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return new EditorSettings();
        try
        {
            var data = JsonSerializer.Deserialize(File.ReadAllBytes(path), EditorSettingsJson.Default.EditorSettingsData);
            if (data is null)
                return new EditorSettings();
            return new EditorSettings
            {
                Accent = data.Accent ?? DefaultAccent,
                AutosaveMinutes = data.AutosaveMinutes,
                CodeEditorCommand = data.CodeEditor ?? "",
                AutoReloadCode = data.AutoReloadCode ?? true,
                CheckForUpdates = data.CheckForUpdates ?? true,
                PluginFolders = data.PluginFolders ?? [],
                MidiInputs = data.MidiInputs ?? [],
                ViewResolution = Enum.TryParse<ViewResolution>(data.ViewResolution, ignoreCase: true, out var resolution) &&
                                 Enum.IsDefined(resolution) ? resolution : ViewResolution.Auto,
            };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Editor settings '{path}' could not be read ({e.Message}); using the defaults.");
            return new EditorSettings();
        }
    }

    /// <summary>Writes the settings atomically (LF, indented).</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = new EditorSettingsData
        {
            Format = 1,
            Accent = Accent,
            AutosaveMinutes = AutosaveMinutes,
            CodeEditor = CodeEditorCommand,
            AutoReloadCode = AutoReloadCode,
            CheckForUpdates = CheckForUpdates,
            PluginFolders = PluginFolders.Count > 0 ? PluginFolders : null,
            MidiInputs = MidiInputs.Count > 0 ? MidiInputs : null,
            ViewResolution = ViewResolution == ViewResolution.Auto ? null : JsonNamingPolicy.CamelCase.ConvertName(ViewResolution.ToString()),
        };
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(data, EditorSettingsJson.Default.EditorSettingsData));
    }

    public EditorSettings Clone() => new()
    {
        Accent = Accent,
        AutosaveMinutes = AutosaveMinutes,
        CodeEditorCommand = CodeEditorCommand,
        AutoReloadCode = AutoReloadCode,
        CheckForUpdates = CheckForUpdates,
        PluginFolders = [.. PluginFolders],
        MidiInputs = [.. MidiInputs],
        ViewResolution = ViewResolution,
    };

    /// <summary>
    /// Splits <paramref name="template"/> into a program and arguments (double quotes group, no shell) and fills the
    /// placeholders. Null when the template is empty.
    /// </summary>
    public static (string Program, IReadOnlyList<string> Arguments)? ExpandCommand(string template, string file, int line, int column, string? project)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(file);
        var parts = Split(template);
        if (parts.Count == 0)
            return null;
        var values = new[]
        {
            (FilePlaceholder, file),
            (LinePlaceholder, Math.Max(1, line).ToString(CultureInfo.InvariantCulture)),
            (ColumnPlaceholder, Math.Max(1, column).ToString(CultureInfo.InvariantCulture)),
            (ProjectPlaceholder, project ?? Path.GetDirectoryName(file) ?? ""),
        };
        var arguments = new List<string>(parts.Count - 1);
        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            foreach (var (name, value) in values)
                part = part.Replace(name, value, StringComparison.Ordinal);
            arguments.Add(part);
        }

        // A template without {file} gets the file appended.
        if (!template.Contains(FilePlaceholder, StringComparison.Ordinal))
            arguments.Add(file);
        return (parts[0], arguments);
    }

    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    parts.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any)
            parts.Add(current.ToString());
        return parts;
    }
}

internal sealed class EditorSettingsData
{
    public int Format { get; set; } = 1;
    public string? Accent { get; set; }
    public int AutosaveMinutes { get; set; }
    public string? CodeEditor { get; set; }
    public bool? AutoReloadCode { get; set; }
    public bool? CheckForUpdates { get; set; }
    public List<string>? PluginFolders { get; set; }
    public List<string>? MidiInputs { get; set; }
    public string? ViewResolution { get; set; }
}

/// <summary>The 3D view's resolution (Editor Settings): see <see cref="EditorSettings.ViewResolutionScale"/>.</summary>
public enum ViewResolution
{
    /// <summary>One pixel per point: every display pixel on a 1× display, a quarter of them on a 2× one.</summary>
    Auto,

    /// <summary>Every display pixel.</summary>
    Full,

    /// <summary>Three quarters of the display's pixels on each side.</summary>
    ThreeQuarters,

    /// <summary>Half of the display's pixels on each side.</summary>
    Half,
}

[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EditorSettingsData))]
internal sealed partial class EditorSettingsJson : JsonSerializerContext;

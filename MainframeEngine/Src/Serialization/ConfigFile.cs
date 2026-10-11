using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// Godot's <c>ConfigFile</c>: named sections holding keys, for options and other small key/value data (the player's
/// <c>settings.json</c>, <see cref="UserSettings"/>). Two deviations from Godot: the file is JSON (one parser, the same
/// <see cref="JsonMigrations"/>, readable by every tool), and values are typed through overloads instead of a Variant, so
/// reading a number never boxes.
/// </summary>
/// <remarks>
/// <para>
/// On disk each section is a top-level JSON object; the top-level keys <c>format</c> (the engine's container layout,
/// <see cref="CurrentFormat"/>) and <c>version</c> (the game's own, <see cref="Version"/>) are reserved. Comments and
/// trailing commas are tolerated.
/// </para>
/// <para>
/// A player may hand-edit the file, so reading never throws: a value of the wrong type returns the default and logs one
/// warning per key; <see cref="Load"/> moves an unparseable file aside (<see cref="DurableFile.QuarantineCorrupt"/>), tries
/// the backup and falls back to an empty config. <see cref="Save"/> writes through <see cref="DurableFile"/> with one backup.
/// </para>
/// <para>
/// The values live in a <see cref="JsonObject"/> tree: meant for options, not per-frame reads (callers cache values).
/// Game-thread only.
/// </para>
/// </remarks>
public sealed class ConfigFile
{
    /// <summary>The container layout this engine writes (top-level <c>format</c>).</summary>
    public const int CurrentFormat = 1;

    /// <summary>How many previous versions <see cref="Save"/> keeps (<c>.bak1</c>).</summary>
    public const int Backups = 1;

    // The engine's container migrations (format N → N+1); none yet.
    private static readonly JsonMigration[] FormatMigrations = [];

    private readonly JsonObject _sections = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>An empty config.</summary>
    public ConfigFile()
    {
    }

    /// <summary>
    /// The game's version of its own sections (top-level <c>version</c>; 0 = not versioned, not written). <see cref="Load"/>
    /// sets it to its <c>currentVersion</c> after running the migrations, or to the file's value when it is not versioning.
    /// </summary>
    public int Version
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>Names the file in warnings: its path, or <c>"config"</c> for an in-memory config.</summary>
    public string Source { get; private set; } = "config";

    /// <summary>Whether <paramref name="section"/> exists (it holds at least one key once written).</summary>
    public bool HasSection(string section) => _sections[section] is JsonObject;

    public bool HasSectionKey(string section, string key) => Section(section)?.ContainsKey(key) == true;

    /// <summary>The section names, in file order.</summary>
    public IReadOnlyList<string> GetSections()
    {
        var names = new List<string>(_sections.Count);
        foreach (var (name, _) in _sections)
            names.Add(name);
        return names;
    }

    /// <summary>The keys of <paramref name="section"/>, in file order (empty when it does not exist).</summary>
    public IReadOnlyList<string> GetSectionKeys(string section)
    {
        if (Section(section) is not { } values)
            return [];
        var keys = new List<string>(values.Count);
        foreach (var (key, _) in values)
            keys.Add(key);
        return keys;
    }

    /// <summary>A boolean; <paramref name="default"/> when missing or not a boolean (warned once).</summary>
    public bool GetBool(string section, string key, bool @default = false)
    {
        if (GetNode(section, key) is not { } node)
            return @default;
        if (node is JsonValue value && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return value.GetValue<bool>();
        return WrongType(section, key, "a boolean", @default);
    }

    /// <summary>An integer; <paramref name="default"/> when missing or not an integer in range (warned once).</summary>
    public int GetInt(string section, string key, int @default = 0)
    {
        if (GetNode(section, key) is not { } node)
            return @default;
        if (TryGetNumber(node, out var number) && number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue)
            return (int)number;
        return WrongType(section, key, "an integer", @default);
    }

    /// <summary>A number; <paramref name="default"/> when missing or not a number (warned once).</summary>
    public double GetDouble(string section, string key, double @default = 0)
    {
        if (GetNode(section, key) is not { } node)
            return @default;
        return TryGetNumber(node, out var number) ? number : WrongType(section, key, "a number", @default);
    }

    /// <summary>A string; <paramref name="default"/> when missing or not a string (warned once).</summary>
    public string? GetString(string section, string key, string? @default = null)
    {
        if (GetNode(section, key) is not { } node)
            return @default;
        if (node is JsonValue value && node.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>();
        return WrongType(section, key, "a string", @default);
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with a string array. False, with <paramref name="into"/> empty, when
    /// the key is missing or is not an array of strings (warned once).
    /// </summary>
    public bool GetStrings(string section, string key, List<string> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (GetNode(section, key) is not { } node)
            return false;
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonValue value || item.GetValueKind() != JsonValueKind.String)
                {
                    into.Clear();
                    return WrongType(section, key, "an array of strings", false);
                }

                into.Add(value.GetValue<string>());
            }

            return true;
        }

        return WrongType(section, key, "an array of strings", false);
    }

    /// <summary>
    /// A structured value read with the game's source-generated <paramref name="typeInfo"/> (no reflection; trim- and
    /// AOT-safe); default when missing or not readable as <typeparamref name="T"/> (warned once).
    /// </summary>
    public T? Get<T>(string section, string key, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (GetNode(section, key) is not { } node)
            return default;
        try
        {
            return node.Deserialize(typeInfo);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return WrongType<T?>(section, key, typeof(T).Name, default);
        }
    }

    public void SetValue(string section, string key, bool value) => SetNode(section, key, JsonValue.Create(value));

    public void SetValue(string section, string key, int value) => SetNode(section, key, JsonValue.Create(value));

    /// <summary>Sets a number; JSON has no NaN or infinity, so those throw <see cref="ArgumentOutOfRangeException"/>.</summary>
    public void SetValue(string section, string key, double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Config values must be finite numbers.");
        SetNode(section, key, JsonValue.Create(value));
    }

    /// <summary>Sets a string; null erases the key (Godot's <c>set_value</c> with <c>null</c>).</summary>
    public void SetValue(string section, string key, string? value)
    {
        if (value is null)
            EraseSectionKey(section, key);
        else
            SetNode(section, key, JsonValue.Create(value));
    }

    /// <summary>Sets a string array.</summary>
    public void SetValue(string section, string key, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(JsonValue.Create(value ?? throw new ArgumentException("Config string arrays cannot hold null.", nameof(values))));
        SetNode(section, key, array);
    }

    /// <summary>Sets a structured value written with <paramref name="typeInfo"/>; a null value erases the key.</summary>
    public void Set<T>(string section, string key, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (JsonSerializer.SerializeToNode(value, typeInfo) is { } node)
            SetNode(section, key, node);
        else
            EraseSectionKey(section, key);
    }

    /// <summary>Removes a key; an emptied section goes too (as in Godot). False when it did not exist.</summary>
    public bool EraseSectionKey(string section, string key)
    {
        if (Section(section) is not { } values || !values.Remove(key))
            return false;
        _warned.Remove(WarnKey(section, key));
        if (values.Count == 0)
            _sections.Remove(section);
        return true;
    }

    /// <summary>Removes a section and its keys. False when it did not exist.</summary>
    public bool EraseSection(string section)
    {
        if (!_sections.Remove(section))
            return false;
        _warned.RemoveWhere(k => k.StartsWith(section + ".", StringComparison.Ordinal));
        return true;
    }

    /// <summary>Removes every section (<see cref="Version"/> is kept).</summary>
    public void Clear()
    {
        _sections.Clear();
        _warned.Clear();
    }

    /// <summary>
    /// Reads <paramref name="path"/>. A missing file is an empty config. With <paramref name="currentVersion"/> &gt; 0, an
    /// older game <see cref="Version"/> runs through <paramref name="migrations"/> (a missing version means 1). Never throws
    /// for bad content: an unparseable file is moved aside as <c>path.corrupt-{utc}</c>, then the newest valid copy
    /// (<see cref="DurableFile.RecoveryCandidates"/>: the <c>.tmp</c>, the backup) is restored, and failing that the config
    /// starts empty. A file from a newer engine or game version is left untouched and the config starts empty; its
    /// content survives as the backup of the next <see cref="Save"/>.
    /// </summary>
    public static ConfigFile Load(string path, int currentVersion = 0, IReadOnlyList<JsonMigration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(currentVersion);
        var full = Path.GetFullPath(path);
        var corrupt = false;
        foreach (var candidate in DurableFile.RecoveryCandidates(full, Backups))
        {
            var isTarget = string.Equals(candidate, full, StringComparison.Ordinal);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(candidate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Config] Could not read '{candidate}': {e.Message}");
                continue;
            }

            try
            {
                var config = Parse(bytes, candidate, currentVersion, migrations);
                config.Source = full;
                if (!isTarget)
                {
                    TryRestore(full, candidate);
                    Log.Warning($"[Config] '{full}' was {(corrupt ? "damaged" : "missing")}; restored it from '{Path.GetFileName(candidate)}'.");
                }

                return config;
            }
            catch (InvalidDataException e) when (e.Data.Contains(TooNewKey))
            {
                // Never quarantine or replace a newer file: an older build must not destroy a newer one's settings.
                Log.Error($"[Config] {e.Message} Starting with defaults.");
                return new ConfigFile { Source = full, Version = currentVersion };
            }
            catch (InvalidDataException e)
            {
                Log.Error($"[Config] {e.Message}");
                if (!isTarget)
                    continue; // a partial .tmp or a bad backup: the next save replaces it
                corrupt = true;
                if (DurableFile.QuarantineCorrupt(candidate) is { } moved)
                    Log.Warning($"[Config] Kept the damaged file as '{Path.GetFileName(moved)}'.");
            }
        }

        if (corrupt)
            Log.Warning($"[Config] No valid copy of '{full}'; starting with defaults.");
        return new ConfigFile { Source = full, Version = currentVersion };
    }

    /// <summary>
    /// Parses config JSON; <paramref name="source"/> names it in errors. Unlike <see cref="Load"/> this throws
    /// <see cref="InvalidDataException"/> for invalid JSON, a newer <c>format</c> or <c>version</c>, or a missing migration.
    /// Top-level values that are not objects are dropped with a warning.
    /// </summary>
    public static ConfigFile Parse(ReadOnlySpan<byte> json, string source, int currentVersion = 0, IReadOnlyList<JsonMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(currentVersion);
        try
        {
            return ParseCore(json, source, currentVersion, migrations ?? []);
        }
        catch (ArgumentException e)
        {
            // JsonObject materializes properties lazily and throws ArgumentException for a duplicate key; a migration may
            // throw one too. Every content problem surfaces as InvalidDataException.
            throw new InvalidDataException($"'{source}' is invalid: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException($"'{source}' is invalid: {e.Message}", e);
        }
    }

    private static ConfigFile ParseCore(ReadOnlySpan<byte> json, string source, int currentVersion, IReadOnlyList<JsonMigration> migrations)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: SceneFormat.ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"'{source}' is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not valid JSON: {e.Message}", e);
        }

        var format = ReadNumber(root, "format", CurrentFormat, source, "settings format");
        JsonMigrations.Upgrade(root, format, CurrentFormat, FormatMigrations, source, "settings format");

        int version;
        if (currentVersion > 0)
        {
            version = ReadNumber(root, "version", currentVersion, source, "settings version");
            JsonMigrations.Upgrade(root, version, currentVersion, migrations, source, "settings version");
            version = currentVersion;
        }
        else
        {
            // Not versioning: keep whatever the file says, so a later Save does not lose it.
            version = root["version"] is JsonValue v && v.TryGetValue(out int n) && n > 0 ? n : 0;
        }

        var config = new ConfigFile { Source = source, Version = version };
        var names = new List<string>(root.Count);
        foreach (var (name, _) in root)
            names.Add(name);
        foreach (var name in names)
        {
            if (name is "format" or "version")
                continue;
            var node = root[name];
            root.Remove(name); // detach so it can live in the config's tree
            if (node is JsonObject section)
                config._sections[name] = section;
            else
                Log.Warning($"[Config] '{source}': top-level '{name}' is not a section (an object); ignored.");
        }

        return config;
    }

    // JsonMigrations.ReadVersion, but a newer number is its own exception (the file must stay untouched).
    private static int ReadNumber(JsonObject root, string key, int current, string source, string what)
    {
        var value = JsonMigrations.ReadVersion(root, key, int.MaxValue, source, what);
        if (value > current)
            throw new InvalidDataException($"'{source}' has {what} {value}; this build reads up to {current}.") { Data = { [TooNewKey] = true } };
        return value;
    }

    private static void TryRestore(string path, string source)
    {
        try
        {
            DurableFile.Restore(path, source, Backups);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Config] Could not restore '{path}' from '{source}': {e.Message}");
        }
    }

    /// <summary>Writes the config to <paramref name="path"/> durably (<see cref="DurableFile.Write"/>, keeping one backup).</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DurableFile.Write(path, ToJson(), Backups);
        Source = Path.GetFullPath(path);
    }

    /// <summary>The file content: <c>format</c>, <c>version</c> (when not 0), then the sections. UTF-8, LF, indented.</summary>
    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, SceneFormat.WriteOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("format", CurrentFormat);
            if (Version > 0)
                w.WriteNumber("version", Version);
            foreach (var (name, section) in _sections)
            {
                w.WritePropertyName(name);
                section!.WriteTo(w);
            }

            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    // ── Engine access (UserSettings stores nested values: audio.volume.{bus}) ────────────────────────────────────────

    /// <summary>The raw value of a key, or null.</summary>
    internal JsonNode? GetNode(string section, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Section(section)?[key];
    }

    /// <summary>Replaces a key's raw value (the node must not have a parent).</summary>
    internal void SetNode(string section, string key, JsonNode node)
    {
        CheckSection(section);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_sections[section] is not JsonObject values)
            _sections[section] = values = new JsonObject();
        values[key] = node;
        _warned.Remove(WarnKey(section, key));
    }

    /// <summary>Logs "<paramref name="what"/> is not <paramref name="expected"/>" once per <paramref name="warnKey"/>.</summary>
    internal void WarnOnce(string warnKey, string what, string expected)
    {
        if (_warned.Add(warnKey))
            Log.Warning($"[Config] '{Source}': {what} is not {expected}; using the default.");
    }

    internal static bool TryGetNumber(JsonNode node, out double number)
    {
        number = 0;
        if (node is not JsonValue value || node.GetValueKind() != JsonValueKind.Number)
            return false;
        if (value.TryGetValue(out number))
            return true;
        if (value.TryGetValue(out int i))
            number = i;
        else if (value.TryGetValue(out long l))
            number = l;
        else if (value.TryGetValue(out float f))
            number = f;
        else if (value.TryGetValue(out decimal m))
            number = (double)m;
        else if (value.TryGetValue(out JsonElement element) && element.TryGetDouble(out var d))
            number = d;
        else
            return false;
        return true;
    }

    private JsonObject? Section(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return _sections[section] as JsonObject;
    }

    private static void CheckSection(string section)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        if (section is "format" or "version")
            throw new ArgumentException($"'{section}' is reserved for the file's own header.", nameof(section));
    }

    private T WrongType<T>(string section, string key, string expected, T @default)
    {
        WarnOnce(WarnKey(section, key), $"{section}.{key}", expected);
        return @default;
    }

    private static string WarnKey(string section, string key) => section + "." + key;

    // Marks the InvalidDataException of a file from a newer engine or game version (Load leaves that file untouched).
    private const string TooNewKey = "MainframeEngine.ConfigFile.TooNew";
}

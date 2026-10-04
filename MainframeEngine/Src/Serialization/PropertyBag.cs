using System.Buffers;
using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// The raw (JSON) serialized properties of one node or resource, handed to
/// <see cref="SerializedMigrationAttribute"/> methods to upgrade old data before it is applied. Values are
/// <see cref="JsonElement"/>s in the file's encoding (vectors as arrays, enums as names, ...).
/// </summary>
public sealed class PropertyBag
{
    private readonly OrderedDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public PropertyBag()
    {
    }

    internal PropertyBag(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object)
            return;
        foreach (var p in props.EnumerateObject())
            _values[p.Name] = p.Value.Clone();
    }

    /// <summary>The type name (rarely needed; a migration may change it to a replacement type).</summary>
    public string? TypeName { get; set; }

    public int Count => _values.Count;

    public IEnumerable<string> Names => _values.Keys;

    internal IEnumerable<KeyValuePair<string, JsonElement>> Entries => _values;

    public bool Contains(string name) => _values.ContainsKey(name);

    public bool TryGet(string name, out JsonElement value) => _values.TryGetValue(name, out value);

    public void Set(string name, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _values[name] = value.Clone();
    }

    public bool Remove(string name) => _values.Remove(name);

    /// <summary>Renames a property, keeping its value; returns false when it is absent.</summary>
    public bool Rename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (!_values.Remove(oldName, out var value))
            return false;
        _values[newName] = value;
        return true;
    }

    public void SetNumber(string name, double value) => Set(name, Build(w => w.WriteNumberValue(value)));

    public void SetString(string name, string value) => Set(name, Build(w => w.WriteStringValue(value)));

    public void SetBoolean(string name, bool value) => Set(name, Build(w => w.WriteBooleanValue(value)));

    /// <summary>Sets a float array (vectors, quaternions, colors).</summary>
    public void SetNumbers(string name, params ReadOnlySpan<float> values)
    {
        var copy = values.ToArray();
        Set(name, Build(w =>
        {
            w.WriteStartArray();
            foreach (var v in copy)
                w.WriteNumberValue(v);
            w.WriteEndArray();
        }));
    }

    public float GetSingle(string name) => _values[name].GetSingle();

    public string? GetString(string name) => _values[name].GetString();

    /// <summary>Reads a float array property (e.g. a vector).</summary>
    public float[] GetNumbers(string name) => [.. _values[name].EnumerateArray().Select(e => e.GetSingle())];

    private static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}

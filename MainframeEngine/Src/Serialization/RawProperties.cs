using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// Keeps the resource references inside the raw properties of a <see cref="MissingNode"/>/<see cref="MissingResource"/>
/// alive across a save: <c>{"res": "key"}</c> objects are resolved when the file is read and re-keyed into the new
/// file's resource table when it is written, so inline resources used only by an unknown type are not dropped.
/// </summary>
internal static class RawProperties
{
    /// <summary>Resolves every resource reference in <paramref name="props"/> (null when there is none).</summary>
    public static Dictionary<string, Resource>? ResolveReferences(JsonElement props, DeserializationContext context)
    {
        Dictionary<string, Resource>? references = null;
        Visit(props, context, ref references);
        return references;
    }

    private static void Visit(JsonElement element, DeserializationContext context, ref Dictionary<string, Resource>? references)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetReferenceKey(element, out var key))
                {
                    references ??= new Dictionary<string, Resource>(StringComparer.Ordinal);
                    if (!references.ContainsKey(key))
                        references[key] = context.GetResource(key);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                    Visit(property.Value, context, ref references);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Visit(item, context, ref references);
                break;
        }
    }

    /// <summary>Writes <paramref name="props"/>, re-keying resolved references through <paramref name="context"/>.</summary>
    public static void Write(Utf8JsonWriter writer, JsonElement props, IReadOnlyDictionary<string, Resource>? references,
        SerializationContext context)
    {
        if (references is null || references.Count == 0)
        {
            props.WriteTo(writer);
            return;
        }

        switch (props.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetReferenceKey(props, out var key) && references.TryGetValue(key, out var resource))
                {
                    writer.WriteStartObject();
                    writer.WriteString("res", context.AddResource(resource));
                    writer.WriteEndObject();
                    return;
                }

                writer.WriteStartObject();
                foreach (var property in props.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, references, context);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in props.EnumerateArray())
                    Write(writer, item, references, context);
                writer.WriteEndArray();
                break;
            default:
                props.WriteTo(writer);
                break;
        }
    }

    // A reference is an object with exactly one property, "res", holding a string key.
    private static bool TryGetReferenceKey(JsonElement element, out string key)
    {
        key = string.Empty;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (++count > 1 || !property.NameEquals("res") || property.Value.ValueKind != JsonValueKind.String)
                return false;
            key = property.Value.GetString() ?? string.Empty;
        }

        return count == 1 && key.Length > 0;
    }
}

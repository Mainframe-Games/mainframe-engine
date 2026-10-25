using System.Numerics;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// A material running a <see cref="MainframeEngine.Shader"/> written in Godot's shading language with per-material uniform
/// values (Godot's <c>ShaderMaterial</c>): a canvas_item shader on canvas items (ADR 0113), a spatial shader on
/// <see cref="GeometryInstance3D"/>s — <see cref="MeshInstance3D"/>, <see cref="MultiMeshInstance3D"/>, overrides, overlays and
/// next passes (ADR 0205). Values are set by name; unset uniforms use the shader's defaults. Samplers take a
/// <see cref="Texture2D"/>. Saved with the material as <see cref="ShaderParameters"/> (<c>.mres</c>, <c>.mscene</c>).
/// </summary>
[EditorIcon("palette")]
public sealed class ShaderMaterial : Material
{
    private readonly Dictionary<string, object> _parameters = new(StringComparer.Ordinal);

    [Export]
    public Shader? Shader
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>
    /// Sets a uniform by name: float, int, bool, <see cref="Vector2"/>/<c>3</c>/<c>4</c> (colours are Vector4: a spatial shader's
    /// <c>source_color</c> values are sRGB and converted to linear), arrays of those for array uniforms, or a
    /// <see cref="Texture2D"/> for a sampler.
    /// </summary>
    public void SetShaderParameter(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _parameters[name] = value;
        Touch();
    }

    /// <summary>The value set for a uniform, or null when it was never set (the shader's default applies).</summary>
    public object? GetShaderParameter(string name) => _parameters.GetValueOrDefault(name);

    /// <summary>Every parameter set on this material.</summary>
    public IReadOnlyDictionary<string, object> Parameters => _parameters;

    /// <summary>
    /// The parameters as saved in scenes and resources (Godot's <c>shader_parameter/…</c> entries), by name. Assigning
    /// replaces every parameter.
    /// </summary>
    [Export]
    public ShaderParameter[] ShaderParameters
    {
        get => [.. _parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ShaderParameter(p.Key, p.Value))];
        set
        {
            _parameters.Clear();
            foreach (var parameter in value ?? [])
                if (parameter.Name is { Length: > 0 } && parameter.Value is not null)
                    _parameters[parameter.Name] = parameter.Value;
            Touch();
        }
    }

    /// <summary>A spatial shader's render modes (ADR 0205); canvas materials keep no 3D state.</summary>
    public override MaterialRenderState RenderState => Shader?.Spatial?.RenderState ?? default;
}

/// <summary>
/// One <see cref="ShaderMaterial"/> uniform value as scenes and resources store it: a bool, a number, a vector (2–4
/// floats), an array (of numbers or vectors), or a resource (a <see cref="Texture2D"/>).
/// </summary>
[SerializableValue]
public readonly record struct ShaderParameter(string Name, object Value)
{
    /// <summary>
    /// JSON: <c>{"name": …, "value": …}</c> — <c>true</c>, a number, <c>[x, y(, z(, w))]</c> or <c>{"res": key}</c> — or
    /// <c>{"name": …, "array": [...]}</c> for array uniforms (numbers or vectors).
    /// </summary>
    internal sealed class Codec : ValueCodec<ShaderParameter>
    {
        public override void Write(Utf8JsonWriter writer, ShaderParameter value, SerializationContext context)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            switch (value.Value)
            {
                case float[] or double[] or int[] or Vector2[] or Vector3[] or Vector4[]:
                    writer.WritePropertyName("array");
                    writer.WriteStartArray();
                    foreach (var element in (System.Collections.IEnumerable)value.Value)
                        WriteScalarOrVector(writer, element);
                    writer.WriteEndArray();
                    break;
                case Resource resource:
                    writer.WritePropertyName("value");
                    writer.WriteStartObject();
                    writer.WriteString("res", context.AddResource(resource));
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WritePropertyName("value");
                    WriteScalarOrVector(writer, value.Value);
                    break;
            }

            writer.WriteEndObject();
        }

        private static void WriteScalarOrVector(Utf8JsonWriter writer, object? value)
        {
            Span<float> v = stackalloc float[4];
            var n = 0;
            switch (value)
            {
                case bool b:
                    writer.WriteBooleanValue(b);
                    return;
                case float f:
                    Codecs.WriteFloat(writer, f);
                    return;
                case double d:
                    Codecs.WriteFloat(writer, (float)d);
                    return;
                case int i:
                    writer.WriteNumberValue(i);
                    return;
                case uint u:
                    writer.WriteNumberValue(u);
                    return;
                case long l:
                    writer.WriteNumberValue(l);
                    return;
                case Vector2 v2: v[0] = v2.X; v[1] = v2.Y; n = 2; break;
                case Vector3 v3: v[0] = v3.X; v[1] = v3.Y; v[2] = v3.Z; n = 3; break;
                case Vector4 v4: v[0] = v4.X; v[1] = v4.Y; v[2] = v4.Z; v[3] = v4.W; n = 4; break;
                case System.Drawing.Color c: v[0] = c.R / 255f; v[1] = c.G / 255f; v[2] = c.B / 255f; v[3] = c.A / 255f; n = 4; break;
                default:
                    throw new JsonException($"A shader parameter of type {value?.GetType().Name ?? "null"} cannot be saved.");
            }

            writer.WriteStartArray();
            for (var k = 0; k < n; k++)
                Codecs.WriteFloat(writer, v[k]);
            writer.WriteEndArray();
        }

        public override ShaderParameter Read(JsonElement element, DeserializationContext context)
        {
            var name = element.GetProperty("name").GetString() ?? "";
            if (element.TryGetProperty("array", out var array))
            {
                var count = array.GetArrayLength();
                if (count == 0 || array[0].ValueKind != JsonValueKind.Array)
                    return new ShaderParameter(name, array.EnumerateArray().Select(Codecs.ReadFloat).ToArray());
                return array[0].GetArrayLength() switch
                {
                    2 => new ShaderParameter(name, array.EnumerateArray().Select(e => (Vector2)ReadVector(e)).ToArray()),
                    3 => new ShaderParameter(name, array.EnumerateArray().Select(e => (Vector3)ReadVector(e)).ToArray()),
                    _ => new ShaderParameter(name, array.EnumerateArray().Select(e => (Vector4)ReadVector(e)).ToArray()),
                };
            }

            var value = element.GetProperty("value");
            return new ShaderParameter(name, value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => Codecs.ReadFloat(value),
                JsonValueKind.Array => ReadVector(value),
                JsonValueKind.Object when value.TryGetProperty("res", out var key) => context.GetResource(key.GetString()!),
                _ => throw new JsonException($"Shader parameter '{name}' has an unreadable value ({value.ValueKind})."),
            });
        }

        private static object ReadVector(JsonElement e)
        {
            var f = e.EnumerateArray().Select(Codecs.ReadFloat).ToArray();
            return f.Length switch
            {
                2 => new Vector2(f[0], f[1]),
                3 => new Vector3(f[0], f[1], f[2]),
                4 => new Vector4(f[0], f[1], f[2], f[3]),
                _ => throw new JsonException($"A shader parameter vector has {f.Length} components (2–4 expected)."),
            };
        }

        public override bool ValueEquals(ShaderParameter a, ShaderParameter b) =>
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) && (a.Value, b.Value) switch
            {
                (Resource ra, Resource rb) => ResourceValueEquality.AreEqual(ra, rb),
                (System.Collections.IEnumerable ea, System.Collections.IEnumerable eb) when a.Value is not string =>
                    ea.Cast<object>().SequenceEqual(eb.Cast<object>()),
                _ => Equals(a.Value, b.Value),
            };
    }
}

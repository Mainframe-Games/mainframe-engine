using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Serialization;

/// <summary>Resolves resource references while writing a scene or resource file.</summary>
public abstract class SerializationContext
{
    /// <summary>Registers <paramref name="resource"/> in the file's resource table and returns its key.</summary>
    public abstract string AddResource(Resource resource);
}

/// <summary>Resolves resource references while reading a scene or resource file.</summary>
public abstract class DeserializationContext
{
    /// <summary>The resource stored under <paramref name="key"/> in the file's resource table.</summary>
    public abstract Resource GetResource(string key);
}

/// <summary>
/// Reads and writes one value type as JSON. Vectors, quaternions and colors are arrays, enums are names,
/// resources are <c>{"res": "key"}</c> references into the file's resource table.
/// </summary>
public abstract class ValueCodec<T>
{
    public abstract void Write(Utf8JsonWriter writer, T value, SerializationContext context);

    public abstract T Read(JsonElement element, DeserializationContext context);

    /// <summary>Equality used to skip default values when saving and to diff scene instances.</summary>
    public virtual bool ValueEquals(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);
}

/// <summary>Built-in codecs. Generated registration code calls these; game code rarely needs to.</summary>
public static class Codecs
{
    static Codecs()
    {
        Cache<bool>.Value = new BooleanCodec();
        Cache<byte>.Value = new IntegerCodec<byte>();
        Cache<sbyte>.Value = new IntegerCodec<sbyte>();
        Cache<short>.Value = new IntegerCodec<short>();
        Cache<ushort>.Value = new IntegerCodec<ushort>();
        Cache<int>.Value = new IntegerCodec<int>();
        Cache<uint>.Value = new IntegerCodec<uint>();
        Cache<long>.Value = new IntegerCodec<long>();
        Cache<ulong>.Value = new IntegerCodec<ulong>();
        Cache<float>.Value = new FloatCodec();
        Cache<double>.Value = new DoubleCodec();
        Cache<string>.Value = new StringCodec();
        Cache<Vector2>.Value = new FloatArrayCodec<Vector2>(2, static (v, d) => { d[0] = v.X; d[1] = v.Y; }, static s => new Vector2(s[0], s[1]));
        Cache<Vector3>.Value = new FloatArrayCodec<Vector3>(3, static (v, d) => { d[0] = v.X; d[1] = v.Y; d[2] = v.Z; }, static s => new Vector3(s[0], s[1], s[2]));
        Cache<Vector4>.Value = new FloatArrayCodec<Vector4>(4, static (v, d) => { d[0] = v.X; d[1] = v.Y; d[2] = v.Z; d[3] = v.W; }, static s => new Vector4(s[0], s[1], s[2], s[3]));
        Cache<Quaternion>.Value = new FloatArrayCodec<Quaternion>(4, static (v, d) => { d[0] = v.X; d[1] = v.Y; d[2] = v.Z; d[3] = v.W; }, static s => new Quaternion(s[0], s[1], s[2], s[3]));
        Cache<DrawingColor>.Value = new FloatArrayCodec<DrawingColor>(4,
            static (c, d) => { d[0] = c.R / 255f; d[1] = c.G / 255f; d[2] = c.B / 255f; d[3] = c.A / 255f; },
            static s => DrawingColor.FromArgb(ToByte(s[3]), ToByte(s[0]), ToByte(s[1]), ToByte(s[2])),
            // Color.Equals also compares the "known color" name: Color.White != FromArgb(255, 255, 255, 255).
            static (a, b) => a.ToArgb() == b.ToArgb());
        Cache<Transform3D>.Value = new FloatArrayCodec<Transform3D>(12,
            static (t, d) =>
            {
                d[0] = t.Basis.X.X; d[1] = t.Basis.X.Y; d[2] = t.Basis.X.Z;
                d[3] = t.Basis.Y.X; d[4] = t.Basis.Y.Y; d[5] = t.Basis.Y.Z;
                d[6] = t.Basis.Z.X; d[7] = t.Basis.Z.Y; d[8] = t.Basis.Z.Z;
                d[9] = t.Origin.X; d[10] = t.Origin.Y; d[11] = t.Origin.Z;
            },
            static s => new Transform3D(
                new Basis(new Vector3(s[0], s[1], s[2]), new Vector3(s[3], s[4], s[5]), new Vector3(s[6], s[7], s[8])),
                new Vector3(s[9], s[10], s[11])));
        Cache<Transform2D>.Value = new FloatArrayCodec<Transform2D>(6,
            static (t, d) => { d[0] = t.X.X; d[1] = t.X.Y; d[2] = t.Y.X; d[3] = t.Y.Y; d[4] = t.Origin.X; d[5] = t.Origin.Y; },
            static s => new Transform2D(new Vector2(s[0], s[1]), new Vector2(s[2], s[3]), new Vector2(s[4], s[5])));
        Cache<Rect2>.Value = new FloatArrayCodec<Rect2>(4,
            static (r, d) => { d[0] = r.Position.X; d[1] = r.Position.Y; d[2] = r.Size.X; d[3] = r.Size.Y; },
            static s => new Rect2(s[0], s[1], s[2], s[3]));
        Cache<NodePath>.Value = new NodePathCodec();
    }

    /// <summary>The built-in codec for <typeparamref name="T"/> (primitives, string, vectors, colors, transforms, NodePath).</summary>
    public static ValueCodec<T> Get<T>() =>
        Cache<T>.Value ?? throw new NotSupportedException($"No serialization codec for {typeof(T).FullName}.");

    /// <summary>Registers (or replaces) the codec for <typeparamref name="T"/>.</summary>
    public static void Register<T>(ValueCodec<T> codec) => Cache<T>.Value = codec ?? throw new ArgumentNullException(nameof(codec));

    /// <summary>
    /// A codec that looks up <typeparamref name="T"/>'s registered codec when used, not when created: generated type
    /// registration (which may run before the game registers its codecs) uses it for <see cref="SerializableValueAttribute"/>
    /// types.
    /// </summary>
    public static ValueCodec<T> Deferred<T>() => DeferredCodec<T>.Instance;

    private sealed class DeferredCodec<T> : ValueCodec<T>
    {
        public static readonly DeferredCodec<T> Instance = new();

        private static ValueCodec<T> Target => Cache<T>.Value is { } codec && codec is not DeferredCodec<T>
            ? codec
            : throw new InvalidOperationException($"No codec is registered for {typeof(T).FullName} ([SerializableValue]): call Codecs.Register before loading scenes or resources.");

        public override void Write(Utf8JsonWriter writer, T value, SerializationContext context) => Target.Write(writer, value, context);

        public override T Read(JsonElement element, DeserializationContext context) => Target.Read(element, context);

        public override bool ValueEquals(T a, T b) => Target.ValueEquals(a, b);
    }

    /// <summary>
    /// A codec storing <typeparamref name="T"/> as a JSON array of <paramref name="count"/> numbers, like the built-in
    /// vectors and colours (for <see cref="SerializableValueAttribute"/> types).
    /// </summary>
    public static ValueCodec<T> FloatArray<T>(int count, Action<T, Span<float>> unpack, Func<float[], T> pack, Func<T, T, bool>? equals = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentNullException.ThrowIfNull(unpack);
        ArgumentNullException.ThrowIfNull(pack);
        return new FloatArrayCodec<T>(count, (v, d) => unpack(v, d), pack, equals);
    }

    /// <summary>Enums by name (flags as <c>"A, B"</c>).</summary>
    public static ValueCodec<TEnum> EnumOf<TEnum>() where TEnum : struct, Enum => EnumCache<TEnum>.Value;

    /// <summary>Resource references (<c>{"res": "key"}</c> or null).</summary>
    public static ValueCodec<TResource?> ResourceOf<TResource>() where TResource : Resource => ResourceCache<TResource>.Value;

    /// <summary>A JSON array of <paramref name="element"/> values; null lists are written as null.</summary>
    public static ValueCodec<List<T>> ListOf<T>(ValueCodec<T> element) => new ListCodec<T>(element);

    /// <summary>A JSON array of <paramref name="element"/> values; null arrays are written as null.</summary>
    public static ValueCodec<T[]> ArrayOf<T>(ValueCodec<T> element) => new ArrayCodec<T>(element);

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    internal static void WriteFloat(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
            writer.WriteNumberValue(value);
        else
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    internal static float ReadFloat(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? float.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
            : element.GetSingle();

    private static class Cache<T>
    {
        public static ValueCodec<T>? Value;
    }

    private static class EnumCache<TEnum> where TEnum : struct, Enum
    {
        public static readonly ValueCodec<TEnum> Value = new EnumCodec<TEnum>();
    }

    private static class ResourceCache<TResource> where TResource : Resource
    {
        public static readonly ValueCodec<TResource?> Value = new ResourceCodec<TResource>();
    }

    private sealed class BooleanCodec : ValueCodec<bool>
    {
        public override void Write(Utf8JsonWriter writer, bool value, SerializationContext context) => writer.WriteBooleanValue(value);
        public override bool Read(JsonElement element, DeserializationContext context) => element.GetBoolean();
    }

    private sealed class IntegerCodec<T> : ValueCodec<T> where T : struct, IBinaryInteger<T>
    {
        public override void Write(Utf8JsonWriter writer, T value, SerializationContext context)
        {
            // Every integer type fits in decimal exactly, so one writer path covers all of them.
            writer.WriteNumberValue(decimal.CreateChecked(value));
        }

        public override T Read(JsonElement element, DeserializationContext context) => T.CreateChecked(element.GetDecimal());
    }

    private sealed class FloatCodec : ValueCodec<float>
    {
        public override void Write(Utf8JsonWriter writer, float value, SerializationContext context) => WriteFloat(writer, value);
        public override float Read(JsonElement element, DeserializationContext context) => ReadFloat(element);
    }

    private sealed class DoubleCodec : ValueCodec<double>
    {
        public override void Write(Utf8JsonWriter writer, double value, SerializationContext context)
        {
            if (double.IsFinite(value))
                writer.WriteNumberValue(value);
            else
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }

        public override double Read(JsonElement element, DeserializationContext context) =>
            element.ValueKind == JsonValueKind.String
                ? double.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                : element.GetDouble();
    }

    private sealed class StringCodec : ValueCodec<string>
    {
        public override void Write(Utf8JsonWriter writer, string value, SerializationContext context)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }

        public override string Read(JsonElement element, DeserializationContext context) => element.GetString()!;
    }

    private sealed class NodePathCodec : ValueCodec<NodePath>
    {
        public override void Write(Utf8JsonWriter writer, NodePath value, SerializationContext context) => writer.WriteStringValue(value.Path);
        public override NodePath Read(JsonElement element, DeserializationContext context) => new(element.GetString() ?? string.Empty);
    }

    private sealed class FloatArrayCodec<T>(int count, FloatArrayCodec<T>.Unpack unpack, Func<float[], T> pack, Func<T, T, bool>? equals = null)
        : ValueCodec<T>
    {
        public override bool ValueEquals(T a, T b) => equals?.Invoke(a, b) ?? base.ValueEquals(a, b);

        public delegate void Unpack(T value, Span<float> destination);

        public override void Write(Utf8JsonWriter writer, T value, SerializationContext context)
        {
            Span<float> values = stackalloc float[count];
            unpack(value, values);
            writer.WriteStartArray();
            foreach (var v in values)
                WriteFloat(writer, v);
            writer.WriteEndArray();
        }

        public override T Read(JsonElement element, DeserializationContext context)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
                throw new JsonException($"Expected an array of {count} numbers for {typeof(T).Name}, got {element.ValueKind}.");
            var values = new float[count];
            var i = 0;
            foreach (var item in element.EnumerateArray())
                values[i++] = ReadFloat(item);
            return pack(values);
        }
    }

    private sealed class EnumCodec<TEnum> : ValueCodec<TEnum> where TEnum : struct, Enum
    {
        public override void Write(Utf8JsonWriter writer, TEnum value, SerializationContext context) => writer.WriteStringValue(value.ToString());

        public override TEnum Read(JsonElement element, DeserializationContext context) =>
            element.ValueKind == JsonValueKind.Number
                ? (TEnum)Enum.ToObject(typeof(TEnum), element.GetInt64())
                : Enum.Parse<TEnum>(element.GetString()!);
    }

    private sealed class ResourceCodec<TResource> : ValueCodec<TResource?> where TResource : Resource
    {
        public override void Write(Utf8JsonWriter writer, TResource? value, SerializationContext context)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("res", context.AddResource(value));
            writer.WriteEndObject();
        }

        public override TResource? Read(JsonElement element, DeserializationContext context)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("res", out var key))
                throw new JsonException($"Expected a resource reference {{\"res\": \"key\"}} for {typeof(TResource).Name}.");
            var resource = context.GetResource(key.GetString()!);
            return resource as TResource
                   ?? throw new JsonException($"Resource '{key.GetString()}' is a {resource.GetType().Name}, not a {typeof(TResource).Name}.");
        }

        public override bool ValueEquals(TResource? a, TResource? b) => ResourceValueEquality.AreEqual(a, b);
    }

    private sealed class ListCodec<T>(ValueCodec<T> element) : ValueCodec<List<T>>
    {
        public override void Write(Utf8JsonWriter writer, List<T> value, SerializationContext context)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (var item in value)
                element.Write(writer, item, context);
            writer.WriteEndArray();
        }

        public override List<T> Read(JsonElement json, DeserializationContext context)
        {
            if (json.ValueKind == JsonValueKind.Null)
                return null!;
            var list = new List<T>(json.GetArrayLength());
            foreach (var item in json.EnumerateArray())
                list.Add(element.Read(item, context));
            return list;
        }

        public override bool ValueEquals(List<T> a, List<T> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a is null || b is null || a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                if (!element.ValueEquals(a[i], b[i]))
                    return false;
            return true;
        }
    }

    private sealed class ArrayCodec<T>(ValueCodec<T> element) : ValueCodec<T[]>
    {
        public override void Write(Utf8JsonWriter writer, T[] value, SerializationContext context)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (var item in value)
                element.Write(writer, item, context);
            writer.WriteEndArray();
        }

        public override T[] Read(JsonElement json, DeserializationContext context)
        {
            if (json.ValueKind == JsonValueKind.Null)
                return null!;
            var array = new T[json.GetArrayLength()];
            var i = 0;
            foreach (var item in json.EnumerateArray())
                array[i++] = element.Read(item, context);
            return array;
        }

        public override bool ValueEquals(T[] a, T[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a is null || b is null || a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
                if (!element.ValueEquals(a[i], b[i]))
                    return false;
            return true;
        }
    }
}

using System.Runtime.CompilerServices;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// How an animatable value type turns into floats and back (G1.1): value tracks store and interpolate every value as
/// <see cref="Components"/> floats. No allocation; <see cref="Discrete"/> values (bools, integers, enums, integer
/// vectors) change only when the playhead reaches a key.
/// </summary>
public interface IAnimationValueOps<T>
{
    int Components { get; }

    bool Discrete { get; }

    void Unpack(in T value, Span<float> destination);

    T Pack(ReadOnlySpan<float> source);
}

/// <summary>
/// The <see cref="IAnimationValueOps{T}"/> of a type: built in for numbers, bools and enums, and for every type whose
/// serialization codec is a float array (vectors, quaternions, both colour types, transforms, rects, and game
/// <see cref="SerializableValueAttribute"/> types registered with <see cref="Codecs.FloatArray{T}"/>). No reflection:
/// the generic cache is closed over the property's own type by the generated registration.
/// </summary>
public static class AnimationValueOps
{
    /// <summary>The ops for <typeparamref name="T"/>, or null when it cannot be animated (strings, resources, paths).</summary>
    public static IAnimationValueOps<T>? Get<T>()
    {
        if (Cache<T>.Value is { } ops)
            return ops;
        // Game types register their codecs after the type registry is built: look again each time until one exists.
        if (Codecs.FloatVector<T>() is { } codec)
            return Cache<T>.Value = new CodecOps<T>(codec, IsIntegerVector(typeof(T)));
        return null;
    }

    private static bool IsIntegerVector(Type type) => type == typeof(Vector2I) || type == typeof(Rect2I);

    private static class Cache<T>
    {
        public static IAnimationValueOps<T>? Value = Create();

        private static IAnimationValueOps<T>? Create()
        {
            object? ops = Type.GetTypeCode(typeof(T)) switch
            {
                _ when typeof(T).IsEnum => new EnumOps<T>(),
                TypeCode.Single => new FloatOps(),
                TypeCode.Double => new DoubleOps(),
                TypeCode.Boolean => new BoolOps(),
                TypeCode.Int32 => new IntegerOps<int>(),
                TypeCode.Int64 => new IntegerOps<long>(),
                TypeCode.Int16 => new IntegerOps<short>(),
                TypeCode.UInt16 => new IntegerOps<ushort>(),
                TypeCode.UInt32 => new IntegerOps<uint>(),
                TypeCode.Byte => new IntegerOps<byte>(),
                TypeCode.SByte => new IntegerOps<sbyte>(),
                _ => null,
            };
            return ops as IAnimationValueOps<T>;
        }
    }

    private sealed class FloatOps : IAnimationValueOps<float>
    {
        public int Components => 1;

        public bool Discrete => false;

        public void Unpack(in float value, Span<float> destination) => destination[0] = value;

        public float Pack(ReadOnlySpan<float> source) => source[0];
    }

    private sealed class DoubleOps : IAnimationValueOps<double>
    {
        public int Components => 1;

        public bool Discrete => false;

        public void Unpack(in double value, Span<float> destination) => destination[0] = (float)value;

        public double Pack(ReadOnlySpan<float> source) => source[0];
    }

    private sealed class BoolOps : IAnimationValueOps<bool>
    {
        public int Components => 1;

        public bool Discrete => true;

        public void Unpack(in bool value, Span<float> destination) => destination[0] = value ? 1f : 0f;

        public bool Pack(ReadOnlySpan<float> source) => source[0] >= 0.5f;
    }

    private sealed class IntegerOps<T> : IAnimationValueOps<T> where T : struct, System.Numerics.INumber<T>
    {
        public int Components => 1;

        public bool Discrete => true;

        public void Unpack(in T value, Span<float> destination) => destination[0] = float.CreateTruncating(value);

        public T Pack(ReadOnlySpan<float> source) => T.CreateSaturating(MathF.Round(source[0]));
    }

    // Enums of any underlying size, without boxing: the value's bits are read and written as its underlying integer.
    private sealed class EnumOps<T> : IAnimationValueOps<T>
    {
        public int Components => 1;

        public bool Discrete => true;

        public void Unpack(in T value, Span<float> destination)
        {
            var copy = value;
            destination[0] = Unsafe.SizeOf<T>() switch
            {
                1 => Unsafe.As<T, sbyte>(ref copy),
                2 => Unsafe.As<T, short>(ref copy),
                4 => Unsafe.As<T, int>(ref copy),
                _ => Unsafe.As<T, long>(ref copy),
            };
        }

        public T Pack(ReadOnlySpan<float> source)
        {
            var integer = (long)MathF.Round(source[0]);
            T value = default!;
            switch (Unsafe.SizeOf<T>())
            {
                case 1:
                    Unsafe.As<T, sbyte>(ref value) = (sbyte)integer;
                    break;
                case 2:
                    Unsafe.As<T, short>(ref value) = (short)integer;
                    break;
                case 4:
                    Unsafe.As<T, int>(ref value) = (int)integer;
                    break;
                default:
                    Unsafe.As<T, long>(ref value) = integer;
                    break;
            }

            return value;
        }
    }

    private sealed class CodecOps<T>(IFloatVectorCodec<T> codec, bool discrete) : IAnimationValueOps<T>
    {
        public int Components => codec.Count;

        public bool Discrete => discrete;

        public void Unpack(in T value, Span<float> destination) => codec.UnpackTo(value, destination);

        public T Pack(ReadOnlySpan<float> source) => codec.PackFrom(source);
    }

    /// <summary>
    /// The component index a property suffix names (<c>x y z w</c> or <c>r g b a</c>, Tween's), or −1 for none; false for
    /// an unknown suffix.
    /// </summary>
    public static bool TryParseComponent(ReadOnlySpan<char> suffix, out int component)
    {
        component = suffix switch
        {
            "x" or "r" => 0,
            "y" or "g" => 1,
            "z" or "b" => 2,
            "w" or "a" => 3,
            _ => -2,
        };
        if (component == -2)
        {
            component = -1;
            return false;
        }

        return true;
    }
}

/// <summary>
/// A node property a value track writes (G1.1): typed get and set through the generated registration, with the value
/// as floats (all components, or the one a <c>:x</c>-style suffix names). Writes allocate nothing.
/// </summary>
public abstract class PropertyBinding
{
    /// <summary>The object whose property this is.</summary>
    public abstract object Owner { get; }

    /// <summary>The property's registered info.</summary>
    public abstract ExportPropertyInfo Info { get; }

    /// <summary>Floats a value of this binding has (1 for a sub-component).</summary>
    public abstract int Components { get; }

    /// <summary>The value changes only at keys (bools, integers, enums, integer vectors).</summary>
    public abstract bool Discrete { get; }

    /// <summary>Reads the current value into <paramref name="destination"/> (<see cref="Components"/> floats).</summary>
    public abstract void Read(Span<float> destination);

    /// <summary>Writes <paramref name="value"/> (<see cref="Components"/> floats).</summary>
    public abstract void Write(ReadOnlySpan<float> value);

    /// <summary>
    /// A binding of <paramref name="property"/> (a name, optionally with a component suffix: <c>Position:y</c>,
    /// <c>Modulate:a</c>) on <paramref name="owner"/>, or null with the reason when it cannot be animated.
    /// </summary>
    public static PropertyBinding? Create(object owner, string property, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(property);
        var colon = property.IndexOf(':');
        var name = colon < 0 ? property : property[..colon];
        var component = -1;
        if (colon >= 0 && !AnimationValueOps.TryParseComponent(property.AsSpan(colon + 1), out component))
        {
            problem = $"unknown component '{property[(colon + 1)..]}'";
            return null;
        }

        if (TypeRegistry.GetNearest(owner.GetType())?.FindProperty(name) is not { } info)
        {
            problem = $"{owner.GetType().Name} has no exported property '{name}'";
            return null;
        }

        var binding = info.Bind(owner, component);
        problem = binding is null ? $"{info.ValueType.Name} values cannot be animated" + (component >= 0 ? " by component" : "") : null;
        return binding;
    }
}

internal sealed class PropertyBinding<TOwner, TValue>(TOwner owner, ExportPropertyInfo<TOwner, TValue> property, IAnimationValueOps<TValue> ops, int component)
    : PropertyBinding where TOwner : class
{
    public override object Owner => owner;

    public override ExportPropertyInfo Info => property;

    public override int Components => component < 0 ? ops.Components : 1;

    public override bool Discrete => ops.Discrete;

    public override void Read(Span<float> destination)
    {
        if (component < 0)
        {
            ops.Unpack(property.Get(owner), destination);
            return;
        }

        Span<float> all = stackalloc float[ops.Components];
        ops.Unpack(property.Get(owner), all);
        destination[0] = all[component];
    }

    public override void Write(ReadOnlySpan<float> value)
    {
        if (component < 0)
        {
            property.Set(owner, ops.Pack(value));
            return;
        }

        // Read-modify-write one component.
        Span<float> all = stackalloc float[ops.Components];
        ops.Unpack(property.Get(owner), all);
        all[component] = value[0];
        property.Set(owner, ops.Pack(all));
    }
}

using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// One <see cref="ExportAttribute"/> member of a node or resource type: name, value type, hints and typed
/// accessors. The non-generic surface (boxed <see cref="GetValue"/>/<see cref="SetValue"/>) is for tools such as
/// the editor inspector; serialization goes through the generic subclass without boxing.
/// </summary>
public abstract class ExportPropertyInfo
{
    private protected ExportPropertyInfo(string name, Type valueType, ExportHints? hints, string? group)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        ValueType = valueType;
        Hints = hints ?? ExportHints.None;
        Group = string.IsNullOrEmpty(group) ? null : group;
    }

    public string Name { get; }

    public Type ValueType { get; }

    public ExportHints Hints { get; }

    /// <summary>Inspector group from <see cref="ExportGroupAttribute"/>, or null.</summary>
    public string? Group { get; }

    /// <summary>The type that declares the member.</summary>
    public NodeTypeInfo DeclaringType { get; internal set; } = null!;

    public abstract object? GetValue(object owner);

    public abstract void SetValue(object owner, object? value);

    /// <summary>True when the member has the same value on both instances (codec equality).</summary>
    public abstract bool ValueEquals(object ownerA, object ownerB);

    /// <summary>Copies the value from one instance to another (resources are shared, not cloned).</summary>
    public abstract void CopyValue(object source, object destination);

    internal abstract void Write(Utf8JsonWriter writer, object owner, SerializationContext context);

    internal abstract void Read(JsonElement element, object owner, DeserializationContext context);

    public override string ToString() => $"{Name}: {ValueType.Name}";
}

/// <summary>Typed accessors for an exported member of <typeparamref name="TOwner"/>.</summary>
public sealed class ExportPropertyInfo<TOwner, TValue> : ExportPropertyInfo where TOwner : class
{
    private readonly Func<TOwner, TValue> _getter;
    private readonly Action<TOwner, TValue> _setter;

    public ExportPropertyInfo(
        string name,
        Func<TOwner, TValue> getter,
        Action<TOwner, TValue> setter,
        ValueCodec<TValue> codec,
        ExportHints? hints = null,
        string? group = null)
        : base(name, typeof(TValue), hints, group)
    {
        _getter = getter ?? throw new ArgumentNullException(nameof(getter));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public ValueCodec<TValue> Codec { get; }

    public TValue Get(TOwner owner) => _getter(owner);

    public void Set(TOwner owner, TValue value) => _setter(owner, value);

    public override object? GetValue(object owner) => _getter((TOwner)owner);

    public override void SetValue(object owner, object? value) => _setter((TOwner)owner, (TValue)value!);

    public override bool ValueEquals(object ownerA, object ownerB) =>
        Codec.ValueEquals(_getter((TOwner)ownerA), _getter((TOwner)ownerB));

    public override void CopyValue(object source, object destination) => _setter((TOwner)destination, _getter((TOwner)source));

    internal override void Write(Utf8JsonWriter writer, object owner, SerializationContext context) =>
        Codec.Write(writer, _getter((TOwner)owner), context);

    internal override void Read(JsonElement element, object owner, DeserializationContext context) =>
        _setter((TOwner)owner, Codec.Read(element, context));
}

/// <summary>
/// A <see cref="SignalAttribute"/> event: its delegate type and parameters, and generated add/remove accessors,
/// so connections can be made by name without reflection on the event.
/// </summary>
public sealed class SignalInfo
{
    private readonly Action<object, Delegate> _add;
    private readonly Action<object, Delegate> _remove;
    private readonly Func<Action<object?[]>, Delegate>? _createForwarder;

    public SignalInfo(
        string name,
        Type delegateType,
        Type[] parameterTypes,
        Action<object, Delegate> add,
        Action<object, Delegate> remove,
        Func<Action<object?[]>, Delegate>? createForwarder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        DelegateType = delegateType ?? throw new ArgumentNullException(nameof(delegateType));
        ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
        _add = add ?? throw new ArgumentNullException(nameof(add));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        _createForwarder = createForwarder;
    }

    public string Name { get; }

    public Type DelegateType { get; }

    public IReadOnlyList<Type> ParameterTypes { get; }

    public NodeTypeInfo DeclaringType { get; internal set; } = null!;

    /// <summary>Subscribes <paramref name="handler"/> (of <see cref="DelegateType"/>) to the event on <paramref name="source"/>.</summary>
    public void Add(object source, Delegate handler) => _add(source, handler);

    public void Remove(object source, Delegate handler) => _remove(source, handler);

    /// <summary>
    /// A delegate of <see cref="DelegateType"/> that packs its arguments into an array for <paramref name="sink"/>
    /// (deferred and one-shot connections). Only void signals with up to 8 parameters support it.
    /// </summary>
    public Delegate CreateForwarder(Action<object?[]> sink) =>
        _createForwarder?.Invoke(sink)
        ?? throw new NotSupportedException($"Signal '{Name}' cannot be connected deferred or one-shot.");

    public override string ToString() => $"{Name}({string.Join(", ", ParameterTypes.Select(t => t.Name))})";
}

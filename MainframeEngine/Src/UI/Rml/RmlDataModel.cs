using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>
/// An RmlUi data model (<c>data-model="name"</c>): C# values, lists, structs and event callbacks bound by name for
/// <c>{{ value }}</c>, <c>data-for</c>, <c>data-value</c>, <c>data-event-*</c>, ... Create it with
/// <see cref="RmlContext.CreateDataModel"/> and bind everything <b>before</b> loading documents that use it. After a
/// bound value changes, call <see cref="Dirty"/> (or <see cref="DirtyAll"/>); views update on the next context update.
/// </summary>
/// <remarks>
/// <para>Typed and allocation-free per frame: getters are invoked through generic bindings (no boxing), strings are
/// encoded on the stack. Supported scalar types: <see cref="bool"/>, <see cref="int"/>, <see cref="uint"/>,
/// <see cref="long"/>, <see cref="float"/>, <see cref="double"/>, <see cref="string"/> and 32-bit enums.</para>
/// <para>Generator-friendly: the <c>Bind(name, owner, static o =&gt; o.Value, static (o, v) =&gt; o.Value = v)</c>
/// overloads take the owner as state, so generated code needs no closures.</para>
/// <para>Every binding holds a <see cref="GCHandle"/> that RmlUi releases exactly once — when the model is
/// disposed, its context destroyed or RmlUi shut down.</para>
/// </remarks>
public sealed unsafe class RmlDataModel : IDisposable
{
    private readonly RmlDataModelHandle _handle = new();
    private readonly RmlContext _context;

    internal RmlDataModel(RmlContext context, string name, nint native)
    {
        _context = context;
        Name = name;
        _handle.Attach(native);
    }

    public string Name { get; }

    public RmlContext Context => _context;

    /// <summary>False once disposed or removed with its context.</summary>
    public bool IsValid => !_handle.IsClosed && !_handle.IsInvalid;

    private nint Handle => _handle.Value;

    // ── Scalars ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Binds a scalar through a getter and an optional setter (two-way bindings such as <c>data-value</c>).</summary>
    public RmlDataModel Bind<T>(string name, Func<T> get, Action<T>? set = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        RmlValue<T>.EnsureSupported();
        return BindScalar(name, new FuncBinding<T>(get, set), set is not null);
    }

    /// <summary>Binds a scalar with the owner passed as state (static lambdas: no closures).</summary>
    public RmlDataModel Bind<TOwner, T>(string name, TOwner owner, Func<TOwner, T> get, Action<TOwner, T>? set = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        RmlValue<T>.EnsureSupported();
        return BindScalar(name, new StateBinding<TOwner, T>(owner, get, set), set is not null);
    }

    private RmlDataModel BindScalar(string name, ScalarBinding binding, bool writable)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var gc = GCHandle.Alloc(binding);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        int status;
        fixed (byte* p = n)
            status = RmlNative.DataModelBindFunc(Handle, p, &GetScalar, writable ? &SetScalar : null, &ReleaseBinding, GCHandle.ToIntPtr(gc));
        return Bound(status, gc, name);
    }

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Binds an event callback for <c>data-event-click="name"</c> and friends.</summary>
    public RmlDataModel Event(string name, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Event(name, new EventBinding(handler, null));
    }

    /// <summary>Binds an event callback receiving the event and its arguments (<c>data-event-click="buy(item.id, 2)"</c>).</summary>
    public RmlDataModel Event(string name, RmlDataEventCallback handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Event(name, new EventBinding(null, handler));
    }

    private RmlDataModel Event(string name, EventBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var gc = GCHandle.Alloc(binding);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        int status;
        fixed (byte* p = n)
            status = RmlNative.DataModelBindEventCallback(Handle, p, &OnDataEvent, &ReleaseBinding, GCHandle.ToIntPtr(gc));
        return Bound(status, gc, name);
    }

    // ── Lists and structs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds a list of scalars (<c>data-for="v : name"</c>). The list is read live: add/remove items and call
    /// <see cref="Dirty"/>. A writable <see cref="IList{T}"/> also supports two-way element bindings.
    /// </summary>
    public RmlDataModel BindList<T>(string name, IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        RmlValue<T>.EnsureSupported();
        return BindVariable(name, RmlNative.VariableArray, new ListBinding<T>(items, null));
    }

    /// <summary>Binds a list of objects whose members are described by <paramref name="type"/> (<c>{{ item.name }}</c>).</summary>
    public RmlDataModel BindList<T>(string name, IReadOnlyList<T> items, RmlStructType<T> type)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(type);
        return BindVariable(name, RmlNative.VariableArray, new ListBinding<T>(items, type));
    }

    /// <summary>Binds an object read through <paramref name="get"/>, its members described by <paramref name="type"/> (<c>{{ player.hp }}</c>).</summary>
    public RmlDataModel BindStruct<T>(string name, Func<T> get, RmlStructType<T> type)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(type);
        return BindVariable(name, RmlNative.VariableStruct, new StructBinding<T>(get, type));
    }

    private RmlDataModel BindVariable(string name, int rootKind, VariableBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var gc = GCHandle.Alloc(binding);
        var callbacks = new RmlNative.VariableCallbacks
        {
            StructSize = RmlNative.SizeOf<RmlNative.VariableCallbacks>(),
            UserData = GCHandle.ToIntPtr(gc),
            Get = &VarGet,
            Set = &VarSet,
            Size = &VarSize,
            Child = &VarChild,
            Release = &ReleaseBinding,
        };
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        int status;
        fixed (byte* p = n)
            status = RmlNative.DataModelBindVariable(Handle, p, rootKind, 0, &callbacks);
        return Bound(status, gc, name);
    }

    private RmlDataModel Bound(int status, GCHandle gc, string name)
    {
        if (status >= 0)
            return this;
        gc.Free(); // a failed bind never calls release
        throw new RmlException($"Binding '{name}' in data model '{Name}'", status);
    }

    // ── Dirtying ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Marks a bound name changed; its views update on the next context update. No allocation.</summary>
    public void Dirty(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* p = n)
            RmlException.ThrowIfFailed(RmlNative.DataModelDirtyVariable(Handle, p), "DataModelDirtyVariable");
    }

    /// <summary>Marks every binding changed.</summary>
    public void DirtyAll() => RmlException.ThrowIfFailed(RmlNative.DataModelDirtyAllVariables(Handle), "DataModelDirtyAllVariables");

    public bool IsDirty(ReadOnlySpan<char> name)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var n = new RmlUtf8Arg(name, scratch);
        fixed (byte* p = n)
            return RmlNative.DataModelIsVariableDirty(Handle, p) == 1;
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Removes the model from its context (bound elements lose the binding); bindings are released.</summary>
    public void Dispose()
    {
        if (!IsValid)
            return;
        _context.ForgetModel(this);
        _handle.ReleaseNow();
    }

    internal void MarkRemovedWithContext() => _handle.MarkDestroyedByLibrary();

    public override string ToString() => $"RmlDataModel '{Name}'";

    // ── Native callbacks ─────────────────────────────────────────────────────────────────────────────────────

    private static T Target<T>(nint user) where T : class => Unsafe.As<T>(GCHandle.FromIntPtr(user).Target!);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetScalar(nint user, nint variant)
    {
        try
        {
            Target<ScalarBinding>(user).Get(new RmlVariant(variant));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data binding getter");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetScalar(nint user, nint variant)
    {
        try
        {
            Target<ScalarBinding>(user).Set(new RmlVariant(variant));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data binding setter");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseBinding(nint user)
    {
        try
        {
            GCHandle.FromIntPtr(user).Free();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data binding release");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDataEvent(nint user, nint model, nint evt, nint* arguments, int count)
    {
        try
        {
            Target<EventBinding>(user).Invoke(new RmlDataEvent(evt, arguments, count));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data event");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VarGet(nint user, ulong node, nint variant)
    {
        try
        {
            return Target<VariableBinding>(user).Get(node, new RmlVariant(variant)) ? 1 : 0;
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data variable get");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VarSet(nint user, ulong node, nint variant)
    {
        try
        {
            return Target<VariableBinding>(user).Set(node, new RmlVariant(variant)) ? 1 : 0;
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data variable set");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VarSize(nint user, ulong node)
    {
        try
        {
            return Target<VariableBinding>(user).Size(node);
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data variable size");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VarChild(nint user, ulong node, int index, byte* name, ulong* child)
    {
        try
        {
            var kind = Target<VariableBinding>(user).Child(node, index, RmlUtf8.Span(name), out var token);
            *child = token;
            return kind;
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "data variable child");
            return -1;
        }
    }

    // ── Binding objects ──────────────────────────────────────────────────────────────────────────────────────

    private abstract class ScalarBinding
    {
        public abstract void Get(RmlVariant value);
        public abstract void Set(RmlVariant value);
    }

    private sealed class FuncBinding<T>(Func<T> get, Action<T>? set) : ScalarBinding
    {
        public override void Get(RmlVariant value) => RmlValue<T>.Write(value, get());

        public override void Set(RmlVariant value) => set?.Invoke(RmlValue<T>.Read(value));
    }

    private sealed class StateBinding<TOwner, T>(TOwner owner, Func<TOwner, T> get, Action<TOwner, T>? set) : ScalarBinding
    {
        public override void Get(RmlVariant value) => RmlValue<T>.Write(value, get(owner));

        public override void Set(RmlVariant value) => set?.Invoke(owner, RmlValue<T>.Read(value));
    }

    private sealed class EventBinding(Action? simple, RmlDataEventCallback? full)
    {
        public void Invoke(RmlDataEvent e)
        {
            if (full is not null)
                full(e);
            else
                simple?.Invoke();
        }
    }

    // Node tokens: bits 16..47 = element index + 1 (0: the root), bits 0..15 = member index + 1 (0: none).
    internal abstract class VariableBinding
    {
        public const int MemberBits = 16;
        public const ulong MemberMask = (1ul << MemberBits) - 1;

        public static ulong ElementToken(int index) => (ulong)(index + 1) << MemberBits;
        public static int ElementIndex(ulong token) => (int)(token >> MemberBits) - 1;
        public static int MemberIndex(ulong token) => (int)(token & MemberMask) - 1;

        public abstract bool Get(ulong node, RmlVariant value);
        public abstract bool Set(ulong node, RmlVariant value);
        public abstract int Size(ulong node);
        public abstract int Child(ulong node, int index, ReadOnlySpan<byte> name, out ulong child);
    }

    internal sealed class ListBinding<T>(IReadOnlyList<T> items, RmlStructType<T>? type) : VariableBinding
    {
        public override int Size(ulong node) => node == 0 ? items.Count : 0;

        public override int Child(ulong node, int index, ReadOnlySpan<byte> name, out ulong child)
        {
            child = 0;
            if (node == 0)
            {
                if (index < 0 || index >= items.Count)
                    return -1;
                child = ElementToken(index);
                return type is null ? RmlNative.VariableScalar : RmlNative.VariableStruct;
            }

            if (type is null || MemberIndex(node) >= 0 || index != -1)
                return -1;
            var member = type.IndexOf(name);
            if (member < 0)
                return -1;
            child = node | (uint)(member + 1);
            return RmlNative.VariableScalar;
        }

        public override bool Get(ulong node, RmlVariant value)
        {
            var i = ElementIndex(node);
            if (i < 0 || i >= items.Count)
                return false;
            var m = MemberIndex(node);
            if (m < 0)
            {
                if (type is not null)
                    return false;
                RmlValue<T>.Write(value, items[i]);
                return true;
            }

            return type is not null && type.Write(m, items[i], value);
        }

        public override bool Set(ulong node, RmlVariant value)
        {
            var i = ElementIndex(node);
            if (i < 0 || i >= items.Count)
                return false;
            var m = MemberIndex(node);
            if (m < 0)
            {
                if (type is not null || items is not IList<T> { IsReadOnly: false } list)
                    return false;
                list[i] = RmlValue<T>.Read(value);
                return true;
            }

            return type is not null && type.Read(m, items[i], value);
        }
    }

    internal sealed class StructBinding<T>(Func<T> get, RmlStructType<T> type) : VariableBinding
    {
        public override int Size(ulong node) => 0;

        public override int Child(ulong node, int index, ReadOnlySpan<byte> name, out ulong child)
        {
            child = 0;
            if (node != 0 || index != -1)
                return -1;
            var member = type.IndexOf(name);
            if (member < 0)
                return -1;
            child = (uint)(member + 1);
            return RmlNative.VariableScalar;
        }

        public override bool Get(ulong node, RmlVariant value)
        {
            var m = MemberIndex(node);
            return m >= 0 && ElementIndex(node) < 0 && type.Write(m, get(), value);
        }

        public override bool Set(ulong node, RmlVariant value)
        {
            var m = MemberIndex(node);
            return m >= 0 && ElementIndex(node) < 0 && type.Read(m, get(), value);
        }
    }
}

/// <summary>A data event: the RmlUi event and the expression's arguments, valid only during the callback.</summary>
public readonly unsafe ref struct RmlDataEvent
{
    private readonly nint* _arguments;

    internal RmlDataEvent(nint evt, nint* arguments, int count)
    {
        Event = new RmlEvent(evt);
        _arguments = arguments;
        ArgumentCount = arguments is null ? 0 : Math.Max(0, count);
    }

    public RmlEvent Event { get; }

    public int ArgumentCount { get; }

    /// <summary>Argument <paramref name="index"/> (numbers arrive as <see cref="RmlVariantType.Float"/>).</summary>
    public RmlVariant GetArgument(int index)
    {
        if ((uint)index >= (uint)ArgumentCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new RmlVariant(_arguments[index]);
    }
}

/// <summary>Handles a data event (<c>data-event-*</c>); the event is valid only during the call.</summary>
public delegate void RmlDataEventCallback(RmlDataEvent e);

/// <summary>
/// Describes the members of a bound object type for <see cref="RmlDataModel.BindList{T}(string, IReadOnlyList{T}, RmlStructType{T})"/>
/// and <see cref="RmlDataModel.BindStruct{T}"/>. Build it once (e.g. a static field) and share it:
/// <code>
/// static readonly RmlStructType&lt;Item&gt; ItemType = new RmlStructType&lt;Item&gt;()
///     .Member("name", static i =&gt; i.Name)
///     .Member("count", static i =&gt; i.Count, static (i, v) =&gt; i.Count = v);
/// </code>
/// Members are scalars (the types <see cref="RmlDataModel"/> supports). Setters on value-type items act on a copy.
/// </summary>
public sealed class RmlStructType<T>
{
    private readonly List<MemberInfo> _members = [];

    public int MemberCount => _members.Count;

    public RmlStructType<T> Member<TValue>(string name, Func<T, TValue> get, Action<T, TValue>? set = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(get);
        RmlValue<TValue>.EnsureSupported();
        var utf8 = Encoding.UTF8.GetBytes(name);
        foreach (var m in _members)
            if (m.Name.AsSpan().SequenceEqual(utf8))
                throw new ArgumentException($"Member '{name}' is already defined.", nameof(name));
        _members.Add(new TypedMember<TValue>(utf8, get, set));
        return this;
    }

    internal int IndexOf(ReadOnlySpan<byte> name)
    {
        for (var i = 0; i < _members.Count; i++)
            if (_members[i].Name.AsSpan().SequenceEqual(name))
                return i;
        return -1;
    }

    internal bool Write(int member, T item, RmlVariant value)
    {
        if ((uint)member >= (uint)_members.Count)
            return false;
        _members[member].Write(item, value);
        return true;
    }

    internal bool Read(int member, T item, RmlVariant value) =>
        (uint)member < (uint)_members.Count && _members[member].Read(item, value);

    private abstract class MemberInfo(byte[] name)
    {
        public byte[] Name { get; } = name;
        public abstract void Write(T item, RmlVariant value);
        public abstract bool Read(T item, RmlVariant value);
    }

    private sealed class TypedMember<TValue>(byte[] name, Func<T, TValue> get, Action<T, TValue>? set) : MemberInfo(name)
    {
        public override void Write(T item, RmlVariant value) => RmlValue<TValue>.Write(value, get(item));

        public override bool Read(T item, RmlVariant value)
        {
            if (set is null)
                return false;
            set(item, RmlValue<TValue>.Read(value));
            return true;
        }
    }
}

/// <summary>Allocation-free conversion between supported scalar types and RmlUi variants.</summary>
internal static class RmlValue<T>
{
    private static readonly bool Supported =
        typeof(T) == typeof(bool) || typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(long) ||
        typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(string) ||
        (typeof(T).IsEnum && Unsafe.SizeOf<T>() == 4);

    public static void EnsureSupported()
    {
        if (!Supported)
            throw new NotSupportedException(
                $"{typeof(T).Name} cannot be bound to RmlUi; use bool, int, uint, long, float, double, string or a 32-bit enum.");
    }

    public static void Write(RmlVariant variant, T value)
    {
        if (typeof(T) == typeof(bool)) variant.Set(Unsafe.As<T, bool>(ref value));
        else if (typeof(T) == typeof(int)) variant.Set(Unsafe.As<T, int>(ref value));
        else if (typeof(T) == typeof(uint)) variant.Set((long)Unsafe.As<T, uint>(ref value));
        else if (typeof(T) == typeof(long)) variant.Set(Unsafe.As<T, long>(ref value));
        else if (typeof(T) == typeof(float)) variant.Set(Unsafe.As<T, float>(ref value));
        else if (typeof(T) == typeof(double)) variant.Set(Unsafe.As<T, double>(ref value));
        else if (typeof(T) == typeof(string)) variant.Set(Unsafe.As<T, string?>(ref value));
        else if (typeof(T).IsEnum) variant.Set(Unsafe.As<T, int>(ref value));
        else variant.SetNone();
    }

    public static T Read(RmlVariant variant)
    {
        if (typeof(T) == typeof(bool)) { var v = variant.GetBool(); return Unsafe.As<bool, T>(ref v); }
        if (typeof(T) == typeof(int)) { var v = variant.GetInt32(); return Unsafe.As<int, T>(ref v); }
        if (typeof(T) == typeof(uint)) { var v = (uint)Math.Clamp(variant.GetInt64(), 0, uint.MaxValue); return Unsafe.As<uint, T>(ref v); }
        if (typeof(T) == typeof(long)) { var v = variant.GetInt64(); return Unsafe.As<long, T>(ref v); }
        if (typeof(T) == typeof(float)) { var v = variant.GetSingle(); return Unsafe.As<float, T>(ref v); }
        if (typeof(T) == typeof(double)) { var v = variant.GetDouble(); return Unsafe.As<double, T>(ref v); }
        if (typeof(T) == typeof(string)) { var v = variant.GetString(); return Unsafe.As<string, T>(ref v); }
        if (typeof(T).IsEnum) { var v = variant.GetInt32(); return Unsafe.As<int, T>(ref v); }
        return default!;
    }
}

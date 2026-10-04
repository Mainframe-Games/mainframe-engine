using System.Text;

namespace MainframeEngine.Localization;

/// <summary>
/// Format arguments for <see cref="Tr"/>'s format-safe path. Each arity is a struct, so the generic formatter is
/// specialised per call shape and value-type arguments are never boxed (<see cref="string.Format{TArg0}(IFormatProvider, CompositeFormat, TArg0)"/>).
/// </summary>
internal interface IFormatArgs
{
    int Count { get; }

    string Format(IFormatProvider provider, CompositeFormat format);
}

internal readonly struct Args1<T0>(T0 arg0) : IFormatArgs
{
    public int Count => 1;

    public string Format(IFormatProvider provider, CompositeFormat format) => string.Format(provider, format, arg0);
}

internal readonly struct Args2<T0, T1>(T0 arg0, T1 arg1) : IFormatArgs
{
    public int Count => 2;

    public string Format(IFormatProvider provider, CompositeFormat format) => string.Format(provider, format, arg0, arg1);
}

internal readonly struct Args3<T0, T1, T2>(T0 arg0, T1 arg1, T2 arg2) : IFormatArgs
{
    public int Count => 3;

    public string Format(IFormatProvider provider, CompositeFormat format) => string.Format(provider, format, arg0, arg1, arg2);
}

internal readonly ref struct ArgsSpan(ReadOnlySpan<object?> args) : IFormatArgs
{
    private readonly ReadOnlySpan<object?> _args = args;

    public int Count => _args.Length;

    public string Format(IFormatProvider provider, CompositeFormat format) => string.Format(provider, format, _args);
}

/// <summary>Arguments for a plural message: <c>n</c> is always <c>{0}</c>, the caller's arguments follow.</summary>
internal readonly ref struct PluralArgsSpan(long n, ReadOnlySpan<object?> args) : IFormatArgs
{
    private readonly ReadOnlySpan<object?> _args = args;

    public int Count => _args.Length + 1;

    public string Format(IFormatProvider provider, CompositeFormat format)
    {
        var all = new object?[_args.Length + 1];
        all[0] = n;
        _args.CopyTo(all.AsSpan(1));
        return string.Format(provider, format, all);
    }
}

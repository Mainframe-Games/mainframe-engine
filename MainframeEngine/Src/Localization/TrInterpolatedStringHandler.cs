using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MainframeEngine.Localization;

/// <summary>
/// Lets <see cref="Tr"/> take interpolated strings: <c>Tr._($"Score: {score}")</c> looks up the msgid
/// <c>"Score: {0}"</c> — exactly what the GetText.NET extractor writes for that call — and formats the
/// translation with the captured values. Literal braces are re-escaped (<c>{{</c>), holes keep their alignment and
/// format (<c>{0,5:N2}</c>).
/// </summary>
/// <remarks>
/// Convenient but not allocation-free (value-type holes are boxed and the result is a new string). Hot paths use
/// <c>Tr._("Score: {0}", score)</c>, which formats without boxing, or cache the result.
/// </remarks>
[InterpolatedStringHandler]
public ref struct TrInterpolatedStringHandler
{
    private char[] _chars;
    private int _length;
    private object?[]? _args;
    private int _argCount;

    public TrInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _chars = ArrayPool<char>.Shared.Rent(Math.Max(32, literalLength + 8 + formattedCount * 6));
        _args = formattedCount > 0 ? ArrayPool<object?>.Shared.Rent(formattedCount) : null;
    }

    /// <summary>The composite format (the msgid) built so far.</summary>
    internal readonly ReadOnlySpan<char> Format => _chars.AsSpan(0, _length);

    /// <summary>The values of the holes, in order.</summary>
    internal readonly ReadOnlySpan<object?> Arguments => _args is null ? default : _args.AsSpan(0, _argCount);

    public void AppendLiteral(string value)
    {
        foreach (var c in value)
        {
            if (c is '{' or '}')
                Append(c);
            Append(c);
        }
    }

    public void AppendFormatted<T>(T value) => AppendHole(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendHole(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment) => AppendHole(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string? format) => AppendHole(value, alignment, format);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) =>
        AppendHole(value.ToString(), alignment, format);

    private void AppendHole(object? value, int alignment, string? format)
    {
        if (_args is null || _argCount == _args.Length)
        {
            var grown = ArrayPool<object?>.Shared.Rent(Math.Max(4, _argCount * 2));
            if (_args is not null)
            {
                _args.AsSpan(0, _argCount).CopyTo(grown);
                Array.Clear(_args);
                ArrayPool<object?>.Shared.Return(_args);
            }

            _args = grown;
        }

        var index = _argCount;
        _args[_argCount++] = value;

        Append('{');
        AppendNumber(index);
        if (alignment != 0)
        {
            Append(',');
            AppendNumber(alignment);
        }

        if (!string.IsNullOrEmpty(format))
        {
            Append(':');
            foreach (var c in format)
                Append(c);
        }

        Append('}');
    }

    private void AppendNumber(int value)
    {
        Span<char> digits = stackalloc char[12];
        value.TryFormat(digits, out var written, provider: CultureInfo.InvariantCulture);
        foreach (var c in digits[..written])
            Append(c);
    }

    private void Append(char c)
    {
        if (_length == _chars.Length)
        {
            var grown = ArrayPool<char>.Shared.Rent(_chars.Length * 2);
            _chars.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<char>.Shared.Return(_chars);
            _chars = grown;
        }

        _chars[_length++] = c;
    }

    /// <summary>Returns the pooled buffers. Called by <see cref="Tr"/> once the message is formatted.</summary>
    internal void Release()
    {
        if (_chars is not null)
        {
            ArrayPool<char>.Shared.Return(_chars);
            _chars = null!;
        }

        if (_args is not null)
        {
            Array.Clear(_args, 0, _argCount);
            ArrayPool<object?>.Shared.Return(_args);
            _args = null;
        }

        _length = 0;
        _argCount = 0;
    }
}

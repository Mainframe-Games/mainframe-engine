using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>
/// A NUL-terminated UTF-8 copy of a string argument for one native call. Short strings are encoded into a
/// caller-provided stack buffer (<see cref="StackSize"/> bytes), longer ones into a pooled array, so string arguments
/// never allocate:
/// <code>
/// Span&lt;byte&gt; scratch = stackalloc byte[RmlUtf8Arg.StackSize];
/// using var id = new RmlUtf8Arg(elementId, scratch);
/// fixed (byte* p = id) RmlNative.ElementGetElementById(element, p);
/// </code>
/// A null string pins as a null pointer.
/// </summary>
internal ref struct RmlUtf8Arg
{
    /// <summary>Stack buffer size callers should provide (strings up to ~85 characters of any script fit).</summary>
    public const int StackSize = 256;

    private byte[]? _rented;
    private readonly Span<byte> _bytes; // includes the NUL terminator; empty for a null string

    public RmlUtf8Arg(string? text, Span<byte> scratch) : this(text is null ? default : text.AsSpan(), text is null, scratch)
    {
    }

    public RmlUtf8Arg(ReadOnlySpan<char> text, Span<byte> scratch) : this(text, false, scratch)
    {
    }

    private RmlUtf8Arg(ReadOnlySpan<char> text, bool isNull, Span<byte> scratch)
    {
        _rented = null;
        if (isNull)
        {
            _bytes = default;
            return;
        }

        var max = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
        Span<byte> target;
        if (max <= scratch.Length)
        {
            target = scratch;
        }
        else
        {
            // GetMaxByteCount is pessimistic; size exactly before renting a large buffer.
            var exact = Encoding.UTF8.GetByteCount(text) + 1;
            if (exact <= scratch.Length)
            {
                target = scratch;
            }
            else
            {
                _rented = ArrayPool<byte>.Shared.Rent(exact);
                target = _rented;
            }
        }

        var written = Encoding.UTF8.GetBytes(text, target);
        target[written] = 0;
        _bytes = target[..(written + 1)];
    }

    /// <summary>Encoded length in bytes, excluding the terminator.</summary>
    public readonly int Length => _bytes.IsEmpty ? 0 : _bytes.Length - 1;

    /// <summary>The bytes without the terminator.</summary>
    public readonly ReadOnlySpan<byte> Bytes => _bytes.IsEmpty ? default : _bytes[..^1];

    /// <summary>Pins the bytes (null pointer for a null string).</summary>
    public readonly ref byte GetPinnableReference() => ref MemoryMarshal.GetReference(_bytes);

    public void Dispose()
    {
        if (_rented is null)
            return;
        ArrayPool<byte>.Shared.Return(_rented);
        _rented = null;
    }
}

/// <summary>UTF-8 helpers for the native boundary: NUL-terminated input, caller-buffer string output.</summary>
internal static unsafe class RmlUtf8
{
    /// <summary>Length of a NUL-terminated UTF-8 string.</summary>
    public static int StrLen(byte* utf8) => utf8 is null ? 0 : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(utf8).Length;

    /// <summary>A NUL-terminated UTF-8 string as a span (empty for null).</summary>
    public static ReadOnlySpan<byte> Span(byte* utf8) =>
        utf8 is null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(utf8);

    /// <summary>Decodes a NUL-terminated UTF-8 string (allocates; null for a null pointer).</summary>
    public static string? ToString(byte* utf8) => utf8 is null ? null : Encoding.UTF8.GetString(Span(utf8));

    /// <summary>
    /// Reads a string through the caller-buffer pattern (<c>fn(handle, buffer, capacity)</c> returns the full length
    /// or a negative error). Returns null on error. Allocates the result string only.
    /// </summary>
    public static string? Read(nint handle, delegate*<nint, byte*, int, int> fn)
    {
        const int stack = 256;
        var buffer = stackalloc byte[stack];
        var length = fn(handle, buffer, stack);
        if (length < 0)
            return null;
        if (length < stack)
            return Encoding.UTF8.GetString(buffer, length);

        var rented = ArrayPool<byte>.Shared.Rent(length + 1);
        try
        {
            fixed (byte* p = rented)
            {
                length = fn(handle, p, length + 1);
                return length < 0 ? null : Encoding.UTF8.GetString(p, Math.Min(length, rented.Length - 1));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads a string through the caller-buffer pattern into <paramref name="destination"/> without allocating.
    /// Returns the number of chars written, or -1 if the call failed or the text does not fit.
    /// </summary>
    public static int Read(nint handle, delegate*<nint, byte*, int, int> fn, Span<char> destination)
    {
        const int stack = 512;
        var buffer = stackalloc byte[stack];
        var length = fn(handle, buffer, stack);
        if (length < 0 || length >= stack)
            return -1;
        var bytes = new ReadOnlySpan<byte>(buffer, length);
        if (Encoding.UTF8.GetCharCount(bytes) > destination.Length)
            return -1;
        return Encoding.UTF8.GetChars(bytes, destination);
    }

    /// <summary>Compares a NUL-terminated UTF-8 string with ASCII/UTF-8 bytes (e.g. a <c>"blur"u8</c> literal).</summary>
    public static bool Equals(byte* utf8, ReadOnlySpan<byte> other) => Span(utf8).SequenceEqual(other);
}

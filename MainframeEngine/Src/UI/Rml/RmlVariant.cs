using System.Numerics;
using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>Value category of an <see cref="RmlVariant"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the RmlUi variant categories.")]
public enum RmlVariantType
{
    None = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    String = 4,
    Vector2 = 5,
    Vector3 = 6,
    Vector4 = 7,
    Colourf = 8,
    Colourb = 9,
    ColorStopList = 10,
    Other = 255,
}

/// <summary>
/// A borrowed <c>Rml::Variant</c> (data binding values, event parameters, filter/shader parameters). Valid only inside
/// the callback that received it — it is a ref struct so it cannot escape. Getters convert like RmlUi does
/// (numbers ↔ strings, bools); a failed conversion returns the fallback.
/// </summary>
public readonly unsafe ref struct RmlVariant
{
    internal RmlVariant(nint handle) => Handle = handle;

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;

    public RmlVariantType Type
    {
        get
        {
            var t = RmlNative.VariantGetType(Handle);
            return t < 0 ? RmlVariantType.None : (RmlVariantType)t;
        }
    }

    public bool GetBool(bool fallback = false)
    {
        int value;
        return RmlNative.VariantGetBool(Handle, &value) == RmlNative.Ok ? value != 0 : fallback;
    }

    public long GetInt64(long fallback = 0)
    {
        long value;
        return RmlNative.VariantGetInt64(Handle, &value) == RmlNative.Ok ? value : fallback;
    }

    public int GetInt32(int fallback = 0) => (int)Math.Clamp(GetInt64(fallback), int.MinValue, int.MaxValue);

    public double GetDouble(double fallback = 0)
    {
        double value;
        return RmlNative.VariantGetDouble(Handle, &value) == RmlNative.Ok ? value : fallback;
    }

    public float GetSingle(float fallback = 0) => (float)GetDouble(fallback);

    /// <summary>The value as text (allocates the string).</summary>
    public string GetString(string fallback = "") => RmlUtf8.Read(Handle, &ReadString) ?? fallback;

    /// <summary>The value as text into <paramref name="destination"/> (no allocation); -1 if it does not fit or fails.</summary>
    public int GetString(Span<char> destination) => RmlUtf8.Read(Handle, &ReadString, destination);

    private static int ReadString(nint h, byte* buffer, int capacity) => RmlNative.VariantGetString(h, buffer, capacity);

    /// <summary>Vector2/3/4 and colours as four floats (unused components 0; Colourb as 0..255).</summary>
    public Vector4 GetVector4(Vector4 fallback = default)
    {
        var v = stackalloc float[4];
        return RmlNative.VariantGetFloat4(Handle, v) == RmlNative.Ok ? new Vector4(v[0], v[1], v[2], v[3]) : fallback;
    }

    /// <summary>A straight-alpha (not premultiplied) RGBA8 colour, packed R | G&lt;&lt;8 | B&lt;&lt;16 | A&lt;&lt;24.</summary>
    public uint GetColourb(uint fallback = 0)
    {
        uint rgba;
        return RmlNative.VariantGetColourb(Handle, (byte*)&rgba) == RmlNative.Ok ? rgba : fallback;
    }

    /// <summary>Gradient stops into <paramref name="destination"/>; returns the total count (may exceed the span).</summary>
    public int GetColorStops(Span<RmlColorStop> destination)
    {
        fixed (RmlColorStop* p = destination)
        {
            var count = RmlNative.VariantGetColorStops(Handle, p, destination.Length);
            return count < 0 ? 0 : count;
        }
    }

    public void SetNone() => RmlException.ThrowIfFailed(RmlNative.VariantSetNone(Handle), "VariantSetNone");

    public void Set(bool value) => RmlException.ThrowIfFailed(RmlNative.VariantSetBool(Handle, value ? 1 : 0), "VariantSetBool");

    public void Set(long value) => RmlException.ThrowIfFailed(RmlNative.VariantSetInt64(Handle, value), "VariantSetInt64");

    public void Set(int value) => RmlException.ThrowIfFailed(RmlNative.VariantSetInt64(Handle, value), "VariantSetInt64");

    public void Set(double value) => RmlException.ThrowIfFailed(RmlNative.VariantSetDouble(Handle, value), "VariantSetDouble");

    public void Set(float value) => RmlException.ThrowIfFailed(RmlNative.VariantSetDouble(Handle, value), "VariantSetDouble");

    /// <summary>Sets text without allocating (UTF-8 encoded on the stack or into a pooled buffer).</summary>
    public void Set(ReadOnlySpan<char> value)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var arg = new RmlUtf8Arg(value, scratch);
        fixed (byte* p = arg)
            RmlException.ThrowIfFailed(RmlNative.VariantSetString(Handle, p, arg.Length), "VariantSetString");
    }

    public void Set(string? value)
    {
        if (value is null)
            SetNone();
        else
            Set(value.AsSpan());
    }

    /// <summary>Sets UTF-8 text.</summary>
    public void SetUtf8(ReadOnlySpan<byte> value)
    {
        fixed (byte* p = value)
            RmlException.ThrowIfFailed(RmlNative.VariantSetString(Handle, p, value.Length), "VariantSetString");
    }

    public override string ToString() => IsNull ? "<null>" : $"{Type}: {GetString()}";
}

/// <summary>A borrowed <c>Rml::Dictionary</c> (event, filter and shader parameters), valid only inside its callback.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Mirrors Rml::Dictionary.")]
public readonly unsafe ref struct RmlDictionary
{
    internal RmlDictionary(nint handle) => Handle = handle;

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;

    public int Count => IsNull ? 0 : Math.Max(0, RmlNative.DictionaryGetCount(Handle));

    /// <summary>The value for <paramref name="key"/>, or a null variant.</summary>
    public RmlVariant this[string key] => Find(key.AsSpan());

    /// <summary>Looks up a key without allocating.</summary>
    public RmlVariant Find(ReadOnlySpan<char> key)
    {
        if (IsNull)
            return default;
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var k = new RmlUtf8Arg(key, scratch);
        fixed (byte* p = k)
            return new RmlVariant(RmlNative.DictionaryFind(Handle, p));
    }

    /// <summary>Looks up a UTF-8 key (e.g. <c>"value"u8</c>; must be NUL-free) without allocating.</summary>
    public RmlVariant FindUtf8(ReadOnlySpan<byte> key)
    {
        if (IsNull)
            return default;
        Span<byte> z = stackalloc byte[key.Length + 1];
        key.CopyTo(z);
        z[^1] = 0;
        fixed (byte* p = z)
            return new RmlVariant(RmlNative.DictionaryFind(Handle, p));
    }

    public bool TryGetValue(string key, out RmlVariant value)
    {
        value = Find(key.AsSpan());
        return !value.IsNull;
    }

    /// <summary>The key of entry <paramref name="index"/> (unspecified order; allocates).</summary>
    public string GetKey(int index)
    {
        var buffer = stackalloc byte[256];
        var length = RmlNative.DictionaryGetKey(Handle, index, buffer, 256);
        return length switch
        {
            < 0 => "",
            < 256 => Encoding.UTF8.GetString(buffer, length),
            _ => ReadLongKey(index, length),
        };
    }

    /// <summary>The value of entry <paramref name="index"/>.</summary>
    public RmlVariant GetValue(int index) => new(RmlNative.DictionaryGetValue(Handle, index));

    private string ReadLongKey(int index, int length)
    {
        var bytes = new byte[length + 1];
        fixed (byte* p = bytes)
            RmlException.ThrowIfFailed(RmlNative.DictionaryGetKey(Handle, index, p, bytes.Length), "DictionaryGetKey");
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}

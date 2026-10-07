using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The editor ↔ <c>mfplughost</c> control protocol (mirrors <c>Native/PluginHost/src/protocol.hpp</c>; ADR 0146). One
/// frame per message, little-endian: <c>u32 payloadLength | u16 type | u16 flags | u32 requestId | payload</c>. A reply
/// carries its request's id and <c>type | 0x8000</c>, or <see cref="PluginHostMessage.Error"/> (<c>u32 code, str message</c>).
/// Strings are <c>u32</c> byte length + UTF-8. Types 0x0100–0x01FF are reserved for plugins, 0x0200–0x02FF for MIDI.
/// </summary>
public static class PluginHostProtocol
{
    /// <summary>The protocol version this editor speaks; the handshake fails on any other.</summary>
    public const uint Version = 1;

    public const int HeaderSize = 12;
    public const ushort ReplyBit = 0x8000;
    public const int MaxPayload = 64 * 1024 * 1024;

    public static void WriteHeader(Span<byte> header, int payloadLength, ushort type, uint id)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], id);
    }

    public static (int PayloadLength, ushort Type, uint Id) ReadHeader(ReadOnlySpan<byte> header) =>
        ((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header), int.MaxValue), BinaryPrimitives.ReadUInt16LittleEndian(header[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
}

/// <summary>Message types (requests; replies are <c>type | 0x8000</c>).</summary>
public enum PluginHostMessage : ushort
{
    /// <summary><c>u32 protocolVersion, str client</c> → <c>u32 protocolVersion, str helperVersion, u32 capabilities, u32 pid</c>.</summary>
    Hello = 0x0001,

    /// <summary>Any bytes → the same bytes.</summary>
    Ping = 0x0002,

    /// <summary>→ empty; the helper then exits.</summary>
    Shutdown = 0x0003,

    /// <summary><c>u32 milliseconds</c> → empty after that long (diagnostics: timeout tests).</summary>
    Sleep = 0x0004,

    /// <summary><c>str wavPath, str oggPath, f32 quality</c> → <c>u64 frames, u32 sampleRate, u16 channels</c>.</summary>
    Encode = 0x0010,

    /// <summary><c>str path, u64 size</c> → empty: maps the shared memory file (ADR 0147).</summary>
    PluginSetupShm = 0x0100,

    /// <summary><c>str bundlePath</c> → <c>str json</c> (as <c>mfplughost --scan</c>).</summary>
    PluginScan = 0x0101,

    /// <summary><c>str bundle, str classId, u32 slot, u32 rate, u32 maxBlock</c> → <c>u32 id, u32 latency, u16 in, u16 out, u32 flags, str name</c>.</summary>
    PluginLoad = 0x0102,

    PluginUnload = 0x0103,
    PluginGetState = 0x0104,
    PluginSetState = 0x0105,
    PluginLatency = 0x0106,
    PluginSetOffline = 0x0107,
    PluginOpenEditor = 0x0108,
    PluginCloseEditor = 0x0109,

    /// <summary><c>u32 frames, u32 flags, f64 tempo, i64 frame, u32 count, count × (u32 id, u32 inputSlot)</c> → empty.</summary>
    PluginProcess = 0x010A,

    /// <summary>Helper → editor notification (request id 0): <c>u32 id</c>, the user closed that instance's editor window.</summary>
    PluginEditorClosed = 0x010B,

    /// <summary>Reply only: <c>u32 code, str message</c>.</summary>
    Error = 0xFFFF,
}

/// <summary>Builds a payload.</summary>
public sealed class PluginHostPayloadWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public PluginHostPayloadWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
        return this;
    }

    public PluginHostPayloadWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    public PluginHostPayloadWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
        return this;
    }

    public PluginHostPayloadWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    public PluginHostPayloadWriter Str(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        U32((uint)bytes.Length);
        _buffer.Write(bytes);
        return this;
    }

    public PluginHostPayloadWriter Bytes(ReadOnlySpan<byte> value)
    {
        _buffer.Write(value);
        return this;
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}

/// <summary>Reads a payload; a short payload throws <see cref="PluginHostException"/>.</summary>
public ref struct PluginHostPayloadReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public string Str() => Encoding.UTF8.GetString(Take(checked((int)U32())));

    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    public readonly int Remaining => _data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (_data.Length < count)
            throw new PluginHostException("The plugin host sent a malformed reply.");
        var taken = _data[..count];
        _data = _data[count..];
        return taken;
    }
}

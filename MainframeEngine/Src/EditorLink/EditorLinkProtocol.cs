using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MainframeEngine.EditorLink;

/// <summary>Kinds of frames on the editor link.</summary>
public enum EditorLinkMessageType : byte
{
    /// <summary>Local only (<see cref="EditorLinkServer"/>): a game connected. Never sent on the wire.</summary>
    Connected = 0,

    /// <summary>Game → editor, first frame of every connection: <see cref="EditorLinkHello"/>.</summary>
    Hello = 1,

    /// <summary>Game → editor: one <see cref="LogEntry"/>.</summary>
    Log = 2,

    /// <summary>Game → editor: <see cref="EditorLinkStatus"/> (sent periodically and on change).</summary>
    Status = 3,

    /// <summary>Game → editor: log entries were dropped because the link could not keep up (count).</summary>
    LogDropped = 4,

    /// <summary>Game → editor: the game is exiting (exit code).</summary>
    Goodbye = 5,

    /// <summary>Game → editor: a snapshot of the running scene tree (<see cref="EditorLinkMessage.Tree"/>), on request.</summary>
    Tree = 6,

    /// <summary>
    /// Editor → game, first frame of every connection: the editor's protocol version. The game sends logs only after it,
    /// because a completed TCP handshake does not prove an editor is reading (see <see cref="EditorLinkClient"/>).
    /// </summary>
    Welcome = 7,

    /// <summary>Editor → game: an <see cref="EditorLinkCommand"/>.</summary>
    Command = 16,

    /// <summary>Local only (<see cref="EditorLinkServer"/>): the game's connection closed. Never sent on the wire.</summary>
    Disconnected = 255,
}

/// <summary>What the editor asks a running game to do.</summary>
public enum EditorCommandKind : byte
{
    /// <summary>Quit (exit code 0).</summary>
    Stop = 1,

    /// <summary>Pause the scene tree (<see cref="SceneTree.Paused"/>).</summary>
    Pause = 2,

    /// <summary>Unpause the scene tree.</summary>
    Resume = 3,

    /// <summary>Re-read the current scene (or <see cref="EditorLinkCommand.Argument"/>, a scene path or UID) from disk and restart it.</summary>
    ReloadScene = 4,

    /// <summary>Ask for an immediate <see cref="EditorLinkMessageType.Status"/>.</summary>
    Ping = 5,

    /// <summary>Ask for a <see cref="EditorLinkMessageType.Tree"/> snapshot (the editor's remote scene tree).</summary>
    RequestTree = 6,
}

/// <summary>One node of a remote tree snapshot, depth-first: its depth below the root, name and type name.</summary>
public readonly record struct EditorLinkTreeNode(int Depth, string Name, string Type);

/// <summary>The game's run state in <see cref="EditorLinkStatus"/>.</summary>
public enum GameRunState : byte
{
    Starting = 0,
    Running = 1,
    Paused = 2,
    Stopping = 3,
}

/// <summary>The first frame a game sends.</summary>
public readonly record struct EditorLinkHello(int ProtocolVersion, int ProcessId, string ProjectName, string EngineVersion);

/// <summary>A game's periodic status report.</summary>
public readonly record struct EditorLinkStatus(GameRunState State, ulong Frame, float FramesPerSecond, string ScenePath);

/// <summary>A command from the editor; <see cref="Argument"/> is command-specific (empty when unused).</summary>
public readonly record struct EditorLinkCommand(EditorCommandKind Kind, string Argument = "");

/// <summary>A decoded frame (only the member matching <see cref="Type"/> is meaningful).</summary>
public readonly record struct EditorLinkMessage
{
    public EditorLinkMessageType Type { get; init; }

    /// <summary>The connection it came from (<see cref="EditorLinkServer"/> numbers games from 1; 0 when decoded directly).</summary>
    public int GameId { get; init; }

    public EditorLinkHello Hello { get; init; }

    /// <summary><see cref="EditorLinkMessageType.Welcome"/>: the editor's protocol version.</summary>
    public int EditorProtocolVersion { get; init; }

    public LogEntry Log { get; init; }

    public EditorLinkStatus Status { get; init; }

    /// <summary><see cref="EditorLinkMessageType.LogDropped"/>: entries dropped since the last report.</summary>
    public long DroppedCount { get; init; }

    /// <summary><see cref="EditorLinkMessageType.Goodbye"/>: the game's exit code.</summary>
    public int ExitCode { get; init; }

    public EditorLinkCommand Command { get; init; }

    /// <summary><see cref="EditorLinkMessageType.Tree"/>: the nodes, depth-first from the root.</summary>
    public EditorLinkTreeNode[]? Tree { get; init; }

    /// <summary><see cref="EditorLinkMessageType.Tree"/>: more nodes existed than <see cref="EditorLinkProtocol.MaxTreeNodes"/>.</summary>
    public bool TreeTruncated { get; init; }
}

/// <summary>
/// The editor link's wire format: little-endian frames of <c>u32 length</c> + <c>u8 type</c> + payload (length counts
/// the type byte and the payload, at most <see cref="MaxFrameLength"/>). Strings are <c>u32 byte count</c> + UTF-8.
/// <list type="bullet">
/// <item>Hello: <c>i32 protocol, i32 pid, str project, str engineVersion</c></item>
/// <item>Log: <c>u8 level, i64 utcTicks, str category, str message, str member, str file, i32 line</c></item>
/// <item>Status: <c>u8 state, u64 frame, f32 fps, str scene</c></item>
/// <item>LogDropped: <c>i64 count</c> · Goodbye: <c>i32 exitCode</c> · Command: <c>u8 kind, str argument</c></item>
/// <item>Tree: <c>u8 truncated, i32 count, count × (i32 depth, str name, str type)</c></item>
/// <item>Welcome: <c>i32 protocol</c></item>
/// </list>
/// </summary>
public static class EditorLinkProtocol
{
    /// <summary>Bumped on any incompatible wire change; the editor checks <see cref="EditorLinkHello.ProtocolVersion"/>.</summary>
    public const int Version = 3;   // 2: Tree / RequestTree · 3: Welcome

    /// <summary>Nodes in one tree snapshot at most (names are cut to <see cref="MaxTreeNameLength"/>), keeping a frame under the limit.</summary>
    public const int MaxTreeNodes = 3000; // × (12 + 2 × 48 chars × 3 UTF-8 bytes) < MaxFrameLength

    /// <summary>Characters kept of a node's name or type in a snapshot.</summary>
    public const int MaxTreeNameLength = 48;

    /// <summary>Largest frame accepted (type byte + payload). Longer log messages are truncated when encoded.</summary>
    public const int MaxFrameLength = 1 << 20;

    /// <summary>Size of the length prefix.</summary>
    public const int HeaderLength = 4;

    private const int MaxStringBytes = 256 * 1024;

    public static void WriteHello(ArrayBufferWriter<byte> output, in EditorLinkHello hello)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Hello);
        w.Int32(hello.ProtocolVersion);
        w.Int32(hello.ProcessId);
        w.String(hello.ProjectName);
        w.String(hello.EngineVersion);
        w.Finish();
    }

    public static void WriteWelcome(ArrayBufferWriter<byte> output, int protocolVersion)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Welcome);
        w.Int32(protocolVersion);
        w.Finish();
    }

    public static void WriteLog(ArrayBufferWriter<byte> output, in LogEntry entry)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Log);
        w.Byte((byte)WireLevel(entry.Level));
        w.Int64(entry.Timestamp.ToUniversalTime().Ticks);
        w.String(entry.Category);
        w.String(entry.Message);
        w.String(entry.CallerMember);
        w.String(entry.CallerFile);
        w.Int32(entry.CallerLine);
        w.Finish();
    }

    public static void WriteStatus(ArrayBufferWriter<byte> output, in EditorLinkStatus status)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Status);
        w.Byte((byte)status.State);
        w.UInt64(status.Frame);
        w.Single(status.FramesPerSecond);
        w.String(status.ScenePath);
        w.Finish();
    }

    public static void WriteLogDropped(ArrayBufferWriter<byte> output, long count)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.LogDropped);
        w.Int64(count);
        w.Finish();
    }

    public static void WriteGoodbye(ArrayBufferWriter<byte> output, int exitCode)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Goodbye);
        w.Int32(exitCode);
        w.Finish();
    }

    public static void WriteTree(ArrayBufferWriter<byte> output, IReadOnlyList<EditorLinkTreeNode> nodes, bool truncated)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Tree);
        var count = Math.Min(nodes.Count, MaxTreeNodes);
        w.Byte(truncated || nodes.Count > MaxTreeNodes ? (byte)1 : (byte)0);
        w.Int32(count);
        for (var i = 0; i < count; i++)
        {
            w.Int32(nodes[i].Depth);
            w.String(Cut(nodes[i].Name));
            w.String(Cut(nodes[i].Type));
        }

        w.Finish();

        static string Cut(string text) => text.Length <= MaxTreeNameLength ? text : text[..MaxTreeNameLength];
    }

    public static void WriteCommand(ArrayBufferWriter<byte> output, in EditorLinkCommand command)
    {
        var w = new FrameWriter(output, EditorLinkMessageType.Command);
        w.Byte((byte)command.Kind);
        w.String(command.Argument);
        w.Finish();
    }

    /// <summary>
    /// Reads the frame length from a 4-byte header; false when it is outside 1..<see cref="MaxFrameLength"/> (a corrupt
    /// or hostile stream: close the connection).
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < HeaderLength)
            return false;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (value is 0 or > MaxFrameLength)
            return false;
        length = (int)value;
        return true;
    }

    /// <summary>Decodes one frame body (type byte + payload, without the length prefix); false when malformed.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, out EditorLinkMessage message)
    {
        message = default;
        if (body.IsEmpty)
            return false;
        var r = new FrameReader(body[1..]);
        var type = (EditorLinkMessageType)body[0];
        switch (type)
        {
            case EditorLinkMessageType.Hello:
                if (!r.Int32(out var protocol) || !r.Int32(out var pid) || !r.String(out var project) || !r.String(out var version))
                    return false;
                message = new EditorLinkMessage { Type = type, Hello = new EditorLinkHello(protocol, pid, project, version) };
                break;
            case EditorLinkMessageType.Welcome:
                if (!r.Int32(out var editorProtocol))
                    return false;
                message = new EditorLinkMessage { Type = type, EditorProtocolVersion = editorProtocol };
                break;
            case EditorLinkMessageType.Log:
                if (!r.Byte(out var level) || !r.Int64(out var ticks) || !r.String(out var category) || !r.String(out var text)
                    || !r.String(out var member) || !r.String(out var file) || !r.Int32(out var line))
                    return false;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !IsSingleLevel(level))
                    return false;
                message = new EditorLinkMessage
                {
                    Type = type,
                    Log = new LogEntry((Log.Level)level, new DateTime(ticks, DateTimeKind.Utc), category, text, member, file, line),
                };
                break;
            case EditorLinkMessageType.Status:
                if (!r.Byte(out var state) || !r.UInt64(out var frame) || !r.Single(out var fps) || !r.String(out var scene))
                    return false;
                if (state > (byte)GameRunState.Stopping)
                    return false;
                message = new EditorLinkMessage { Type = type, Status = new EditorLinkStatus((GameRunState)state, frame, fps, scene) };
                break;
            case EditorLinkMessageType.LogDropped:
                if (!r.Int64(out var dropped) || dropped < 0)
                    return false;
                message = new EditorLinkMessage { Type = type, DroppedCount = dropped };
                break;
            case EditorLinkMessageType.Goodbye:
                if (!r.Int32(out var exitCode))
                    return false;
                message = new EditorLinkMessage { Type = type, ExitCode = exitCode };
                break;
            case EditorLinkMessageType.Command:
                if (!r.Byte(out var kind) || !r.String(out var argument))
                    return false;
                if (kind is < (byte)EditorCommandKind.Stop or > (byte)EditorCommandKind.RequestTree)
                    return false;
                message = new EditorLinkMessage { Type = type, Command = new EditorLinkCommand((EditorCommandKind)kind, argument) };
                break;
            case EditorLinkMessageType.Tree:
                {
                    if (!r.Byte(out var truncated) || !r.Int32(out var count) || count < 0 || count > MaxTreeNodes)
                        return false;
                    var nodes = new EditorLinkTreeNode[count];
                    for (var i = 0; i < count; i++)
                    {
                        if (!r.Int32(out var depth) || depth < 0 || !r.String(out var name) || !r.String(out var nodeType))
                            return false;
                        nodes[i] = new EditorLinkTreeNode(depth, name, nodeType);
                    }

                    message = new EditorLinkMessage { Type = type, Tree = nodes, TreeTruncated = truncated != 0 };
                    break;
                }

            default:
                return false;
        }

        return r.AtEnd;
    }

    /// <summary>
    /// The single severity sent for <paramref name="level"/>: its highest severity flag (Verbose ignored), Info when it
    /// has none — a hand-built entry with a combined or empty level must not make the receiver drop the connection.
    /// </summary>
    public static Log.Level WireLevel(Log.Level level)
    {
        var severity = (uint)level & (uint)(MainframeEngine.Log.Level.Debug | MainframeEngine.Log.Level.Info | MainframeEngine.Log.Level.Warning
                                            | MainframeEngine.Log.Level.Error | MainframeEngine.Log.Level.Fatal);
        return severity == 0 ? MainframeEngine.Log.Level.Info : (Log.Level)(1u << (31 - System.Numerics.BitOperations.LeadingZeroCount(severity)));
    }

    private static bool IsSingleLevel(byte level) =>
        level is (byte)MainframeEngine.Log.Level.Debug or (byte)MainframeEngine.Log.Level.Info or (byte)MainframeEngine.Log.Level.Warning
            or (byte)MainframeEngine.Log.Level.Error or (byte)MainframeEngine.Log.Level.Fatal;

    // Writes "u32 length, u8 type, payload" into the output, patching the length when finished.
    private ref struct FrameWriter
    {
        private readonly ArrayBufferWriter<byte> _output;
        private readonly int _headerOffset;
        private int _length;

        public FrameWriter(ArrayBufferWriter<byte> output, EditorLinkMessageType type)
        {
            ArgumentNullException.ThrowIfNull(output);
            _output = output;
            _headerOffset = output.WrittenCount;
            // The length prefix is written as 0 and patched by Finish once the payload size is known.
            var span = output.GetSpan(HeaderLength + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0);
            span[HeaderLength] = (byte)type;
            output.Advance(HeaderLength + 1);
            _length = 1;
        }

        public void Byte(byte value)
        {
            var span = _output.GetSpan(1);
            span[0] = value;
            Advance(1);
        }

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_output.GetSpan(4), value);
            Advance(4);
        }

        public void Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_output.GetSpan(8), value);
            Advance(8);
        }

        public void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_output.GetSpan(8), value);
            Advance(8);
        }

        public void Single(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_output.GetSpan(4), value);
            Advance(4);
        }

        public void String(string? value)
        {
            value ??= string.Empty;
            var max = Math.Min(MaxStringBytes, Math.Max(0, MaxFrameLength - _length - 64));
            var chars = value.AsSpan();
            var bytes = Encoding.UTF8.GetByteCount(chars);
            if (bytes > max)
            {
                // Truncate on a character boundary (oversized log messages only).
                var keep = Math.Min(chars.Length, max / 3);
                if (keep > 0 && char.IsHighSurrogate(chars[keep - 1]))
                    keep--;
                chars = chars[..keep];
                bytes = Encoding.UTF8.GetByteCount(chars);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(_output.GetSpan(4), (uint)bytes);
            Advance(4);
            if (bytes == 0)
                return;
            var written = Encoding.UTF8.GetBytes(chars, _output.GetSpan(bytes));
            Advance(written);
        }

        public readonly void Finish()
        {
            // ArrayBufferWriter's written memory is its own array: patch the length prefix in place.
            var written = System.Runtime.InteropServices.MemoryMarshal.AsMemory(_output.WrittenMemory).Span;
            BinaryPrimitives.WriteUInt32LittleEndian(written.Slice(_headerOffset, HeaderLength), (uint)_length);
        }

        private void Advance(int count)
        {
            _output.Advance(count);
            _length += count;
        }
    }

    private ref struct FrameReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public readonly bool AtEnd => _offset == _data.Length;

        public bool Byte(out byte value)
        {
            value = 0;
            if (_data.Length - _offset < 1)
                return false;
            value = _data[_offset++];
            return true;
        }

        public bool Int32(out int value)
        {
            value = 0;
            if (_data.Length - _offset < 4)
                return false;
            value = BinaryPrimitives.ReadInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return true;
        }

        public bool Int64(out long value)
        {
            value = 0;
            if (_data.Length - _offset < 8)
                return false;
            value = BinaryPrimitives.ReadInt64LittleEndian(_data[_offset..]);
            _offset += 8;
            return true;
        }

        public bool UInt64(out ulong value)
        {
            value = 0;
            if (_data.Length - _offset < 8)
                return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_offset..]);
            _offset += 8;
            return true;
        }

        public bool Single(out float value)
        {
            value = 0;
            if (_data.Length - _offset < 4)
                return false;
            value = BinaryPrimitives.ReadSingleLittleEndian(_data[_offset..]);
            _offset += 4;
            return true;
        }

        public bool String(out string value)
        {
            value = string.Empty;
            if (_data.Length - _offset < 4)
                return false;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            if (length > (uint)(_data.Length - _offset))
                return false;
            try
            {
                value = length == 0 ? string.Empty : StrictUtf8.GetString(_data.Slice(_offset, (int)length));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            _offset += (int)length;
            return true;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

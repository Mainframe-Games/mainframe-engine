using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace MainframeEngine.UI.Rml;

/// <summary>RmlUi log message types (<c>Rml::Log::Type</c>).</summary>
public enum RmlLogType
{
    Always = 0,
    Error = 1,
    Assert = 2,
    Warning = 3,
    Info = 4,
    Debug = 5,
}

/// <summary>
/// A string output parameter of a native callback (<c>mfrmlui_string*</c>), valid only inside the callback.
/// Writing it never allocates.
/// </summary>
public readonly unsafe ref struct RmlStringSink
{
    private readonly nint _sink;

    internal RmlStringSink(nint sink) => _sink = sink;

    /// <summary>Replaces the output with UTF-8 bytes.</summary>
    public void Set(ReadOnlySpan<byte> utf8)
    {
        fixed (byte* p = utf8)
            RmlException.ThrowIfFailed(RmlNative.StringSet(_sink, p, utf8.Length), "StringSet");
    }

    /// <summary>Replaces the output with text.</summary>
    public void Set(ReadOnlySpan<char> text)
    {
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var arg = new RmlUtf8Arg(text, scratch);
        fixed (byte* p = arg)
            RmlException.ThrowIfFailed(RmlNative.StringSet(_sink, p, arg.Length), "StringSet");
    }
}

/// <summary>
/// RmlUi's system interface: clock, logging, localization, cursor, clipboard and on-screen keyboard / IME hooks.
/// Install with <see cref="RmlCore.Initialise"/>. Every method is called on the RmlUi thread; exceptions are caught
/// and logged at the native boundary (they never unwind into RmlUi).
/// </summary>
/// <remarks>The defaults behave like RmlUi's own: a monotonic clock, <see cref="Log"/> output, no translation,
/// a process-local clipboard.</remarks>
public class RmlSystemInterface
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    private string _clipboard = "";

    /// <summary>Seconds since an arbitrary epoch. Called several times per context update: must not allocate.</summary>
    public virtual double GetElapsedTime() => Stopwatch.GetElapsedTime(Start).TotalSeconds;

    /// <summary>
    /// Localization hook (<c>TranslateString</c>): write a translation of <paramref name="input"/> (UTF-8) to
    /// <paramref name="output"/> and return the number of translations performed; 0 leaves the text unchanged.
    /// Called for every text node at load and for every data-bound text change, so it must not allocate when
    /// nothing is translated.
    /// </summary>
    public virtual int TranslateString(ReadOnlySpan<byte> input, RmlStringSink output) => 0;

    /// <summary>A message from RmlUi. Return false to break into a debugger (asserts only).</summary>
    public virtual bool LogMessage(RmlLogType type, string message)
    {
        switch (type)
        {
            case RmlLogType.Error:
            case RmlLogType.Assert:
                Log.Error($"[RmlUi] {message}");
                break;
            case RmlLogType.Warning:
                Log.Warning($"[RmlUi] {message}");
                break;
            default:
                // RmlUi's Info messages are load chatter ("Loaded font face … from '<absolute path>'"): debug detail.
                Log.Debug($"[RmlUi] {message}");
                break;
        }

        return true;
    }

    /// <summary>The cursor an element asks for (RCSS <c>cursor</c>: <c>pointer</c>, <c>text</c>, <c>move</c>, ...; empty = default).</summary>
    public virtual void SetMouseCursor(ReadOnlySpan<byte> cursorName)
    {
    }

    public virtual void SetClipboardText(string text) => _clipboard = text;

    public virtual string GetClipboardText() => _clipboard;

    /// <summary>A text field gained focus: caret position (context pixels) and line height, for IME placement.</summary>
    public virtual void ActivateKeyboard(Vector2 caretPosition, float lineHeight)
    {
    }

    /// <summary>Text input ended.</summary>
    public virtual void DeactivateKeyboard()
    {
    }
}

/// <summary>
/// Where RmlUi reads documents, style sheets, templates, fonts and images. Paths are as written in the documents
/// (joined with the document's own path by RmlUi). Called on the RmlUi thread at load time.
/// </summary>
public abstract class RmlFileInterface
{
    /// <summary>Opens a readable, seekable stream, or returns null if the file does not exist.</summary>
    public abstract Stream? Open(string path);
}

/// <summary>A <see cref="RmlFileInterface"/> over the file system, resolving paths with <see cref="ContentPaths.Resolve(string)"/>.</summary>
public class RmlContentFileInterface : RmlFileInterface
{
    public override Stream? Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = ResolvePath(path);
        return full is null ? null : new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
    }

    /// <summary>The absolute file for <paramref name="path"/>, or null if it does not exist.</summary>
    public virtual string? ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var full = ContentPaths.Resolve(path);
        return File.Exists(full) ? full : null;
    }
}

/// <summary>Stream helpers for file callbacks.</summary>
internal static class RmlStreams
{
    public static byte[] ReadAll(Stream stream, out int length)
    {
        if (stream.CanSeek)
        {
            var size = checked((int)(stream.Length - stream.Position));
            var data = new byte[size];
            stream.ReadExactly(data);
            length = size;
            return data;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        length = (int)copy.Length;
        return copy.GetBuffer();
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

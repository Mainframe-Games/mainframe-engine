using System.Numerics;
using System.Runtime.InteropServices;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace MainframeEngine;

/// <summary>
/// Localization hook for UI text (RmlUi's <c>TranslateString</c>; M9 installs a catalog here). Return the translation
/// of <paramref name="source"/> as UTF-8 into <paramref name="output"/> and true, or false to leave it unchanged.
/// Called for every text node at load and for every data-bound text change: it must not allocate when it does not
/// translate.
/// </summary>
public delegate bool UiTranslator(ReadOnlySpan<byte> source, RmlStringSink output);

/// <summary>
/// The engine's RmlUi system interface: the UI server's clock, <see cref="Log"/>, the localization hook, SDL system
/// cursors, the SDL clipboard and IME placement (SDL text-input rectangle at the caret). Works without a window
/// (headless tests): cursor, clipboard and IME calls then fall back to process-local behaviour.
/// </summary>
internal sealed unsafe class UiSystemInterface : RmlSystemInterface
{
    private readonly IWindow? _window;
    private readonly IInputContext? _input;
    private StandardCursor _cursor = StandardCursor.Default;
    private string _localClipboard = "";

    public UiSystemInterface(IWindow? window, IInputContext? input)
    {
        _window = window;
        _input = input;
    }

    /// <summary>Seconds of UI time (advanced by the server each frame, so fixed-step runs are deterministic).</summary>
    public double Time { get; set; }

    public UiTranslator? Translator { get; set; }

    /// <summary>Framebuffer pixels per window point (caret positions are converted back to points for SDL).</summary>
    public float PixelScale { get; set; } = 1f;

    /// <summary>True while a UI text field has keyboard focus (between ActivateKeyboard and DeactivateKeyboard).</summary>
    public bool TextInputActive { get; private set; }

    /// <summary>The caret rectangle of the active text field, in context (framebuffer) pixels.</summary>
    public RmlRect Caret { get; private set; }

    public override double GetElapsedTime() => Time;

    public override int TranslateString(ReadOnlySpan<byte> input, RmlStringSink output) =>
        Translator is { } translate && translate(input, output) ? 1 : 0;

    public override void SetMouseCursor(ReadOnlySpan<byte> cursorName)
    {
        var cursor = MapCursor(cursorName);
        if (cursor == _cursor)
            return;
        _cursor = cursor;
        if (_input is null)
            return;
        var mice = _input.Mice;
        for (var i = 0; i < mice.Count; i++)
            if (mice[i].Cursor.CursorMode == CursorMode.Normal)
                mice[i].Cursor.StandardCursor = cursor;
    }

    /// <summary>RCSS <c>cursor</c> names → SDL system cursors.</summary>
    internal static StandardCursor MapCursor(ReadOnlySpan<byte> name) => name switch
    {
        _ when name.SequenceEqual("pointer"u8) => StandardCursor.Hand,
        _ when name.SequenceEqual("text"u8) => StandardCursor.IBeam,
        _ when name.SequenceEqual("move"u8) => StandardCursor.ResizeAll,
        _ when name.SequenceEqual("resize"u8) || name.SequenceEqual("nwse-resize"u8) => StandardCursor.NwseResize,
        _ when name.SequenceEqual("nesw-resize"u8) => StandardCursor.NeswResize,
        _ when name.SequenceEqual("ew-resize"u8) || name.SequenceEqual("col-resize"u8) => StandardCursor.HResize,
        _ when name.SequenceEqual("ns-resize"u8) || name.SequenceEqual("row-resize"u8) => StandardCursor.VResize,
        _ when name.SequenceEqual("cross"u8) || name.SequenceEqual("crosshair"u8) => StandardCursor.Crosshair,
        _ when name.SequenceEqual("unavailable"u8) || name.SequenceEqual("not-allowed"u8) => StandardCursor.NotAllowed,
        _ when name.SequenceEqual("wait"u8) => StandardCursor.Wait,
        _ when name.SequenceEqual("progress"u8) => StandardCursor.WaitArrow,
        _ => StandardCursor.Default,
    };

    private Sdl? Api => _window is not null && SdlWindowing.IsViewSdl(_window) ? SdlWindowing.GetExistingApi(_window) : null;

    public override void SetClipboardText(string text)
    {
        if (Api is { } sdl)
            sdl.SetClipboardText(text);
        else
            _localClipboard = text;
    }

    public override string GetClipboardText() => Api is { } sdl ? sdl.GetClipboardTextS() ?? "" : _localClipboard;

    public override void ActivateKeyboard(Vector2 caretPosition, float lineHeight)
    {
        TextInputActive = true;
        Caret = new RmlRect(caretPosition.X, caretPosition.Y, 1, lineHeight);
        if (Api is not { } sdl)
            return;

        // IME candidate window at the caret (SDL wants window points). SDL keeps text input enabled for the whole
        // session (Silk's KeyChar relies on it), so only the rectangle moves.
        var scale = PixelScale > 0 ? PixelScale : 1f;
        var rect = new Rectangle<int>((int)(caretPosition.X / scale), (int)(caretPosition.Y / scale), 1,
            Math.Max(1, (int)(lineHeight / scale)));
        sdl.SetTextInputRect(&rect);
    }

    public override void DeactivateKeyboard() => TextInputActive = false;
}

/// <summary>
/// IME composition from SDL (<c>SDL_TEXTEDITING</c>), which Silk.NET's input layer does not surface. An SDL event
/// watch copies the composition string; <see cref="UiServer"/> publishes it on the main thread. The OS IME draws the
/// candidate window at the caret (<see cref="UiSystemInterface.ActivateKeyboard"/>); committed text arrives as normal
/// text input.
/// </summary>
internal sealed unsafe class UiImeWatch : IDisposable
{
    private readonly Sdl _sdl;
    private GCHandle _self;
    private readonly Lock _lock = new();
    private string? _pending;
    private int _pendingCursor;
    private bool _changed;

    private UiImeWatch(Sdl sdl)
    {
        _sdl = sdl;
        _self = GCHandle.Alloc(this);
        _sdl.AddEventWatch(new PfnEventFilter(&OnEvent), (void*)GCHandle.ToIntPtr(_self));
    }

    /// <summary>Installs the watch on an SDL window, or returns null (headless, non-SDL).</summary>
    public static UiImeWatch? TryCreate(IWindow? window)
    {
        if (window is null || !SdlWindowing.IsViewSdl(window) || SdlWindowing.GetExistingApi(window) is not { } sdl)
            return null;
        return new UiImeWatch(sdl);
    }

    /// <summary>Takes the latest composition change, if any (main thread).</summary>
    public bool TryTake(out string composition, out int cursor)
    {
        lock (_lock)
        {
            composition = _pending ?? "";
            cursor = _pendingCursor;
            var changed = _changed;
            _changed = false;
            return changed;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnEvent(void* user, Event* e)
    {
        try
        {
            if (e is null || e->Type != (uint)EventType.Textediting)
                return 1;
            var self = (UiImeWatch)GCHandle.FromIntPtr((nint)user).Target!;
            var text = RmlUtf8.ToString(e->Edit.Text) ?? "";
            lock (self._lock)
            {
                self._pending = text;
                self._pendingCursor = e->Edit.Start;
                self._changed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[UI] IME event watch: {ex.Message}");
        }

        return 1;
    }

    public void Dispose()
    {
        if (!_self.IsAllocated)
            return;
        _sdl.DelEventWatch(new PfnEventFilter(&OnEvent), (void*)GCHandle.ToIntPtr(_self));
        _self.Free();
    }
}

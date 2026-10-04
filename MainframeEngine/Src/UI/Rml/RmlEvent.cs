using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine.UI.Rml;

/// <summary>Event propagation phase (<c>Rml::EventPhase</c>).</summary>
[Flags]
public enum RmlEventPhase
{
    None = 0,
    Capture = 1,
    Target = 2,
    Bubble = 4,
}

/// <summary>
/// A borrowed <c>Rml::Event</c>, valid only during the listener or data-event callback (a ref struct, so it cannot
/// escape). Parameters depend on the event: <c>mouse_x</c>/<c>mouse_y</c>/<c>button</c> for mouse events,
/// <c>key_identifier</c> for keys, <c>value</c> for <c>change</c>/<c>submit</c>.
/// </summary>
public readonly unsafe ref struct RmlEvent
{
    internal RmlEvent(nint handle) => Handle = handle;

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;

    /// <summary>The event type, e.g. "click" (allocates; prefer <see cref="IsType"/> in hot paths).</summary>
    public string Type => RmlUtf8.Read(Handle, &ReadType) ?? "";

    private static int ReadType(nint h, byte* buffer, int capacity) => RmlNative.EventGetType(h, buffer, capacity);

    /// <summary>True when the type equals <paramref name="utf8Type"/> (e.g. <c>"click"u8</c>); no allocation.</summary>
    public bool IsType(ReadOnlySpan<byte> utf8Type)
    {
        var buffer = stackalloc byte[64];
        var length = RmlNative.EventGetType(Handle, buffer, 64);
        return length >= 0 && length < 64 && new ReadOnlySpan<byte>(buffer, length).SequenceEqual(utf8Type);
    }

    public RmlEventPhase Phase
    {
        get
        {
            var p = RmlNative.EventGetPhase(Handle);
            return p < 0 ? RmlEventPhase.None : (RmlEventPhase)p;
        }
    }

    /// <summary>The element the event was dispatched to.</summary>
    public RmlElement Target => new(RmlNative.EventGetTargetElement(Handle));

    /// <summary>The element whose listener is running.</summary>
    public RmlElement CurrentElement => new(RmlNative.EventGetCurrentElement(Handle));

    public void StopPropagation() => RmlException.ThrowIfFailed(RmlNative.EventStopPropagation(Handle), "EventStopPropagation");

    public void StopImmediatePropagation() => RmlException.ThrowIfFailed(RmlNative.EventStopImmediatePropagation(Handle), "EventStopImmediatePropagation");

    public RmlDictionary Parameters => new(RmlNative.EventGetParameters(Handle));

    /// <summary>A parameter, or a null variant.</summary>
    public RmlVariant GetParameter(string name) => Parameters.Find(name.AsSpan());

    public double GetParameter(string name, double fallback)
    {
        var v = GetParameter(name);
        return v.IsNull ? fallback : v.GetDouble(fallback);
    }

    public int GetParameter(string name, int fallback)
    {
        var v = GetParameter(name);
        return v.IsNull ? fallback : v.GetInt32(fallback);
    }

    public bool GetParameter(string name, bool fallback)
    {
        var v = GetParameter(name);
        return v.IsNull ? fallback : v.GetBool(fallback);
    }

    /// <summary>The <c>value</c> parameter of change/submit events (allocates).</summary>
    public string Value => GetParameter("value") is { IsNull: false } v ? v.GetString() : "";
}

/// <summary>Handles an element event; the <see cref="RmlEvent"/> is valid only during the call.</summary>
public delegate void RmlEventCallback(RmlEvent e);

/// <summary>
/// An event listener attached to an element (<see cref="RmlElement.AddEventListener"/>). Detached by
/// <see cref="Remove"/>, or by RmlUi when the element is destroyed; <see cref="Detached"/> runs exactly once either
/// way. Exceptions thrown by the handler are logged and swallowed at the native boundary.
/// </summary>
public sealed unsafe class RmlEventListener
{
    private nint _native;
    private GCHandle _self;

    private RmlEventListener(RmlEventCallback handler)
    {
        Handler = handler;
    }

    public RmlEventCallback Handler { get; }

    /// <summary>True until removed or until its element is destroyed.</summary>
    public bool IsAttached => _native != 0;

    /// <summary>Raised once when the listener is detached (removed, or its element destroyed).</summary>
    public event Action<RmlEventListener>? Detached;

    internal static RmlEventListener? Attach(nint element, ReadOnlySpan<char> eventType, RmlEventCallback handler, bool capture)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (element == 0)
            throw new ArgumentException("The element is null.", nameof(element));

        var listener = new RmlEventListener(handler);
        listener._self = GCHandle.Alloc(listener);
        Span<byte> scratch = stackalloc byte[RmlUtf8Arg.StackSize];
        using var type = new RmlUtf8Arg(eventType, scratch);
        fixed (byte* t = type)
        {
            listener._native = RmlNative.ElementAddEventListener(element, t, capture ? 1 : 0, &OnEvent, &OnDetach,
                GCHandle.ToIntPtr(listener._self));
        }

        if (listener._native == 0)
        {
            listener._self.Free(); // on_detach is not called when attaching fails
            return null;
        }

        return listener;
    }

    /// <summary>Detaches the listener (no-op if already detached).</summary>
    public void Remove()
    {
        if (_native == 0)
            return;
        var native = _native;
        // on_detach runs inside this call and clears _native.
        var status = RmlNative.EventListenerRemove(native);
        if (status < 0 && _native != 0)
            DetachNow();
    }

    private void DetachNow()
    {
        _native = 0;
        if (_self.IsAllocated)
            _self.Free();
        Detached?.Invoke(this);
    }

    private static RmlEventListener Self(nint user) => Unsafe.As<RmlEventListener>(GCHandle.FromIntPtr(user).Target!);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(nint user, nint evt)
    {
        try
        {
            Self(user).Handler(new RmlEvent(evt));
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "element event listener");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDetach(nint user)
    {
        try
        {
            Self(user).DetachNow();
        }
        catch (Exception e)
        {
            RmlCore.Report(e, "event listener detach");
        }
    }
}

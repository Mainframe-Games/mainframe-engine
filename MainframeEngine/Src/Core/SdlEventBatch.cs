using System.Collections;
using System.Reflection;
using Silk.NET.SDL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace MainframeEngine;

/// <summary>
/// Hands Silk's SDL view its polled events without allocating (ADR 0139). Silk.NET 2.23's <c>SdlPlatform.DoEvents</c>
/// polls each frame's events into a <c>List&lt;Event&gt;</c> and raises <c>EventReceived</c>; the view's handler
/// (<c>SdlView.OnEventReceived(IEnumerable&lt;Event&gt;)</c>) walks it with <c>foreach</c> through the interface, which
/// boxes the list's enumerator: 88 B on every frame with at least one event — every frame of mouse look. This re-subscribes
/// that same handler behind a reusable <see cref="IEnumerable{T}"/>, so Silk's own routing runs unchanged.
/// </summary>
/// <remarks>
/// Reaches Silk's internals by reflection once, at start-up (the version is pinned in Directory.Packages.props). When a
/// Silk upgrade moves them, <see cref="TryInstall"/> logs a warning and leaves Silk as it is (events still flow, with
/// the allocation); the render test <c>GameHostSteadyStateAllocatesNothing</c> then fails.
/// </remarks>
internal sealed class SdlEventBatch : IEnumerable<Event>, IEnumerator<Event>
{
    private readonly object _platform;
    private readonly EventInfo _eventReceived;
    private readonly Action<List<Event>> _silkHandler;
    private readonly Action<IEnumerable<Event>> _viewHandler;
    private readonly Action<List<Event>> _ourHandler;
    private List<Event>? _events;
    private int _index = -1;
    private bool _uninstalled;

    private SdlEventBatch(object platform, EventInfo eventReceived, Action<List<Event>> silkHandler, Action<IEnumerable<Event>> viewHandler)
    {
        _platform = platform;
        _eventReceived = eventReceived;
        _silkHandler = silkHandler;
        _viewHandler = viewHandler;
        _ourHandler = Dispatch;
    }

    /// <summary>
    /// Swaps the view's <c>EventReceived</c> handler for the allocation-free one, or returns null (not an SDL view, or
    /// Silk's internals are not where this expects them).
    /// </summary>
    public static SdlEventBatch? TryInstall(IView window)
    {
        if (!SdlWindowing.IsViewSdl(window))
            return null;
        try
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var viewType = FindDeclaring(window.GetType(), "OnEventReceived");
            var method = viewType?.GetMethod("OnEventReceived", instance, [typeof(IEnumerable<Event>)]);
            var platform = viewType?.GetField("_platform", instance)?.GetValue(window);
            var eventReceived = platform?.GetType().GetEvent("EventReceived", instance);
            if (method is null || platform is null || eventReceived?.EventHandlerType != typeof(Action<List<Event>>))
                return Unavailable("SdlView.OnEventReceived, SdlView._platform or SdlPlatform.EventReceived not found");

            // Silk subscribed `_platform.EventReceived += OnEventReceived` (an Action<List<Event>> over the view): an equal
            // delegate removes it. Check it is gone before adding ours, so events are never delivered twice.
            var silkHandler = (Action<List<Event>>)Delegate.CreateDelegate(typeof(Action<List<Event>>), window, method);
            var viewHandler = (Action<IEnumerable<Event>>)Delegate.CreateDelegate(typeof(Action<IEnumerable<Event>>), window, method);
            eventReceived.RemoveEventHandler(platform, silkHandler);
            if (Subscribed(platform, eventReceived, silkHandler) is not false)
            {
                eventReceived.AddEventHandler(platform, silkHandler); // no-op unless the removal did nothing
                return Unavailable("could not unsubscribe SdlView.OnEventReceived");
            }

            var batch = new SdlEventBatch(platform, eventReceived, silkHandler, viewHandler);
            eventReceived.AddEventHandler(platform, batch._ourHandler);
            return batch;
        }
        catch (Exception e) when (e is TargetInvocationException or ArgumentException or MemberAccessException or InvalidOperationException)
        {
            return Unavailable(e.Message);
        }
    }

    private static Type? FindDeclaring(Type? type, string method)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
                return type;
        }

        return null;
    }

    // The event's backing field (a field-like event); null when it cannot be read.
    private static bool? Subscribed(object platform, EventInfo info, Delegate handler)
    {
        var field = platform.GetType().GetField(info.Name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(platform) is not Delegate current)
            return field is null ? null : false;
        return Array.IndexOf(current.GetInvocationList(), handler) >= 0;
    }

    private static SdlEventBatch? Unavailable(string reason)
    {
        Log.Warning($"[Window] Silk's SDL event pump allocates 88 B per frame with events: {reason} (ADR 0139).");
        return null;
    }

    // Silk's DoEvents → EventReceived(list): the view's handler walks the list through this object (no boxed enumerator).
    private void Dispatch(List<Event> events)
    {
        _events = events;
        _index = -1;
        try
        {
            _viewHandler(this);
        }
        finally
        {
            _events = null;
        }
    }

    public IEnumerator<Event> GetEnumerator()
    {
        _index = -1;
        return this;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Event Current => _events![_index];

    object IEnumerator.Current => Current;

    public bool MoveNext() => _events is not null && ++_index < _events.Count;

    public void Reset() => _index = -1;

    void IDisposable.Dispose() => _index = -1; // foreach's Dispose; the batch lives on

    /// <summary>Puts Silk's own handler back (before the window is reset or disposed).</summary>
    public void Uninstall()
    {
        if (_uninstalled)
            return;
        _uninstalled = true;
        _eventReceived.RemoveEventHandler(_platform, _ourHandler);
        _eventReceived.AddEventHandler(_platform, _silkHandler);
    }
}

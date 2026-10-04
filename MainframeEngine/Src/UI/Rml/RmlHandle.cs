using System.Runtime.InteropServices;

namespace MainframeEngine.UI.Rml;

/// <summary>
/// A <see cref="SafeHandle"/> over an owned <c>mfrmlui</c> object (context, data model, render interface). Released
/// exactly once: by <see cref="IDisposable.Dispose"/> on the RmlUi thread, or — if its owner was collected without
/// being disposed — queued and released on the RmlUi thread by <see cref="RmlCore.ProcessPendingReleases"/>, because
/// RmlUi must never be called from the finalizer thread. Handles whose objects RmlUi destroys itself (a context's data
/// models when the context goes, everything at <see cref="RmlCore.Shutdown"/>) are marked invalid instead of released.
/// </summary>
public abstract class RmlHandle : SafeHandle
{
    [ThreadStatic]
    private static bool _explicitRelease;

    protected RmlHandle() : base(0, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == 0;

    /// <summary>True when <see cref="RmlCore.Shutdown"/> destroys the object (contexts, data models).</summary>
    internal virtual bool DiesWithLibrary => true;

    internal void Attach(nint value)
    {
        SetHandle(value);
        RmlCore.Track(this);
    }

    /// <summary>The raw handle for a native call; throws if released.</summary>
    internal nint Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed || IsInvalid, this);
            return handle;
        }
    }

    /// <summary>Releases now (RmlUi thread).</summary>
    internal void ReleaseNow()
    {
        _explicitRelease = true;
        try
        {
            Dispose();
        }
        finally
        {
            _explicitRelease = false;
        }
    }

    /// <summary>The library destroyed the object: forget the handle without releasing it.</summary>
    internal void MarkDestroyedByLibrary()
    {
        RmlCore.Untrack(this);
        SetHandleAsInvalid();
    }

    protected sealed override bool ReleaseHandle()
    {
        if (!_explicitRelease)
        {
            // Finalizer thread: hand the raw value to a fresh handle released later on the RmlUi thread.
            var deferred = CreateDeferredCopy(handle);
            RmlCore.DeferRelease(deferred);
            return true;
        }

        RmlCore.Untrack(this);
        var status = Release(handle);
        return status >= 0;
    }

    /// <summary>A handle of the same kind owning <paramref name="value"/> (used only for deferred release).</summary>
    private protected abstract RmlHandle CreateDeferredCopy(nint value);

    /// <summary>Destroys the native object; returns an mfrmlui status.</summary>
    private protected abstract int Release(nint value);
}

/// <summary>Owned <c>mfrmlui_context*</c>.</summary>
public sealed class RmlContextHandle : RmlHandle
{
    private protected override RmlHandle CreateDeferredCopy(nint value)
    {
        var copy = new RmlContextHandle();
        copy.SetHandle(value);
        return copy;
    }

    private protected override int Release(nint value) => RmlCore.IsInitialised ? RmlNative.ContextDestroy(value) : RmlNative.Ok;
}

/// <summary>Owned <c>mfrmlui_data_model*</c>.</summary>
public sealed class RmlDataModelHandle : RmlHandle
{
    private protected override RmlHandle CreateDeferredCopy(nint value)
    {
        var copy = new RmlDataModelHandle();
        copy.SetHandle(value);
        return copy;
    }

    private protected override int Release(nint value) => RmlCore.IsInitialised ? RmlNative.DataModelRemove(value) : RmlNative.Ok;
}

/// <summary>Owned <c>mfrmlui_render_interface*</c> (outlives <see cref="RmlCore.Shutdown"/>).</summary>
public sealed class RmlRenderInterfaceHandle : RmlHandle
{
    internal override bool DiesWithLibrary => false;

    private protected override RmlHandle CreateDeferredCopy(nint value)
    {
        var copy = new RmlRenderInterfaceHandle();
        copy.SetHandle(value);
        return copy;
    }

    private protected override int Release(nint value) => RmlNative.RenderInterfaceDestroy(value);
}

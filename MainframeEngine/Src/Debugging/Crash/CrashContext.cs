using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// What a crash report knows beyond the exception: the node and callback an exception left (recorded by the scene tree's
/// exception filters before the stack unwinds), and providers read only at crash time (scene, frames, the GPU, the
/// settings snapshot). Nothing here runs per frame.
/// </summary>
public static class CrashContext
{
    /// <summary>The node an exception left, as text: the tree may be gone (or its types unloaded) by the time the report is written.</summary>
    internal sealed record NodeNote(string Path, string Type, NodeCallback Callback);

    // Weak on the exception: a noted exception that was caught after all (the editor's simulation guard) must not keep a
    // game node, type or collectible assembly alive (code reload unloads them).
    private static readonly ConditionalWeakTable<Exception, NodeNote> Notes = new();

    /// <summary>
    /// Records that <paramref name="exception"/> left <paramref name="node"/>'s <paramref name="callback"/>. Always
    /// returns false, so as an exception filter (<c>catch (Exception e) when (CrashContext.NoteNode(node, …, e))</c>) it
    /// never catches: the exception propagates unchanged, and the note is taken before the stack unwinds. The innermost
    /// node wins (filters run inner first), so a child's <c>OnReady</c> thrown through its parent's names the child.
    /// Only runs when an exception leaves a callback; the non-exceptional path costs nothing.
    /// </summary>
    public static bool NoteNode(Node? node, NodeCallback callback, Exception exception)
    {
        try
        {
            if (node is not null && exception is not null && !Notes.TryGetValue(exception, out _))
                Notes.TryAdd(exception, new NodeNote(PathOf(node), node.GetType().FullName ?? node.GetType().Name, callback));
        }
        catch (Exception)
        {
            // Out of memory (or a broken node) while noting: the report goes without the node.
        }

        return false;
    }

    private static string PathOf(Node node)
    {
        try
        {
            return node.IsInsideTree ? node.GetPath().ToString() : node.Name;
        }
        catch (Exception)
        {
            return node.Name;
        }
    }

    /// <summary>The node noted for <paramref name="exception"/> or one of its inner exceptions, or null.</summary>
    internal static NodeNote? NodeOf(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (Notes.TryGetValue(e, out var note))
                return note;
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (Notes.TryGetValue(inner, out var innerNote))
                        return innerNote;
        }

        return null;
    }

    /// <summary>The callback's name as a report shows it (<c>OnPhysicsProcess</c>).</summary>
    internal static string CallbackName(NodeCallback callback) => callback switch
    {
        NodeCallback.EnterTree => "OnEnterTree",
        NodeCallback.Ready => "OnReady",
        NodeCallback.ExitTree => "OnExitTree",
        NodeCallback.Process => "OnProcess",
        NodeCallback.PhysicsProcess => "OnPhysicsProcess",
        NodeCallback.Input => "OnInput",
        NodeCallback.UnhandledInput => "OnUnhandledInput",
        NodeCallback.Deferred => "CallDeferred",
        _ => "",
    };

    /// <summary>The GPU of the running renderer (set when the renderer starts; null headless).</summary>
    public static GpuInfo? Gpu
    {
        get;
        set
        {
            field = value;
            if (value is not null)
                CrashReporter.OnGpuKnown(value);
        }
    }

    /// <summary>The current scene's path (<see cref="GameHost"/> sets it; read only at crash time).</summary>
    public static Func<string?>? Scene { get; set; }

    /// <summary>Frames rendered so far (read only at crash time).</summary>
    public static Func<long>? RenderedFrames { get; set; }

    /// <summary>Scene-tree process frames so far (read only at crash time).</summary>
    public static Func<long>? ProcessFrames { get; set; }

    /// <summary>The bundle's <c>settings.json</c>: the effective project settings and live render state (read only at crash time).</summary>
    public static Func<byte[]?>? Settings { get; set; }

    /// <summary>The renderer's report on a lost device: breadcrumbs and fault info (set by the Vulkan renderer).</summary>
    internal static Func<CrashGpuLoss?>? DeviceLoss { get; set; }

    internal static void Reset()
    {
        Notes.Clear();
        Scene = null;
        RenderedFrames = null;
        ProcessFrames = null;
        Settings = null;
        DeviceLoss = null;
    }
}

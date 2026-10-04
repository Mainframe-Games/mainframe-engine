using System.Reflection;

namespace MainframeEngine;

/// <summary>
/// Helpers for releasing engine-held references into a game assembly that is being unloaded
/// (<see cref="GameAssemblyLoader"/>): delegates subscribed to engine events by game code that never unsubscribed.
/// </summary>
internal static class CodeReload
{
    /// <summary>True when <paramref name="handler"/>'s method or target belongs to <paramref name="assembly"/>.</summary>
    public static bool IsFrom(Delegate handler, Assembly assembly) =>
        handler.Method.DeclaringType?.Assembly == assembly || handler.Target?.GetType().Assembly == assembly;

    /// <summary>True when <paramref name="value"/>'s runtime type (or <paramref name="value"/> as a delegate) belongs to <paramref name="assembly"/>.</summary>
    public static bool IsFrom(object? value, Assembly assembly) => value switch
    {
        null => false,
        Delegate d => IsFrom(d, assembly),
        _ => value.GetType().Assembly == assembly,
    };

    /// <summary>
    /// <paramref name="handlers"/> without the invocations whose code lives in <paramref name="assembly"/>; each removed
    /// one is logged as a warning naming <paramref name="eventName"/> (the game should unsubscribe in OnExitTree).
    /// </summary>
    public static TDelegate? Without<TDelegate>(TDelegate? handlers, Assembly assembly, string eventName)
        where TDelegate : Delegate
    {
        if (handlers is null)
            return null;
        Delegate? result = handlers;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (!IsFrom(handler, assembly))
                continue;
            result = Delegate.Remove(result, handler);
            Log.Warning($"[GameAssembly] Removed a {eventName} handler of unloaded game code " +
                        $"({handler.Method.DeclaringType?.FullName}.{handler.Method.Name}); unsubscribe when the node leaves the tree.");
        }

        return (TDelegate?)result;
    }
}

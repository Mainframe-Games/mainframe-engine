using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// macOS: keeps a process whose window starts hidden (render tests, <c>--hidden</c>, smoke tests, QA scripts) out of the
/// Dock and the app switcher, so it never takes focus from the app the developer is typing in. SDL otherwise makes every
/// process a regular app and activates it when its video subsystem starts, hidden window or not.
/// </summary>
/// <remarks>
/// SDL reads <c>SDL_MAC_BACKGROUND_APP</c> once, when video starts (<c>Cocoa_RegisterApp</c>); with it set the app gets no
/// regular activation policy and is not activated. The hint goes into the native environment, which SDL's
/// <c>SDL_GetHint</c> falls back to, so it is in place before anything touches SDL: on macOS
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/> only changes .NET's copy, so libc <c>setenv</c>.
/// </remarks>
internal static partial class MacBackgroundApp
{
    public const string Hint = "SDL_MAC_BACKGROUND_APP";

    /// <summary>Sets the hint (macOS only; a no-op elsewhere). Call before SDL's video subsystem starts.</summary>
    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        if (SetEnv(Hint, "1", 1) != 0)
            Log.Warning($"[Window] setenv({Hint}) failed (errno {Marshal.GetLastPInvokeError()}); a hidden window may take focus");
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnv(string name, string value, int overwrite);
}

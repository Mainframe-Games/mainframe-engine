using System.Reflection;
using System.Runtime.InteropServices;
using Steamworks;

namespace MainframeEngine.Steamworks;

/// <summary>
/// Native plumbing: where <c>steam_api</c> is loaded from, the flat-API calls Facepunch.Steamworks keeps internal, and
/// the native environment on macOS and Linux.
/// </summary>
internal static partial class SteamworksNative
{
#if STEAMWORKS_WIN64
    /// <summary>The import name Facepunch.Steamworks.Win64 uses.</summary>
    public const string Library = "steam_api64";

    /// <summary>The file it is in.</summary>
    public const string FileName = "steam_api64.dll";
#else
    /// <summary>The import name Facepunch.Steamworks.Posix uses.</summary>
    public const string Library = "libsteam_api";

    /// <summary>The file it is in.</summary>
    public static readonly string FileName = OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : "libsteam_api.so";
#endif

    private static readonly Lock Sync = new();
    private static bool _registered;
    private static nint _handle;

    /// <summary>
    /// Loads <c>steam_api</c> for Facepunch's imports and ours from the app directory (Speed Golf's SteamNativeLoader):
    /// checkout builds and published games have it there. Elsewhere (a NuGet package game's RID-less build keeps it in
    /// runtimes/&lt;rid&gt;/native) the runtime's default probing, which reads deps.json, finds it. Idempotent.
    /// </summary>
    public static void RegisterResolver()
    {
        lock (Sync)
        {
            if (_registered)
                return;
            NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, Resolve);
            NativeLibrary.SetDllImportResolver(typeof(SteamworksNative).Assembly, Resolve);
            _registered = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library)
            return 0;

        lock (Sync)
        {
            if (_handle != 0)
                return _handle;
            var path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                _handle = handle;
            return _handle;
        }
    }

    /// <summary>
    /// Sets a variable in the native environment as well as .NET's. On macOS and Linux
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> only changes .NET's copy, so the <c>SteamAppId</c>
    /// Facepunch sets before <c>SteamAPI_Init</c> never reaches steam_api's <c>getenv</c> there and init fails with
    /// "No appID found" (Speed Golf, 2026-09-25). Windows needs nothing: .NET writes the process environment itself.
    /// </summary>
    public static void SetEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
#if STEAMWORKS_POSIX
        var result = OperatingSystem.IsMacOS() ? SetEnvMac(name, value, 1) : SetEnvLinux(name, value, 1);
        if (result != 0)
            throw new InvalidOperationException($"setenv({name}) failed with errno {Marshal.GetLastPInvokeError()}");
#endif
    }

#if STEAMWORKS_POSIX
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnvMac(string name, string value, int overwrite);

    // "libc" alone can resolve to the libc.so linker script on glibc systems with development packages; the soname cannot.
    [LibraryImport("libc.so.6", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnvLinux(string name, string value, int overwrite);
#endif

    // --- steam_api flat calls that Facepunch.Steamworks 2.4.1 does not expose -------------------------------------
    // ISteamFriends v017 is the interface version this steam_api (SDK 1.6x, the one Facepunch 2.4.1 was built for) and
    // Facepunch itself use.

    [LibraryImport(Library, EntryPoint = "SteamAPI_SteamFriends_v017")]
    private static partial nint SteamFriends();

    [LibraryImport(Library, EntryPoint = "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialogConnectString",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial void ActivateGameOverlayInviteDialogConnectString(nint self, string connect);

    [LibraryImport(Library, EntryPoint = "SteamAPI_ISteamFriends_ActivateGameOverlayRemotePlayTogetherInviteDialog")]
    private static partial void ActivateGameOverlayRemotePlayTogetherInviteDialog(nint self, ulong lobbyId);

    /// <summary>The overlay's invite dialog; an accepted invite launches the friend's game with <paramref name="connect"/>.</summary>
    public static void OpenInviteDialog(string connect) => ActivateGameOverlayInviteDialogConnectString(SteamFriends(), connect);

    /// <summary>The overlay's Remote Play Together invite dialog.</summary>
    public static void OpenRemotePlayInviteDialog() => ActivateGameOverlayRemotePlayTogetherInviteDialog(SteamFriends(), 0);
}

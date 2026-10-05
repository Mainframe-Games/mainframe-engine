using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>State of the Steam service (<see cref="Steam.Status"/>).</summary>
public enum SteamStatus
{
    /// <summary><see cref="Steam.TryInitialize"/> has not been called.</summary>
    NotInitialized,

    /// <summary>Steam is initialized; wrappers are live and <see cref="Steam.RunCallbacks"/> must be pumped.</summary>
    Running,

    /// <summary>
    /// This process cannot use Steamworks.NET 2024.8.0: its managed assemblies are x86/x86-64 only, so arm64
    /// processes (Apple Silicon, Windows/Linux on ARM) cannot load them.
    /// </summary>
    UnsupportedPlatform,

    /// <summary>The app id was 0.</summary>
    InvalidAppId,

    /// <summary>The <c>steam_api</c> native library is not next to the app (it is not shipped yet; see docs/design/steamworks.md).</summary>
    NativeLibraryMissing,

    /// <summary>The Steam client is not running (or not logged in).</summary>
    SteamNotRunning,

    /// <summary><c>SteamAPI_InitEx</c> failed for another reason (version mismatch, app not owned, …); see <see cref="Steam.StatusMessage"/>.</summary>
    InitFailed,

    /// <summary><see cref="Steam.Shutdown"/> was called.</summary>
    Shutdown,
}

/// <summary>
/// Engine-owned Steam service. Steam is optional: <see cref="TryInitialize"/> never throws and leaves every Steam
/// wrapper a no-op (returning empty/default values) unless Steam really started.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="TryInitialize"/> once at startup, <see cref="RunCallbacks"/> once per frame on the main
/// thread (Steam callbacks, lobby results and async continuations run inside it), <see cref="Shutdown"/> at exit.
/// <para>
/// No member of this or any other public Steam wrapper mentions a Steamworks.NET type: the Steamworks.NET assembly is
/// only loaded once the platform is known to support it (see <see cref="SteamStatus.UnsupportedPlatform"/>).
/// </para>
/// </remarks>
public static class Steam
{
    private static readonly Lock Sync = new();

    /// <summary>Current state.</summary>
    public static SteamStatus Status { get; private set; } = SteamStatus.NotInitialized;

    /// <summary>Human-readable detail for <see cref="Status"/> (why Steam is unavailable).</summary>
    public static string StatusMessage { get; private set; } = "Steam.TryInitialize has not been called.";

    /// <summary>True while Steam is running; every wrapper checks it.</summary>
    public static bool Valid => Status == SteamStatus.Running;

    /// <summary>App id passed to <see cref="TryInitialize"/> (0 before).</summary>
    public static uint AppId { get; private set; }

    /// <summary>
    /// Whether this process can load Steamworks.NET 2024.8.0 at all: its managed assemblies target x86-64 (and x86 on
    /// Windows), so arm64 processes cannot.
    /// </summary>
    public static bool IsPlatformSupported =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
        || (RuntimeInformation.ProcessArchitecture == Architecture.X86 && OperatingSystem.IsWindows());

    /// <summary>File name of the Steamworks native library for this platform (from the Steamworks SDK <c>redistributable_bin</c>).</summary>
    public static string NativeLibraryFileName =>
        OperatingSystem.IsWindows() ? (Environment.Is64BitProcess ? "steam_api64.dll" : "steam_api.dll") :
        OperatingSystem.IsMacOS() ? "libsteam_api.dylib" :
        "libsteam_api.so";

    /// <summary>
    /// Starts Steam if it is available. Never throws: on failure <see cref="Status"/> and <see cref="StatusMessage"/>
    /// say why, and the game continues without Steam.
    /// </summary>
    /// <param name="appId">The game's Steam app id (480 = Spacewar, Valve's test app).</param>
    /// <param name="writeDevAppIdFile">
    /// Development only: write <c>steam_appid.txt</c> next to the executable and set <c>SteamAppId</c> for this process,
    /// so a build started outside Steam (IDE, <c>dotnet run</c>) still initializes. Never enable it in shipped builds:
    /// it bypasses the "launched through Steam" check.
    /// </param>
    /// <returns>True when Steam is running.</returns>
    public static bool TryInitialize(uint appId, bool writeDevAppIdFile = false)
    {
        lock (Sync)
        {
            if (Valid)
            {
                if (appId != AppId)
                    Log.Warning($"[Steam] already running with app id {AppId}; ignoring {appId}");
                return true;
            }

            AppId = appId;
            try
            {
                var (status, message) = Initialize(appId, writeDevAppIdFile);
                SetStatus(status, message);
            }
            catch (Exception e)
            {
                // Last line of defence: Steam is optional and must never take the game down at startup.
                SetStatus(SteamStatus.InitFailed, $"Steam initialization threw {e.GetType().Name}: {e.Message}");
            }

            if (Valid)
                Log.Info($"[Steam] running (app {appId}, user {Username})");
            else
                Log.Info($"[Steam] disabled: {StatusMessage}");
            return Valid;
        }
    }

    private static (SteamStatus Status, string Message) Initialize(uint appId, bool writeDevAppIdFile)
    {
        if (appId == 0)
            return (SteamStatus.InvalidAppId, "App id 0 is not a valid Steam app id.");

        if (!IsPlatformSupported)
        {
            return (SteamStatus.UnsupportedPlatform,
                $"Steamworks.NET 2024.8.0 only ships x86/x86-64 assemblies; this process is {RuntimeInformation.ProcessArchitecture} " +
                $"({RuntimeInformation.RuntimeIdentifier}).");
        }

        if (writeDevAppIdFile)
            SteamAppIdFile.Apply(AppContext.BaseDirectory, appId);

        return SteamApi.Initialize(NativeLibraryFileName);
    }

    /// <summary>
    /// Shipped builds, before anything else: true when the game was started outside the Steam client and Steam is now
    /// launching it again through Steam (<c>SteamAPI_RestartAppIfNecessary</c>), so this process must exit at once. False
    /// means carry on: launched by Steam, or Steam cannot be asked here (app id 0, unsupported platform, no native
    /// library, an error). Never throws. Development runs skip it (a <c>steam_appid.txt</c> also makes it return false).
    /// </summary>
    public static bool RestartAppIfNecessary(uint appId)
    {
        if (appId == 0 || !IsPlatformSupported)
            return false;
        try
        {
            return SteamApi.RestartAppIfNecessary(appId);
        }
        catch (Exception e)
        {
            Log.Warning($"[Steam] RestartAppIfNecessary threw {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Pumps Steam callbacks. Call once per frame on the main thread; a no-op unless <see cref="Valid"/>.</summary>
    public static void RunCallbacks()
    {
        if (Valid)
            SteamApi.RunCallbacks();
    }

    /// <summary>Shuts Steam down (a no-op unless <see cref="Valid"/>). Wrappers become no-ops again.</summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!Valid)
                return;

            SteamLobby.Reset();
            try
            {
                SteamApi.Shutdown();
            }
            catch (Exception e)
            {
                Log.Warning($"[Steam] shutdown threw {e.GetType().Name}: {e.Message}");
            }

            SetStatus(SteamStatus.Shutdown, "Steam.Shutdown was called.");
        }
    }

    private static void SetStatus(SteamStatus status, string message)
    {
        Status        = status;
        StatusMessage = message;
    }

    /// <summary>SteamID of the logged-in user, or 0.</summary>
    public static ulong SteamId => Valid ? SteamApi.GetSteamId() : 0;

    /// <summary>Persona name of the logged-in user, or empty.</summary>
    public static string Username => Valid ? SteamApi.GetPersonaName() : string.Empty;

    /// <summary>The beta branch the game was launched on, or empty (default branch or no Steam).</summary>
    public static string Branch => Valid ? SteamApi.GetBranch() : string.Empty;

    /// <summary>
    /// The value after <paramref name="flag"/> in the command line Steam launched the game with (e.g. from a
    /// <c>steam://run</c> link or a join invite). Null when absent or Steam is not running.
    /// </summary>
    public static string? GetLaunchCommandLine(string flag)
    {
        ArgumentException.ThrowIfNullOrEmpty(flag);
        if (!Valid)
            return null;

        var parts = SteamApi.GetLaunchCommandLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == flag)
                return parts[i + 1];
        }

        return null;
    }

    /// <summary>All friends of the logged-in user; empty without Steam.</summary>
    public static IReadOnlyList<SteamFriend> GetFriends() => Valid ? SteamApi.GetFriends() : [];

    /// <summary>Persona name of a friend (or any user Steam knows), or empty.</summary>
    public static string GetFriendUsername(ulong steamId) => Valid ? SteamApi.GetFriendPersonaName(steamId) : string.Empty;

    /// <summary>Whether the DLC is installed; false without Steam.</summary>
    public static bool HasDLC(uint dlcId) => Valid && SteamApi.IsDlcInstalled(dlcId);
}

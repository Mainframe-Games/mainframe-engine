using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using SdlProvider = Silk.NET.SDL.SdlProvider;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// macOS-only: makes the window layer (SDL) and Silk.NET's <see cref="Vk"/> bind the SAME Vulkan
/// library. Modern macOS dyld no longer searches /usr/local/lib for leaf-name dlopen, and left alone
/// SDL and Silk can bind different libraries (SDK loader vs. bundled MoltenVK), whose instances are
/// not interchangeable — <c>SDL_Vulkan_CreateSurface</c> then rejects the instance.
/// <para>
/// Two steps, both before the window is created: <see cref="Probe"/> finds and loads a Vulkan entry
/// point (SDK loader first, bundled MoltenVK last); <see cref="HandOffToSdl"/> passes that path to
/// <c>SDL_Vulkan_LoadLibrary</c>. <see cref="TryCreateVk"/> then builds a <see cref="Vk"/> on the same
/// path for the renderer.
/// </para>
/// </summary>
internal static unsafe class VulkanLoaderBootstrap
{
    /// <summary>Forces one library path (QA of each loader source, e.g. the bundled libMoltenVK.dylib).</summary>
    internal const string OverrideVariable = "MAINFRAME_VULKAN_LIBRARY";

    private static bool _probed;
    private static bool _handedOff;

    /// <summary>Full path of the Vulkan library every component must share; null off-macOS or when none was found.</summary>
    internal static string? ActiveLibraryPath { get; private set; }

    /// <summary>Probe + SDL handoff. Call after selecting SDL and before <c>Window.Create</c>. Idempotent.</summary>
    public static void Initialize()
    {
        Probe();
        HandOffToSdl();
    }

    /// <summary>Locates and loads the Vulkan library (macOS only); sets <see cref="ActiveLibraryPath"/>.</summary>
    public static void Probe()
    {
        if (_probed || !OperatingSystem.IsMacOS()) return;
        _probed = true;

        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VulkanSDK");
        var candidates = Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } forced
            ? [forced]
            : CandidatePaths(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("VULKAN_SDK"),
                Directory.Exists(sdkRoot) ? Directory.GetDirectories(sdkRoot) : [], RuntimeInformation.ProcessArchitecture);
        foreach (var path in candidates)
        {
            // The handle stays loaded for the process lifetime; SDL and Silk dlopen the same image.
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
            {
                ActiveLibraryPath = path;
                Log.Info($"[Vulkan] Using {path}");
                return;
            }
        }

        Log.Warning("[Vulkan] No Vulkan loader or MoltenVK found. Install the Vulkan SDK " +
                    "(https://vulkan.lunarg.com) for validation layers, or ship libMoltenVK.dylib with the app.");
    }

    /// <summary>
    /// Gives the probed library to SDL (<c>SDL_Vulkan_LoadLibrary</c>). Must run after SDL's video
    /// subsystem is up (the shared <see cref="SdlProvider"/> instance initialises it) and before the
    /// first Vulkan window exists. No-op when nothing was probed: SDL then loads its default library.
    /// </summary>
    public static void HandOffToSdl()
    {
        if (_handedOff || ActiveLibraryPath is null) return;
        _handedOff = true;

        var sdl = SdlProvider.SDL.Value;
        if (sdl.VulkanLoadLibrary(ActiveLibraryPath) != 0)
        {
            // Not fatal on its own: SDL falls back to its default search, which matches the already
            // loaded image when the leaf names agree. The surface creation fails loudly otherwise.
            Log.Warning($"[Vulkan] SDL_Vulkan_LoadLibrary('{ActiveLibraryPath}') failed: {sdl.GetErrorS()}");
        }
    }

    /// <summary>
    /// A Vk API bound to the same library SDL was given, so instances created through it are valid
    /// for <c>SDL_Vulkan_CreateSurface</c>. Null off-macOS — callers fall back to <c>Vk.GetApi()</c>.
    /// </summary>
    public static Vk? TryCreateVk()
        => ActiveLibraryPath is null ? null : new Vk(new DefaultNativeContext(ActiveLibraryPath));

    /// <summary>
    /// Probe order: a loader shipped next to the app, the SDK loader (<c>$VULKAN_SDK</c>,
    /// /usr/local/lib, <c>~/VulkanSDK/&lt;newest&gt;</c>), then the bundled MoltenVK ICD.
    /// </summary>
    internal static IEnumerable<string> CandidatePaths(string baseDir, string? vulkanSdk,
        IReadOnlyList<string> sdkVersionDirs, Architecture architecture)
    {
        // 1) A loader shipped next to the app (wins if present).
        foreach (var dir in AppNativeDirs(baseDir, architecture))
            yield return Path.Combine(dir, "libvulkan.1.dylib");

        // 2) The SDK loader — enables validation layers and all installed drivers.
        if (!string.IsNullOrEmpty(vulkanSdk))
            yield return Path.Combine(vulkanSdk, "lib", "libvulkan.1.dylib");

        yield return "/usr/local/lib/libvulkan.1.dylib";

        // Numeric version ordering — lexicographic would sort e.g. 1.4.341 above 1.4.1000.
        foreach (var dir in sdkVersionDirs.OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)))
            yield return Path.Combine(dir, "macOS", "lib", "libvulkan.1.dylib");

        // 3) Bundled MoltenVK (Silk.NET.MoltenVK.Native) — machines without the SDK.
        foreach (var dir in AppNativeDirs(baseDir, architecture))
            yield return Path.Combine(dir, "libMoltenVK.dylib");
    }

    private static IEnumerable<string> AppNativeDirs(string baseDir, Architecture architecture)
    {
        yield return baseDir;

        var arch = architecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        yield return Path.Combine(baseDir, "runtimes", arch, "native");
        yield return Path.Combine(baseDir, "runtimes", "osx", "native");
    }
}

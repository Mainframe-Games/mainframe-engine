using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// macOS-only: locates a Vulkan entry point (SDK loader or bundled MoltenVK) and hands the
/// same library to both GLFW (before window creation) and Silk.NET's Vk API. Needed because
/// modern macOS dyld no longer searches /usr/local/lib when GLFW dlopens "libvulkan.1.dylib"
/// by leaf name, and Silk.NET's own search never tries that name — left alone the two can
/// bind different libraries, whose instances are not interchangeable.
/// </summary>
internal static unsafe class VulkanLoaderBootstrap
{
    private static bool _initialized;

    /// <summary>Full path of the Vulkan library every component must share; null off-macOS or when none was found.</summary>
    internal static string? ActiveLibraryPath { get; private set; }

    public static void Initialize()
    {
        if (_initialized || !OperatingSystem.IsMacOS()) return;
        _initialized = true;

        var handle = LoadVulkanLibrary();
        if (handle == IntPtr.Zero)
        {
            Log.Warning("[Vulkan] No Vulkan loader or MoltenVK found. Install the Vulkan SDK " +
                        "(https://vulkan.lunarg.com) for validation layers, or ship libMoltenVK.dylib with the app.");
            return; // GLFW raises its own (now-accurate) error if truly nothing is available
        }

        // GLFW 3.4+: hand it vkGetInstanceProcAddr directly so its internal
        // dlopen("libvulkan.1.dylib") is bypassed. Callable before glfwInit.
        if (NativeLibrary.TryGetExport(handle, "vkGetInstanceProcAddr", out var pfn) &&
            GlfwProvider.GLFW.Value.Context.TryGetProcAddress("glfwInitVulkanLoader", out var init))
        {
            ((delegate* unmanaged<nint, void>)init)(pfn);
        }
        else
        {
            // Not fatal: the full-path load above already lets GLFW's own leaf-name
            // dlopen match the already-loaded image.
            Log.Warning("[Vulkan] Could not hand the loader to glfwInitVulkanLoader; relying on dyld image matching.");
        }
    }

    /// <summary>
    /// A Vk API bound to the same library GLFW was given, so instances created through it are
    /// valid for glfwCreateWindowSurface. Null off-macOS — callers fall back to Vk.GetApi().
    /// </summary>
    public static Vk? TryCreateVk()
        => ActiveLibraryPath is null ? null : new Vk(new DefaultNativeContext(ActiveLibraryPath));

    private static IntPtr LoadVulkanLibrary()
    {
        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                ActiveLibraryPath = path;
                Log.Info($"[Vulkan] Using {path}");
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // 1) A loader shipped next to the app (wins if present).
        foreach (var dir in AppNativeDirs())
            yield return Path.Combine(dir, "libvulkan.1.dylib");

        // 2) The SDK loader — enables validation layers and all installed drivers.
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrEmpty(sdk))
            yield return Path.Combine(sdk, "lib", "libvulkan.1.dylib");

        yield return "/usr/local/lib/libvulkan.1.dylib";

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VulkanSDK");
        if (Directory.Exists(root))
        {
            // Numeric version ordering — lexicographic would sort e.g. 1.4.341 above 1.4.1000.
            var versioned = Directory.GetDirectories(root)
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0));
            foreach (var dir in versioned)
                yield return Path.Combine(dir, "macOS", "lib", "libvulkan.1.dylib");
        }

        // 3) Bundled MoltenVK (Silk.NET.MoltenVK.Native) — machines without the SDK.
        foreach (var dir in AppNativeDirs())
            yield return Path.Combine(dir, "libMoltenVK.dylib");
    }

    private static IEnumerable<string> AppNativeDirs()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;

        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        yield return Path.Combine(baseDir, "runtimes", arch, "native");
        yield return Path.Combine(baseDir, "runtimes", "osx", "native");
    }
}

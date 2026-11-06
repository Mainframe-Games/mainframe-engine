using System.Reflection;
using System.Runtime.Loader;

namespace MainframeEngine;

/// <summary>
/// Picks this OS's build of <c>MainframeEngine.Steamworks</c> (docs/design/steamworks.md#layers). Both builds share the
/// assembly name and sit next to the app in <c>steamworks/win64/</c> and <c>steamworks/posix/</c>, never in the app
/// directory itself, so the runtime's own probing misses them and asks <see cref="AssemblyLoadContext.Resolving"/>,
/// which loads the shim and its Facepunch.Steamworks assembly from the right folder.
/// </summary>
internal static class SteamworksLoader
{
    public const string ShimAssemblyName = "MainframeEngine.Steamworks";

    private static readonly Lock Sync = new();
    private static bool _registered;

    /// <summary><c>win64</c> on Windows, <c>posix</c> on macOS and Linux.</summary>
    public static string Variant => OperatingSystem.IsWindows() ? "win64" : "posix";

    /// <summary>The folder this OS's build is loaded from.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "steamworks", Variant);

    /// <summary>Path of this OS's shim assembly.</summary>
    public static string ShimPath => Path.Combine(Directory, ShimAssemblyName + ".dll");

    /// <summary>
    /// Whether the shim can be used: a NativeAOT build has it compiled into the executable (MainframeGame.props, ADR 0259);
    /// a JIT build needs <see cref="ShimPath"/> on disk.
    /// </summary>
    public static bool IsShimAvailable =>
        !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported || File.Exists(ShimPath); // disk-io: the shim assembly, beside the app

    /// <summary>Hooks the resolver once. Call before the first call into the shim (the <c>SteamApi</c> adapter's members).</summary>
    public static void Register()
    {
        lock (Sync)
        {
            if (_registered)
                return;
            AssemblyLoadContext.Default.Resolving += Resolve;
            _registered = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "JIT builds load the shim, a whole assembly, from its folder. A NativeAOT build compiles this OS's shim into the executable " +
        "(MainframeGame.props, ADR 0259), so the runtime never asks this resolver for it.")]
    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not (ShimAssemblyName or "Facepunch.Steamworks.Win64" or "Facepunch.Steamworks.Posix"))
            return null;
        var path = Path.Combine(Directory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}

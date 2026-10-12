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

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not (ShimAssemblyName or "Facepunch.Steamworks.Win64" or "Facepunch.Steamworks.Posix"))
            return null;
        var path = Path.Combine(Directory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}

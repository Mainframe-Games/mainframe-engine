using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace MainframeEngine;

/// <summary>
/// Lets Silk.NET find the native libraries of its packages (SDL2 from Ultz.Native.SDL, later Assimp, ...) in a
/// RID-agnostic build on every Linux distribution.
/// <para>
/// A RID-agnostic build (<c>dotnet build/run/test</c>) keeps package natives in
/// <c>runtimes/&lt;rid&gt;/native/</c>. Silk's <see cref="DefaultPathResolver"/> picks the folder from the RID of
/// <c>Microsoft.DotNet.PlatformAbstractions</c>, which is distro-specific (<c>ubuntu.24.04-x64</c>), and maps it
/// back to a portable RID with a hard-coded distro list that lacks Ubuntu (and Mint, Rocky, Amazon Linux, ...).
/// The deps.json of a .NET 8+ app carries no RID graph to fall back on, so Silk never probes
/// <c>runtimes/linux-x64/native</c> and SDL fails to load. macOS (<c>osx.14-arm64</c> → <c>osx-arm64</c> →
/// <c>osx</c>) and Windows (<c>win10-x64</c> → <c>win-x64</c>) are mapped correctly.
/// </para>
/// <para>
/// <see cref="Install"/> appends one resolver to Silk's process-wide <see cref="PathResolver.Default"/> that
/// probes the portable RID folders (<see cref="CandidateRids"/>). It runs after Silk's own resolvers, so it only
/// changes the outcome when they found nothing. RID-specific builds and publishes (<c>-r linux-x64</c>) copy the
/// natives next to the app and never reach it.
/// </para>
/// </summary>
internal static class SilkNativeResolver
{
    private static int _installed;

    /// <summary>Registers the resolver. Call before the first Silk native load (the <see cref="Engine"/> constructor does). Idempotent.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        if (PathResolver.Default is DefaultPathResolver resolver)
            resolver.Resolvers.Add(Resolve);
        else
            Log.Warning("[Natives] Silk.NET's default path resolver was replaced; portable runtimes/<rid>/native probing is off.");
    }

    private static IEnumerable<string> Resolve(string name)
        => CandidatePaths(AppContext.BaseDirectory, name, CandidateRids(RuntimeInformation.RuntimeIdentifier, CurrentOs(), RuntimeInformation.ProcessArchitecture));

    /// <summary>
    /// Existing <c>&lt;baseDir&gt;/runtimes/&lt;rid&gt;/native/&lt;name&gt;</c> files, in <paramref name="rids"/> order.
    /// Only bare file names are resolved; Silk passes paths it already built (base directory, NuGet cache) through
    /// here too.
    /// </summary>
    internal static IEnumerable<string> CandidatePaths(string baseDir, string name, IReadOnlyList<string> rids)
    {
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(Path.GetDirectoryName(name)))
            yield break;

        foreach (var rid in rids)
        {
            var path = Path.Combine(baseDir, "runtimes", rid, "native", name);
            if (File.Exists(path))
                yield return path;
        }
    }

    /// <summary>
    /// The runtime's own RID (portable for Microsoft-built runtimes, distro-specific for source-built ones), then the
    /// portable <c>&lt;os&gt;-&lt;arch&gt;</c> RID, then the bare OS (Ultz packages ship universal macOS binaries
    /// under <c>osx</c>).
    /// </summary>
    internal static IReadOnlyList<string> CandidateRids(string runtimeRid, string? os, Architecture architecture)
    {
        var rids = new List<string>(3);
        if (!string.IsNullOrEmpty(runtimeRid))
            rids.Add(runtimeRid);
        if (os is null)
            return rids;

        // linux-musl-x64 stays as reported by the runtime; glibc "linux-x64" is the portable fallback either way.
        var portable = $"{os}-{ArchitectureName(architecture)}";
        if (!rids.Contains(portable))
            rids.Add(portable);
        if (!rids.Contains(os))
            rids.Add(os);
        return rids;
    }

    private static string? CurrentOs()
        => OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsFreeBSD() ? "freebsd"
            : null;

    private static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}

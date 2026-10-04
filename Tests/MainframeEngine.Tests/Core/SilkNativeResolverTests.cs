using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace MainframeEngine.Tests.Core;

public sealed class SilkNativeResolverTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mf-silk-resolver", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void DistroRidFallsBackToThePortableLinuxRid()
    {
        // What Silk sees on GitHub's ubuntu-24.04 runner with a source-built runtime, and the portable fallback.
        Assert.Equal(["ubuntu.24.04-x64", "linux-x64", "linux"], SilkNativeResolver.CandidateRids("ubuntu.24.04-x64", "linux", Architecture.X64));
        Assert.Equal(["linux-x64", "linux"], SilkNativeResolver.CandidateRids("linux-x64", "linux", Architecture.X64));
        Assert.Equal(["osx-arm64", "osx"], SilkNativeResolver.CandidateRids("osx-arm64", "osx", Architecture.Arm64));
        Assert.Equal(["win-x64", "win"], SilkNativeResolver.CandidateRids("", "win", Architecture.X64));
        Assert.Equal(["custom-rid"], SilkNativeResolver.CandidateRids("custom-rid", null, Architecture.X64));
    }

    [Fact]
    public void ResolvesBareNamesToExistingRuntimesFolderFilesInRidOrder()
    {
        var linux = Path.Combine(_base, "runtimes", "linux-x64", "native");
        Directory.CreateDirectory(linux);
        File.WriteAllBytes(Path.Combine(linux, "libSDL2-2.0.so"), [0]);

        var paths = SilkNativeResolver.CandidatePaths(_base, "libSDL2-2.0.so", ["ubuntu.24.04-x64", "linux-x64", "linux"]).ToList();

        Assert.Equal([Path.Combine(linux, "libSDL2-2.0.so")], paths);
    }

    [Fact]
    public void IgnoresPathsAndMissingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_base, "runtimes", "linux-x64", "native"));

        Assert.Empty(SilkNativeResolver.CandidatePaths(_base, "libmissing.so", ["linux-x64"]));
        Assert.Empty(SilkNativeResolver.CandidatePaths(_base, Path.Combine(_base, "libSDL2-2.0.so"), ["linux-x64"]));
        Assert.Empty(SilkNativeResolver.CandidatePaths("", "libSDL2-2.0.so", ["linux-x64"]));
    }

    [Fact]
    public void InstallAddsOneResolverToSilksDefaultPathResolver()
    {
        var resolvers = Assert.IsType<DefaultPathResolver>(PathResolver.Default).Resolvers;

        SilkNativeResolver.Install();
        var count = resolvers.Count;
        SilkNativeResolver.Install();

        Assert.Equal(count, resolvers.Count);
        Assert.Contains(resolvers, r => r.Method.DeclaringType == typeof(SilkNativeResolver));
    }
}

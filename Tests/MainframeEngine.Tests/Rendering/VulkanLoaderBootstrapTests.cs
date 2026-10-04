using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Rendering;

public sealed class VulkanLoaderBootstrapTests
{
    private const string Base = "/app";

    [Fact]
    public void ProbeOrderIsAppLoaderThenSdkThenBundledMoltenVk()
    {
        var paths = VulkanLoaderBootstrap.CandidatePaths(Base, "/sdk", ["/home/VulkanSDK/1.4.0"], Architecture.Arm64).ToList();

        Assert.Equal(Path.Combine(Base, "libvulkan.1.dylib"), paths[0]);
        var sdk = paths.IndexOf(Path.Combine("/sdk", "lib", "libvulkan.1.dylib"));
        var usrLocal = paths.IndexOf("/usr/local/lib/libvulkan.1.dylib");
        var home = paths.IndexOf(Path.Combine("/home/VulkanSDK/1.4.0", "macOS", "lib", "libvulkan.1.dylib"));
        var moltenVk = paths.IndexOf(Path.Combine(Base, "libMoltenVK.dylib"));

        Assert.True(sdk > 0 && usrLocal > sdk && home > usrLocal && moltenVk > home, string.Join("\n", paths));
        Assert.EndsWith("libMoltenVK.dylib", paths[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void SdkVersionsAreTriedNewestFirstNumerically()
    {
        var paths = VulkanLoaderBootstrap.CandidatePaths(Base, null,
            ["/h/VulkanSDK/1.4.341", "/h/VulkanSDK/1.4.1000", "/h/VulkanSDK/notes"], Architecture.Arm64).ToList();

        var i1000 = paths.FindIndex(p => p.Contains("1.4.1000", StringComparison.Ordinal));
        var i341 = paths.FindIndex(p => p.Contains("1.4.341", StringComparison.Ordinal));
        Assert.True(i1000 >= 0 && i1000 < i341, "1.4.1000 must be probed before 1.4.341");
    }

    [Theory]
    [InlineData(Architecture.Arm64, "osx-arm64")]
    [InlineData(Architecture.X64, "osx-x64")]
    public void RidSpecificNativeFolderMatchesArchitecture(Architecture arch, string rid)
    {
        var paths = VulkanLoaderBootstrap.CandidatePaths(Base, null, [], arch);

        Assert.Contains(Path.Combine(Base, "runtimes", rid, "native", "libMoltenVK.dylib"), paths);
        Assert.Contains(Path.Combine(Base, "runtimes", "osx", "native", "libMoltenVK.dylib"), paths);
    }
}

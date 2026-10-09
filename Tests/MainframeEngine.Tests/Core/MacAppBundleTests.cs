namespace MainframeEngine.Tests.Core;

public sealed class MacAppBundleTests
{
    // The test host runs unbundled (dotnet/testhost), so it has no bundle icon: GameHost keeps window.icon there.
    [Fact]
    public void AnUnbundledProcessHasNoBundleIcon() => Assert.False(MacAppBundle.HasIcon);
}

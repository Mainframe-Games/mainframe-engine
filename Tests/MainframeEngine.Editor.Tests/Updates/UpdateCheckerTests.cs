using System.Net;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateCheckerTests
{
    private static async Task<(UpdateCheckResult Result, StubHandler Handler)> Check(StubHandler handler, string current = "1.0.0", string? rid = "linux-x64")
    {
        using var http = new HttpClient(handler);
        var result = await new UpdateChecker(new ReleaseFeed(http, current), current, rid).CheckAsync(TestContext.Current.CancellationToken);
        return (result, handler);
    }

    [Fact]
    public async Task ANewerReleaseWithThisPlatformsBuildIsAnUpdate()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0")));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.IsUpdate);
        Assert.Equal("MainframeEngine-1.1.0-linux-x64.tar.gz", result.Asset!.Name);
        Assert.Equal("Mainframe Engine v1.1.0 is available (you have v1.0.0).", result.Describe("1.0.0"));
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")] // never a downgrade
    public async Task TheSameOrAnOlderReleaseIsUpToDate(string current)
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0")), current);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.IsUpdate);
        Assert.Equal($"Mainframe Engine v{current} is the latest version.", result.Describe(current));
    }

    [Fact]
    public async Task ALatestReleaseWithoutAReleaseTagIsUpToDate()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("nightly")));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task DevelopmentBuildsNeverAskGitHub()
    {
        var (result, handler) = await Check(StubHandler.Json(ReleaseFixtures.Latest()), "0.0.0-dev");
        Assert.Equal(UpdateCheckStatus.DevelopmentBuild, result.Status);
        Assert.Empty(handler.Requests);
        Assert.Contains("development build", result.Describe("0.0.0-dev"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreleasedPlatformsNeverAskGitHub()
    {
        var (result, handler) = await Check(StubHandler.Json(ReleaseFixtures.Latest()), rid: null);
        Assert.Equal(UpdateCheckStatus.UnsupportedPlatform, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AReleaseWithoutThisPlatformsBuildIsUnsupported()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0", true, "osx-arm64")));
        Assert.Equal(UpdateCheckStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("The latest release has no editor build for this platform.", result.Describe("1.0.0"));
    }

    [Fact]
    public async Task AnAssetWithoutAChecksumIsRefused()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0", digests: false)));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("checksum", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailuresBecomeFailedResults()
    {
        var (status, _) = await Check(StubHandler.Status(HttpStatusCode.TooManyRequests));
        Assert.Equal(UpdateCheckStatus.Failed, status.Status);
        Assert.Contains("rate limit", status.Describe("1.0.0"), StringComparison.Ordinal);

        var (network, _) = await Check(StubHandler.Throws(new HttpRequestException("offline")));
        Assert.Equal(UpdateCheckStatus.Failed, network.Status);
        Assert.Contains("offline", network.Error, StringComparison.Ordinal);

        var (garbage, _) = await Check(StubHandler.Json("<html>"));
        Assert.Equal(UpdateCheckStatus.Failed, garbage.Status);
    }
}

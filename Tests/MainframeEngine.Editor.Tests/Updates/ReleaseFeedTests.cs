using System.Net;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class ReleaseFeedTests
{
    [Fact]
    public void ParsesTheRecordedRelease()
    {
        var release = ReleaseFeed.Parse(ReleaseFixtures.Bytes(ReleaseFixtures.Recorded))!;

        Assert.Equal(new ReleaseVersion(1, 0, 0), release.Version);
        Assert.Equal("v1.0.0", release.Tag);
        Assert.StartsWith("## What's Changed", release.Notes, StringComparison.Ordinal);
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/tag/v1.0.0", release.PageUrl.AbsoluteUri);
        Assert.Equal(3, release.Assets.Count);
        var linux = release.FindAsset("MainframeEngine-1.0.0-linux-x64.tar.gz")!;
        Assert.Equal(56920893, linux.Size);
        Assert.Equal("dfdf31c3d560f72aa9850e71af5511fe6e03f63e0e5fb052010fe2b16fa2f89e", linux.Sha256);
        // Upper-case hex is normalised; a non-SHA-256 digest is no digest.
        Assert.Equal("857ef126ffc089afcc78405195c60550ded974703a418ec5e03eaf5c61afe6da", release.FindAsset("MainframeEngine-1.0.0-osx-arm64.tar.gz")!.Sha256);
        Assert.Null(release.FindAsset("MainframeEngine-1.0.0-win-x64.zip")!.Sha256);
        Assert.Null(release.FindAsset("MainframeEngine-1.0.0-osx-x64.tar.gz"));
    }

    [Theory]
    [InlineData("v1.1.0-beta.1")]
    [InlineData("nightly")]
    [InlineData("1.1.0")] // release tags always start with v
    public void TagsThatAreNotReleaseVersionsGiveNoRelease(string tag) =>
        Assert.Null(ReleaseFeed.Parse(ReleaseFixtures.Bytes(ReleaseFixtures.Latest(tag))));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "v1.0.0" }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "html_url": "x", "assets": [] }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "html_url": "https://github.com/x", "assets": [ { "name": "a" } ] }""")]
    public void MalformedResponsesThrowUpdateException(string json) =>
        Assert.Throws<UpdateException>(() => ReleaseFeed.Parse(ReleaseFixtures.Bytes(json)));

    [Fact]
    public async Task GetLatestSendsGitHubsHeaders()
    {
        var handler = StubHandler.Json(ReleaseFixtures.Latest());
        using var http = new HttpClient(handler);

        var release = await new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ReleaseVersion(1, 1, 0), release!.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ReleaseFeed.LatestReleaseUri, request.RequestUri);
        Assert.Equal("MainframeEngine-Editor/1.0.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    public async Task ErrorStatusesThrowUpdateException(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(StubHandler.Status(status));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailuresThrowUpdateException()
    {
        using var http = new HttpClient(StubHandler.Throws(new HttpRequestException("No route to host")));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("No route to host", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutThrowsUpdateException()
    {
        using var http = new HttpClient(StubHandler.Throws(new TaskCanceledException("timed out")));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("did not answer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotAnUpdateError()
    {
        using var http = new HttpClient(StubHandler.Json(ReleaseFixtures.Latest()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(cancelled.Token));
    }
}

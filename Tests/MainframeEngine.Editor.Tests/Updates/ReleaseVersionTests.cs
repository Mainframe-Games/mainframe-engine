namespace MainframeEngine.Editor.Tests.Updates;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("v10.0.42", 10, 0, 42)]
    public void ParsesReleaseVersions(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
        Assert.Equal($"{major}.{minor}.{patch}", version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("0.0.0-dev")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3+build")]
    [InlineData("1.-2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("nightly")]
    public void RejectsAnythingElse(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void ComparesNumerically()
    {
        Assert.True(new ReleaseVersion(1, 10, 0) > new ReleaseVersion(1, 9, 9));
        Assert.True(new ReleaseVersion(2, 0, 0) > new ReleaseVersion(1, 99, 99));
        Assert.True(new ReleaseVersion(1, 0, 1) >= new ReleaseVersion(1, 0, 1));
        Assert.True(new ReleaseVersion(1, 0, 0) < new ReleaseVersion(1, 0, 1));
        Assert.Equal(0, new ReleaseVersion(1, 2, 3).CompareTo(new ReleaseVersion(1, 2, 3)));
    }
}

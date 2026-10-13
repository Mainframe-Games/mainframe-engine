using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FfmpegCapabilitiesTests
{
    [Theory]
    [InlineData("ffmpeg version 9.0.1 Copyright (c) 2000-2026 the FFmpeg developers", 9)]
    [InlineData("ffmpeg version 7.1.1 Copyright (c) 2000-2025 the FFmpeg developers", 7)]
    [InlineData("ffmpeg version n7.0-31-g1234 Copyright", 7)]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright", 6)]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright", 4)]
    [InlineData("ffmpeg version 2025-01-15-git-abc123-full_build-www.gyan.dev Copyright", null)]
    [InlineData("ffmpeg version N-118000-g1234abcd Copyright", null)]
    [InlineData("", null)]
    public void ParsesTheMajorVersionFromTheBanner(string banner, int? expectedMajor)
    {
        Assert.Equal(expectedMajor, FfmpegCapabilities.Parse(banner).Major);
    }

    [Theory]
    [InlineData("ffmpeg version 9.0.1", true)]
    [InlineData("ffmpeg version 7.0", true)]
    [InlineData("ffmpeg version 6.1.1", false)]
    [InlineData("ffmpeg version N-118000-g1234abcd", true)]
    public void OptionFilesNeedFfmpeg7AndGitBuildsAreAssumedModern(string banner, bool expected)
    {
        Assert.Equal(expected, FfmpegCapabilities.Parse(banner).SupportsOptionFiles);
    }

    [Fact]
    public void FilterScriptArgumentsUseTheSyntaxTheBinaryUnderstands()
    {
        Assert.Equal(["-/filter_complex", "/tmp/g.txt"], FfmpegCapabilities.Parse("ffmpeg version 9.0").FilterComplexScriptArguments("/tmp/g.txt"));
        Assert.Equal(["-filter_complex_script", "/tmp/g.txt"], FfmpegCapabilities.Parse("ffmpeg version 6.0").FilterComplexScriptArguments("/tmp/g.txt"));
    }
}

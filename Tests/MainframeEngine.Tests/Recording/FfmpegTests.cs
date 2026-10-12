namespace MainframeEngine.Tests.Recording;

/// <summary>ffmpeg lookup order, encoder preference per OS, and the exact command lines (ffmpeg itself never runs here).</summary>
public sealed class FfmpegTests
{
    [Fact]
    public void ExplicitPathWinsThenEnvThenPathThenKnownFolders()
    {
        var exists = new HashSet<string> { "/x/ffmpeg", "/env/ffmpeg", "/usr/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg" };
        Assert.Equal("/x/ffmpeg", FfmpegResolver.Resolve("/x/ffmpeg", "/env/ffmpeg", "/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/env/ffmpeg", FfmpegResolver.Resolve(null, "/env/ffmpeg", "/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/usr/bin/ffmpeg", FfmpegResolver.Resolve(null, null, "/nope:/usr/bin", OSPlatformKind.MacOS, exists.Contains));
        Assert.Equal("/opt/homebrew/bin/ffmpeg", FfmpegResolver.Resolve(null, null, "/nope", OSPlatformKind.MacOS, exists.Contains));
    }

    [Fact]
    public void AnExplicitPathThatDoesNotExistIsAnError()
    {
        var ex = Assert.Throws<FfmpegNotFoundException>(() =>
            FfmpegResolver.Resolve("/missing/ffmpeg", null, "/usr/bin", OSPlatformKind.Linux, _ => false));
        Assert.Contains("/missing/ffmpeg", ex.Message);
    }

    [Theory]
    [InlineData("MacOS", "brew install ffmpeg")]
    [InlineData("Windows", "winget install Gyan.FFmpeg")]
    [InlineData("Linux", "sudo apt install ffmpeg")]
    public void NotFoundNamesThePlatformInstallCommand(string platform, string command)
    {
        // A string, not the enum: OSPlatformKind is internal and a public test method cannot take it.
        var os = Enum.Parse<OSPlatformKind>(platform);
        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegResolver.Resolve(null, null, "", os, _ => false));
        Assert.Contains(command, ex.Message);
    }

    [Fact]
    public void WindowsLooksForTheExeName()
    {
        Assert.Equal(@"C:\ff\ffmpeg.exe", FfmpegResolver.Resolve(null, null, @"C:\ff", OSPlatformKind.Windows, p => p == @"C:\ff\ffmpeg.exe"));
    }

    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void ParsesEncoderNamesFromTheListing()
    {
        Assert.Equal(["libx264", "h264_videotoolbox", "aac"], EncoderSelector.ParseEncoders(EncodersOutput));
    }

    [Fact]
    public void HardwareComesFirstThenSoftwareFallbacks()
    {
        var available = EncoderSelector.ParseEncoders(EncodersOutput).ToHashSet();
        Assert.Equal(["h264_videotoolbox", "libx264"], EncoderSelector.Candidates(OSPlatformKind.MacOS, available));
        Assert.Equal(["libx264"], EncoderSelector.Candidates(OSPlatformKind.Windows, available));
    }

    [Fact]
    public void WindowsOrderIsNvencQsvAmfMf()
    {
        var all = new HashSet<string> { "h264_mf", "h264_amf", "h264_qsv", "h264_nvenc", "libopenh264" };
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"], EncoderSelector.Candidates(OSPlatformKind.Windows, all));
    }

    [Fact]
    public void BitrateIsAQuarterBitPerPixelPerFrame()
    {
        Assert.Equal(31_104_000, FfmpegArgs.Bitrate(1920, 1080, 60));
    }

    [Fact]
    public void EncodeReadsRawFramesFromStdinWithOneSecondGop()
    {
        var args = FfmpegArgs.Encode("h264_videotoolbox", 1920, 1080, 60, PixelLayout.Bgra, "/r/video.mkv");
        Assert.Equal(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", "1920x1080", "-r", "60", "-i", "-",
            "-an", "-c:v", "h264_videotoolbox", "-b:v", "31104000", "-g", "60", "-pix_fmt", "yuv420p",
            "-progress", "pipe:1", "-nostats", "/r/video.mkv",
        ], args);
    }

    [Fact]
    public void Libx264UsesQualityModeNotBitrate()
    {
        var args = FfmpegArgs.Encode("libx264", 1280, 720, 30, PixelLayout.Rgba, "/v.mkv");
        Assert.Contains("rgba", args);
        Assert.Equal("ultrafast", args[args.ToList().IndexOf("-preset") + 1]);
        Assert.Equal("14", args[args.ToList().IndexOf("-crf") + 1]);
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void OddDimensionsAreCroppedToEven()
    {
        var args = FfmpegArgs.Encode("libx264", 1281, 721, 60, PixelLayout.Bgra, "/v.mkv").ToList();
        Assert.Equal("crop=1280:720:0:0", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("1281x721", args[args.IndexOf("-s") + 1]); // input size is still the real one
    }

    [Fact]
    public void MuxCopiesVideoAndEncodesAac()
    {
        Assert.Equal(
        [
            "-hide_banner", "-loglevel", "error", "-y", "-i", "/r/video.mkv", "-i", "/r/audio.wav",
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "/r/out.mp4",
        ], FfmpegArgs.Mux("/r/video.mkv", "/r/audio.wav", "/r/out.mp4"));
    }

    [Fact]
    public void ProbeEncodesATenthOfASecondToNull()
    {
        Assert.Equal(
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-"],
            FfmpegArgs.Probe("h264_nvenc"));
    }
}

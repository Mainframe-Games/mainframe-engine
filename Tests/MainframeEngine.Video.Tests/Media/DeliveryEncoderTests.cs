using System.Runtime.InteropServices;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Tests.Media;

public sealed class DeliveryEncoderTests
{
    private const string Listing = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void ParseEncodersListsNamesAndSkipsTheLegend() =>
        Assert.Equal(["libx264", "libopenh264", "h264_videotoolbox", "h264_nvenc", "aac"], DeliveryEncoder.ParseEncoders(Listing));

    [Fact]
    public void CandidatesPutLibx264FirstThenHardwareThenOpenH264()
    {
        var available = DeliveryEncoder.ParseEncoders(Listing);
        Assert.Equal(["libx264", "h264_videotoolbox", "libopenh264"], DeliveryEncoder.Candidates(OSPlatform.OSX, available));
        Assert.Equal(["libx264", "h264_nvenc", "libopenh264"], DeliveryEncoder.Candidates(OSPlatform.Linux, available));
    }

    [Fact]
    public void CandidatesOnWindowsTryEveryVendorInOrder() =>
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"],
            DeliveryEncoder.Candidates(OSPlatform.Windows, ["h264_mf", "h264_amf", "h264_qsv", "h264_nvenc", "libopenh264"]));

    [Fact]
    public void CandidatesAreEmptyWithoutAnH264Encoder() =>
        Assert.Empty(DeliveryEncoder.Candidates(OSPlatform.OSX, ["aac", "libvpx-vp9"]));

    [Fact]
    public void Libx264KeepsConstantQuality() =>
        Assert.Equal(["-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p"], DeliveryEncoder.VideoArguments("libx264", 12_000_000));

    [Fact]
    public void OtherEncodersGetABitrateTarget() =>
        Assert.Equal(["-c:v", "h264_videotoolbox", "-b:v", "12000000", "-maxrate", "18000000", "-bufsize", "24000000", "-pix_fmt", "yuv420p"],
            DeliveryEncoder.VideoArguments("h264_videotoolbox", 12_000_000));

    [Theory]
    [InlineData(1920, 1080, 60.0, 12_441_600)]
    [InlineData(1280, 720, 30.0, 2_764_800)]
    [InlineData(3840, 2160, 60.0, 49_766_400)]
    [InlineData(320, 180, 30.0, 2_000_000)]
    [InlineData(0, 0, 0.0, 12_000_000)]
    public void TargetBitrateIsATenthOfABitPerPixelClamped(int w, int h, double fps, long expected) =>
        Assert.Equal(expected, DeliveryEncoder.TargetBitrate(w, h, fps));

    [Fact]
    public void ProbeArgumentsEncodeOneTinyFrameToNowhere() =>
        Assert.Equal(["-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-"], DeliveryEncoder.ProbeArguments("h264_nvenc"));

    [Fact]
    public void TheNoEncoderMessageNamesLibx264AndTheInstallCommand()
    {
        Assert.Contains("brew install ffmpeg", DeliveryEncoder.NoEncoderMessage(OSPlatform.OSX), StringComparison.Ordinal);
        Assert.Contains("libx264", DeliveryEncoder.NoEncoderMessage(OSPlatform.OSX), StringComparison.Ordinal);
        Assert.Contains("winget install", DeliveryEncoder.NoEncoderMessage(OSPlatform.Windows), StringComparison.Ordinal);
        Assert.Contains("package manager", DeliveryEncoder.NoEncoderMessage(OSPlatform.Linux), StringComparison.Ordinal);
    }
}

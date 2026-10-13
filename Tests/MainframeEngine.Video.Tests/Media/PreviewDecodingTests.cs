using MainframeEngine.Video.Media.Preview;

namespace MainframeEngine.Video.Tests.Media;

public sealed class PreviewDecodingTests
{
    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 960, 540)]
    [InlineData(1280, 720, 960, 540)]
    [InlineData(640, 360, 640, 360)]
    [InlineData(721, 405, 720, 404)]
    [InlineData(1000, 1000, 960, 960)]
    public void PreviewSizeCapsTheWidthKeepsTheAspectAndStaysEven(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), PreviewDecoding.PreviewSize(w, h));

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-4, 4)]
    public void PreviewSizeRejectsADegenerateSource(int w, int h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.PreviewSize(w, h));

    [Theory]
    [InlineData(60.0, 30.0)]
    [InlineData(30.0, 30.0)]
    [InlineData(29.97, 29.97)]
    [InlineData(24.0, 24.0)]
    [InlineData(0.0, 30.0)]
    [InlineData(double.NaN, 30.0)]
    public void PreviewFrameRateIsTheSourceRateCappedAt30(double source, double expected) =>
        Assert.Equal(expected, PreviewDecoding.PreviewFrameRate(source));

    [Fact]
    public void VideoArgumentsSeekScaleAndEmitRawRgba() =>
        Assert.Equal(
            ["-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-an", "-vf", "fps=30,scale=960:540:flags=bilinear",
             "-pix_fmt", "rgba", "-f", "rawvideo", "-"],
            PreviewDecoding.VideoArguments("/r/a.mp4", 4.86, 12.04, 960, 540, 30));

    [Fact]
    public void AudioArgumentsFadeEachEndAndEmitStereoFloat() =>
        Assert.Equal(
            ["-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-vn", "-af", "afade=t=in:st=0:d=0.008,afade=t=out:st=7.172:d=0.008",
             "-ac", "2", "-ar", "48000", "-f", "f32le", "-"],
            PreviewDecoding.AudioArguments("/r/a.mp4", 4.86, 12.04, 48000));

    [Fact]
    public void TheAudioFadeShrinksForVeryShortSegments() =>
        Assert.Contains("afade=t=in:st=0:d=0.005,afade=t=out:st=0.005:d=0.005", PreviewDecoding.AudioArguments("/r/a.mp4", 1.0, 1.01, 48000));

    [Fact]
    public void FrameArgumentsDecodeOneScaledFrame() =>
        Assert.Equal(
            ["-ss", "3.5", "-i", "/r/a.mp4", "-frames:v", "1", "-an", "-vf", "scale=960:540:flags=bilinear",
             "-pix_fmt", "rgba", "-f", "rawvideo", "-"],
            PreviewDecoding.FrameArguments("/r/a.mp4", 3.5, 960, 540));

    [Fact]
    public void AnEmptyRangeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.VideoArguments("/r/a.mp4", 5, 5, 960, 540, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.AudioArguments("/r/a.mp4", 6, 5, 48000));
    }
}

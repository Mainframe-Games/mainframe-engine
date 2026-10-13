using MainframeEngine.Video.Analysis;
using Whisper.net.Ggml;

namespace MainframeEngine.Video.Tests.Analysis;

public sealed class WhisperModelInfoTests
{
    [Fact]
    public void EveryModelHasInfoWithAUniqueKey()
    {
        var infos = Enum.GetValues<WhisperModel>().Select(WhisperModelInfo.For).ToList();

        Assert.Equal(infos.Count, infos.Select(i => i.Key).Distinct().Count());
        Assert.All(infos, i => Assert.True(i.ApproximateBytes > 0));
        Assert.Equivalent(Enum.GetValues<WhisperModel>(), WhisperModelInfo.All.Select(i => i.Model));
    }

    [Fact]
    public void BaseEnIsTheDefault()
    {
        Assert.Equal(WhisperModel.BaseEn, WhisperModelInfo.Default.Model);
        Assert.Equal("base.en", WhisperModelInfo.Default.Key);
        Assert.Equal(GgmlType.BaseEn, WhisperModelInfo.Default.GgmlType);
        Assert.Equal("ggml-base.en.bin", WhisperModelInfo.Default.FileName);
    }

    [Theory]
    [InlineData("tiny.en", WhisperModel.TinyEn)]
    [InlineData("small.en", WhisperModel.SmallEn)]
    [InlineData("MEDIUM.EN", WhisperModel.MediumEn)]
    [InlineData("large", WhisperModel.BaseEn)]
    [InlineData("", WhisperModel.BaseEn)]
    [InlineData(null, WhisperModel.BaseEn)]
    public void ParseMapsKeysAndFallsBackToTheDefault(string? key, WhisperModel expected)
    {
        Assert.Equal(expected, WhisperModelInfo.Parse(key));
    }

    [Fact]
    public void EveryModelHasAMatchingAlignmentHeadsPreset()
    {
        foreach (var model in Enum.GetValues<WhisperModel>())
        {
            Assert.NotEqual(Whisper.net.WhisperAlignmentHeadsPreset.None, WhisperTranscriber.AlignmentHeadsFor(model)); // every model needs DTW heads
        }
    }

    [Fact]
    public void DisplayNameShowsTheApproximateSizeInMegabytes()
    {
        Assert.Equal("base.en (~148 MB)", WhisperModelInfo.For(WhisperModel.BaseEn).DisplayName);
    }
}

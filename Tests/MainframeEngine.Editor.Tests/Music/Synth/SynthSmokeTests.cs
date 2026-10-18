using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.SynthTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

public sealed class SynthSmokeTests
{
    [Fact]
    public void TheInitPatchPlaysANoteAndReleasesIt()
    {
        var synth = Create();
        var output = Note(synth, 57, 1.0, hold: 0.5);
        Assert.All(output, v => Assert.True(float.IsFinite(v)));
        Assert.True(Rms(output, Rate / 10, Rate / 2) > 0.05, $"sounding: {Rms(output, Rate / 10, Rate / 2)}");
        Assert.True(Rms(output, (int)(0.8 * Rate), Rate) < 1e-4, "released");
        Assert.Equal(0, synth.ActiveVoices);
    }
}

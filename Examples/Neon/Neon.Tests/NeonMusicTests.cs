using MainframeEngine;
using MainframeEngine.Audio.Music;

namespace Neon.Tests;

/// <summary>The stereo's songs: their stems (deterministic, seamless, mixed with headroom) and the adaptive-music manifest.</summary>
public sealed class NeonMusicTests
{
    public static TheoryData<int> Songs => [0, 1];

    [Theory]
    [MemberData(nameof(Songs))]
    public void StemsAreDeterministicWholeBarsAndMixedWithHeadroom(int index)
    {
        var song = NeonMusic.Songs[index];
        Assert.Equal(0, NeonMusic.Rate * 60 % song.Tempo); // a whole number of frames a beat: the grid is exact
        var stems = NeonMusic.RenderStems(index, 3);
        Assert.Equal(NeonMusic.Layers.Length, stems.Length);
        Assert.All(stems, s => Assert.Equal(NeonMusic.LoopFrames(song) * 2, s.Length));
        Assert.Equal(stems[0], NeonMusic.RenderStems(index, 3)[0]);
        Assert.NotEqual(stems[0], NeonMusic.RenderStems(index, 4)[0]); // the drums' noise follows the seed

        var mix = NeonMusic.Mix(stems);
        Assert.All(mix, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(NeonMusic.MixPeak, SignalTools.Peak(mix), 3);
        var rms = SignalTools.Rms(mix);
        Assert.InRange(rms, 0.06f, 0.3f);
        for (var l = 0; l < stems.Length; l++)
            Assert.True(SignalTools.Rms(stems[l]) > rms * 0.1f, $"{NeonMusic.Layers[l]} is nearly silent");
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void TheLoopIsSeamless(int index)
    {
        // Each pass's tails (pad releases, echoes) are folded onto the start, so the loop point is no jump.
        var mix = NeonMusic.Mix(NeonMusic.RenderStems(index, 1));
        var frames = mix.Length / 2;
        var steps = new float[frames - 1];
        for (var i = 1; i < frames; i++)
            steps[i - 1] = MathF.Abs(mix[i * 2] - mix[(i - 1) * 2]);
        Array.Sort(steps);
        var seam = MathF.Abs(mix[0] - mix[(frames - 1) * 2]);
        Assert.True(seam <= steps[(int)(steps.Length * 0.995)], $"seam {seam}");
        // And the tail is really there: the first beat is not silent before the downbeat's attack.
        Assert.True(MathF.Abs(mix[(frames - 1) * 2]) > 0f);
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void TheManifestCompilesWithAMuffleOnEveryLayer(int index)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"neon-music-manifest-{Guid.NewGuid():N}");
        try
        {
            var music = NeonMusic.CreateMusic(index, folder: folder, inMemory: true);
            Assert.Empty(music.Plan.Problems);
            Assert.True(music.Preload());
            var manifest = music.Manifest;
            Assert.Equal(NeonMusic.Songs[index].Tempo, manifest.Tempo);
            var section = Assert.Single(manifest.Sections);
            Assert.True(section.Loop);
            Assert.Equal(NeonMusic.Bars, section.Bars);
            Assert.Equal(NeonMusic.Layers, manifest.Layers.Select(l => l.Id));
            Assert.All(manifest.Layers, l => Assert.Equal(MusicLayerFilter.LowPass, l.Filter));
            Assert.Equal(NeonMusic.MuffleParameter, Assert.Single(manifest.Parameters).Name);
            Assert.Equal(NeonMusic.Layers.Length, manifest.Curves.Count(c => c.Target == MusicCurveTarget.Cutoff));
            Assert.Equal(NeonMusic.Layers.Length, Directory.GetFiles(folder, "*.wav").Length);

            // A second call reuses the cached stems.
            var stamp = File.GetLastWriteTimeUtc(Directory.GetFiles(folder, "*.wav")[0]);
            NeonMusic.CreateMusic(index, folder: folder);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Directory.GetFiles(folder, "*.wav")[0]));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a stream may still hold a file
            }
        }
    }

    [Fact]
    public void TheSongsAreDifferent()
    {
        Assert.NotEqual(NeonMusic.Songs[0].Tempo, NeonMusic.Songs[1].Tempo);
        Assert.NotEqual(NeonMusic.Songs[0].Style, NeonMusic.Songs[1].Style);
        Assert.All(NeonMusic.Songs, s => Assert.Equal(NeonMusic.Bars, s.Chords.Length));
        Assert.All(NeonMusic.Songs, s => Assert.All(s.Melody, n => Assert.InRange(n.Beat + n.Length, 0f, NeonMusic.Bars * 4f)));
    }
}

using System.Globalization;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using VideoCapture = MainframeEngine.Video.Capture;

namespace MainframeEngine.Editor;

/// <summary>
/// The <c>video-timeline</c> golden's recording, built in code so the render tests need neither ffmpeg nor a video file:
/// a 60 s 1080p project with kept, silence, gameplay and manual regions (the manual cut strikes four words), a synthetic waveform with a silent stretch, a
/// recording sidecar with markers, a short transcript, and a preview decoder that "decodes" colour bars. Everything is
/// computed from fixed numbers, so every run draws the same pixels.
/// </summary>
internal static class VideoGolden
{
    public const double Duration = 60;

    /// <summary>Where the playhead sits for the capture: inside the word "building", which the transcript highlights.</summary>
    public const double Playhead = 21.3;

    public const string Notice = "Cut 2 stretches of idle gameplay, 0:12.5 removed.";

    /// <summary>The document: never probed, never saved (the source path does not exist).</summary>
    public static VideoDocument Document()
    {
        var source = new SourceInfo(Path.Combine(GoldenPaths.Root, "forest-run.mp4"), new string('0', 64), Duration, 1920, 1080, 60);
        Segment[] segments =
        [
            Segment.Create(0, 6.0, true, SegmentOrigin.Auto),
            Segment.Create(6.0, 9.6, false, SegmentOrigin.Auto, Reason("silence {0:0.00}s", 9.6 - 6.0)),
            Segment.Create(9.6, 14.5, true, SegmentOrigin.Auto),
            Segment.Create(14.5, 19.0, false, SegmentOrigin.Gameplay, Reason("loading {0:0.0}s", 19.0 - 14.5)),
            Segment.Create(19.0, 31.0, true, SegmentOrigin.Auto),
            Segment.Create(31.0, 34.5, false, SegmentOrigin.Manual),
            Segment.Create(34.5, 44.0, true, SegmentOrigin.Manual),
            Segment.Create(44.0, 52.0, false, SegmentOrigin.Gameplay, Reason("idle {0:0.0}s", 52.0 - 44.0)),
            Segment.Create(52.0, Duration, true, SegmentOrigin.Auto),
        ];
        Word[] words =
        [
            new("so", 19.4, 19.7, 0.92, 19.5), new("um", 19.9, 20.3, 0.71, 20.0), new("today", 20.4, 20.8, 0.95, 20.5),
            new("we're", 20.9, 21.1, 0.9, 20.95), new("building", 21.2, 21.7, 0.96, 21.3), new("the", 21.8, 21.9, 0.9, 21.85),
            new("forest", 22.0, 22.5, 0.94, 22.1), new("level.", 22.6, 23.1, 0.93, 22.7),
            new("Hang", 31.6, 31.9, 0.8, 31.7), new("on,", 32.0, 32.3, 0.8, 32.1), new("wrong", 32.4, 32.8, 0.85, 32.5),
            new("take.", 32.9, 33.3, 0.85, 33.0),
            new("Here's", 26.0, 26.3, 0.9, 26.1), new("the", 26.4, 26.5, 0.9, 26.45), new("boss.", 26.6, 27.1, 0.92, 26.7),
        ];
        var project = new VideoProject(source, segments, words, DetectionSettings.Default);
        var capture = new VideoCapture.CaptureSidecar(VideoCapture.CaptureMode.Live, Duration, 60, 1920, 1080, "Forest",
            "res://Scenes/Forest.mscene",
            Input: [new(0, 14), new(19, 44), new(52, Duration)],
            Loading: [new(14.5, 19)],
            Paused: [],
            Keep: [],
            Skip: [],
            Markers: [new(26, "boss intro"), new(41, "chest opens")]);
        return VideoDocument.FromProject(project, projectPath: null, capture, []);
    }

    private static string Reason(string format, double seconds) => string.Format(CultureInfo.InvariantCulture, format, seconds);

    /// <summary>
    /// The waveform the extractor would make (its rate, bucket and levels) of a voice: syllable bursts under a slow phrase
    /// envelope, silent where the silence cut is. Integer hashing for the grain, so it is the same on every machine.
    /// </summary>
    public static Waveform Waveform()
    {
        const int sampleRate = WaveformExtractor.SampleRate;
        const int perBucket = WaveformExtractor.FinestSamplesPerBucket;
        var count = (int)(Duration * sampleRate / perBucket);
        var peaks = new Peak[count];
        for (var i = 0; i < count; i++)
        {
            var t = (double)i * perBucket / sampleRate;
            var phrase = 0.55 + 0.45 * Math.Sin(t * 0.9);
            var syllable = Math.Pow(Math.Abs(Math.Sin(t * Math.PI * 2.6)), 0.6);
            var grain = (Hash(i) & 0xFF) / 255.0;
            var level = t is > 6.4 and < 9.2 ? 0.01 : phrase * syllable * (0.75 + 0.25 * grain);
            var amplitude = (short)Math.Clamp(level * 26000, 60, short.MaxValue);
            var skew = (short)(amplitude * (0.8 + 0.2 * ((Hash(i + count) & 0xFF) / 255.0)));
            peaks[i] = new Peak((short)-skew, amplitude);
        }

        return MainframeEngine.Video.Media.Waveform.Build(peaks, perBucket, sampleRate, WaveformExtractor.LevelFactor, WaveformExtractor.LevelCount);
    }

    private static uint Hash(int value)
    {
        var x = (uint)value * 0x9E3779B1u;
        x ^= x >> 15;
        x *= 0x85EBCA77u;
        x ^= x >> 13;
        return x;
    }

    /// <summary>
    /// "Decodes" any request into one frame of colour bars at the size the preview asked for (its <c>scale=W:H</c>
    /// filter): the paused preview's first-frame scrub then shows a picture without ffmpeg.
    /// </summary>
    public sealed class Decoders : IVideoDecoderFactory
    {
        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            var (width, height) = ScaleOf(arguments);
            return new Decoder(width > 0 ? Bars(width, height) : []);
        }

        private static (int Width, int Height) ScaleOf(IReadOnlyList<string> arguments)
        {
            foreach (var argument in arguments)
            {
                var at = argument.IndexOf("scale=", StringComparison.Ordinal);
                if (at < 0)
                    continue;
                var size = argument[(at + "scale=".Length)..].Split(':');
                if (size.Length >= 2 && int.TryParse(size[0], CultureInfo.InvariantCulture, out var w) &&
                    int.TryParse(size[1], CultureInfo.InvariantCulture, out var h))
                    return (w, h);
            }

            return (0, 0);
        }

        // SMPTE-like bars over a grey ramp.
        private static byte[] Bars(int width, int height)
        {
            ReadOnlySpan<uint> bars = [0xC0C0C0, 0xC0C000, 0x00C0C0, 0x00C000, 0xC000C0, 0xC00000, 0x0000C0];
            var pixels = new byte[width * height * 4];
            var barsHeight = height * 2 / 3;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    if (y < barsHeight)
                    {
                        var rgb = bars[x * bars.Length / width];
                        pixels[i] = (byte)(rgb >> 16);
                        pixels[i + 1] = (byte)(rgb >> 8);
                        pixels[i + 2] = (byte)rgb;
                    }
                    else
                    {
                        var grey = (byte)(x * 255 / Math.Max(1, width - 1));
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
                    }

                    pixels[i + 3] = 255;
                }

            return pixels;
        }

        private sealed class Decoder(byte[] frame) : IVideoDecoder
        {
            public Stream Output { get; } = new MemoryStream(frame, writable: false);

            public bool Failed => false;

            public void Dispose()
            {
                // Nothing to kill; the preview's reader may still be reading the stream, which only has to end.
            }
        }
    }
}

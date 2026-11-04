using System.Globalization;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Editor;

/// <summary>
/// The <c>video-timeline</c> golden's timeline, built in code so the render tests need neither ffmpeg nor a video file: a
/// 1080×1920 timeline with three video and two audio tracks, a linked 1080p recording on V1/A1 with silence and idle cuts
/// collapsed inside it, a picture-in-picture clip on V2, music on A2; a synthetic waveform; and a preview decoder that
/// "decodes" colour bars. Everything is computed from fixed numbers, so every run draws the same pixels.
/// </summary>
internal static class VideoGolden
{
    public const double Duration = 60;

    /// <summary>Where the playhead sits for the capture.</summary>
    public const double Playhead = 6.5;

    public const string Notice = "Detected 2 silences, 0:03.6 removed.";

    public static MediaRef Recording { get; } = new("ast_000000000001", "Assets/Video/Recordings/forest-run.mp4", new string('1', 64), Duration, 1920, 1080, 60, true, true);

    public static MediaRef Music { get; } = new("aud_000000000002", "Assets/Video/Media/theme.ogg", new string('2', 64), 90, 0, 0, 0, false, true);

    /// <summary>
    /// A game project in a fixed temp folder for the video goldens: the media pool's folders with the timeline's media
    /// (placeholder bytes: nothing decodes them, the decoders are fakes) and two timelines. Made fresh on every call.
    /// </summary>
    public static string Project()
    {
        var root = Path.Combine(GoldenPaths.Root, "VideoGame");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        new ProjectSettings { Name = "VideoGame" }.Save(Directory.CreateDirectory(root).FullName);
        foreach (var (file, bytes) in new[]
        {
            (Recording.Path, 48 * 1024),
            ("Assets/Video/Recordings/forest-boss.mp4", 96 * 1024),
            (Music.Path, 12 * 1024),
            ("Assets/Video/Media/whoosh.wav", 3 * 1024),
        })
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        var timelines = Directory.CreateDirectory(Path.Combine(root, "Assets", "Video", "Timelines")).FullName;
        File.WriteAllText(Path.Combine(timelines, "Trailer.mftimeline"), TimelineSerializer.Serialize(Timeline()));
        File.WriteAllText(Path.Combine(timelines, "Steam Capsule.mftimeline"),
            TimelineSerializer.Serialize(TimelineFactory.New(new TimelineSettings(1920, 1080, new FrameRate(30, 1)))));
        return root;
    }

    /// <summary>The timeline: its media are <see cref="Project"/>'s placeholder files.</summary>
    public static Timeline Timeline()
    {
        var t = TimelineFactory.New(new TimelineSettings(1080, 1920, new FrameRate(30, 1)));
        t = TimelineEdits.AddTrack(t, TrackKind.Video);
        var v1 = t.TracksOf(TrackKind.Video)[0].Id;
        var v2 = t.TracksOf(TrackKind.Video)[1].Id;
        var a1 = t.TracksOf(TrackKind.Audio)[0].Id;
        var a2 = t.TracksOf(TrackKind.Audio)[1].Id;
        t = TimelineEdits.Append(t, Recording, 2, 16, v1, a1);
        t = TimelineEdits.Append(t, Recording, 30, 40, v1, a1);
        var first = t.ClipsOn(v1)[0];
        t = TimelineEdits.SetClipCuts(t, first.Id, list =>
        {
            list.SetRange(6.0, 9.6, enabled: false, SegmentOrigin.Auto, Reason("silence {0:0.00}s", 3.6));
            list.SetRange(12.0, 13.5, enabled: false, SegmentOrigin.Gameplay, Reason("idle {0:0.0}s", 1.5));
        });
        t = TimelineEdits.Overwrite(t, Recording, 44, 49, at: 3, v2, null);
        var pip = t.ClipsOn(v2)[0];
        t = TimelineEdits.SetTransform(t, pip.Id, new ClipTransform(FrameFit.Fit, 0.5, 0, -560, new CropRect(240, 0, 240, 0), 1));
        t = TimelineEdits.Append(t, Music, 0, 20, null, a2);
        return TimelineEdits.AddMarker(t, 12, "boss");
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
    /// "Decodes" any request into one frame of colour bars at the size the preview asked for (a timeline graph's base
    /// <c>color=…:s=WxH</c>, or a file's <c>scale=W:H</c> filter): the paused preview's first-frame scrub then shows a picture without ffmpeg. That scrub reads on the
    /// thread pool, so the golden waits for <see cref="FirstFrameRead"/> before it goes on: the capture never races it.
    /// </summary>
    public sealed class Decoders : IVideoDecoderFactory
    {
        private readonly TaskCompletionSource _firstFrameRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a decoder's whole frame has been read, or a decoder was disposed before that.</summary>
        public Task FirstFrameRead => _firstFrameRead.Task;

        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            var (width, height) = ScaleOf(arguments);
            return new Decoder(width > 0 ? Bars(width, height) : [], _firstFrameRead);
        }

        private static (int Width, int Height) ScaleOf(IReadOnlyList<string> arguments)
        {
            foreach (var argument in arguments)
            {
                var color = argument.IndexOf("color=c=", StringComparison.Ordinal);
                var s = color < 0 ? -1 : argument.IndexOf(":s=", color, StringComparison.Ordinal);
                if (s >= 0)
                {
                    var size = argument[(s + 3)..].Split(':')[0].Split('x');
                    if (size.Length == 2 && int.TryParse(size[0], CultureInfo.InvariantCulture, out var cw) &&
                        int.TryParse(size[1], CultureInfo.InvariantCulture, out var ch))
                        return (cw, ch);
                }
            }

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

        private sealed class Decoder(byte[] frame, TaskCompletionSource read) : IVideoDecoder
        {
            public Stream Output { get; } = new FrameStream(frame, read);

            public bool Failed => false;

            // Nothing to kill; the preview's reader may still be reading the stream, which only has to end.
            public void Dispose() => read.TrySetResult();
        }

        // The frame's bytes; reaching their end sets the event.
        private sealed class FrameStream(byte[] frame, TaskCompletionSource read) : MemoryStream(frame, writable: false)
        {
            public override int Read(byte[] buffer, int offset, int count) => Signal(base.Read(buffer, offset, count));

            public override int Read(Span<byte> buffer) => Signal(base.Read(buffer));

            private int Signal(int bytes)
            {
                if (Position >= Length)
                    read.TrySetResult();
                return bytes;
            }
        }
    }
}

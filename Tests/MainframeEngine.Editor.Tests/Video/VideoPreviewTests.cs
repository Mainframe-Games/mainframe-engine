using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

[Collection(nameof(SerialEditor))]
public sealed class VideoPreviewTests
{
    private sealed class FakeDecoder(byte[] bytes) : IVideoDecoder
    {
        public Stream Output { get; } = new MemoryStream(bytes);
        public bool Failed => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory(int frameBytes) : IVideoDecoderFactory
    {
        public List<(IReadOnlyList<string> Args, FakeDecoder Decoder)> Started { get; } = [];

        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            var decoder = new FakeDecoder(new byte[frameBytes * 90]); // 3 s of frames at 30 fps
            lock (Started) // scrubs and prefetches start on other threads
                Started.Add((arguments, decoder));
            return decoder;
        }

        public int Count
        {
            get
            {
                lock (Started)
                    return Started.Count;
            }
        }
    }

    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    private static VideoPreview Preview(FakeFactory factory) => new(factory, () => null, "/r/a.mp4", 640, 360, 30, number: 1);

    [Fact]
    public void ThePreviewIsAtMost960WideWithTheSourceAspect()
    {
        using var preview = new VideoPreview(new FakeFactory(4), () => null, "/r/a.mp4", 1920, 1080, 60, 1);
        Assert.Equal((960, 540), (preview.Width, preview.Height));
        Assert.Equal(960, preview.Viewport.Width);
    }

    [Fact]
    public void PlayStartsTheFirstRangesVideoDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 2);
        Assert.True(preview.IsPlaying);
        Assert.Equal(["-ss", "2", "-i", "/r/a.mp4", "-t", "2"], Assert.Single(factory.Started).Args.Take(6));
    }

    [Fact]
    public void PauseKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0);
        preview.Pause();
        Assert.False(preview.IsPlaying);
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
        Assert.Equal(0, preview.LiveDecoders);
    }

    [Fact]
    public void DisposeKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        var preview = Preview(factory);
        preview.Play(Segments, 0);
        preview.Dispose();
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
    }

    [Fact]
    public void ASegmentEditRebuildsThePlanFromThePlayhead()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0, segmentsVersion: 0);
        Segment[] edited = [Segment.Create(0, 1, true, SegmentOrigin.Auto), Segment.Create(1, 10, false, SegmentOrigin.Manual)];
        preview.Update(edited, segmentsVersion: 1);
        Assert.Equal(2, factory.Started.Count);
        Assert.True(factory.Started[0].Decoder.Disposed);
    }

    [Fact]
    public void SeekWhilePausedDecodesOneFrame()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Seek(3.5);
        Assert.Equal(3.5, preview.Position, 9);
        UpdateUntil(preview, Segments, () => !preview.IsScrubbing); // the scrub task starts its decoder (fix round 1)
        Assert.Contains("-frames:v", Assert.Single(factory.Started).Args);
    }

    // ── Beyond the brief's six: timing, scrub queueing, failures and leaks ──────────────────────────────────────

    /// <summary>A decoder whose output blocks until <see cref="Release"/>, then yields one frame of <c>fill</c>.</summary>
    private sealed class GatedDecoder(int frameBytes, byte fill, bool failed = false) : IVideoDecoder
    {
        private readonly GateStream _stream = new(frameBytes, fill);

        public Stream Output => _stream;
        public bool Failed => failed;
        public bool Disposed { get; private set; }

        public void Release() => _stream.Gate.Set();

        /// <summary>The reader got this decoder's frame.</summary>
        public bool Read => _stream.Done;

        /// <summary>A reader is reading (so the preview has published this decoder).</summary>
        public bool Reading => _stream.Entered;

        public void Dispose()
        {
            Disposed = true;
            _stream.Gate.Set(); // like a killed process: the reader sees the end
        }
    }

    private sealed class GateStream(int length, byte fill) : Stream
    {
        private int _position;
        private volatile bool _done;
        private volatile bool _entered;

        public ManualResetEventSlim Gate { get; } = new();
        public bool Done => _done;
        public bool Entered => _entered;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _entered = true;
            Gate.Wait();
            var n = Math.Min(count, length - _position);
            buffer.AsSpan(offset, n).Fill(fill);
            _position += n;
            _done |= _position == length;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Gate.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class GatedFactory(int frameBytes, bool failed = false) : IVideoDecoderFactory
    {
        private readonly List<(IReadOnlyList<string> Args, GatedDecoder Decoder, int Thread)> _started = [];

        public int Count
        {
            get
            {
                lock (_started)
                    return _started.Count;
            }
        }

        public (IReadOnlyList<string> Args, GatedDecoder Decoder, int Thread) this[int index]
        {
            get
            {
                lock (_started)
                    return _started[index];
            }
        }

        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            lock (_started)
            {
                var decoder = new GatedDecoder(frameBytes, (byte)(_started.Count + 1), failed);
                if (failed)
                    decoder.Release(); // a failed process has exited: its output is at its end
                _started.Add((arguments, decoder, Environment.CurrentManagedThreadId));
                return decoder;
            }
        }

        public void AllDisposed()
        {
            lock (_started)
                Assert.All(_started, s => Assert.True(s.Decoder.Disposed));
        }
    }

    private static VideoPreview Preview(GatedFactory factory) => new(factory, () => null, "/r/a.mp4", 640, 360, 30, number: 1);

    private static void UpdateUntil(VideoPreview preview, IReadOnlyList<Segment> segments, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            preview.Update(segments, 0);
            Thread.Sleep(5);
        }
    }

    private static byte FirstPixel(VideoPreview preview) => ((Sprite2D)preview.Viewport.GetChild(0)).Texture!.DecodePixels().Rgba[0];

    [Fact]
    public void TheTextureStartsBlackAndOpaque()
    {
        using var preview = Preview(new FakeFactory(640 * 360 * 4));
        var pixels = ((Sprite2D)preview.Viewport.GetChild(0)).Texture!.DecodePixels().Rgba;
        Assert.Equal([0, 0, 0, 255], pixels[..4]);
        Assert.Equal(255, pixels[^1]);
    }

    [Fact]
    public void OnlyTheNewestScrubWaitsBehindTheRunningOne()
    {
        var factory = new GatedFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Seek(1);
        preview.Seek(2);
        preview.Seek(3);
        Assert.Equal(3, preview.Position, 9);
        UpdateUntil(preview, Segments, () => factory.Count == 1);
        Assert.Equal("1", factory[0].Args[1]);

        factory[0].Decoder.Release();
        UpdateUntil(preview, Segments, () => factory.Count == 2);
        Assert.Equal(1, FirstPixel(preview)); // the first scrub's frame was shown
        Assert.Equal("3", factory[1].Args[1]); // 2 was dropped for 3
        Assert.True(factory[0].Decoder.Disposed);

        factory[1].Decoder.Release();
        UpdateUntil(preview, Segments, () => !preview.IsScrubbing);
        Assert.Equal(2, FirstPixel(preview));
        Assert.Equal(2, factory.Count);
        Assert.Equal(0, preview.LiveDecoders);
    }

    [Fact]
    public void PlayCancelsAScrubAndItsFrameIsNeverShown()
    {
        var factory = new GatedFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Seek(1);
        UpdateUntil(preview, Segments, () => factory.Count == 1 && factory[0].Decoder.Reading);
        var scrub = factory[0].Decoder;
        preview.Play(Segments, 0); // kills the scrub's decoder, which releases its whole frame to the reader
        Assert.True(scrub.Disposed);
        preview.Pause();
        UpdateUntil(preview, Segments, () => scrub.Read && !preview.IsScrubbing && preview.LiveDecoders == 0);
        Assert.Equal(0, FirstPixel(preview)); // a whole frame was read, and dropped
    }

    [Fact]
    public void APrefetchDecoderStartsOffTheCallingThread()
    {
        // kept [0, 0.3) · cut · kept [1, 2): the second range is inside the prefetch lead from the start.
        Segment[] segments =
        [
            Segment.Create(0, 0.3, true, SegmentOrigin.Auto),
            Segment.Create(0.3, 1, false, SegmentOrigin.Auto),
            Segment.Create(1, 2, true, SegmentOrigin.Auto),
            Segment.Create(2, 3, false, SegmentOrigin.Auto),
        ];
        var factory = new GatedFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(segments, 0);
        Assert.Equal(Environment.CurrentManagedThreadId, factory[0].Thread); // the user's Play starts range 0 itself
        preview.Update(segments, 0);
        UpdateUntil(preview, segments, () => factory.Count == 2);
        Assert.Equal("1", factory[1].Args[1]);
        Assert.NotEqual(Environment.CurrentManagedThreadId, factory[1].Thread);

        preview.Pause();
        preview.Seek(5);
        UpdateUntil(preview, segments, () => factory.Count == 3);
        Assert.NotEqual(Environment.CurrentManagedThreadId, factory[2].Thread); // scrubs too
    }

    [Fact]
    public void PlaybackPrefetchesTheNextRangeSwitchesAtTheCutAndStopsAtTheEnd()
    {
        Segment[] segments =
        [
            Segment.Create(0, 0.3, true, SegmentOrigin.Auto),
            Segment.Create(0.3, 1, false, SegmentOrigin.Auto),
            Segment.Create(1, 2, true, SegmentOrigin.Auto),
            Segment.Create(2, 3, false, SegmentOrigin.Auto),
        ];
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(segments, 0);
        UpdateUntil(preview, segments, () => factory.Count == 2);
        Assert.Equal("1", factory.Started[1].Args[1]);

        UpdateUntil(preview, segments, () => factory.Started[0].Decoder.Disposed);
        Assert.True(preview.IsPlaying);
        Assert.InRange(preview.Position, 1, 2); // past the cut: the first range's stream is gone

        UpdateUntil(preview, segments, () => !preview.IsPlaying);
        Assert.Equal(2, preview.Position, 9);
        UpdateUntil(preview, segments, () => preview.LiveDecoders == 0);
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
    }

    [Fact]
    public void AFailedDecoderPausesPlayback()
    {
        var factory = new GatedFactory(640 * 360 * 4, failed: true);
        using var preview = Preview(factory);
        preview.Play(Segments, 0);
        UpdateUntil(preview, Segments, () => !preview.IsPlaying); // its output ends at once, then Failed is read
        UpdateUntil(preview, Segments, () => preview.LiveDecoders == 0);
    }

    [Fact]
    public void DisposeLeavesNoLiveDecoderAndFreesTheViewport()
    {
        var factory = new GatedFactory(640 * 360 * 4);
        var preview = Preview(factory);
        preview.Play(Segments, 0);
        preview.Update(Segments, 0);
        preview.Seek(1); // playing: a restart, not a scrub
        preview.Pause();
        preview.Seek(2); // a scrub whose decoder starts on its task, maybe after Dispose began
        preview.Dispose();
        Assert.Equal(0, preview.LiveDecoders);
        Assert.True(preview.Viewport.IsFreed);
        factory.AllDisposed();
    }
}

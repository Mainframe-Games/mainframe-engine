namespace MainframeEngine;

/// <summary>
/// The receiving end of an <see cref="IFrameStreamSource"/>. Streams the presented swapchain image back to the CPU
/// every frame for the gameplay recorder: one host-visible buffer per frame in flight, read when BeginFrame's fence
/// wait has already proven the copy finished, so recording adds a copy but never a stall. Every call arrives on the
/// main thread, from inside the renderer's BeginFrame/EndFrame or EndStream.
/// </summary>
internal interface IFrameStreamSink
{
    /// <summary>Main thread, in EndFrame before the copy is recorded. False skips copying this frame.</summary>
    bool ShouldCapture(out double stamp);

    /// <summary>Main thread, once the GPU has finished the frame. <paramref name="pixels"/> is valid only during the call.</summary>
    void OnFrame(ReadOnlySpan<byte> pixels, int width, int height, double stamp);

    /// <summary>The swapchain changed size; the stream has ended. Pending frames were delivered first.</summary>
    void OnStreamEnded(string reason);
}

/// <summary>
/// Streams the presented swapchain image back to the CPU every frame for the gameplay recorder: one host-visible
/// buffer per frame in flight, read when BeginFrame's fence wait has already proven the copy finished, so recording
/// adds a copy but never a stall. Implemented by the Vulkan renderer; needs <c>EngineOptions.EnableFrameCapture</c>
/// (the swapchain must allow TRANSFER_SRC). The stream is sized to the swapchain, so a resize ends it.
/// </summary>
internal interface IFrameStreamSource
{
    /// <summary>The swapchain can be copied from (frame capture enabled and a supported 8-bit format).</summary>
    bool CanStream { get; }

    /// <summary>Byte order of the pixels handed to <see cref="IFrameStreamSink.OnFrame"/> (alpha is not meaningful).</summary>
    PixelLayout StreamLayout { get; }

    /// <summary>The swapchain size in pixels; a stream begun now delivers frames of exactly this size.</summary>
    (int Width, int Height) StreamSize { get; }

    /// <summary>Starts copying every frame the sink accepts. Throws when streaming is unavailable or already running.</summary>
    void BeginStream(IFrameStreamSink sink);

    /// <summary>Waits for and delivers any pending frames, then stops.</summary>
    void EndStream();
}

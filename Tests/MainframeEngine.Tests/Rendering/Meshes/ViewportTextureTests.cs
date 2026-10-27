namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>
/// Viewport textures on 3D materials (ADR 0216; no GPU): a material's <see cref="TextureGpu"/> borrows the sub-viewport's
/// image instead of decoding pixels, so before the view has drawn it waits quietly instead of failing an upload.
/// </summary>
public sealed class ViewportTextureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMaterialSlotWithAViewportTextureWaitsForTheViewInsteadOfDecoding(bool disable3D)
    {
        var view = new SubViewport { Name = $"Monitor{disable3D}", Width = 320, Height = 180, Disable3D = disable3D };
        var material = new StandardMaterial3D { AlbedoTexture = view.GetTexture(), EmissionTexture = view.GetTexture() };
        var albedo = new TextureGpu(null!, material.AlbedoTexture!, TextureColorSpace.Srgb);
        var sink = new MemoryLogSink { Levels = Log.Level.Warning | Log.Level.Error };
        Log.AddSink(sink);
        try
        {
            // Before: DecodePixels threw "has no CPU pixels" and the slot logged "cannot be uploaded" every version.
            Assert.False(albedo.Update());
            Assert.Null(albedo.Gpu);
            var generation = albedo.Generation;
            Assert.False(albedo.Update()); // no image yet: nothing changes, nothing to rewrite
            Assert.Equal(generation, albedo.Generation);
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        Assert.DoesNotContain(sink.Snapshot(), e => e.Message.Contains("cannot be uploaded", StringComparison.Ordinal));
        Assert.Same(view, material.AlbedoTexture!.Viewport);
        Assert.Equal((320, 180), (material.AlbedoTexture.Width, material.AlbedoTexture.Height));
        albedo.Release();
        view.Free();
    }

    [Fact]
    public void CpuConsumersStillGetNoPixels()
    {
        // Bakes and packers read pixels on the CPU: a viewport texture has none (they fall back as before).
        var view = new SubViewport { Name = "Cpu" };
        Assert.Throws<InvalidOperationException>(() => view.GetTexture().DecodePixels());
        view.Free();
    }
}

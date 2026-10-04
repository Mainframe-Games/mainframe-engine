using System.Buffers.Binary;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

public sealed class PipelineCacheTests
{
    private static readonly byte[] Uuid = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
    private static readonly PipelineCache.DeviceIdentity Device = new(0x106B, 0x1B00020A, 0x28A1, Uuid);

    private static byte[] Header(uint length = 32, uint version = 1, uint vendor = 0x106B, uint device = 0x1B00020A, byte[]? uuid = null)
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data, length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), device);
        (uuid ?? Uuid).CopyTo(data, 16);
        return data;
    }

    [Fact]
    public void AMatchingHeaderIsAccepted() => Assert.True(PipelineCache.HeaderMatches(Header(), Device));

    [Fact]
    public void DataFromAnotherDeviceOrDriverIsRejected()
    {
        Assert.False(PipelineCache.HeaderMatches(Header(vendor: 0x10DE), Device));
        Assert.False(PipelineCache.HeaderMatches(Header(device: 1), Device));
        Assert.False(PipelineCache.HeaderMatches(Header(uuid: new byte[16]), Device));
        Assert.False(PipelineCache.HeaderMatches(Header(version: 2), Device));
        Assert.False(PipelineCache.HeaderMatches(Header(length: 16), Device));
        Assert.False(PipelineCache.HeaderMatches(Header().AsSpan(0, 20), Device));
    }

    [Fact]
    public void TheFileNameIdentifiesVendorDeviceDriverAndUuid()
    {
        Assert.Equal("pipelines-106b-1b00020a-000028a1-0102030405060708090a0b0c0d0e0f10.bin", PipelineCache.FileName(Device));
    }

    [Fact]
    public void TheOverrideVariableChoosesOrDisablesTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-cache");
        Assert.Equal(Path.GetFullPath(dir), PipelineCache.ResolveDirectory(dir));
        Assert.Null(PipelineCache.ResolveDirectory("off"));
        Assert.Null(PipelineCache.ResolveDirectory("OFF"));
        Assert.Null(PipelineCache.ResolveDirectory(""));
    }

    [Fact]
    public void TheDefaultDirectoryIsAPerUserCacheFolder()
    {
        var dir = PipelineCache.ResolveDirectory(null);

        Assert.NotNull(dir);
        Assert.True(Path.IsPathRooted(dir));
        Assert.Contains(OperatingSystem.IsLinux() ? "mainframe-engine" : "MainframeEngine", dir, StringComparison.Ordinal);
    }
}

public sealed class FrameDataTests
{
    [Theory]
    [InlineData(0.1f, 1000f)]
    [InlineData(0.5f, 50f)]
    public void ClipPlanesAreRecoveredFromAPerspectiveProjection(float near, float far)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 16f / 9f, near, far);

        var (n, f) = FrameData.ClipPlanes(projection);

        Assert.Equal(near, n, 3);
        Assert.Equal(far, f, far * 1e-3f);
    }

    [Fact]
    public void ClipPlanesAreRecoveredFromAnOrthographicProjection()
    {
        var projection = Matrix4x4.CreateOrthographicOffCenter(-10, 10, -10, 10, 0.5f, 40f);

        var (n, f) = FrameData.ClipPlanes(projection);

        Assert.Equal(0.5f, n, 4);
        Assert.Equal(40f, f, 3);
    }

    [Fact]
    public void TheBlockMatchesTheStd140Size()
    {
        Assert.Equal(FrameData.Size, System.Runtime.InteropServices.Marshal.SizeOf<FrameData>());
    }

    [Fact]
    public void FromFillsTheDerivedMatrices()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 3, 7), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 4f / 3f, 0.1f, 1000f);

        var data = FrameData.From(view, projection, new Vector3(0, 3, 7), new Extent2D(640, 480), 2f, 1.5f);

        Assert.Equal(view * projection, data.ViewProjection);
        Assert.True(Matrix4x4.Invert(projection, out var inv) && inv == data.InverseProjection);
        Assert.Equal(0f, data.InverseViewRotation.M41); // rotation only
        Assert.Equal(new Vector4(640, 480, 1f / 640, 1f / 480), data.Viewport);
        Assert.Equal(2f, data.Clip.Z);
        Assert.Equal(1.5f, data.Clip.W);
    }
}

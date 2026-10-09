using System.Buffers.Binary;
using System.Numerics;
using Silk.NET.Vulkan;
using VkPipelineCache = Silk.NET.Vulkan.PipelineCache;

namespace MainframeEngine.Tests.Rendering;

public sealed class PipelineCacheTests : IDisposable
{
    private static readonly byte[] Uuid = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
    private static readonly PipelineCache.DeviceIdentity Device = new(0x106B, 0x1B00020A, 0x28A1, Uuid);
    private static readonly PipelineCache.DeviceInfo MoltenVk = new(Device, IsMoltenVk: true);
    private static readonly PipelineCache.DeviceInfo Desktop = new(Device, IsMoltenVk: false);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-pipeline-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

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

    /// <summary>Valid cache data of <paramref name="size"/> bytes whose payload is <paramref name="fill"/>.</summary>
    private static byte[] Data(int size = 256, byte fill = 7)
    {
        var data = new byte[size];
        data.AsSpan(32).Fill(fill);
        Header().AsSpan(0, 32).CopyTo(data);
        return data;
    }

    private string FilePath => Path.Combine(_dir, PipelineCache.FileName("Game", Device));

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
    public void TheFileNameIdentifiesApplicationVendorDeviceDriverAndUuid()
    {
        Assert.Equal("pipelines-Forest.Desktop-106b-1b00020a-000028a1-0102030405060708090a0b0c0d0e0f10.bin",
            PipelineCache.FileName("Forest.Desktop", Device));
        Assert.Equal("pipelines-106b-1b00020a-000028a1-0102030405060708090a0b0c0d0e0f10.bin", PipelineCache.LegacyFileName(Device));
    }

    [Fact]
    public void ApplicationNamesAreSanitisedForTheFileName()
    {
        Assert.Equal("My_Game__2_", PipelineCache.SanitizeApplication("My Game (2)"));
        Assert.Equal("a.b_c-d", PipelineCache.SanitizeApplication("a.b_c-d"));
        Assert.Equal("app", PipelineCache.SanitizeApplication(""));
        Assert.Equal(48, PipelineCache.SanitizeApplication(new string('x', 100)).Length);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCacheRoundTripsThroughTheFile(bool moltenVk)
    {
        var info = moltenVk ? MoltenVk : Desktop;
        var driver = new FakeDriver();
        var first = new PipelineCache(driver, info, _dir, "Game");
        Assert.Equal(0, first.LoadedBytes);
        driver.Add(first.Handle, Data(300)); // pipelines created this run
        first.Dispose();
        Assert.Equal(300, new FileInfo(FilePath).Length);

        var second = new PipelineCache(driver, info, _dir, "Game");
        second.CompletePendingLoad(wait: true);
        Assert.Equal(300, second.LoadedBytes);
        Assert.Equal(Data(300), driver.DataOf(second.Handle));
        second.Dispose();
        Assert.Equal(0, driver.LiveCaches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnchangedCacheIsNotRewritten(bool moltenVk)
    {
        var info = moltenVk ? MoltenVk : Desktop;
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        var driver = new FakeDriver();
        var cache = new PipelineCache(driver, info, _dir, "Game");
        cache.CompletePendingLoad(wait: true);
        File.Delete(FilePath); // a write would bring it back

        cache.Dispose();

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void ACacheThatGrewIsRewritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        var driver = new FakeDriver();
        var cache = new PipelineCache(driver, Desktop, _dir, "Game");
        driver.Add(cache.Handle, new byte[50]); // a new pipeline

        cache.Dispose();

        Assert.Equal(350, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void AFileOverTheCapIsNotLoadedAndIsReplacedByThisRunsData()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data((int)PipelineCache.MaxLoadBytesMoltenVk + 1));
        var driver = new FakeDriver();

        var cache = new PipelineCache(driver, MoltenVk, _dir, "Game");

        Assert.Equal(0, cache.LoadedBytes);
        Assert.False(cache.IsLoading);
        Assert.Equal("0", string.Join(',', driver.CreatedSizes));
        driver.Add(cache.Handle, Data(200));
        cache.Dispose();
        Assert.Equal(200, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void TheCapIsLargerOffMoltenVk()
    {
        Assert.True(PipelineCache.MaxLoadBytesOther > PipelineCache.MaxLoadBytesMoltenVk);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data((int)PipelineCache.MaxLoadBytesMoltenVk + 1));

        var cache = new PipelineCache(new FakeDriver(), Desktop, _dir, "Game");

        Assert.Equal((int)PipelineCache.MaxLoadBytesMoltenVk + 1, cache.LoadedBytes);
        cache.Dispose();
    }

    [Fact]
    public void TheSharedPre0176FileIsDeleted()
    {
        Directory.CreateDirectory(_dir);
        var legacy = Path.Combine(_dir, PipelineCache.LegacyFileName(Device));
        File.WriteAllBytes(legacy, Data(300));

        var cache = new PipelineCache(new FakeDriver(), Desktop, _dir, "Game");

        Assert.False(File.Exists(legacy));
        Assert.Equal(0, cache.LoadedBytes);
        cache.Dispose();
    }

    [Fact]
    public void OnMoltenVkTheFileLoadsInTheBackgroundWithoutBlockingStartup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        using var release = new ManualResetEventSlim();
        var driver = new FakeDriver { BlockLoadsUntil = release };

        var cache = new PipelineCache(driver, MoltenVk, _dir, "Game"); // returns while the driver is still "compiling"

        Assert.True(cache.LoadsInBackground);
        Assert.True(cache.IsLoading);
        Assert.Equal(300, cache.LoadedBytes);
        Assert.Empty(driver.DataOf(cache.Handle)); // the live cache starts empty
        cache.CompletePendingLoad(wait: false); // not ready: no merge, no wait
        Assert.True(cache.IsLoading);

        release.Set();
        cache.CompletePendingLoad(wait: true);

        Assert.False(cache.IsLoading);
        Assert.Equal(Data(300), driver.DataOf(cache.Handle));
        Assert.Equal(1, driver.LiveCaches); // the seed cache was destroyed after the merge
        cache.Dispose();
        Assert.Equal(0, driver.LiveCaches);
    }

    [Fact]
    public void ALoadStillRunningAtExitIsLeftOutOfTheFileAndAwaitedBeforeDestruction()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        using var release = new ManualResetEventSlim();
        var driver = new FakeDriver { BlockLoadsUntil = release, OnGetData = release.Set };
        var cache = new PipelineCache(driver, MoltenVk, _dir, "Game");
        driver.Add(cache.Handle, Data(120)); // this run's pipelines; the load is still "compiling"

        cache.Dispose(); // saves first (releasing the load from GetData), then waits for it

        Assert.Equal(0, driver.LiveCaches);
        Assert.Equal("create get create+data destroy destroy", string.Join(' ', driver.Calls));
        Assert.Equal(120, new FileInfo(FilePath).Length); // pruned to the working set
    }

    [Fact]
    public void ALoadThatFinishedIsMergedBeforeSaving()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        var driver = new FakeDriver();
        var cache = new PipelineCache(driver, MoltenVk, _dir, "Game");
        driver.Add(cache.Handle, new byte[40]); // created before the load finished
        cache.CompletePendingLoad(wait: true); // what the next CreateGraphicsPipeline does once the load is done

        cache.Dispose();

        Assert.Equal("create create+data merge destroy get destroy", string.Join(' ', driver.Calls));
        Assert.Equal(340, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void DataTheDriverRejectsInTheBackgroundIsDropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        var driver = new FakeDriver { RejectData = true };
        var cache = new PipelineCache(driver, MoltenVk, _dir, "Game");

        cache.CompletePendingLoad(wait: true);

        Assert.Equal(0, cache.LoadedBytes);
        Assert.DoesNotContain("merge", driver.Calls);
        cache.Dispose();
    }

    [Fact]
    public void DataTheDriverRejectsAtStartupFallsBackToAnEmptyCache()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, Data(300));
        var driver = new FakeDriver { RejectData = true };

        var cache = new PipelineCache(driver, Desktop, _dir, "Game");

        Assert.Equal(0, cache.LoadedBytes);
        Assert.Equal("300,0", string.Join(',', driver.CreatedSizes));
        cache.Dispose();
    }

    [Fact]
    public void UnchangedMeansSameLengthOnMoltenVkAndSameBytesElsewhere()
    {
        var data = Data(300);
        var hash = System.Security.Cryptography.SHA256.HashData(data);
        var reordered = Data(300, fill: 9);

        Assert.True(PipelineCache.IsUnchanged(data, hash, 300, appendOnly: false));
        Assert.False(PipelineCache.IsUnchanged(reordered, hash, 300, appendOnly: false));
        Assert.True(PipelineCache.IsUnchanged(reordered, [], 300, appendOnly: true));
        Assert.False(PipelineCache.IsUnchanged(Data(301), [], 300, appendOnly: true));
        Assert.False(PipelineCache.IsUnchanged(data, null, 0, appendOnly: true)); // nothing was loaded
    }

    /// <summary>Caches are byte arrays; merging appends the source's bytes (deduplication is the driver's business).</summary>
    private sealed class FakeDriver : PipelineCache.IDriver
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<ulong, byte[]> _caches = [];
        private ulong _next = 1;

        public ManualResetEventSlim? BlockLoadsUntil { get; init; }
        public Action? OnGetData { get; init; }
        public bool RejectData { get; init; }
        public List<string> Calls { get; } = [];
        public List<int> CreatedSizes { get; } = [];

        public int LiveCaches
        {
            get
            {
                lock (_lock)
                    return _caches.Count;
            }
        }

        public Result Create(byte[]? initialData, out VkPipelineCache cache)
        {
            if (initialData is not null)
                BlockLoadsUntil?.Wait();
            lock (_lock)
            {
                Calls.Add(initialData is null ? "create" : "create+data");
                CreatedSizes.Add(initialData?.Length ?? 0);
                if (initialData is not null && RejectData)
                {
                    cache = default;
                    return Result.ErrorInitializationFailed;
                }

                cache = new VkPipelineCache(_next++);
                _caches[cache.Handle] = initialData is null ? [] : [.. initialData];
                return Result.Success;
            }
        }

        public Result Merge(VkPipelineCache destination, VkPipelineCache source)
        {
            lock (_lock)
            {
                Calls.Add("merge");
                _caches[destination.Handle] = [.. _caches[source.Handle], .. _caches[destination.Handle]];
                return Result.Success;
            }
        }

        public byte[] GetData(VkPipelineCache cache)
        {
            OnGetData?.Invoke();
            lock (_lock)
            {
                Calls.Add("get");
                return [.. _caches[cache.Handle]];
            }
        }

        public void Destroy(VkPipelineCache cache)
        {
            lock (_lock)
            {
                Calls.Add("destroy");
                Assert.True(_caches.Remove(cache.Handle), "destroyed an unknown or already destroyed cache");
            }
        }

        public void Add(VkPipelineCache cache, byte[] bytes)
        {
            lock (_lock)
                _caches[cache.Handle] = [.. _caches[cache.Handle], .. bytes];
        }

        public byte[] DataOf(VkPipelineCache cache)
        {
            lock (_lock)
                return [.. _caches[cache.Handle]];
        }
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

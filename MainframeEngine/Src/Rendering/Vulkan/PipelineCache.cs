using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Silk.NET.Vulkan;
using VkPipelineCache = Silk.NET.Vulkan.PipelineCache;

namespace MainframeEngine;

/// <summary>
/// The device's <c>VkPipelineCache</c>, persisted between runs so pipeline compilation (shader translation on
/// MoltenVK, driver compiles elsewhere) is paid once. The file lives in the user cache directory, named by the
/// application and the device's vendor ID, device ID, driver version and pipeline-cache UUID, so a driver update or
/// another GPU starts a fresh cache instead of feeding the driver foreign data. Every engine pipeline is created through
/// <see cref="CreateGraphicsPipeline"/>; the data is written back on <see cref="Dispose"/> when it changed.
/// </summary>
/// <remarks>
/// <para>Location: <c>$MAINFRAME_PIPELINE_CACHE_DIR</c> when set (<c>off</c> disables persistence), otherwise
/// <c>~/Library/Caches/MainframeEngine</c> (macOS), <c>%LOCALAPPDATA%\MainframeEngine\Cache</c> (Windows) or
/// <c>$XDG_CACHE_HOME/mainframe-engine</c> / <c>~/.cache/mainframe-engine</c> (Linux). A file whose header does not
/// match the device is ignored. Failures to read or write are logged and never fatal.</para>
/// <para>MoltenVK (ADR 0176): <c>vkCreatePipelineCache</c> with initial data recompiles the Metal library of every
/// cached shader before it returns, and nothing ever removes an entry, so the file only grows (each shader edit adds
/// its new translation) and loading it could stall startup for 15–20 s. On MoltenVK the file is therefore loaded on a
/// background thread into a second cache that is merged into <see cref="Handle"/> by the next
/// <see cref="CreateGraphicsPipeline"/> after it is ready; startup never waits for it. A file over
/// <see cref="MaxLoadBytes"/> for the driver is not loaded at all, so the next save writes only what that run used; a
/// load still running at <see cref="Save"/> is left out of the file the same way. Other drivers parse the data lazily
/// and load it synchronously as before.</para>
/// </remarks>
public sealed unsafe class PipelineCache : IDisposable
{
    /// <summary>Environment variable overriding the cache directory (<c>off</c> disables the file).</summary>
    public const string DirectoryVariable = "MAINFRAME_PIPELINE_CACHE_DIR";

    /// <summary>Largest file loaded on MoltenVK (about four times the Forest's 1.1 MB working set).</summary>
    internal const long MaxLoadBytesMoltenVk = 4L << 20;

    /// <summary>Largest file loaded on other drivers (they store compiled binaries, several times larger).</summary>
    internal const long MaxLoadBytesOther = 64L << 20;

    private const int HeaderSize = 32; // VkPipelineCacheHeaderVersionOne

    private readonly IDriver _driver;
    private readonly Vk? _vk;
    private readonly Device _device;
    private readonly Lock _gate = new();
    private Task<(Result Result, VkPipelineCache Cache)>? _pendingLoad;
    private long _loadStarted;
    private byte[]? _loadedHash;
    private bool _disposed;

    internal PipelineCache(Vk vk, PhysicalDevice physicalDevice, Device device)
        : this(new VkDriver(vk, device), Describe(vk, physicalDevice), ResolveDirectory(Environment.GetEnvironmentVariable(DirectoryVariable)), ApplicationName())
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>The device-independent core (also used by the unit tests with a fake <see cref="IDriver"/>).</summary>
    internal PipelineCache(IDriver driver, in DeviceInfo device, string? directory, string application)
    {
        _driver = driver;
        Identity = device.Identity;
        LoadsInBackground = device.IsMoltenVk;
        MaxLoadBytes = device.IsMoltenVk ? MaxLoadBytesMoltenVk : MaxLoadBytesOther;
        FilePath = directory is null ? null : Path.Combine(directory, FileName(application, Identity));
        if (directory is not null)
            DeleteLegacyFile(Path.Combine(directory, LegacyFileName(Identity)));

        var initial = TryReadValidData(FilePath, Identity, MaxLoadBytes);
        LoadedBytes = initial?.Length ?? 0;
        // MoltenVK compares lengths only (IsUnchanged): no hash, so no crypto start-up on the main thread.
        _loadedHash = initial is null ? null : LoadsInBackground ? [] : SHA256.HashData(initial);

        if (initial is not null && LoadsInBackground)
        {
            Handle = CreateOrThrow(null);
            _loadStarted = Stopwatch.GetTimestamp();
            _pendingLoad = StartBackgroundLoad(driver, initial);
            return;
        }

        var result = driver.Create(initial, out var handle);
        if (result != Result.Success && initial is not null)
        {
            // Corrupt data the driver rejects: start empty rather than fail.
            Log.Warning($"[PipelineCache] Driver rejected {FilePath} ({result}); starting empty.");
            LoadedBytes = 0;
            _loadedHash = null;
            result = driver.Create(null, out handle);
        }

        result.Check("vkCreatePipelineCache");
        Handle = handle;
        if (LoadedBytes > 0)
            Log.Debug($"[PipelineCache] Loaded {LoadedBytes} bytes from {FilePath}.");
    }

    /// <summary>Pass to <c>vkCreateGraphicsPipelines</c> / <c>vkCreateComputePipelines</c>.</summary>
    public VkPipelineCache Handle { get; }

    /// <summary>The persisted cache file, or null when persistence is disabled.</summary>
    public string? FilePath { get; }

    /// <summary>
    /// Bytes of cache data read from disk and handed to the driver at startup (0 on a cold start, or when the driver
    /// rejected them). On MoltenVK the driver may still be loading them: see <see cref="IsLoading"/>.
    /// </summary>
    public int LoadedBytes { get; private set; }

    /// <summary>True while the file is still being loaded in the background (MoltenVK only).</summary>
    public bool IsLoading => _pendingLoad is not null;

    /// <summary>Whether this driver loads the file on a background thread (MoltenVK).</summary>
    public bool LoadsInBackground { get; }

    /// <summary>Files larger than this are not loaded (the next save rewrites them with what the run used).</summary>
    public long MaxLoadBytes { get; }

    internal DeviceIdentity Identity { get; }

    /// <summary>Creates one graphics pipeline through the cache, checking the result.</summary>
    public Pipeline CreateGraphicsPipeline(in GraphicsPipelineCreateInfo info, string what)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingLoad is { IsCompleted: true })
            CompletePendingLoad(wait: false);
        _vk!.CreateGraphicsPipelines(_device, Handle, 1, in info, null, out var pipeline)
            .Check($"vkCreateGraphicsPipelines ({what})");
        return pipeline;
    }

    /// <summary>
    /// Merges the background-loaded data into <see cref="Handle"/> once it is ready (or after waiting for it when
    /// <paramref name="wait"/> is set; with <paramref name="merge"/> false it is only destroyed). Runs on the thread
    /// creating pipelines: <c>vkMergePipelineCaches</c> needs the destination cache externally synchronised.
    /// </summary>
    internal void CompletePendingLoad(bool wait, bool merge = true)
    {
        lock (_gate)
        {
            var pending = _pendingLoad;
            if (pending is null || (!wait && !pending.IsCompleted))
                return;
            _pendingLoad = null;

            var (result, seed) = pending.GetAwaiter().GetResult();
            var ms = Stopwatch.GetElapsedTime(_loadStarted).TotalMilliseconds;
            if (result != Result.Success)
            {
                Log.Warning($"[PipelineCache] Driver rejected {FilePath} ({result}); continuing with an empty cache.");
                LoadedBytes = 0;
                _loadedHash = null;
                return;
            }

            var merged = merge ? _driver.Merge(Handle, seed) : Result.Success;
            _driver.Destroy(seed); // never used for a pipeline, so it can go immediately (no deletion queue)
            if (merged != Result.Success)
            {
                Log.Warning($"[PipelineCache] vkMergePipelineCaches failed ({merged}); continuing without {FilePath}.");
                LoadedBytes = 0;
                _loadedHash = null;
                return;
            }

            if (merge)
                Log.Debug($"[PipelineCache] Loaded {LoadedBytes} bytes from {FilePath} in the background ({ms:F0} ms).");
            else
                Log.Info($"[PipelineCache] Discarded the background load of {FilePath} after {ms:F0} ms (still running at exit).");
        }
    }

    /// <summary>
    /// Writes the current cache data to <see cref="FilePath"/> (atomically: temp file + rename), unless it is what was
    /// loaded. A background load that has not finished is left out: the file then holds only the pipelines this run
    /// created, so a file too slow to load (a cold Metal shader cache, stale entries) shrinks to the working set.
    /// </summary>
    public void Save()
    {
        if (_disposed || FilePath is null)
            return;

        CompletePendingLoad(wait: false);
        if (_pendingLoad is not null)
            Log.Info($"[PipelineCache] {FilePath} is still loading; saving only this run's pipelines.");
        try
        {
            var data = _driver.GetData(Handle);
            if (data.Length == 0)
                return;
            if (IsUnchanged(data, _loadedHash, LoadedBytes, appendOnly: LoadsInBackground))
                return; // nothing new this run

            WriteAtomically(FilePath, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VulkanException)
        {
            Log.Warning($"[PipelineCache] Could not save {FilePath}: {e.Message}");
        }
    }

    /// <summary>
    /// Saves the cache and destroys it. Called by the renderer before the device is destroyed; waits for a background
    /// load still running (the device must outlive the call) and discards it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Save();
        CompletePendingLoad(wait: true, merge: false);
        _disposed = true;
        _driver.Destroy(Handle);
    }

    private VkPipelineCache CreateOrThrow(byte[]? data)
    {
        _driver.Create(data, out var handle).Check("vkCreatePipelineCache");
        return handle;
    }

    private static Task<(Result, VkPipelineCache)> StartBackgroundLoad(IDriver driver, byte[] data)
    {
        // A dedicated low-priority thread: MoltenVK compiles one Metal library per cached shader inside this call,
        // which can take seconds when the Metal shader cache is cold. Only this thread touches the new cache until
        // CompletePendingLoad, which the task's completion publishes.
        var done = new TaskCompletionSource<(Result, VkPipelineCache)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var result = driver.Create(data, out var cache);
                done.SetResult((result, cache));
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        {
            Name = "PipelineCacheLoad",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return done.Task;
    }

    // ── Driver access (Vulkan, or a fake in the unit tests) ──────────────────────

    /// <summary>The cache-level Vulkan calls, abstracted so the load/merge/save logic is testable without a GPU.</summary>
    internal interface IDriver
    {
        /// <summary><c>vkCreatePipelineCache</c> with optional initial data (null = empty). May run on any thread.</summary>
        Result Create(byte[]? initialData, out VkPipelineCache cache);

        /// <summary><c>vkMergePipelineCaches(dst, 1, &amp;src)</c>.</summary>
        Result Merge(VkPipelineCache destination, VkPipelineCache source);

        /// <summary><c>vkGetPipelineCacheData</c> (throws <see cref="VulkanException"/> on failure).</summary>
        byte[] GetData(VkPipelineCache cache);

        void Destroy(VkPipelineCache cache);
    }

    private sealed class VkDriver(Vk vk, Device device) : IDriver
    {
        public Result Create(byte[]? initialData, out VkPipelineCache cache)
        {
            fixed (byte* data = initialData)
            {
                var info = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)(initialData?.Length ?? 0),
                    PInitialData = data,
                };
                return vk.CreatePipelineCache(device, in info, null, out cache);
            }
        }

        public Result Merge(VkPipelineCache destination, VkPipelineCache source) =>
            vk.MergePipelineCaches(device, destination, 1, &source);

        public byte[] GetData(VkPipelineCache cache)
        {
            nuint size = 0;
            vk.GetPipelineCacheData(device, cache, &size, null).Check("vkGetPipelineCacheData (size)");
            if (size == 0)
                return [];
            var data = new byte[size];
            fixed (byte* p = data)
                vk.GetPipelineCacheData(device, cache, &size, p).Check("vkGetPipelineCacheData");
            return size == (nuint)data.Length ? data : data.AsSpan(0, (int)size).ToArray();
        }

        public void Destroy(VkPipelineCache cache) => vk.DestroyPipelineCache(device, cache, null);
    }

    // ── Pure helpers (unit-tested) ────────────────────────────────────────────

    /// <summary>What a cache file must match: <c>VkPipelineCacheHeaderVersionOne</c> fields.</summary>
    internal readonly record struct DeviceIdentity(uint VendorId, uint DeviceId, uint DriverVersion, byte[] Uuid);

    /// <summary>The device's identity and whether its driver is MoltenVK (background loading).</summary>
    internal readonly record struct DeviceInfo(DeviceIdentity Identity, bool IsMoltenVk);

    private static DeviceInfo Describe(Vk vk, PhysicalDevice physicalDevice)
    {
        var driver = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driver };
        vk.GetPhysicalDeviceProperties2(physicalDevice, &props);
        var p = props.Properties;
        var uuid = new ReadOnlySpan<byte>(p.PipelineCacheUuid, 16).ToArray();
        return new DeviceInfo(new DeviceIdentity(p.VendorID, p.DeviceID, p.DriverVersion, uuid), driver.DriverID == DriverId.Moltenvk);
    }

    /// <summary>The cache file of <paramref name="application"/> on this device.</summary>
    internal static string FileName(string application, in DeviceIdentity id) => string.Create(CultureInfo.InvariantCulture,
        $"pipelines-{SanitizeApplication(application)}-{id.VendorId:x4}-{id.DeviceId:x4}-{id.DriverVersion:x8}-{Convert.ToHexStringLower(id.Uuid)}.bin");

    /// <summary>The name before ADR 0176 (one file shared by every application); deleted when found.</summary>
    internal static string LegacyFileName(in DeviceIdentity id) => string.Create(CultureInfo.InvariantCulture,
        $"pipelines-{id.VendorId:x4}-{id.DeviceId:x4}-{id.DriverVersion:x8}-{Convert.ToHexStringLower(id.Uuid)}.bin");

    /// <summary>The entry assembly's name (the game, the editor, a test host): each keeps its own file.</summary>
    internal static string ApplicationName() =>
        Assembly.GetEntryAssembly()?.GetName().Name ?? Process.GetCurrentProcess().ProcessName;

    /// <summary>Letters, digits, '.', '_' and '-' only (other characters become '_'), at most 48 characters.</summary>
    internal static string SanitizeApplication(string application)
    {
        if (string.IsNullOrWhiteSpace(application))
            return "app";
        var chars = application.Length > 48 ? application.AsSpan(0, 48).ToArray() : application.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '.' or '_' or '-'))
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>The cache directory for this platform, or null when disabled.</summary>
    internal static string? ResolveDirectory(string? overrideValue)
    {
        if (overrideValue is not null)
        {
            return overrideValue.Length == 0 || overrideValue.Equals("off", StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.GetFullPath(overrideValue);
        }

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "MainframeEngine");
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MainframeEngine", "Cache");

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
        return Path.Combine(root, "mainframe-engine");
    }

    /// <summary>True when <paramref name="data"/> starts with a version-one header for <paramref name="id"/>.</summary>
    internal static bool HeaderMatches(ReadOnlySpan<byte> data, in DeviceIdentity id)
    {
        if (data.Length < HeaderSize)
            return false;
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        return headerLength >= HeaderSize &&
               version == 1 && // VK_PIPELINE_CACHE_HEADER_VERSION_ONE
               BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == id.VendorId &&
               BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == id.DeviceId &&
               data.Slice(16, 16).SequenceEqual(id.Uuid);
    }

    /// <summary>The file's data when it exists, fits <paramref name="maxBytes"/> and matches the device; else null.</summary>
    internal static byte[]? TryReadValidData(string? path, in DeviceIdentity id, long maxBytes)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var length = new FileInfo(path).Length;
            if (length > maxBytes)
            {
                // Grown past the cap (stale shaders accumulate): start empty, the next save keeps only this run's.
                Log.Info($"[PipelineCache] Ignoring {path}: {length} bytes is over the {maxBytes}-byte cap.");
                return null;
            }

            var data = File.ReadAllBytes(path);
            if (HeaderMatches(data, id))
                return data;
            Log.Info($"[PipelineCache] Ignoring {path}: written by another device or driver.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[PipelineCache] Could not read {path}: {e.Message}");
        }

        return null;
    }

    /// <summary>
    /// True when the cache data to save is what was loaded. MoltenVK's data is append-only (entries are added, never
    /// replaced or removed) but its order changes after a merge, so there an equal length means nothing new; other
    /// drivers compare a hash of the bytes.
    /// </summary>
    internal static bool IsUnchanged(ReadOnlySpan<byte> data, byte[]? loadedHash, int loadedLength, bool appendOnly)
    {
        if (loadedHash is null || data.Length != loadedLength)
            return false;
        return appendOnly || SHA256.HashData(data).AsSpan().SequenceEqual(loadedHash);
    }

    /// <summary>Writes <paramref name="data"/> to a temp file next to <paramref name="path"/>, then renames it over.</summary>
    internal static void WriteAtomically(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private static void DeleteLegacyFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            File.Delete(path);
            Log.Info($"[PipelineCache] Deleted the pre-0176 shared cache file {path}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"[PipelineCache] Could not delete {path}: {e.Message}");
        }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using Silk.NET.Vulkan;
using VkPipelineCache = Silk.NET.Vulkan.PipelineCache;

namespace MainframeEngine;

/// <summary>
/// The device's <c>VkPipelineCache</c>, persisted between runs so pipeline compilation (shader translation on
/// MoltenVK, driver compiles elsewhere) is paid once. The file lives in the user cache directory, named by the
/// device's vendor ID, device ID, driver version and pipeline-cache UUID, so a driver update or another GPU
/// starts a fresh cache instead of feeding the driver foreign data. Every engine pipeline is created through
/// <see cref="Handle"/>; the data is written back on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Location: <c>$MAINFRAME_PIPELINE_CACHE_DIR</c> when set (<c>off</c> disables persistence), otherwise
/// <c>~/Library/Caches/MainframeEngine</c> (macOS), <c>%LOCALAPPDATA%\MainframeEngine\Cache</c> (Windows) or
/// <c>$XDG_CACHE_HOME/mainframe-engine</c> / <c>~/.cache/mainframe-engine</c> (Linux). A file whose header does not
/// match the device is ignored. Failures to read or write are logged and never fatal.
/// </remarks>
public sealed unsafe class PipelineCache : IDisposable
{
    /// <summary>Environment variable overriding the cache directory (<c>off</c> disables the file).</summary>
    public const string DirectoryVariable = "MAINFRAME_PIPELINE_CACHE_DIR";

    private const int HeaderSize = 32; // VkPipelineCacheHeaderVersionOne

    private readonly Vk _vk;
    private readonly Device _device;
    private bool _disposed;

    internal PipelineCache(Vk vk, PhysicalDevice physicalDevice, Device device)
    {
        _vk = vk;
        _device = device;

        vk.GetPhysicalDeviceProperties(physicalDevice, out var props);
        var uuid = new ReadOnlySpan<byte>(props.PipelineCacheUuid, 16).ToArray();
        Identity = new DeviceIdentity(props.VendorID, props.DeviceID, props.DriverVersion, uuid);

        var directory = ResolveDirectory(Environment.GetEnvironmentVariable(DirectoryVariable));
        FilePath = directory is null ? null : Path.Combine(directory, FileName(Identity));

        var initial = TryReadValidData(FilePath, Identity);
        LoadedBytes = initial?.Length ?? 0;

        fixed (byte* data = initial)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initial?.Length ?? 0),
                PInitialData = data,
            };
            var result = vk.CreatePipelineCache(device, in info, null, out var handle);
            if (result != Result.Success && initial is not null)
            {
                // Corrupt data the driver rejects: start empty rather than fail.
                Log.Warning($"[PipelineCache] Driver rejected {FilePath} ({result}); starting empty.");
                info.InitialDataSize = 0;
                info.PInitialData = null;
                LoadedBytes = 0;
                result = vk.CreatePipelineCache(device, in info, null, out handle);
            }

            result.Check("vkCreatePipelineCache");
            Handle = handle;
        }

        if (LoadedBytes > 0)
            Log.Debug($"[PipelineCache] Loaded {LoadedBytes} bytes from {FilePath}.");
    }

    /// <summary>Pass to <c>vkCreateGraphicsPipelines</c> / <c>vkCreateComputePipelines</c>.</summary>
    public VkPipelineCache Handle { get; }

    /// <summary>The persisted cache file, or null when persistence is disabled.</summary>
    public string? FilePath { get; }

    /// <summary>Bytes of cache data accepted from disk at startup (0 on a cold start).</summary>
    public int LoadedBytes { get; private set; }

    internal DeviceIdentity Identity { get; }

    /// <summary>Creates one graphics pipeline through the cache, checking the result.</summary>
    public Pipeline CreateGraphicsPipeline(in GraphicsPipelineCreateInfo info, string what)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _vk.CreateGraphicsPipelines(_device, Handle, 1, in info, null, out var pipeline)
            .Check($"vkCreateGraphicsPipelines ({what})");
        return pipeline;
    }

    /// <summary>Writes the current cache data to <see cref="FilePath"/> (atomically: temp file + rename).</summary>
    public void Save()
    {
        if (_disposed || FilePath is null)
            return;

        try
        {
            nuint size = 0;
            _vk.GetPipelineCacheData(_device, Handle, &size, null).Check("vkGetPipelineCacheData (size)");
            if (size == 0)
                return;
            var data = new byte[size];
            fixed (byte* p = data)
                _vk.GetPipelineCacheData(_device, Handle, &size, p).Check("vkGetPipelineCacheData");

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, data.AsSpan(0, (int)size).ToArray());
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VulkanException)
        {
            Log.Warning($"[PipelineCache] Could not save {FilePath}: {e.Message}");
        }
    }

    /// <summary>Saves the cache and destroys it. Called by the renderer before the device is destroyed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Save();
        _disposed = true;
        _vk.DestroyPipelineCache(_device, Handle, null);
    }

    // ── Pure helpers (unit-tested) ────────────────────────────────────────────

    /// <summary>What a cache file must match: <c>VkPipelineCacheHeaderVersionOne</c> fields.</summary>
    internal readonly record struct DeviceIdentity(uint VendorId, uint DeviceId, uint DriverVersion, byte[] Uuid);

    internal static string FileName(in DeviceIdentity id) => string.Create(CultureInfo.InvariantCulture,
        $"pipelines-{id.VendorId:x4}-{id.DeviceId:x4}-{id.DriverVersion:x8}-{Convert.ToHexStringLower(id.Uuid)}.bin");

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

    private static byte[]? TryReadValidData(string? path, in DeviceIdentity id)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
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
}

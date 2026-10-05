using System.Globalization;
using MainframeEngine.Networking;
using Silk.NET.Vulkan;

namespace MainframeEngine;

// What the built-in panels of the DevOverlay show. Each panel owns one snapshot object: Refresh() copies the numbers
// from the servers (called at the overlay's refresh rate while it is visible), the panel binds its fields by name.
// Everything is allocation-free in the steady state: numbers are bound as int/float and formatted by the RML, text is
// cached and rebuilt only when its source changes, servers are looked up through the registry (no closures per call).

/// <summary>Where the snapshots read from: the tree's servers, with the engine as a fallback for the renderer.</summary>
internal sealed class DevOverlaySources(ServerRegistry servers, Engine? engine)
{
    public ServerRegistry Servers { get; } = servers;

    public Engine? Engine { get; } = engine;

    public IRenderer? Renderer => Servers.Render?.Renderer ?? Engine?.Renderer;

    public IVulkanContext? Vulkan => Renderer as IVulkanContext;

    public static float Mib(ulong bytes) => (float)(bytes / (1024.0 * 1024.0));
}

internal sealed class FrameStats(DevOverlaySources sources)
{
    public float Fps, Ms, MinMs, MaxMs;
    public long Frame;
    public int UiDrawCalls;
    private float _windowMin = float.MaxValue, _windowMax, _windowSum;
    private int _windowCount;

    public bool VSync
    {
        get => sources.Renderer?.VSync ?? false;
        set
        {
            if (sources.Renderer is { } renderer)
                renderer.VSync = value;
        }
    }

    /// <summary>Called every visible frame: accumulates the frame time.</summary>
    public void Accumulate(float deltaSeconds)
    {
        var ms = deltaSeconds * 1000f;
        _windowMin = MathF.Min(_windowMin, ms);
        _windowMax = MathF.Max(_windowMax, ms);
        _windowSum += ms;
        _windowCount++;
    }

    /// <summary>Publishes the frames accumulated since the last refresh (nothing when there were none).</summary>
    public bool Refresh()
    {
        if (_windowCount == 0)
            return false;
        Ms = _windowSum / _windowCount;
        Fps = 1000f / MathF.Max(Ms, 1e-3f);
        (MinMs, MaxMs) = (_windowMin, _windowMax);
        (_windowMin, _windowMax, _windowSum, _windowCount) = (float.MaxValue, 0f, 0f, 0);
        Frame = sources.Vulkan is { } vk ? (long)vk.FrameNumber : Frame + 1;
        UiDrawCalls = sources.Servers.Get<UiServer>()?.Renderer?.Stats.DrawCalls ?? 0;
        return true;
    }
}

internal sealed class RendererStats(DevOverlaySources sources)
{
    private string _pipeline = "";
    private (Format Scene, Format Swapchain, VulkanRenderer.SwapchainEncoding Encoding) _pipelineKey;

    public bool Has;
    public int Draws, Surfaces, Instances, Culled, PipelineBinds, MaterialBinds, ShadowDraws, IdDraws;
    public int Meshes, Materials, Textures, Pipelines;
    public long CacheHits;

    public float Exposure
    {
        get => sources.Vulkan?.Exposure ?? IVulkanContext.DefaultExposure;
        set
        {
            if (sources.Vulkan is { } vk)
                vk.Exposure = value;
        }
    }

    public string Pipeline => _pipeline;

    public void ResetExposure() => Exposure = IVulkanContext.DefaultExposure;

    public void Refresh()
    {
        var vk = sources.Vulkan;
        Has = vk is not null;
        if (sources.Renderer is VulkanRenderer renderer)
        {
            // Rebuilt only when the formats or the encoding change: formatting enums allocates.
            var key = (VulkanRenderer.SceneColorFormat, renderer.SwapchainFormat, renderer.Encoding);
            if (_pipeline.Length == 0 || key != _pipelineKey)
            {
                _pipelineKey = key;
                _pipeline = string.Create(CultureInfo.InvariantCulture, $"{key.SceneColorFormat} -> {key.SwapchainFormat} ({key.Encoding})");
            }
        }

        if (sources.Servers.Render is not { } server)
            return;
        var stats = server.MeshStats;
        (Draws, Surfaces, Instances, Culled) = (stats.DrawCalls, stats.SurfaceInstances, stats.Instances, stats.Culled);
        (PipelineBinds, MaterialBinds, ShadowDraws, IdDraws) = (stats.PipelineBinds, stats.MaterialBinds, stats.ShadowDrawCalls, stats.ObjectIdDrawCalls);
        (Meshes, Materials, Textures) = server.ResidentResources;
        var pipelines = server.PipelineStates;
        (Pipelines, CacheHits) = (pipelines?.Count ?? 0, pipelines?.Hits ?? 0);
    }
}

internal sealed class ShadowStats(DevOverlaySources sources)
{
    public bool Has;
    public int Passes, Planned, CascadeRes, AtlasSize, AtlasPacked;
    public int Draws, Casters, Culled;
    public float MapMiB, CpuMs, GpuMs;

    private ShadowSystem? Shadows => sources.Servers.Render?.ExistingShadows;

    public bool DebugCascades
    {
        get => Shadows?.DebugCascades ?? false;
        set
        {
            if (Shadows is { } s)
                s.DebugCascades = value;
        }
    }

    public bool Stable
    {
        get => Shadows?.StableCascades ?? false;
        set
        {
            if (Shadows is { } s)
                s.StableCascades = value;
        }
    }

    /// <summary>The <see cref="ShadowFilter"/> as an int (the select box's value).</summary>
    public int Filter
    {
        get => (int)(Shadows?.Filter ?? ShadowFilter.Hard);
        set
        {
            if (Shadows is { } s)
                s.Filter = (ShadowFilter)Math.Clamp(value, (int)ShadowFilter.Hard, (int)ShadowFilter.Poisson16);
        }
    }

    public float Radius
    {
        get => Shadows?.FilterRadius ?? 1f;
        set
        {
            if (Shadows is { } s)
                s.FilterRadius = value;
        }
    }

    public void Refresh()
    {
        var shadows = Shadows;
        Has = shadows is not null;
        if (shadows is null)
            return;
        (Passes, Planned) = (shadows.RenderedPasses, shadows.PlannedPasses);
        (CascadeRes, AtlasSize, AtlasPacked) = (shadows.CascadeResolution, shadows.AtlasSize, shadows.AtlasPackCount);
        MapMiB = DevOverlaySources.Mib((ulong)shadows.MapMemoryBytes);
        (CpuMs, GpuMs) = ((float)shadows.LastCpuMilliseconds, (float)shadows.LastGpuMilliseconds);
        var stats = sources.Servers.Render!.MeshStats;
        (Draws, Casters, Culled) = (stats.ShadowDrawCalls, stats.ShadowInstances, stats.ShadowCulled);
    }

    /// <summary>The depth map to show for <c>engine://dev-shadow-*</c> (a cascade layer, or the atlas for -1); default when there is none.</summary>
    public static UiTextureView Texture(ServerRegistry servers, int cascade)
    {
        if (servers.Render?.ExistingShadows is not { } shadows)
            return default;
        var view = cascade >= 0 ? shadows.CascadeLayerView(cascade) : shadows.AtlasView;
        if (view.Handle == 0)
            return default;
        var size = (uint)(cascade >= 0 ? shadows.CascadeResolution : shadows.AtlasSize);
        // Depth maps sit in DepthStencilReadOnly between frames; the generation changes whenever maps are re-created.
        return new UiTextureView(view, shadows.DebugSampler, ImageLayout.DepthStencilReadOnlyOptimal, size, size, shadows.MapsGeneration);
    }
}

/// <summary>One memory type row of the GPU memory panel (reused between refreshes).</summary>
internal sealed class GpuTypeRow
{
    public int Index;
    public uint Heap;
    public string Flags = "";
    public int Allocations;
    public float UsedMiB, ReservedMiB;
}

internal sealed class GpuStats(DevOverlaySources sources)
{
    private string[] _flagText = [];

    public bool Has;
    public int Allocations, DeviceMemories, Blocks, Dedicated;
    public float UsedMiB, ReservedMiB, UploadedMiB;
    public long RingUsedKiB, RingKiB, PipelineCacheKiB, Frame;
    public int UploadsPending, DeletionsPending, ShaderModules;

    public List<GpuTypeRow> Types { get; } = [];

    public void Refresh()
    {
        var vk = sources.Vulkan;
        Has = vk is not null;
        if (vk is null)
            return;
        var allocator = vk.Allocator;
        var totals = allocator.Totals;
        (Allocations, DeviceMemories, Blocks, Dedicated) = (totals.AllocationCount, totals.DeviceMemoryCount, totals.BlockCount, totals.DedicatedCount);
        (UsedMiB, ReservedMiB) = (DevOverlaySources.Mib(totals.UsedBytes), DevOverlaySources.Mib(totals.ReservedBytes));

        if (_flagText.Length != allocator.MemoryTypeCount)
            _flagText = new string[allocator.MemoryTypeCount];
        var rows = 0;
        for (var i = 0; i < allocator.MemoryTypeCount; i++)
        {
            var t = allocator.GetMemoryTypeStats(i);
            if (t.ReservedBytes == 0)
                continue;
            if (rows == Types.Count)
                Types.Add(new GpuTypeRow());
            var row = Types[rows++];
            // The property flags of a memory type never change: format them once per type.
            row.Flags = _flagText[i] ??= ((uint)t.Flags).ToString("X", CultureInfo.InvariantCulture);
            (row.Index, row.Heap, row.Allocations) = (t.MemoryTypeIndex, t.HeapIndex, t.AllocationCount);
            (row.UsedMiB, row.ReservedMiB) = (DevOverlaySources.Mib(t.UsedBytes), DevOverlaySources.Mib(t.ReservedBytes));
        }

        if (Types.Count > rows)
            Types.RemoveRange(rows, Types.Count - rows);

        var uploads = vk.Uploads;
        (RingUsedKiB, RingKiB) = ((long)(uploads.RingUsedBytes / 1024), (long)(uploads.RingCapacity / 1024));
        (UploadsPending, UploadedMiB) = (uploads.PendingCount, DevOverlaySources.Mib(uploads.TotalUploadedBytes));
        (DeletionsPending, Frame) = (vk.Deletions.PendingCount, (long)vk.FrameNumber);
        (PipelineCacheKiB, ShaderModules) = (vk.Pipelines.LoadedBytes / 1024, vk.Shaders.Count);
    }
}

/// <summary>The audio buses as a live list: the audio server replaces its bus array when the layout changes.</summary>
internal sealed class AudioBusList(DevOverlaySources sources) : IReadOnlyList<AudioBus>
{
    private IReadOnlyList<AudioBus> Buses => sources.Servers.Get<AudioServer>()?.Buses ?? [];

    public int Count => Buses.Count;

    public AudioBus this[int index] => Buses[index];

    public IEnumerator<AudioBus> GetEnumerator() => Buses.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class AudioStats(DevOverlaySources sources)
{
    public const float MinDb = -60f;

    public bool Has;
    public string Device = "";
    public int Voices, TotalVoices, Underruns;
    public long Steals;

    public AudioBusList Buses { get; } = new(sources);

    /// <summary>Fader dB for the slider (≤ -60 dB is silence); the inverse of <see cref="ToBusDb"/>.</summary>
    public static float FromBusDb(float db) => MathF.Max(db, MinDb);

    public static float ToBusDb(float sliderDb) => sliderDb <= MinDb ? AudioMath.SilenceDb : sliderDb;

    /// <summary>Peak meter on a dB scale: -60 dB .. 0 dB as 0..1.</summary>
    public static float Meter(AudioBus bus) => Math.Clamp((AudioMath.LinearToDb(bus.Peak) + 60f) / 60f, 0f, 1f);

    public void Refresh()
    {
        var audio = sources.Servers.Get<AudioServer>();
        Has = audio is not null;
        if (audio is null)
            return;
        Device = audio.DeviceName;
        var stats = audio.Stats;
        (Voices, TotalVoices, Steals, Underruns) = (stats.ActiveVoices, stats.TotalVoices, stats.Steals, stats.Underruns);
    }
}

internal sealed class PhysicsStats(DevOverlaySources sources)
{
    public bool Has;
    public int Bodies3D, Awake3D, Bodies2D, Awake2D;

    private PhysicsServer3D? Server3D => sources.Servers.Get<PhysicsServer3D>();

    private PhysicsServer2D? Server2D => sources.Servers.Get<PhysicsServer2D>();

    public bool Shapes
    {
        get => Server3D?.DebugDrawEnabled == true || Server2D?.DebugDrawEnabled == true;
        set
        {
            if (Server3D is { } s3)
                s3.DebugDrawEnabled = value;
            if (Server2D is { } s2)
                s2.DebugDrawEnabled = value;
        }
    }

    public bool LightGizmos
    {
        get => sources.Servers.Render?.ShowLightGizmos ?? false;
        set
        {
            if (sources.Servers.Render is { } render)
                render.ShowLightGizmos = value;
        }
    }

    public bool AxisGizmo
    {
        get => sources.Servers.Render?.ShowAxisGizmo ?? false;
        set
        {
            if (sources.Servers.Render is { } render)
                render.ShowAxisGizmo = value;
        }
    }

    public void Refresh(SceneViewport root)
    {
        var server3D = Server3D;
        var server2D = Server2D;
        Has = server3D is not null || server2D is not null;
        (Bodies3D, Awake3D) = server3D?.FindSpace(root.World3D) is { } space3D ? (space3D.ObjectCount, space3D.ActiveBodyCount) : (0, 0);
        (Bodies2D, Awake2D) = server2D?.FindSpace(root.World2D) is { } space2D ? (space2D.ObjectCount, space2D.ActiveBodyCount) : (0, 0);
    }
}

internal sealed class NetworkPanelStats(DevOverlaySources sources)
{
    public bool Active;
    public string Mode = "";
    public long Tick;
    public int Nodes, Clients;
    public float OutBps, InBps;

    public void Refresh()
    {
        var api = sources.Servers.Get<MultiplayerApi>();
        Active = api is { Mode: not MultiplayerMode.Offline };
        if (!Active)
            return;
        Mode = api!.IsServer ? "server" : "client";
        var stats = api.Stats;
        (Tick, Nodes, Clients) = (api.Tick, stats.NetworkedNodes, api.IsServer ? api.ConnectedPeers.Length : 0);
        (OutBps, InBps) = ((float)stats.SendBytesPerSecond, (float)stats.ReceiveBytesPerSecond);
    }
}

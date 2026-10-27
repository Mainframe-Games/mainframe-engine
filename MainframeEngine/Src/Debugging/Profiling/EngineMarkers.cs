namespace MainframeEngine;

/// <summary>The engine's built-in CPU scopes and GPU zones (pass-level), registered once.</summary>
internal static class EngineMarkers
{
    // ── CPU: the frame ───────────────────────────────────────────────────────────────────────────────────────
    public static readonly ProfilerMarker Frame = Cpu("Frame");
    public static readonly ProfilerMarker Update = Cpu("Update");
    public static readonly ProfilerMarker OnUpdate = Cpu("OnUpdate", ProfilerCategory.Script);
    public static readonly ProfilerMarker TreeTick = Cpu("Tree.Tick");
    public static readonly ProfilerMarker SceneLoad = Cpu("Scene load", ProfilerCategory.Loading);
    public static readonly ProfilerMarker PhysicsStep = Cpu("Physics step", ProfilerCategory.Physics);
    public static readonly ProfilerMarker Process = Cpu("Process", ProfilerCategory.Script);
    public static readonly ProfilerMarker Deferred = Cpu("Deferred");
    public static readonly ProfilerMarker TransformSync = Cpu("Transform sync");
    public static readonly ProfilerMarker Render = Cpu("Render", ProfilerCategory.Render);
    public static readonly ProfilerMarker PrepareFrame = Cpu("PrepareFrame", ProfilerCategory.Render);
    public static readonly ProfilerMarker BeginFrame = Cpu("BeginFrame", ProfilerCategory.Render);
    public static readonly ProfilerMarker WaitFence = Cpu("Wait (fence)", ProfilerCategory.Render);
    public static readonly ProfilerMarker Acquire = Cpu("Acquire", ProfilerCategory.Render);
    public static readonly ProfilerMarker Skinning = Cpu("Skinning", ProfilerCategory.Render);
    public static readonly ProfilerMarker Shadows = Cpu("Shadows", ProfilerCategory.Render);
    public static readonly ProfilerMarker Offscreen = Cpu("Offscreen", ProfilerCategory.Render);
    public static readonly ProfilerMarker Prepass = Cpu("Prepass", ProfilerCategory.Render);
    public static readonly ProfilerMarker MainPass = Cpu("Main pass", ProfilerCategory.Render);
    public static readonly ProfilerMarker Overlay = Cpu("Overlay", ProfilerCategory.Render);
    public static readonly ProfilerMarker Submit = Cpu("Submit", ProfilerCategory.Render);
    public static readonly ProfilerMarker Present = Cpu("Present", ProfilerCategory.Render);

    // ── CPU: other threads ───────────────────────────────────────────────────────────────────────────────────
    public static readonly ProfilerMarker LoadScene = Cpu("Load scene", ProfilerCategory.Loading);
    public static readonly ProfilerMarker Instantiate = Cpu("Instantiate", ProfilerCategory.Loading);
    public static readonly ProfilerMarker PrepareLoadables = Cpu("Prepare loadables", ProfilerCategory.Loading);
    public static readonly ProfilerMarker Mix = Cpu("Mix", ProfilerCategory.Audio);

    // ── GPU zones ────────────────────────────────────────────────────────────────────────────────────────────
    public static readonly ProfilerMarker GpuUploads = Gpu("Uploads");
    public static readonly ProfilerMarker GpuSkinning = Gpu("Skinning");
    public static readonly ProfilerMarker GpuShadows = Gpu("Shadows");
    public static readonly ProfilerMarker GpuOffscreen = Gpu("Offscreen");
    public static readonly ProfilerMarker GpuPicking = Gpu("Picking");
    public static readonly ProfilerMarker GpuPrepass = Gpu("Prepass");
    public static readonly ProfilerMarker GpuMainPass = Gpu("Main pass");
    public static readonly ProfilerMarker GpuUiOffscreen = Gpu("UI offscreen", ProfilerCategory.Ui);
    public static readonly ProfilerMarker GpuTonemap = Gpu("Tonemap", ProfilerCategory.Render, ProfilerMarkerKind.GpuInPass);
    public static readonly ProfilerMarker GpuOverlay = Gpu("Overlay", ProfilerCategory.Ui, ProfilerMarkerKind.GpuInPass);

    private static ProfilerMarker Cpu(string name, ProfilerCategory category = ProfilerCategory.Engine) =>
        new(name, category, ProfilerMarkerKind.Cpu, "", 0);

    /// <summary>A GPU zone marker (its own id: the CPU scope of the same name is a separate row).</summary>
    public static ProfilerMarker Gpu(string name, ProfilerCategory category = ProfilerCategory.Render,
        ProfilerMarkerKind kind = ProfilerMarkerKind.Gpu) =>
        new(name, category, kind, "", 0);

    // A marker per type, created once at registration (frame servers, fixed-step servers, overlay renderers).
    private static readonly Dictionary<(Type, string, ProfilerMarkerKind), ProfilerMarker> s_typed = [];

    public static ProfilerMarker ForType(Type type, string suffix = "", ProfilerMarkerKind kind = ProfilerMarkerKind.Cpu,
        ProfilerCategory category = ProfilerCategory.Engine)
    {
        lock (s_typed)
        {
            if (s_typed.TryGetValue((type, suffix, kind), out var marker))
                return marker;
            marker = new ProfilerMarker(type.Name + suffix, category, kind, "", 0);
            s_typed[(type, suffix, kind)] = marker;
            return marker;
        }
    }
}

/// <summary>The engine's built-in counters, sampled at each recorded frame's end (<see cref="Sample"/>).</summary>
internal static class EngineCounters
{
    public static readonly ProfilerCounter DrawCalls = new("Draw calls");
    public static readonly ProfilerCounter Triangles = new("Triangles");
    public static readonly ProfilerCounter Instances = new("Instances");
    public static readonly ProfilerCounter Culled = new("Culled");
    public static readonly ProfilerCounter PrepassDraws = new("Prepass draws");
    public static readonly ProfilerCounter ShadowDraws = new("Shadow draws");
    public static readonly ProfilerCounter IdDraws = new("ID draws");
    public static readonly ProfilerCounter CanvasDraws = new("Canvas draws");
    public static readonly ProfilerCounter UiDraws = new("UI draws");
    public static readonly ProfilerCounter ManagedAllocated = new("Managed allocated", ProfilerCounterUnit.Bytes);
    public static readonly ProfilerCounter Gen0 = new("GC gen 0");
    public static readonly ProfilerCounter Gen1 = new("GC gen 1");
    public static readonly ProfilerCounter Gen2 = new("GC gen 2");
    public static readonly ProfilerCounter GcPause = new("GC pause", ProfilerCounterUnit.Microseconds);
    public static readonly ProfilerCounter GpuMemoryUsed = new("GPU memory used", ProfilerCounterUnit.Bytes);
    public static readonly ProfilerCounter GpuMemoryReserved = new("GPU memory reserved", ProfilerCounterUnit.Bytes);
    public static readonly ProfilerCounter Uploaded = new("Uploaded", ProfilerCounterUnit.Bytes);
    public static readonly ProfilerCounter BodiesAwake = new("Bodies awake");
    public static readonly ProfilerCounter Bodies = new("Bodies");
    public static readonly ProfilerCounter PhysicsAllocated = new("Physics allocated", ProfilerCounterUnit.Bytes);
    public static readonly ProfilerCounter Nodes = new("Nodes");
    public static readonly ProfilerCounter AudioVoices = new("Audio voices");
    public static readonly ProfilerCounter DroppedEvents = new("Profiler dropped");

    /// <summary>Touches the class so every built-in is registered before game counters (stable ids).</summary>
    public static void EnsureRegistered()
    {
    }

    /// <summary>What the deltas compare against (uploads and the physics libraries' allocations since the last sample).</summary>
    internal struct State
    {
        public ulong Uploaded;
        public long PhysicsAllocated;
    }

    /// <summary>
    /// Reads the O(1) statistics that already exist (mesh draws, canvas and UI draws, GPU memory, uploads, physics, nodes,
    /// audio voices) into the counters. The GC counters are the profiler's own (<see cref="Profiler.EndFrame"/>).
    /// </summary>
    public static void Sample(SceneTree tree, IVulkanContext? vk, ref State state)
    {
        var servers = tree.Servers;
        var mesh = servers.Render?.MeshStats ?? default;
        DrawCalls.Set(mesh.DrawCalls);
        Triangles.Set(mesh.Triangles);
        Instances.Set(mesh.Instances);
        Culled.Set(mesh.Culled);
        PrepassDraws.Set(mesh.PrepassDrawCalls);
        ShadowDraws.Set(mesh.ShadowDrawCalls);
        IdDraws.Set(mesh.ObjectIdDrawCalls);
        CanvasDraws.Set(servers.Get<CanvasServer>()?.Renderer?.DrawCalls ?? 0);
        UiDraws.Set(servers.Get<UiServer>()?.Renderer?.Stats.DrawCalls ?? 0);
        if (vk is not null)
        {
            var totals = vk.Allocator.Totals;
            GpuMemoryUsed.Set((long)totals.UsedBytes);
            GpuMemoryReserved.Set((long)totals.ReservedBytes);
            var uploaded = vk.Uploads.TotalUploadedBytes;
            Uploaded.Set((long)(uploaded - Math.Min(uploaded, state.Uploaded)));
            state.Uploaded = uploaded;
        }

        var root = tree.Root;
        int bodies = 0, awake = 0;
        long physicsAllocated = 0;
        if (servers.Get<PhysicsServer3D>()?.FindSpace(root.World3D) is { } space3D)
        {
            bodies += space3D.ObjectCount;
            awake += space3D.ActiveBodyCount;
            physicsAllocated += space3D.LibraryAllocatedBytes;
        }

        if (servers.Get<PhysicsServer2D>()?.FindSpace(root.World2D) is { } space2D)
        {
            bodies += space2D.ObjectCount;
            awake += space2D.ActiveBodyCount;
            physicsAllocated += space2D.LibraryAllocatedBytes;
        }

        Bodies.Set(bodies);
        BodiesAwake.Set(awake);
        PhysicsAllocated.Set(Math.Max(0, physicsAllocated - state.PhysicsAllocated));
        state.PhysicsAllocated = physicsAllocated;
        Nodes.Set(tree.NodeCount);
        AudioVoices.Set(servers.Get<AudioServer>()?.Stats.ActiveVoices ?? 0);
        DroppedEvents.Set(Profiler.DroppedEvents);
    }
}

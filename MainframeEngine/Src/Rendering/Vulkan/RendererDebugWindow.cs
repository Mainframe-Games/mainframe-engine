using System.Globalization;
using ImGuiNET;

namespace MainframeEngine;

/// <summary>
/// ImGui readout of the renderer's GPU resources and colour settings: exposure slider, swapchain encoding, GPU
/// allocator totals and per-memory-type usage, staging ring, pending deletions, pipeline cache. Call from
/// <c>OnImGui</c>. Allocation-free (text is formatted into stack buffers).
/// </summary>
public static class RendererDebugWindow
{
    /// <summary>
    /// Draws the window (does nothing for non-Vulkan renderers). With <paramref name="server"/> it also shows the
    /// frame's mesh draw statistics (instances, culled, draw calls, binds), the pipeline cache and resident
    /// meshes/materials/textures.
    /// </summary>
    public static void Draw(IRenderer renderer, RenderServer? server = null)
    {
        if (renderer is not IVulkanContext vk)
            return;

        // Top right, under the axis gizmo, until the user moves it.
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(display.X - 10f, 90f), ImGuiCond.FirstUseEver, new System.Numerics.Vector2(1f, 0f));
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Renderer", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        Span<char> text = stackalloc char[128];

        var exposure = vk.Exposure;
        if (ImGui.SliderFloat("Exposure", ref exposure, 0.1f, 8f, "%.2f", ImGuiSliderFlags.Logarithmic))
            vk.Exposure = exposure;
        if (ImGui.Button("Reset exposure"))
            vk.Exposure = IVulkanContext.DefaultExposure;

        if (renderer is VulkanRenderer vr)
            ImGui.TextUnformatted(ColourPipelineLine(vr));

        if (server is not null)
        {
            ImGui.SeparatorText("Meshes");
            var stats = server.MeshStats;
            Line(text, $"Draw calls: {stats.DrawCalls} ({stats.SurfaceInstances} surfaces, {stats.Instances} instances, {stats.Culled} culled)");
            Line(text, $"Binds: {stats.PipelineBinds} pipeline, {stats.MaterialBinds} material; shadow draws {stats.ShadowDrawCalls}, ID draws {stats.ObjectIdDrawCalls}");
            var (meshes, materials, textures) = server.ResidentResources;
            var pipelines = server.PipelineStates;
            Line(text, $"Resident: {meshes} meshes, {materials} materials, {textures} textures; {pipelines?.Count ?? 0} pipelines ({pipelines?.Hits ?? 0} hits)");
        }

        if (server?.ExistingShadows is { } shadows)
            DrawShadows(text, vk, server, shadows);

        ImGui.SeparatorText("GPU memory");
        var totals = vk.Allocator.Totals;
        Line(text, $"{totals.AllocationCount} allocations in {totals.DeviceMemoryCount} VkDeviceMemory ({totals.BlockCount} blocks, {totals.DedicatedCount} dedicated)");
        Line(text, $"Used {Mib(totals.UsedBytes):0.0} MiB of {Mib(totals.ReservedBytes):0.0} MiB reserved");

        for (var i = 0; i < vk.Allocator.MemoryTypeCount; i++)
        {
            var t = vk.Allocator.GetMemoryTypeStats(i);
            if (t.ReservedBytes == 0)
                continue;
            Line(text, $"  type {t.MemoryTypeIndex} (heap {t.HeapIndex}, {(uint)t.Flags:X}): {t.AllocationCount} allocs, {Mib(t.UsedBytes):0.0}/{Mib(t.ReservedBytes):0.0} MiB");
        }

        var uploads = vk.Uploads;
        Line(text, $"Staging ring {Mib(uploads.RingUsedBytes):0.0}/{Mib(uploads.RingCapacity):0.0} MiB, {uploads.PendingCount} pending ops, {Mib(uploads.TotalUploadedBytes):0.0} MiB uploaded");
        Line(text, $"Deletion queue: {vk.Deletions.PendingCount} pending (frame {vk.FrameNumber})");
        Line(text, $"Pipeline cache: {vk.Pipelines.LoadedBytes / 1024} KiB loaded, {vk.Shaders.Count} shader modules");

        ImGui.End();
    }

    /// <summary>Opens the shadow "Maps" tree (cascade layers and atlas) every frame.</summary>
    public static bool ExpandShadowMaps { get; set; }

    private static readonly string[] FilterNames = ["Hard", "PCF 3x3", "Poisson 16"];
    private static readonly nint[] ShadowTextureIds = new nint[ShadowSystem.MaxCascades + 1];
    private static readonly Silk.NET.Vulkan.ImageView[] ShadowTextureViews = new Silk.NET.Vulkan.ImageView[ShadowSystem.MaxCascades + 1];
    private static IImGuiTextureRegistry? _shadowTextureRegistry;

    // Shadow settings and statistics; the "Maps" tree shows the cascade layers and the atlas (depth as red).
    private static void DrawShadows(Span<char> text, IVulkanContext vk, RenderServer server, ShadowSystem shadows)
    {
        ImGui.SeparatorText("Shadows");
        var debug = shadows.DebugCascades;
        if (ImGui.Checkbox("Cascade colours", ref debug))
            shadows.DebugCascades = debug;
        var stable = shadows.StableCascades;
        if (ImGui.Checkbox("Stable cascades", ref stable))
            shadows.StableCascades = stable;
        var filter = (int)shadows.Filter;
        if (ImGui.Combo("Filter", ref filter, FilterNames, FilterNames.Length))
            shadows.Filter = (ShadowFilter)filter;
        var radius = shadows.FilterRadius;
        if (ImGui.SliderFloat("Filter radius", ref radius, 0.5f, 8f, "%.2f texels"))
            shadows.FilterRadius = radius;

        var stats = server.MeshStats;
        Line(text, $"Passes {shadows.RenderedPasses}/{shadows.PlannedPasses}, {stats.ShadowDrawCalls} draws, {stats.ShadowInstances} instances, {stats.ShadowCulled} culled");
        Line(text, $"Cascades {shadows.CascadeResolution}², atlas {shadows.AtlasSize}² ({shadows.AtlasPackCount} packs), {Mib((ulong)shadows.MapMemoryBytes):0.0} MiB");
        Line(text, $"CPU {shadows.LastCpuMilliseconds:0.000} ms, GPU {shadows.LastGpuMilliseconds:0.000} ms");

        if (vk.ImGuiTextures is not { } registry)
            return;
        if (ExpandShadowMaps)
            ImGui.SetNextItemOpen(true);
        if (!ImGui.TreeNode("Maps"))
            return;
        if (!ReferenceEquals(registry, _shadowTextureRegistry))
        {
            Array.Clear(ShadowTextureIds);
            Array.Clear(ShadowTextureViews);
            _shadowTextureRegistry = registry;
        }

        for (var i = 0; i <= ShadowSystem.MaxCascades; i++)
        {
            var view = i < ShadowSystem.MaxCascades ? shadows.CascadeLayerView(i) : shadows.AtlasView;
            if (view.Handle == 0)
                continue;
            if (ShadowTextureIds[i] == 0)
                ShadowTextureIds[i] = registry.Register(view, shadows.DebugSampler, Silk.NET.Vulkan.ImageLayout.DepthStencilReadOnlyOptimal);
            else if (ShadowTextureViews[i].Handle != view.Handle)
                registry.Update(ShadowTextureIds[i], view, shadows.DebugSampler, Silk.NET.Vulkan.ImageLayout.DepthStencilReadOnlyOptimal);
            ShadowTextureViews[i] = view;

            var size = i < ShadowSystem.MaxCascades ? 96f : 256f;
            if (i == ShadowSystem.MaxCascades)
                ImGui.NewLine();
            else if (i > 0)
                ImGui.SameLine();
            ImGui.Image(ShadowTextureIds[i], new System.Numerics.Vector2(size, size));
        }

        ImGui.TreePop();
    }

    private static double Mib(ulong bytes) => bytes / (1024.0 * 1024.0);

    // Built only when the swapchain format or encoding changes: formatting enums allocates in some builds (the
    // lavapipe render tests measured ~80 B/frame from this line on x64 CI-style builds), and the text rarely changes.
    private static string? _colourPipelineLine;
    private static (Silk.NET.Vulkan.Format Format, VulkanRenderer.SwapchainEncoding Encoding) _colourPipelineKey;

    private static string ColourPipelineLine(VulkanRenderer renderer)
    {
        var key = (renderer.SwapchainFormat, renderer.Encoding);
        if (_colourPipelineLine is null || key != _colourPipelineKey)
        {
            _colourPipelineKey = key;
            _colourPipelineLine = string.Create(CultureInfo.InvariantCulture,
                $"Scene {VulkanRenderer.SceneColorFormat} -> {key.SwapchainFormat} ({key.Encoding})");
        }

        return _colourPipelineLine;
    }

    private static void Line(Span<char> buffer, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument(nameof(buffer))] ref MemoryExtensions.TryWriteInterpolatedStringHandler handler)
    {
        if (MemoryExtensions.TryWrite(buffer, ref handler, out var written))
            ImGui.TextUnformatted(buffer[..written]);
    }
}

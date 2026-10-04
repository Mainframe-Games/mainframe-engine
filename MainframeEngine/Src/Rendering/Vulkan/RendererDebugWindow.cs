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
    /// <summary>Draws the window (does nothing for non-Vulkan renderers).</summary>
    public static void Draw(IRenderer renderer)
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
            Line(text, $"Scene {VulkanRenderer.SceneColorFormat} -> {vr.SwapchainFormat} ({vr.Encoding})");

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

    private static double Mib(ulong bytes) => bytes / (1024.0 * 1024.0);

    private static void Line(Span<char> buffer, [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument(nameof(buffer))] ref MemoryExtensions.TryWriteInterpolatedStringHandler handler)
    {
        if (MemoryExtensions.TryWrite(buffer, ref handler, out var written))
            ImGui.TextUnformatted(buffer[..written]);
    }
}

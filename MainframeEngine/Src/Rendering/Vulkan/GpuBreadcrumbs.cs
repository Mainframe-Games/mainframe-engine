using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>The passes a frame's breadcrumbs mark, in frame order (G15).</summary>
internal enum GpuPass
{
    Uploads,
    Skinning,
    Shadows,
    Offscreen,
    Prepass,
    Scene,
    UiOffscreen,
    BeforeTonemap,

    /// <summary>The tonemap, the effects after it and the overlay (canvas, gizmos, UI): one render pass sequence.</summary>
    Present,
}

/// <summary>
/// Per-pass GPU breadcrumbs (G15, docs/design/crash-reporting.md#gpu-device-loss-and-breadcrumbs): one host-visible,
/// coherent buffer of <c>uint</c>s, frame slot × pass × {begin, end}. At its start and end each pass writes the frame
/// number into its cells with <c>vkCmdFillBuffer</c>, which is legal only outside render passes, so the marks sit at pass
/// boundaries; an end mark waits (an execution barrier) for everything before it, so a pass whose end equals the frame
/// had finished. After a device loss, the last submitted frame's passes with <c>begin == frame</c> and
/// <c>end != frame</c> were in flight. Two fills a pass (about twenty commands a frame), no allocation, no visual change.
/// </summary>
internal sealed unsafe class GpuBreadcrumbs : IDisposable
{
    public static readonly int PassCount = Enum.GetValues<GpuPass>().Length;

    private static readonly string[] PassNames = Enum.GetNames<GpuPass>();

    private readonly IVulkanContext _context;
    private readonly GpuBuffer _buffer;

    public GpuBreadcrumbs(IVulkanContext context)
    {
        _context = context;
        _buffer = GpuBuffer.Create(context, (ulong)(IVulkanContext.MaxFramesInFlight * PassCount * 2 * sizeof(uint)),
            BufferUsageFlags.TransferDstBit, GpuMemoryUsage.Readback);
        _buffer.MappedSpan.Clear();
    }

    /// <summary>Marks <paramref name="pass"/> beginning in frame <paramref name="frame"/> (slot <paramref name="slot"/>). No render pass may be active.</summary>
    public void Begin(CommandBuffer cb, int slot, GpuPass pass, ulong frame) => Fill(cb, Offset(slot, pass, end: false), frame);

    /// <summary>Marks <paramref name="pass"/> finished: written once everything recorded before it has executed. No render pass may be active.</summary>
    public void End(CommandBuffer cb, int slot, GpuPass pass, ulong frame)
    {
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier };
        _context.Vk.CmdPipelineBarrier(cb, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
        Fill(cb, Offset(slot, pass, end: true), frame);
    }

    private void Fill(CommandBuffer cb, ulong offset, ulong frame) =>
        _context.Vk.CmdFillBuffer(cb, _buffer.Handle, offset, sizeof(uint), unchecked((uint)frame));

    private static ulong Offset(int slot, GpuPass pass, bool end) =>
        (ulong)(((slot * PassCount + (int)pass) * 2 + (end ? 1 : 0)) * sizeof(uint));

    /// <summary>The marks of <paramref name="slot"/>'s passes (begin, end), as the GPU left them.</summary>
    public (uint Begin, uint End)[] Read(int slot)
    {
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_buffer.MappedSpan);
        var result = new (uint, uint)[PassCount];
        for (var p = 0; p < PassCount; p++)
            result[p] = (words[(slot * PassCount + p) * 2], words[(slot * PassCount + p) * 2 + 1]);
        return result;
    }

    /// <summary>The report of frame <paramref name="frame"/> in <paramref name="slot"/>: passes in flight and finished.</summary>
    public CrashGpuLoss Describe(int slot, ulong frame)
    {
        var loss = new CrashGpuLoss { Frame = (long)frame, BreadcrumbsAvailable = true };
        var marks = Read(slot);
        var stamp = unchecked((uint)frame);
        for (var p = 0; p < marks.Length; p++)
        {
            if (marks[p].Begin == stamp && marks[p].End != stamp)
                loss.InFlight.Add(PassNames[p]);
            else if (marks[p].Begin == stamp && marks[p].End == stamp)
                loss.Completed.Add(PassNames[p]);
        }

        return loss;
    }

    public void Dispose() => _buffer.Dispose();
}

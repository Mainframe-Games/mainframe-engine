namespace MainframeEngine.Tests;

/// <summary>Measures how much a steady-state window allocates on the calling thread, for the 0-byte gates.</summary>
/// <remarks>
/// When many other test threads run in the same process, a window occasionally reports a few kilobytes (always less
/// than one 8 KB allocation quantum) although the code under test allocates nothing: instrumenting the networking gates
/// placed the bytes in code that cannot allocate (loop bookkeeping between two rounds), the same test never shows it
/// when run alone, and forcing collections from another thread does not reproduce it. A real steady-state allocation
/// repeats in every window, so the gate runs up to <see cref="MaxWindows"/> windows and keeps the smallest result.
/// </remarks>
internal static class AllocationGate
{
    /// <summary>Most windows run before the smallest allocation is reported.</summary>
    public const int MaxWindows = 3;

    /// <summary>
    /// Runs <paramref name="window"/> until it allocates nothing on this thread, at most <see cref="MaxWindows"/> times,
    /// and returns the fewest bytes one run allocated (0 on success).
    /// </summary>
    public static long SmallestWindow(Action window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var smallest = long.MaxValue;
        for (var attempt = 0; attempt < MaxWindows && smallest != 0; attempt++)
        {
            var before    = GC.GetAllocatedBytesForCurrentThread();
            window();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            smallest = Math.Min(smallest, allocated);
        }

        return smallest;
    }
}

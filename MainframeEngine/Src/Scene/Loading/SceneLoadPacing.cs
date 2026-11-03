namespace MainframeEngine;

/// <summary>
/// While a scene load warms up (<see cref="SceneLoadStage.WarmingUp"/>), the renderer spreads the creation of GPU resources
/// for nodes it sees the first time over several frames (<see cref="MeshRenderer"/>: a per-frame time budget), so no single
/// frame of a large scene's first frames stalls the main loop for seconds; the load waits until a frame created everything.
/// Main thread only.
/// </summary>
internal static class SceneLoadPacing
{
    private static int s_warming;

    /// <summary>A load is warming up: the renderer paces its creations.</summary>
    public static bool IsActive => s_warming > 0;

    /// <summary>Nodes whose GPU resources the last prepared frame left for later (0 when it created everything it met).</summary>
    public static int DeferredLastFrame { get; private set; }

    internal static void Begin() => s_warming++;

    internal static void End()
    {
        s_warming = Math.Max(0, s_warming - 1);
        if (s_warming == 0)
            DeferredLastFrame = 0;
    }

    /// <summary>The renderer's count for the frame it finished preparing.</summary>
    internal static void ReportDeferred(int count) => DeferredLastFrame = count;
}

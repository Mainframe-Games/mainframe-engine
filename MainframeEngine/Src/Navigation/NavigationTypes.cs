using System.Numerics;

namespace MainframeEngine;

/// <summary>When a grid path may move diagonally (Godot's <c>AStarGrid2D.DiagonalMode</c>).</summary>
public enum GridDiagonalMode : byte
{
    /// <summary>Diagonal moves are always allowed, even between two solid cells.</summary>
    Always,

    /// <summary>4-way: never diagonally.</summary>
    Never,

    /// <summary>Diagonally when at least one of the two cells beside the move is walkable.</summary>
    AtLeastOneWalkable,

    /// <summary>Diagonally only when both cells beside the move are walkable (no corner cutting).</summary>
    OnlyIfNoObstacles,
}

/// <summary>The distance a grid uses for step costs and estimates, in cells (Godot's <c>AStarGrid2D.Heuristic</c>).</summary>
public enum GridHeuristic : byte
{
    /// <summary><c>sqrt(dx² + dy²)</c>.</summary>
    Euclidean,

    /// <summary><c>dx + dy</c>.</summary>
    Manhattan,

    /// <summary><c>(√2 − 1) · min(dx, dy) + max(dx, dy)</c>: exact for 8-way grids.</summary>
    Octile,

    /// <summary><c>max(dx, dy)</c>.</summary>
    Chebyshev,
}

/// <summary>The outcome of a path query.</summary>
public enum GridPathStatus : byte
{
    /// <summary>A complete path from the start to the target.</summary>
    Found,

    /// <summary>The target is unreachable (or the search hit its node limit): the path ends at the reachable point closest to it.</summary>
    Partial,

    /// <summary>No path: the target is unreachable and partial paths were not allowed.</summary>
    NotFound,

    /// <summary>The start is solid, disabled or outside the grid (or the target is outside it).</summary>
    InvalidPoint,

    /// <summary>The caller's buffer is too small; <c>count</c> holds the size it needs.</summary>
    BufferTooSmall,
}

/// <summary>How a navigation path is simplified after the search (Godot's <c>NavigationPathQueryParameters2D.PathPostProcessing</c>).</summary>
public enum NavigationPathPostprocessing : byte
{
    /// <summary>Grids: only the cells needed for line of sight (string pulling). Navmeshes (later): the funnel algorithm.</summary>
    CorridorFunnel,

    /// <summary>Grids: the corners of the cell path (collinear cells removed).</summary>
    EdgeCentered,

    /// <summary>Every cell centre of the path.</summary>
    None,
}

/// <summary>Where a <see cref="NavigationGrid2D"/>'s solid cells come from.</summary>
public enum NavigationGridSource : byte
{
    /// <summary>Set from code or by hand (<see cref="NavigationGrid2D.SetCellSolid"/>); <see cref="NavigationGrid2D.Bake"/> only clears.</summary>
    Manual,

    /// <summary>Baked from the static bodies overlapping each cell (<see cref="GridPhysicsBaker2D"/>).</summary>
    Physics,

    /// <summary>Read from a <c>TileMapLayer</c> (G3.6, after tile maps): a baker registered with <see cref="NavigationGridBakers"/>.</summary>
    TileMapLayer,
}

/// <summary>A waypoint a <see cref="NavigationAgent2D"/> reached (Godot's <c>waypoint_reached</c> details, as a struct).</summary>
/// <param name="Position">The waypoint's position (pixels).</param>
/// <param name="Index">Its index in the agent's current path.</param>
/// <param name="Cell">The grid cell behind it.</param>
public readonly record struct NavigationWaypoint2D(Vector2 Position, int Index, Vector2I Cell);

/// <summary>A path query against a <see cref="NavigationMap2D"/>.</summary>
/// <param name="Start">Where the path starts (pixels).</param>
/// <param name="Target">Where it should end (pixels).</param>
/// <param name="NavigationLayers">The region layers the query may use (a region is used when its layers intersect these).</param>
/// <param name="Postprocessing">How the cell path is simplified.</param>
/// <param name="AllowPartialPath">Return the path to the closest reachable point when the target cannot be reached.</param>
public readonly record struct NavigationPathQuery2D(Vector2 Start, Vector2 Target, uint NavigationLayers = 1,
    NavigationPathPostprocessing Postprocessing = NavigationPathPostprocessing.CorridorFunnel,
    bool AllowPartialPath = true);

/// <summary>
/// The colours of the navigation debug draw (<see cref="NavigationServer2D.DebugDrawEnabled"/>), sRGB with alpha. Static
/// and settable: a game can restyle them once at start-up.
/// </summary>
public static class NavigationDebugColors
{
    /// <summary>The region's outline.</summary>
    public static Vector4 Region { get; set; } = new(0.25f, 0.75f, 0.95f, 0.9f);

    /// <summary>The cell lines inside a region.</summary>
    public static Vector4 CellLines { get; set; } = new(0.25f, 0.75f, 0.95f, 0.18f);

    /// <summary>Solid cells (outline and cross).</summary>
    public static Vector4 Solid { get; set; } = new(0.95f, 0.35f, 0.35f, 0.75f);

    /// <summary>Cells with a weight scale other than 1 (dashed outline).</summary>
    public static Vector4 Weighted { get; set; } = new(0.98f, 0.72f, 0.2f, 0.85f);

    /// <summary>An agent's path.</summary>
    public static Vector4 Path { get; set; } = new(0.98f, 0.92f, 0.3f, 1f);

    /// <summary>An agent's next path position.</summary>
    public static Vector4 NextPosition { get; set; } = new(0.4f, 1f, 0.55f, 1f);

    /// <summary>An agent's target.</summary>
    public static Vector4 Target { get; set; } = new(1f, 0.45f, 0.85f, 1f);
}

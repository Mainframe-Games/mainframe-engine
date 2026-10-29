using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The navigation debug draw into a viewport's <see cref="SceneViewport.DebugLines"/> (z = 0 plane, pixels): each grid's
/// outline, cell lines, solid cells (outline and cross) and weighted cells (dashed), and each agent's path, next path
/// position and target, in <see cref="NavigationDebugColors"/>. Cells are culled to the view (the editor camera or the
/// current <see cref="Camera2D"/>); a view showing more than <see cref="MaxDetailedCells"/> cells of a grid draws only
/// its outline. Allocation-free.
/// </summary>
internal static class NavigationDebugDraw
{
    /// <summary>Above this many visible cells a grid draws only its outline (keeps far zooms under the line cap).</summary>
    public const int MaxDetailedCells = 32_768;

    public static bool HasDebugAgents(NavigationMap2D map)
    {
        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
            if (agents[i].DebugEnabled)
                return true;
        return false;
    }

    public static void Draw(NavigationMap2D map, SceneViewport viewport, bool all)
    {
        var lines = viewport.DebugLines;
        var view = VisibleRect(viewport);
        if (all)
        {
            var regions = map.RegionList;
            for (var i = 0; i < regions.Count; i++)
                DrawGrid(lines, regions[i].LiveGrid, view);
        }

        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
            if (all || agents[i].DebugEnabled)
                DrawAgent(lines, agents[i]);
    }

    // The world rectangle the view shows, from the orthographic camera it renders with (null: unknown, draw everything).
    private static (Vector2 Min, Vector2 Max)? VisibleRect(SceneViewport viewport)
    {
        var camera = viewport.CameraOverride ?? viewport.ActiveCamera2D?.RenderCamera;
        if (camera is null)
            return null;
        var projection = camera.ProjectionMatrix;
        if (projection.M44 != 1f || !Matrix4x4.Invert(camera.ViewMatrix * projection, out var inverse))
            return null; // perspective (a 3D view): no culling
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < 4; i++)
        {
            var ndc = new Vector4((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, 0.5f, 1f);
            var world = Vector4.Transform(ndc, inverse);
            var p = new Vector2(world.X, world.Y) / world.W;
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return (min, max);
    }

    private static void DrawGrid(DebugLines lines, AStarGrid2D grid, (Vector2 Min, Vector2 Max)? view)
    {
        var region = grid.Region;
        if (grid.IsDirty || region.Size.X <= 0 || region.Size.Y <= 0)
            return;
        var cell = grid.CellSize;
        var origin = grid.Offset;
        var min = origin + new Vector2(region.Position.X, region.Position.Y) * cell;
        var max = origin + new Vector2(region.End.X, region.End.Y) * cell;
        Rect(lines, min, max, NavigationDebugColors.Region);

        // The visible cells (all of them without a view).
        int x0 = region.Position.X, y0 = region.Position.Y, x1 = region.End.X, y1 = region.End.Y;
        if (view is { } v)
        {
            if (v.Max.X < min.X || v.Max.Y < min.Y || v.Min.X > max.X || v.Min.Y > max.Y)
                return;
            x0 = Math.Max(x0, (int)MathF.Floor((v.Min.X - origin.X) / cell.X));
            y0 = Math.Max(y0, (int)MathF.Floor((v.Min.Y - origin.Y) / cell.Y));
            x1 = Math.Min(x1, (int)MathF.Ceiling((v.Max.X - origin.X) / cell.X));
            y1 = Math.Min(y1, (int)MathF.Ceiling((v.Max.Y - origin.Y) / cell.Y));
        }

        if (x1 <= x0 || y1 <= y0 || (long)(x1 - x0) * (y1 - y0) > MaxDetailedCells)
            return;

        var cellLines = NavigationDebugColors.CellLines;
        var top = origin.Y + y0 * cell.Y;
        var bottom = origin.Y + y1 * cell.Y;
        for (var x = x0 + 1; x < x1; x++)
        {
            var px = origin.X + x * cell.X;
            Line(lines, new Vector2(px, top), new Vector2(px, bottom), cellLines);
        }

        var left = origin.X + x0 * cell.X;
        var right = origin.X + x1 * cell.X;
        for (var y = y0 + 1; y < y1; y++)
        {
            var py = origin.Y + y * cell.Y;
            Line(lines, new Vector2(left, py), new Vector2(right, py), cellLines);
        }

        var solid = NavigationDebugColors.Solid;
        var weighted = NavigationDebugColors.Weighted;
        var inset = cell * 0.08f;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var index = grid.Index(x, y);
                var a = origin + new Vector2(x, y) * cell + inset;
                var b = origin + new Vector2(x + 1, y + 1) * cell - inset;
                if (grid.IsSolid(index))
                {
                    Rect(lines, a, b, solid);
                    Line(lines, a, b, solid);
                    Line(lines, new Vector2(a.X, b.Y), new Vector2(b.X, a.Y), solid);
                }
                else if (grid.HasWeights && grid.WeightAt(index) != 1f)
                {
                    Dashed(lines, a, new Vector2(b.X, a.Y), weighted);
                    Dashed(lines, new Vector2(b.X, a.Y), b, weighted);
                    Dashed(lines, b, new Vector2(a.X, b.Y), weighted);
                    Dashed(lines, new Vector2(a.X, b.Y), a, weighted);
                }
            }
        }
    }

    private static void DrawAgent(DebugLines lines, NavigationAgent2D agent)
    {
        var path = agent.GetCurrentNavigationPath();
        var color = NavigationDebugColors.Path;
        for (var i = 1; i < path.Length; i++)
            Line(lines, path[i - 1], path[i], color);
        if (path.Length == 0 || agent.IsNavigationFinishedNoUpdate)
            return;
        var index = Math.Clamp(agent.GetCurrentNavigationPathIndex(), 0, path.Length - 1);
        Cross(lines, path[index], 5f, NavigationDebugColors.NextPosition);
        var target = agent.TargetPosition;
        var radius = MathF.Max(agent.TargetDesiredDistance, 4f);
        lines.AddCircle2D(Transform2D.Identity, target, radius, NavigationDebugColors.Target);
        Cross(lines, target, radius * 0.6f, NavigationDebugColors.Target);
    }

    private static void Line(DebugLines lines, Vector2 a, Vector2 b, Vector4 color) =>
        lines.AddLine(new Vector3(a, 0f), new Vector3(b, 0f), color);

    private static void Rect(DebugLines lines, Vector2 min, Vector2 max, Vector4 color)
    {
        Line(lines, min, new Vector2(max.X, min.Y), color);
        Line(lines, new Vector2(max.X, min.Y), max, color);
        Line(lines, max, new Vector2(min.X, max.Y), color);
        Line(lines, new Vector2(min.X, max.Y), min, color);
    }

    // Two dashes per edge (the middle fifth and the outer fifths left out).
    private static void Dashed(DebugLines lines, Vector2 a, Vector2 b, Vector4 color)
    {
        var d = b - a;
        Line(lines, a + d * 0.1f, a + d * 0.4f, color);
        Line(lines, a + d * 0.6f, a + d * 0.9f, color);
    }

    private static void Cross(DebugLines lines, Vector2 at, float size, Vector4 color)
    {
        Line(lines, at - new Vector2(size, 0f), at + new Vector2(size, 0f), color);
        Line(lines, at - new Vector2(0f, size), at + new Vector2(0f, size), color);
    }
}

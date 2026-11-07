using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.11: the 3D navigation debug draw into a viewport's <see cref="SceneViewport.DebugLines"/>: each grid's outline on its
/// plane, solid cells (outline and cross) and weighted cells (two dashes per side) a few millimetres above it — the cells
/// only while a grid has at most <see cref="NavigationDebugDraw.MaxDetailedCells"/> — and each agent's path, next position
/// and target, in <see cref="NavigationDebugColors"/>. Allocation-free.
/// </summary>
internal static class NavigationDebugDraw3D
{
    private const float Lift = 0.02f; // above the plane, so the lines are not hidden in the floor

    public static bool HasDebugAgents(NavigationMap3D map)
    {
        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
            if (agents[i].DebugEnabled)
                return true;
        return false;
    }

    public static void Draw(NavigationMap3D map, DebugLines lines, bool all)
    {
        if (all)
        {
            var regions = map.RegionList;
            for (var i = 0; i < regions.Count; i++)
                DrawGrid(lines, regions[i]);
        }

        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
            if (all || agents[i].DebugEnabled)
                DrawAgent(lines, agents[i]);
    }

    private static void DrawGrid(DebugLines lines, NavigationGrid3D region)
    {
        var grid = region.LiveGrid;
        var r = grid.Region;
        if (grid.IsDirty || r.Size.X <= 0 || r.Size.Y <= 0)
            return;
        var y = region.PlacedHeight + Lift;
        var cell = grid.CellSize;
        var origin = grid.Offset;
        Vector3 At(float x, float z) => new(origin.X + x * cell.X, y, origin.Y + z * cell.Y);
        Rect(lines, At(r.Position.X, r.Position.Y), At(r.End.X, r.End.Y), NavigationDebugColors.Region);
        if ((long)r.Size.X * r.Size.Y > NavigationDebugDraw.MaxDetailedCells)
            return;
        var solid = NavigationDebugColors.Solid;
        var weighted = NavigationDebugColors.Weighted;
        for (var z = r.Position.Y; z < r.End.Y; z++)
        {
            for (var x = r.Position.X; x < r.End.X; x++)
            {
                var index = grid.Index(x, z);
                var a = At(x + 0.08f, z + 0.08f);
                var b = At(x + 0.92f, z + 0.92f);
                if (grid.IsSolid(index))
                {
                    Rect(lines, a, b, solid);
                    lines.AddLine(a, b, solid);
                    lines.AddLine(new Vector3(a.X, y, b.Z), new Vector3(b.X, y, a.Z), solid);
                }
                else if (grid.HasWeights && grid.WeightAt(index) != 1f)
                {
                    lines.AddLine(a, a + (new Vector3(b.X, y, a.Z) - a) * 0.4f, weighted);
                    lines.AddLine(b, b + (new Vector3(a.X, y, b.Z) - b) * 0.4f, weighted);
                }
            }
        }
    }

    private static void DrawAgent(DebugLines lines, NavigationAgent3D agent)
    {
        var path = agent.GetCurrentNavigationPath();
        var lift = new Vector3(0f, Lift, 0f);
        var color = NavigationDebugColors.Path;
        for (var i = 1; i < path.Length; i++)
            lines.AddLine(path[i - 1] + lift, path[i] + lift, color);
        if (path.Length == 0 || agent.IsNavigationFinishedNoUpdate)
            return;
        var index = Math.Clamp(agent.GetCurrentNavigationPathIndex(), 0, path.Length - 1);
        Cross(lines, path[index] + lift, 0.25f, NavigationDebugColors.NextPosition);
        var target = agent.TargetPosition + lift;
        var radius = MathF.Max(agent.TargetDesiredDistance, 0.1f);
        lines.AddCircle(Transform3D.Identity, target, Vector3.UnitX, Vector3.UnitZ, radius, NavigationDebugColors.Target);
        Cross(lines, target, radius * 0.6f, NavigationDebugColors.Target);
    }

    private static void Rect(DebugLines lines, Vector3 min, Vector3 max, Vector4 color)
    {
        var y = min.Y;
        lines.AddLine(new Vector3(min.X, y, min.Z), new Vector3(max.X, y, min.Z), color);
        lines.AddLine(new Vector3(max.X, y, min.Z), new Vector3(max.X, y, max.Z), color);
        lines.AddLine(new Vector3(max.X, y, max.Z), new Vector3(min.X, y, max.Z), color);
        lines.AddLine(new Vector3(min.X, y, max.Z), new Vector3(min.X, y, min.Z), color);
    }

    private static void Cross(DebugLines lines, Vector3 at, float size, Vector4 color)
    {
        lines.AddLine(at - new Vector3(size, 0f, 0f), at + new Vector3(size, 0f, 0f), color);
        lines.AddLine(at - new Vector3(0f, 0f, size), at + new Vector3(0f, 0f, size), color);
    }
}

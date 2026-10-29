using System.Numerics;

namespace MainframeEngine;

// Jump point search (Harabor & Grastien) for each diagonal mode: the pruning and forced-neighbour rules of the
// corresponding variants of PathFinding.js (MIT): always, at most one obstacle, no obstacles, never. Jumps are
// iterative (a 2048-cell line never recurses deeply). Every step a rule proposes still goes through CanStep, so a rule
// can only cost speed, never produce an illegal move. Weight scales are ignored, as in Godot.
public partial class AStarGrid2D
{
    private bool SolveJps(int start, Vector2I to, out int closest)
    {
        var search = _search;
        search.Begin();
        search.Open(start, 0f, Estimate(start, to), -1);
        closest = start;
        var limit = MaxSearchNodes;
        _expanded = 0;
        Span<sbyte> dirs = stackalloc sbyte[16];
        while (search.OpenCount > 0)
        {
            var node = search.PopMin();
            if (node == _end)
            {
                closest = node;
                return true;
            }

            if (IsCloser(node, closest))
                closest = node;
            if (limit > 0 && ++_expanded >= limit)
                return false;

            var x = _region.Position.X + node % _width;
            var y = _region.Position.Y + node / _width;
            var count = PrunedDirections(node, x, y, dirs);
            var g = search.G(node);
            for (var i = 0; i < count; i++)
            {
                int dx = dirs[2 * i], dy = dirs[2 * i + 1];
                if (!CanStep(x, y, dx, dy))
                    continue;
                var jump = Jump(x + dx, y + dy, dx, dy);
                if (jump < 0 || search.IsClosed(jump))
                    continue;
                var tentative = g + JumpCost(node, jump);
                if (!search.IsSeen(jump))
                    search.Open(jump, tentative, Estimate(jump, to), node);
                else if (tentative < search.G(jump))
                    search.Decrease(jump, tentative, node);
            }
        }

        return false;
    }

    // A straight or diagonal line of k cells: the heuristic's distance (k or k·√2 for Euclidean and Octile).
    private float JumpCost(int from, int to)
    {
        var a = CellOf(from);
        var b = CellOf(to);
        return _builtInCosts ? Distance(_computeHeuristic, b.X - a.X, b.Y - a.Y) : ComputeCost(a, b);
    }

    private int PrunedDirections(int node, int x, int y, Span<sbyte> dirs)
    {
        var parent = _search.Parent(node);
        var n = 0;
        if (parent < 0)
        {
            var all = _diagonalMode == GridDiagonalMode.Never ? 4 : 8;
            for (var d = 0; d < all; d++)
                Add(dirs, ref n, DirX[d], DirY[d]);
            return n;
        }

        var p = CellOf(parent);
        var dx = Math.Sign(x - p.X);
        var dy = Math.Sign(y - p.Y);
        switch (_diagonalMode)
        {
            case GridDiagonalMode.Never:
                if (dx != 0)
                {
                    Add(dirs, ref n, 0, -1);
                    Add(dirs, ref n, 0, 1);
                    Add(dirs, ref n, dx, 0);
                }
                else
                {
                    Add(dirs, ref n, -1, 0);
                    Add(dirs, ref n, 1, 0);
                    Add(dirs, ref n, 0, dy);
                }

                break;
            case GridDiagonalMode.OnlyIfNoObstacles:
                if (dx != 0 && dy != 0)
                {
                    Add(dirs, ref n, 0, dy);
                    Add(dirs, ref n, dx, 0);
                    Add(dirs, ref n, dx, dy);
                }
                else if (dx != 0)
                {
                    Add(dirs, ref n, dx, 0);
                    Add(dirs, ref n, 0, 1);
                    Add(dirs, ref n, 0, -1);
                    Add(dirs, ref n, dx, 1);
                    Add(dirs, ref n, dx, -1);
                }
                else
                {
                    Add(dirs, ref n, 0, dy);
                    Add(dirs, ref n, 1, 0);
                    Add(dirs, ref n, -1, 0);
                    Add(dirs, ref n, 1, dy);
                    Add(dirs, ref n, -1, dy);
                }

                break;
            default: // Always, AtLeastOneWalkable
                if (dx != 0 && dy != 0)
                {
                    Add(dirs, ref n, 0, dy);
                    Add(dirs, ref n, dx, 0);
                    Add(dirs, ref n, dx, dy);
                    if (!IsWalkable(x - dx, y))
                        Add(dirs, ref n, -dx, dy);
                    if (!IsWalkable(x, y - dy))
                        Add(dirs, ref n, dx, -dy);
                }
                else if (dx != 0)
                {
                    Add(dirs, ref n, dx, 0);
                    if (!IsWalkable(x, y + 1))
                        Add(dirs, ref n, dx, 1);
                    if (!IsWalkable(x, y - 1))
                        Add(dirs, ref n, dx, -1);
                }
                else
                {
                    Add(dirs, ref n, 0, dy);
                    if (!IsWalkable(x + 1, y))
                        Add(dirs, ref n, 1, dy);
                    if (!IsWalkable(x - 1, y))
                        Add(dirs, ref n, -1, dy);
                }

                break;
        }

        return n;
    }

    private static void Add(Span<sbyte> dirs, ref int n, int dx, int dy)
    {
        dirs[2 * n] = (sbyte)dx;
        dirs[2 * n + 1] = (sbyte)dy;
        n++;
    }

    // From (x, y), entered legally by a step of (dx, dy), the next jump point in that direction, or -1.
    private int Jump(int x, int y, int dx, int dy)
    {
        while (true)
        {
            if (!IsWalkable(x, y))
                return -1;
            var index = Index(x, y);
            if (index == _end)
                return index;
            if (dx != 0 && dy != 0)
            {
                if (_diagonalMode != GridDiagonalMode.OnlyIfNoObstacles &&
                    (IsWalkable(x - dx, y + dy) && !IsWalkable(x - dx, y) || IsWalkable(x + dx, y - dy) && !IsWalkable(x, y - dy)))
                    return index;
                if (Jump(x + dx, y, dx, 0) >= 0 || Jump(x, y + dy, 0, dy) >= 0)
                    return index;
                if (!CanStep(x, y, dx, dy))
                    return -1;
            }
            else
            {
                if (StraightForced(x, y, dx, dy))
                    return index;
                if (_diagonalMode == GridDiagonalMode.Never && dy != 0 && (Jump(x + 1, y, 1, 0) >= 0 || Jump(x - 1, y, -1, 0) >= 0))
                    return index;
            }

            x += dx;
            y += dy;
        }
    }

    private bool StraightForced(int x, int y, int dx, int dy)
    {
        if (_diagonalMode is GridDiagonalMode.OnlyIfNoObstacles or GridDiagonalMode.Never)
        {
            if (dx != 0)
                return IsWalkable(x, y - 1) && !IsWalkable(x - dx, y - 1) || IsWalkable(x, y + 1) && !IsWalkable(x - dx, y + 1);
            return IsWalkable(x - 1, y) && !IsWalkable(x - 1, y - dy) || IsWalkable(x + 1, y) && !IsWalkable(x + 1, y - dy);
        }

        if (dx != 0)
            return IsWalkable(x + dx, y + 1) && !IsWalkable(x, y + 1) || IsWalkable(x + dx, y - 1) && !IsWalkable(x, y - 1);
        return IsWalkable(x + 1, y + dy) && !IsWalkable(x + 1, y) || IsWalkable(x - 1, y + dy) && !IsWalkable(x - 1, y);
    }
}

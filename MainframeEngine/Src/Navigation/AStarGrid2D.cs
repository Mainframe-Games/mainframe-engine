using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Godot 4's grid A* (<c>AStarGrid2D</c>): a rectangle of cells (<see cref="Region"/>), each walkable or solid with an
/// optional weight scale, searched with A* or jump point search. A plain class usable without a tree. Queries write into
/// caller buffers and allocate nothing after <see cref="Update"/>; results are deterministic (same grid and query, same
/// path on every OS, CPU and thread). Costs are in cells: the step cost is <see cref="DefaultComputeHeuristic"/> between
/// the two cells times the entered cell's weight scale, the estimate <see cref="DefaultEstimateHeuristic"/> to the target.
/// </summary>
/// <remarks>
/// Deviations from Godot: positions are cell centres (<see cref="GetPointPosition"/>, Godot returns the corner plus
/// <see cref="Offset"/>); only <see cref="Region"/> marks the grid dirty, and <see cref="Update"/> keeps the cells when
/// the region did not change; queries take spans; jump point search lists every cell of the path.
/// </remarks>
public partial class AStarGrid2D
{
    /// <summary>The largest <see cref="Region"/> area (2048 × 2048 cells).</summary>
    public const int MaxCells = 4_194_304;

    private static readonly float OctileF = (float)(Math.Sqrt(2) - 1);

    private readonly GridSearch _search = new();
    private Rect2I _region;
    private Rect2I _built;      // the region the storage was built for
    private Vector2 _cellSize = Vector2.One;
    private GridDiagonalMode _diagonalMode;
    private GridHeuristic _computeHeuristic;
    private GridHeuristic _estimateHeuristic;
    private bool _dirty;
    private ulong[] _solid = [];
    private float[]? _weights;
    private int _width, _height;
    private int _weightedCount;
    private int _solidCount;
    private readonly bool _builtInCosts;
    private int _end;
    private int _expanded;

    public AStarGrid2D()
    {
        _builtInCosts = GetType() == typeof(AStarGrid2D);
    }

    // ── Layout ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cells, in cell coordinates. Changing it marks the grid dirty: call <see cref="Update"/> before using it.</summary>
    public Rect2I Region
    {
        get => _region;
        set
        {
            if (value.Size.X < 0 || value.Size.Y < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The region's size must not be negative.");
            if ((long)value.Size.X * value.Size.Y > MaxCells)
                throw new ArgumentOutOfRangeException(nameof(value), $"The region holds more than {MaxCells} cells.");
            if (value == _region)
                return;
            _region = value;
            _dirty = value != _built;
        }
    }

    /// <summary>A cell's size in world units (pixels for 2D nodes); positive.</summary>
    public Vector2 CellSize
    {
        get => _cellSize;
        set
        {
            if (!(value.X > 0f) || !(value.Y > 0f) || !float.IsFinite(value.X) || !float.IsFinite(value.Y))
                throw new ArgumentOutOfRangeException(nameof(value), "The cell size must be positive and finite.");
            _cellSize = value;
        }
    }

    /// <summary>Where cell (0, 0)'s top-left corner is in world units.</summary>
    public Vector2 Offset { get; set; }

    public GridDiagonalMode DiagonalMode
    {
        get => _diagonalMode;
        set => _diagonalMode = value;
    }

    /// <summary>The step cost between neighbouring cells (times the entered cell's weight scale).</summary>
    public GridHeuristic DefaultComputeHeuristic
    {
        get => _computeHeuristic;
        set => _computeHeuristic = value;
    }

    /// <summary>The estimate from a cell to the target (admissible when it never exceeds the real cost).</summary>
    public GridHeuristic DefaultEstimateHeuristic
    {
        get => _estimateHeuristic;
        set => _estimateHeuristic = value;
    }

    /// <summary>Jump point search: much faster on open grids; like Godot it ignores weight scales.</summary>
    public bool JumpingEnabled { get; set; }

    /// <summary>Stop a search after this many expanded cells (0 = unlimited): the result is then Partial or NotFound.</summary>
    public int MaxSearchNodes { get; set; }

    /// <summary>True after <see cref="Region"/> changed until <see cref="Update"/>; cells cannot be used meanwhile.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Bumped by every change of cells (solidity, weights, <see cref="Update"/>, <see cref="Clear"/>).</summary>
    public uint Version { get; private set; }

    /// <summary>How many cells are solid.</summary>
    public int SolidCount => _solidCount;

    /// <summary>How many cells have a weight scale other than 1.</summary>
    public int WeightedCount => _weightedCount;

    /// <summary>
    /// Builds the storage for <see cref="Region"/> and the search scratch (allocates). Cells are kept when the region
    /// did not change since the last update; a new region starts with every cell walkable at weight 1.
    /// </summary>
    public void Update()
    {
        if (_region != _built || _solid.Length == 0 && _region.Area > 0)
        {
            _width = _region.Size.X;
            _height = _region.Size.Y;
            var cells = _width * _height;
            _solid = new ulong[(cells + 63) >> 6];
            _weights = null;
            _weightedCount = 0;
            _solidCount = 0;
            _search.EnsureCapacity(cells);
            _built = _region;
        }

        _dirty = false;
        Version++;
    }

    /// <summary>Godot's <c>clear</c>: an empty region, every cell gone.</summary>
    public void Clear()
    {
        _region = default;
        _built = default;
        _width = _height = 0;
        _solid = [];
        _weights = null;
        _weightedCount = 0;
        _solidCount = 0;
        _dirty = false;
        Version++;
    }

    // ── Cells ────────────────────────────────────────────────────────────────────────────────────────────────

    public bool IsInBounds(Vector2I id) => IsInBounds(id.X, id.Y);

    public bool IsInBounds(int x, int y) =>
        (uint)(x - _region.Position.X) < (uint)_width && (uint)(y - _region.Position.Y) < (uint)_height && !_dirty;

    public void SetPointSolid(Vector2I id, bool solid = true)
    {
        var index = RequireIndex(id);
        SetSolid(index, solid);
        Version++;
    }

    public bool IsPointSolid(Vector2I id) => IsSolid(RequireIndex(id));

    /// <summary>Sets the multiplier of the cost of entering the cell (≥ 0; 1 = normal). Weights are stored only once one differs from 1.</summary>
    public void SetPointWeightScale(Vector2I id, float weightScale)
    {
        if (!(weightScale >= 0f) || !float.IsFinite(weightScale))
            throw new ArgumentOutOfRangeException(nameof(weightScale), "A weight scale must be finite and not negative.");
        var index = RequireIndex(id);
        SetWeight(index, weightScale);
        Version++;
    }

    public float GetPointWeightScale(Vector2I id)
    {
        var index = RequireIndex(id);
        return _weights is null ? 1f : _weights[index];
    }

    /// <summary>Sets every cell of <paramref name="region"/> (clipped to the grid) solid or walkable.</summary>
    public void FillSolidRegion(Rect2I region, bool solid = true)
    {
        RequireClean();
        var clip = region.Intersection(_region);
        for (var y = clip.Position.Y; y < clip.End.Y; y++)
            for (var x = clip.Position.X; x < clip.End.X; x++)
                SetSolid(Index(x, y), solid);
        Version++;
    }

    /// <summary>Sets the weight scale of every cell of <paramref name="region"/> (clipped to the grid).</summary>
    public void FillWeightScaleRegion(Rect2I region, float weightScale)
    {
        if (!(weightScale >= 0f) || !float.IsFinite(weightScale))
            throw new ArgumentOutOfRangeException(nameof(weightScale), "A weight scale must be finite and not negative.");
        RequireClean();
        var clip = region.Intersection(_region);
        for (var y = clip.Position.Y; y < clip.End.Y; y++)
            for (var x = clip.Position.X; x < clip.End.X; x++)
                SetWeight(Index(x, y), weightScale);
        Version++;
    }

    /// <summary>Makes every cell walkable at weight 1 (keeps the region).</summary>
    public void ClearCells()
    {
        RequireClean();
        Array.Clear(_solid);
        _weights = null;
        _weightedCount = 0;
        _solidCount = 0;
        Version++;
    }

    /// <summary>The centre of cell <paramref name="id"/>: <c>Offset + (id + 0.5) · CellSize</c>.</summary>
    public Vector2 GetPointPosition(Vector2I id) =>
        Offset + new Vector2(id.X + 0.5f, id.Y + 0.5f) * _cellSize;

    /// <summary>The cell containing <paramref name="position"/> (it may be outside the region).</summary>
    public Vector2I GetPointAt(Vector2 position)
    {
        var local = (position - Offset) / _cellSize;
        return new Vector2I((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y));
    }

    // ── Queries ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cells of the cheapest path from <paramref name="from"/> to <paramref name="to"/> (both included), written into
    /// <paramref name="path"/>; <paramref name="count"/> is the number written, or the size needed for
    /// <see cref="GridPathStatus.BufferTooSmall"/>. Allocation-free.
    /// </summary>
    public GridPathStatus GetIdPath(Vector2I from, Vector2I to, Span<Vector2I> path, out int count, bool allowPartialPath = false)
    {
        var status = Solve(from, to, allowPartialPath, out var last);
        count = 0;
        if (status is GridPathStatus.NotFound or GridPathStatus.InvalidPoint)
            return status;
        count = PathLength(last);
        if (count > path.Length)
            return GridPathStatus.BufferTooSmall;
        WritePath(last, path[..count]);
        return status;
    }

    /// <summary>The cell centres of the path (see <see cref="GetIdPath(Vector2I, Vector2I, Span{Vector2I}, out int, bool)"/>).</summary>
    public GridPathStatus GetPointPath(Vector2I from, Vector2I to, Span<Vector2> path, out int count, bool allowPartialPath = false)
    {
        var status = Solve(from, to, allowPartialPath, out var last);
        count = 0;
        if (status is GridPathStatus.NotFound or GridPathStatus.InvalidPoint)
            return status;
        count = PathLength(last);
        if (count > path.Length)
            return GridPathStatus.BufferTooSmall;
        WritePoints(last, path[..count]);
        return status;
    }

    /// <summary>The cells of the path as a new array (empty when there is none). Allocates: for tools and tests.</summary>
    public Vector2I[] GetIdPath(Vector2I from, Vector2I to, bool allowPartialPath = false)
    {
        var status = Solve(from, to, allowPartialPath, out var last);
        if (status is GridPathStatus.NotFound or GridPathStatus.InvalidPoint)
            return [];
        var path = new Vector2I[PathLength(last)];
        WritePath(last, path);
        return path;
    }

    /// <summary>The cell centres of the path as a new array. Allocates: for tools and tests.</summary>
    public Vector2[] GetPointPath(Vector2I from, Vector2I to, bool allowPartialPath = false)
    {
        var ids = GetIdPath(from, to, allowPartialPath);
        var points = new Vector2[ids.Length];
        for (var i = 0; i < ids.Length; i++)
            points[i] = GetPointPosition(ids[i]);
        return points;
    }

    /// <summary>Godot's <c>_compute_cost</c>: the cost of a step from <paramref name="fromId"/> to <paramref name="toId"/> (before the weight scale).</summary>
    protected virtual float ComputeCost(Vector2I fromId, Vector2I toId) =>
        Distance(_computeHeuristic, toId.X - fromId.X, toId.Y - fromId.Y);

    /// <summary>Godot's <c>_estimate_cost</c>: the estimate from <paramref name="fromId"/> to <paramref name="endId"/>.</summary>
    protected virtual float EstimateCost(Vector2I fromId, Vector2I endId) =>
        Distance(_estimateHeuristic, endId.X - fromId.X, endId.Y - fromId.Y);

    /// <summary>A heuristic's distance between two cells <paramref name="dx"/>, <paramref name="dy"/> apart.</summary>
    public static float Distance(GridHeuristic heuristic, int dx, int dy)
    {
        float x = Math.Abs(dx), y = Math.Abs(dy);
        return heuristic switch
        {
            GridHeuristic.Manhattan => x + y,
            GridHeuristic.Octile => x < y ? OctileF * x + y : OctileF * y + x,
            GridHeuristic.Chebyshev => MathF.Max(x, y),
            _ => MathF.Sqrt(x * x + y * y),
        };
    }

    // ── Internals (also used by the navigation map) ──────────────────────────────────────────────────────────

    internal int Width => _width;

    internal int Height => _height;

    internal bool HasWeights => _weights is not null;

    internal int Index(int x, int y) => (y - _region.Position.Y) * _width + (x - _region.Position.X);

    internal Vector2I CellOf(int index) => new(_region.Position.X + index % _width, _region.Position.Y + index / _width);

    internal bool IsSolid(int index) => (_solid[index >> 6] & (1ul << (index & 63))) != 0;

    internal float WeightAt(int index) => _weights is null ? 1f : _weights[index];

    /// <summary>In bounds and not solid.</summary>
    internal bool IsWalkable(int x, int y) => IsInBounds(x, y) && !IsSolid(Index(x, y));

    internal void SetSolid(int index, bool solid)
    {
        ref var word = ref _solid[index >> 6];
        var bit = 1ul << (index & 63);
        var was = (word & bit) != 0;
        if (was == solid)
            return;
        if (solid)
        {
            word |= bit;
            _solidCount++;
        }
        else
        {
            word &= ~bit;
            _solidCount--;
        }
    }

    internal void SetWeight(int index, float weight)
    {
        if (_weights is null)
        {
            if (weight == 1f)
                return;
            _weights = new float[_width * _height];
            Array.Fill(_weights, 1f);
        }

        var was = _weights[index];
        if (was == weight)
            return;
        if (was == 1f)
            _weightedCount++;
        else if (weight == 1f)
            _weightedCount--;
        _weights[index] = weight;
    }

    /// <summary>Marks a batch of internal cell edits (<see cref="SetSolid"/>, <see cref="SetWeight"/>) as one change.</summary>
    internal void BumpVersion() => Version++;

    internal GridSearch Search => _search;

    private void RequireClean()
    {
        if (_dirty)
            throw new InvalidOperationException("The grid's region changed: call Update() first.");
    }

    private int RequireIndex(Vector2I id)
    {
        RequireClean();
        if (!IsInBounds(id))
            throw new ArgumentOutOfRangeException(nameof(id), $"Cell {id} is outside the region {_region}.");
        return Index(id.X, id.Y);
    }

    private float StepCost(int from, int to, int dx, int dy, float straight, float diagonal)
    {
        if (_builtInCosts)
            return dx != 0 && dy != 0 ? diagonal : straight;
        return ComputeCost(CellOf(from), CellOf(to));
    }

    private float Estimate(int node, Vector2I end)
    {
        if (_builtInCosts)
        {
            var cell = CellOf(node);
            return Distance(_estimateHeuristic, end.X - cell.X, end.Y - cell.Y);
        }

        return EstimateCost(CellOf(node), end);
    }

    // Runs the search; last is the path's last cell (the target, or the closest reachable cell for Partial).
    private GridPathStatus Solve(Vector2I from, Vector2I to, bool allowPartial, out int last)
    {
        RequireClean();
        last = -1;
        if (!IsInBounds(from) || !IsInBounds(to))
            return GridPathStatus.InvalidPoint;
        var start = Index(from.X, from.Y);
        if (IsSolid(start))
            return GridPathStatus.InvalidPoint;
        var end = Index(to.X, to.Y);
        if (start == end)
        {
            _search.Begin();
            _search.Open(start, 0f, 0f, -1);
            last = start;
            return GridPathStatus.Found;
        }

        var endSolid = IsSolid(end);
        if (endSolid && !allowPartial)
            return GridPathStatus.NotFound;

        _end = end;
        var found = JumpingEnabled ? SolveJps(start, to, out last) : SolveAStar(start, to, out last);
        if (found)
            return GridPathStatus.Found;
        if (!allowPartial)
            return GridPathStatus.NotFound;
        if (JumpingEnabled)
            SolveAStar(start, to, out last); // JPS skips cells: the closest reachable cell comes from a plain search
        return last >= 0 ? GridPathStatus.Partial : GridPathStatus.NotFound;
    }

    // Neighbour order: right, down, left, up, then the diagonals clockwise from down-right.
    private static readonly sbyte[] DirX = [1, 0, -1, 0, 1, -1, -1, 1];
    private static readonly sbyte[] DirY = [0, 1, 0, -1, 1, 1, -1, -1];

    /// <summary>Whether a step from (x, y) by (dx, dy) is allowed: the cell is walkable and, diagonally, the diagonal rule holds.</summary>
    internal bool CanStep(int x, int y, int dx, int dy)
    {
        if (!IsWalkable(x + dx, y + dy))
            return false;
        if (dx == 0 || dy == 0)
            return true;
        return _diagonalMode switch
        {
            GridDiagonalMode.Always => true,
            GridDiagonalMode.Never => false,
            GridDiagonalMode.AtLeastOneWalkable => IsWalkable(x + dx, y) || IsWalkable(x, y + dy),
            _ => IsWalkable(x + dx, y) && IsWalkable(x, y + dy),
        };
    }

    private bool SolveAStar(int start, Vector2I to, out int closest)
    {
        var search = _search;
        search.Begin();
        search.Open(start, 0f, Estimate(start, to), -1);
        closest = start;
        var straight = Distance(_computeHeuristic, 1, 0);
        var diagonal = Distance(_computeHeuristic, 1, 1);
        var directions = _diagonalMode == GridDiagonalMode.Never ? 4 : 8;
        var limit = MaxSearchNodes;
        _expanded = 0;
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
            var g = search.G(node);
            for (var d = 0; d < directions; d++)
            {
                int dx = DirX[d], dy = DirY[d];
                if (!CanStep(x, y, dx, dy))
                    continue;
                var next = Index(x + dx, y + dy);
                if (search.IsClosed(next))
                    continue;
                var weight = _weights is null ? 1f : _weights[next];
                var tentative = g + StepCost(node, next, dx, dy, straight, diagonal) * weight;
                if (!search.IsSeen(next))
                    search.Open(next, tentative, Estimate(next, to), node);
                else if (tentative < search.G(next))
                    search.Decrease(next, tentative, node);
            }
        }

        return false;
    }

    // Godot's partial-path rule: the visited cell with the lowest estimate, then the lowest cost, then the lowest index.
    private bool IsCloser(int node, int best)
    {
        var h = _search.H(node);
        var bh = _search.H(best);
        if (h != bh)
            return h < bh;
        var g = _search.G(node);
        var bg = _search.G(best);
        if (g != bg)
            return g < bg;
        return node < best;
    }

    private int PathLength(int last)
    {
        var length = 1;
        var node = last;
        var parent = _search.Parent(node);
        while (parent >= 0)
        {
            length += Steps(parent, node);
            node = parent;
            parent = _search.Parent(node);
        }

        return length;
    }

    // Cells between two linked nodes: 1 for A* neighbours, the jump length for jump points (straight or diagonal lines).
    private int Steps(int from, int to)
    {
        var a = CellOf(from);
        var b = CellOf(to);
        return Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private void WritePath(int last, Span<Vector2I> path)
    {
        var index = path.Length - 1;
        var cell = CellOf(last);
        path[index--] = cell;
        for (var parent = _search.Parent(last); parent >= 0; parent = _search.Parent(parent))
        {
            var from = CellOf(parent);
            var dx = Math.Sign(from.X - cell.X);
            var dy = Math.Sign(from.Y - cell.Y);
            while (cell != from)
            {
                cell = new Vector2I(cell.X + dx, cell.Y + dy);
                path[index--] = cell;
            }
        }
    }

    private void WritePoints(int last, Span<Vector2> path)
    {
        var index = path.Length - 1;
        var cell = CellOf(last);
        path[index--] = GetPointPosition(cell);
        for (var parent = _search.Parent(last); parent >= 0; parent = _search.Parent(parent))
        {
            var from = CellOf(parent);
            var dx = Math.Sign(from.X - cell.X);
            var dy = Math.Sign(from.Y - cell.Y);
            while (cell != from)
            {
                cell = new Vector2I(cell.X + dx, cell.Y + dy);
                path[index--] = GetPointPosition(cell);
            }
        }
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The avoidance step of one <see cref="NavigationMap2D"/> (G3.7): gathers the map's avoiding agents and obstacles, finds
/// each agent's neighbours through a uniform hash grid and its obstacle edges through per-polygon bounds, and runs
/// <see cref="Orca2D"/> for every agent that submitted a velocity this tick. Every buffer is owned here and only grows, so
/// a steady crowd allocates nothing. Deterministic: entities are numbered in registration order (agents, then radius
/// obstacles), neighbours sort by distance then number, and the maths is scalar.
/// </summary>
internal sealed class AvoidanceSimulation2D
{
    private const float MinTimeHorizon = 0.01f;

    private struct Entity
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector2 Preferred;
        public float Radius;
        public float MaxSpeed;
        public float NeighborDistance;
        public float TimeHorizon;
        public float TimeHorizonObstacles;
        public float Priority;
        public uint Layers;
        public uint Mask;
        public int MaxNeighbors;
        public bool Avoids;     // an agent that submitted a velocity this tick
        public bool IsAgent;
        public int Cell;        // hash bucket
    }

    private struct Polygon
    {
        public Vector2 Min, Max;
        public int First, Count;
        public uint Layers;
    }

    private Entity[] _entities = new Entity[16];
    private NavigationAgent2D?[] _agentOf = new NavigationAgent2D?[16];
    private int _count;
    private OrcaObstacleVertex[] _vertices = new OrcaObstacleVertex[16];
    private int _vertexCount;
    private Polygon[] _polygons = new Polygon[4];
    private int _polygonCount;

    private int[] _bucketStart = new int[17];
    private int[] _bucketItems = new int[16];
    private int[] _bucketCursor = new int[16];
    private int[] _bucketSeen = new int[16]; // the stamp of the last neighbour search that read the bucket
    private int _bucketMask;
    private float _cellSize = 32f;

    private OrcaNeighbor[] _neighbors = new OrcaNeighbor[16];
    private float[] _neighborDistance = new float[16];
    private int[] _neighborIndex = new int[16];
    private int[] _edges = new int[16];
    private float[] _edgeDistance = new float[16];
    private OrcaLine[] _lines = new OrcaLine[32];
    private OrcaLine[] _projected = new OrcaLine[32];
    private int _stamp;

    /// <summary>Entities in the last step (agents and radius obstacles), for tests.</summary>
    internal int EntityCount => _count;

    /// <summary>Polygon obstacle vertices in the last step, for tests.</summary>
    internal int VertexCount => _vertexCount;

    public void Step(NavigationMap2D map, float delta)
    {
        Gather(map);
        if (!AnyAvoiding())
        {
            ClearSubmissions(map);
            return;
        }

        BuildGrid();
        for (var i = 0; i < _count; i++)
        {
            ref var self = ref _entities[i];
            if (!self.Avoids)
                continue;
            var neighborCount = FindNeighbors(i);
            var edgeCount = FindEdges(in self);
            var lineCapacity = neighborCount + edgeCount;
            Grow(ref _lines, lineCapacity);
            Grow(ref _projected, lineCapacity);
            var result = Orca2D.ComputeNewVelocity(self.Position, self.Velocity, self.Preferred, self.Radius, self.MaxSpeed,
                self.TimeHorizon, self.TimeHorizonObstacles, delta, _vertices.AsSpan(0, _vertexCount), _edges.AsSpan(0, edgeCount),
                _neighbors.AsSpan(0, neighborCount), _lines, _projected);
            if (!float.IsFinite(result.X) || !float.IsFinite(result.Y))
                result = Vector2.Zero;
            _agentOf[i]!.SafeVelocity = result;
        }

        // Every result first, then the signals: a handler that moves its body cannot change another agent's input.
        for (var i = 0; i < _count; i++)
            if (_entities[i].Avoids)
                _agentOf[i]!.DispatchSafeVelocity();
        ClearSubmissions(map);
    }

    private bool AnyAvoiding()
    {
        for (var i = 0; i < _count; i++)
            if (_entities[i].Avoids)
                return true;
        return false;
    }

    private static void ClearSubmissions(NavigationMap2D map)
    {
        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
            agents[i].VelocitySubmitted = false;
    }

    private void Gather(NavigationMap2D map)
    {
        _count = 0;
        var agents = map.AgentList;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (!agent.AvoidanceEnabled || agent.Parent is not Node2D body)
                continue;
            var submitted = agent.VelocitySubmitted;
            ref var e = ref Add(agent);
            e.Position = body.GlobalPosition;
            e.Velocity = submitted ? agent.SafeVelocity : Vector2.Zero;
            e.Preferred = agent.SubmittedVelocity;
            e.Radius = MathF.Max(0f, agent.Radius);
            e.MaxSpeed = MathF.Max(0f, agent.MaxSpeed);
            e.NeighborDistance = MathF.Max(0f, agent.NeighborDistance);
            e.TimeHorizon = MathF.Max(MinTimeHorizon, agent.TimeHorizonAgents);
            e.TimeHorizonObstacles = MathF.Max(MinTimeHorizon, agent.TimeHorizonObstacles);
            e.Priority = agent.AvoidancePriority;
            e.Layers = agent.AvoidanceLayers;
            e.Mask = agent.AvoidanceMask;
            e.MaxNeighbors = Math.Max(0, agent.MaxNeighbors);
            e.Avoids = submitted;
            e.IsAgent = true;
            if (!submitted)
                agent.SafeVelocity = Vector2.Zero; // stopped: neighbours see it standing
        }

        _vertexCount = 0;
        _polygonCount = 0;
        var obstacles = map.ObstacleList;
        for (var i = 0; i < obstacles.Count; i++)
        {
            var obstacle = obstacles[i];
            if (!obstacle.AvoidanceEnabled)
                continue;
            if (obstacle.Radius > 0f)
            {
                ref var e = ref Add(null);
                e.Position = obstacle.GlobalPosition;
                e.Velocity = obstacle.Velocity;
                e.Radius = obstacle.Radius;
                e.Layers = obstacle.AvoidanceLayers;
                e.Priority = float.PositiveInfinity;
            }

            AddPolygon(obstacle);
        }
    }

    private ref Entity Add(NavigationAgent2D? agent)
    {
        if (_count == _entities.Length)
        {
            Array.Resize(ref _entities, _count * 2);
            Array.Resize(ref _agentOf, _count * 2);
        }

        _agentOf[_count] = agent;
        ref var e = ref _entities[_count++];
        e = default;
        return ref e;
    }

    // RVO2's addObstacle: the polygon's vertices counter-clockwise in the maths sense (interior on the left of each edge,
    // so agents outside are pushed out, whatever the authored winding), each with its edge direction and convexity.
    private void AddPolygon(NavigationObstacle2D obstacle)
    {
        var local = obstacle.VerticesSpan;
        var n = local.Length;
        if (n < 2)
            return;
        var transform = obstacle.GlobalTransform;
        var area = 0f;
        for (var i = 0; i < n; i++)
            area += Orca2D.Det(local[i], local[(i + 1) % n]);
        var reverse = area * Determinant(transform) < 0f;

        Grow(ref _vertices, _vertexCount + n);
        if (_polygonCount == _polygons.Length)
            Array.Resize(ref _polygons, _polygonCount * 2);
        var first = _vertexCount;
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < n; i++)
        {
            var p = transform.TransformPoint(local[reverse ? n - 1 - i : i]);
            _vertices[first + i] = new OrcaObstacleVertex { Point = p, Next = first + (i + 1) % n, Previous = first + (i + n - 1) % n };
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        for (var i = 0; i < n; i++)
        {
            ref var v = ref _vertices[first + i];
            v.UnitDir = Orca2D.Normalize(_vertices[v.Next].Point - v.Point);
            v.IsConvex = n == 2 || Orca2D.LeftOf(_vertices[v.Previous].Point, v.Point, _vertices[v.Next].Point) >= 0f;
        }

        _vertexCount += n;
        _polygons[_polygonCount++] = new Polygon { Min = min, Max = max, First = first, Count = n, Layers = obstacle.AvoidanceLayers };
    }

    private static float Determinant(Transform2D t) => t.X.X * t.Y.Y - t.X.Y * t.Y.X;

    // Counting sort of the entities into hash buckets of square cells (4 × the largest radius, at least 8 px).
    private void BuildGrid()
    {
        var maxRadius = 0f;
        for (var i = 0; i < _count; i++)
            maxRadius = MathF.Max(maxRadius, _entities[i].Radius);
        _cellSize = MathF.Max(8f, maxRadius * 4f);

        var buckets = 16;
        while (buckets < _count * 2)
            buckets <<= 1;
        _bucketMask = buckets - 1;
        Grow(ref _bucketStart, buckets + 1);
        Grow(ref _bucketItems, _count);
        Array.Clear(_bucketStart, 0, buckets + 1);
        for (var i = 0; i < _count; i++)
        {
            ref var e = ref _entities[i];
            e.Cell = Bucket(CellOf(e.Position.X), CellOf(e.Position.Y));
            _bucketStart[e.Cell + 1]++;
        }

        for (var b = 0; b < buckets; b++)
            _bucketStart[b + 1] += _bucketStart[b];
        // Fill in number order (stable).
        Grow(ref _bucketCursor, buckets);
        Array.Copy(_bucketStart, _bucketCursor, buckets);
        for (var i = 0; i < _count; i++)
            _bucketItems[_bucketCursor[_entities[i].Cell]++] = i;
        Grow(ref _bucketSeen, buckets); // stamps only increase, so old and new entries are all below the next one
    }

    private int CellOf(float v)
    {
        var c = MathF.Floor(v / _cellSize);
        return c < int.MinValue / 4 ? int.MinValue / 4 : c > int.MaxValue / 4 ? int.MaxValue / 4 : (int)c;
    }

    private int Bucket(int cx, int cy) => (int)(((uint)cx * 92837111u ^ (uint)cy * 689287499u) & (uint)_bucketMask);

    // RVO2's agent neighbours: within NeighborDistance (centre to centre), the MaxNeighbors nearest, nearest first; ties
    // by entity number. Rings of cells outwards until a ring is farther than the (shrinking) range.
    private int FindNeighbors(int self)
    {
        ref readonly var me = ref _entities[self];
        var max = me.MaxNeighbors;
        if (max == 0 || me.NeighborDistance <= 0f)
            return 0;
        Grow(ref _neighbors, max);
        Grow(ref _neighborDistance, max);
        Grow(ref _neighborIndex, max);
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_bucketSeen);
            _stamp = 1;
        }

        var count = 0;
        var rangeSq = me.NeighborDistance * me.NeighborDistance;
        int cx = CellOf(me.Position.X), cy = CellOf(me.Position.Y);
        var maxRing = (int)MathF.Min(1024f, MathF.Ceiling(me.NeighborDistance / _cellSize) + 1f);
        for (var ring = 0; ring <= maxRing; ring++)
        {
            var near = (ring - 1) * _cellSize;
            if (ring > 1 && near * near > rangeSq)
                break;
            for (var y = cy - ring; y <= cy + ring; y++)
            {
                var edgeRow = y == cy - ring || y == cy + ring;
                var stepX = edgeRow || ring == 0 ? 1 : 2 * ring;
                for (var x = cx - ring; x <= cx + ring; x += stepX)
                {
                    var bucket = Bucket(x, y);
                    if (_bucketSeen[bucket] == _stamp)
                        continue; // another cell hashed to this bucket: its entities were read
                    _bucketSeen[bucket] = _stamp;
                    for (var k = _bucketStart[bucket]; k < _bucketStart[bucket + 1]; k++)
                        Consider(self, _bucketItems[k], ref count, max, ref rangeSq);
                }
            }
        }

        // The neighbour structs, nearest first.
        for (var i = 0; i < count; i++)
            _neighbors[i] = NeighborOf(self, _neighborIndex[i]);
        return count;
    }

    private void Consider(int self, int other, ref int count, int max, ref float rangeSq)
    {
        if (other == self)
            return;
        ref readonly var me = ref _entities[self];
        ref readonly var e = ref _entities[other];
        if ((me.Mask & e.Layers) == 0 || e.IsAgent && e.Priority < me.Priority)
            return; // not on the mask, or a lower-priority agent this one does not make way for
        var distSq = Orca2D.AbsSq(e.Position - me.Position);
        int i;
        if (count < max)
        {
            if (distSq >= me.NeighborDistance * me.NeighborDistance)
                return;
            i = count++;
        }
        else
        {
            if (!Before(distSq, other, _neighborDistance[max - 1], _neighborIndex[max - 1]))
                return; // full, and no nearer than the farthest kept
            i = max - 1;
        }

        while (i > 0 && Before(distSq, other, _neighborDistance[i - 1], _neighborIndex[i - 1]))
        {
            _neighborDistance[i] = _neighborDistance[i - 1];
            _neighborIndex[i] = _neighborIndex[i - 1];
            i--;
        }

        _neighborDistance[i] = distSq;
        _neighborIndex[i] = other;
        if (count == max)
            rangeSq = _neighborDistance[max - 1];
    }

    // Nearer, or as near with a lower number.
    private static bool Before(float distSq, int index, float otherDistSq, int otherIndex) =>
        distSq < otherDistSq || distSq == otherDistSq && index < otherIndex;

    private OrcaNeighbor NeighborOf(int self, int other)
    {
        ref readonly var me = ref _entities[self];
        ref readonly var e = ref _entities[other];
        var position = e.Position;
        if (position == me.Position)
            position.X += self < other ? 0.001f : -0.001f; // coincident: apart by number (RVO2 would divide by zero)
        // Reciprocal (half each) only when the other avoids this one back this tick.
        var mutual = e.Avoids && (e.Mask & me.Layers) != 0 && me.Priority >= e.Priority;
        return new OrcaNeighbor { Position = position, Velocity = e.Velocity, Radius = e.Radius, Responsibility = mutual ? 0.5f : 1f };
    }

    // RVO2's obstacle neighbours: edges within TimeHorizonObstacles × MaxSpeed + Radius that the agent sees from outside
    // (it is right of the edge), nearest first; ties by vertex number.
    private int FindEdges(in Entity me)
    {
        if (_polygonCount == 0)
            return 0;
        Grow(ref _edges, _vertexCount);
        Grow(ref _edgeDistance, _vertexCount);
        var range = me.TimeHorizonObstacles * me.MaxSpeed + me.Radius;
        var rangeSq = range * range;
        var count = 0;
        for (var p = 0; p < _polygonCount; p++)
        {
            ref readonly var polygon = ref _polygons[p];
            if ((polygon.Layers & me.Mask) == 0 || me.Position.X < polygon.Min.X - range || me.Position.X > polygon.Max.X + range ||
                me.Position.Y < polygon.Min.Y - range || me.Position.Y > polygon.Max.Y + range)
                continue;
            for (var k = polygon.First; k < polygon.First + polygon.Count; k++)
            {
                ref readonly var v = ref _vertices[k];
                var next = _vertices[v.Next].Point;
                if (Orca2D.LeftOf(v.Point, next, me.Position) >= 0f)
                    continue; // seen from inside (or on the line)
                var distSq = Orca2D.DistSqPointSegment(v.Point, next, me.Position);
                if (distSq >= rangeSq)
                    continue;
                var i = count++;
                while (i > 0 && distSq < _edgeDistance[i - 1])
                {
                    _edgeDistance[i] = _edgeDistance[i - 1];
                    _edges[i] = _edges[i - 1];
                    i--;
                }

                _edgeDistance[i] = distSq;
                _edges[i] = k;
            }
        }

        return count;
    }

    private static void Grow<T>(ref T[] array, int needed)
    {
        if (array.Length < needed)
            Array.Resize(ref array, Math.Max(needed, array.Length * 2));
    }
}

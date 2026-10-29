using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.2: <see cref="AStar2D"/> and <see cref="AStar3D"/>, Godot's point-graph A*.</summary>
public sealed class AStarPointGraphTests
{
    // 0 ─ 1 ─ 2
    // │       │
    // 3 ───── 4      (1 is heavy: weight 10)
    private static AStar2D Square()
    {
        var astar = new AStar2D();
        astar.AddPoint(0, new Vector2(0, 0));
        astar.AddPoint(1, new Vector2(10, 0), 10f);
        astar.AddPoint(2, new Vector2(20, 0));
        astar.AddPoint(3, new Vector2(0, 10));
        astar.AddPoint(4, new Vector2(20, 10));
        astar.ConnectPoints(0, 1);
        astar.ConnectPoints(1, 2);
        astar.ConnectPoints(0, 3);
        astar.ConnectPoints(3, 4);
        astar.ConnectPoints(4, 2);
        return astar;
    }

    [Fact]
    public void TheCheapestPathAvoidsAHeavyPoint()
    {
        var astar = Square();
        Assert.Equal([0L, 3, 4, 2], astar.GetIdPath(0, 2));
        astar.SetPointWeightScale(1, 1f);
        Assert.Equal([0L, 1, 2], astar.GetIdPath(0, 2));
        Assert.Equal([new(0, 0), new(10, 0), new Vector2(20, 0)], astar.GetPointPath(0, 2));
    }

    [Fact]
    public void ConnectionsCanBeOneWayAndDisconnected()
    {
        var astar = new AStar2D();
        for (var i = 0; i < 3; i++)
            astar.AddPoint(i, new Vector2(i * 10, 0));
        astar.ConnectPoints(0, 1, bidirectional: false);
        astar.ConnectPoints(1, 2);
        Assert.True(astar.ArePointsConnected(0, 1, bidirectional: false));
        Assert.False(astar.ArePointsConnected(1, 0, bidirectional: false));
        Assert.True(astar.ArePointsConnected(1, 0));
        Assert.Equal([0L, 1, 2], astar.GetIdPath(0, 2));
        Assert.Empty(astar.GetIdPath(2, 0));

        astar.DisconnectPoints(1, 2, bidirectional: false); // 2 → 1 stays
        Assert.Empty(astar.GetIdPath(0, 2));
        Assert.True(astar.ArePointsConnected(2, 1, bidirectional: false));
        astar.DisconnectPoints(1, 2);
        Assert.False(astar.ArePointsConnected(1, 2));
        Assert.Equal([1L], astar.GetPointConnections(0));
    }

    [Fact]
    public void DisabledPointsAreNotPassedThroughAndRemovedPointsDropTheirLinks()
    {
        var astar = Square();
        astar.SetPointWeightScale(1, 1f);
        astar.SetPointDisabled(1);
        Assert.True(astar.IsPointDisabled(1));
        Assert.Equal([0L, 3, 4, 2], astar.GetIdPath(0, 2));
        astar.SetPointDisabled(1, false);

        astar.RemovePoint(3);
        Assert.False(astar.HasPoint(3));
        Assert.Equal(4, astar.GetPointCount());
        Assert.DoesNotContain(3L, astar.GetPointConnections(0));
        Assert.DoesNotContain(3L, astar.GetPointConnections(4));
        Assert.Equal(3, astar.GetAvailablePointId());
        astar.AddPoint(3, new Vector2(5, 5)); // the slot is reused, with no links
        Assert.Empty(astar.GetPointConnections(3));
        Assert.Throws<ArgumentException>(() => astar.GetPointPosition(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => astar.AddPoint(-1, Vector2.Zero));
    }

    [Fact]
    public void UnreachableTargetsAreNotFoundOrPartialToTheClosestPoint()
    {
        var astar = new AStar2D();
        astar.AddPoint(0, new Vector2(0, 0));
        astar.AddPoint(1, new Vector2(10, 0));
        astar.AddPoint(2, new Vector2(30, 0));
        astar.ConnectPoints(0, 1);
        Span<long> path = stackalloc long[8];
        Assert.Equal(GridPathStatus.NotFound, astar.GetIdPath(0, 2, path, out var count));
        Assert.Equal(0, count);
        Assert.Equal(GridPathStatus.Partial, astar.GetIdPath(0, 2, path, out count, allowPartialPath: true));
        Assert.Equal([0L, 1], path[..count].ToArray());
        Assert.Equal(GridPathStatus.InvalidPoint, astar.GetIdPath(0, 7, path, out _));
        astar.SetPointDisabled(0);
        Assert.Equal(GridPathStatus.InvalidPoint, astar.GetIdPath(0, 1, path, out _));
        Span<long> tiny = stackalloc long[1];
        astar.SetPointDisabled(0, false);
        Assert.Equal(GridPathStatus.BufferTooSmall, astar.GetIdPath(0, 1, tiny, out count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void ClosestPointAndClosestPositionInSegment()
    {
        var astar = Square();
        Assert.Equal(0, astar.GetClosestPoint(new Vector2(1, 1)));
        Assert.Equal(0, astar.GetClosestPoint(new Vector2(5, 0))); // 0 and 1 are equally close: the lower id
        astar.SetPointDisabled(1);
        Assert.Equal(0, astar.GetClosestPoint(new Vector2(9, 0)));
        Assert.Equal(1, astar.GetClosestPoint(new Vector2(9, 0), includeDisabled: true));
        astar.SetPointDisabled(1, false);
        Assert.Equal(new Vector2(12, 0), astar.GetClosestPositionInSegment(new Vector2(12, 3)));
        Assert.Equal(new Vector2(20, 7), astar.GetClosestPositionInSegment(new Vector2(25, 7)));
        Assert.Equal(-1, new AStar2D().GetClosestPoint(Vector2.Zero));
    }

    [Fact]
    public void QueriesIntoSpansAllocateNothing()
    {
        var astar = new AStar2D();
        astar.ReserveSpace(400);
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                astar.AddPoint(y * 20 + x, new Vector2(x, y));
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
            {
                if (x < 19)
                    astar.ConnectPoints(y * 20 + x, y * 20 + x + 1);
                if (y < 19)
                    astar.ConnectPoints(y * 20 + x, (y + 1) * 20 + x);
            }

        var ids = new long[64];
        var points = new Vector2[64];
        astar.GetIdPath(0, 399, ids, out _);
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                astar.GetIdPath(0, 399, ids, out _);
                astar.GetPointPath(19, 380, points, out _);
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AStar3DFindsPathsThroughSpace()
    {
        var astar = new AStar3D();
        astar.AddPoint(1, new Vector3(0, 0, 0));
        astar.AddPoint(2, new Vector3(0, 10, 0));
        astar.AddPoint(3, new Vector3(10, 10, 0));
        astar.AddPoint(4, new Vector3(10, 0, 30)); // a long hop
        astar.ConnectPoints(1, 2);
        astar.ConnectPoints(2, 3);
        astar.ConnectPoints(1, 4);
        astar.ConnectPoints(4, 3);
        Assert.Equal([1L, 2, 3], astar.GetIdPath(1, 3));
        Assert.Equal(new Vector3(0, 10, 0), astar.GetPointPath(1, 3)[1]);
        Assert.Equal(2, astar.GetClosestPoint(new Vector3(1, 9, 0)));
        Assert.Equal(new Vector3(5, 10, 0), astar.GetClosestPositionInSegment(new Vector3(5, 12, 0)));
        astar.SetPointDisabled(2);
        Assert.Equal([1L, 4, 3], astar.GetIdPath(1, 3));
    }

    [Fact]
    public void OverriddenCostsAreUsed()
    {
        var astar = new TollGraph();
        astar.AddPoint(0, Vector2.Zero);
        astar.AddPoint(1, new Vector2(10, 0));
        astar.AddPoint(2, new Vector2(10, 10));
        astar.ConnectPoints(0, 1);
        astar.ConnectPoints(0, 2);
        astar.ConnectPoints(2, 1);
        Assert.Equal([0L, 2, 1], astar.GetIdPath(0, 1)); // the direct road has a toll
    }

    private sealed class TollGraph : AStar2D
    {
        protected override float ComputeCost(long fromId, long toId) => fromId == 0 && toId == 1 ? 100f : base.ComputeCost(fromId, toId);
    }
}

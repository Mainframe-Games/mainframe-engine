using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

public class IdTests
{
    [Fact]
    public void PeerIdEqualityFollowsTheUnderlyingValue()
    {
        PeerId a = 42ul;
        PeerId b = new(42ul);
        PeerId c = 7ul;

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.True(a.Equals(42ul));
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals((object)42ul)); // boxed ulong is not a PeerId
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(42ul, (ulong)a);
        Assert.Equal("(PeerId: 42)", a.ToString());
    }

    [Fact]
    public void NodeIdEqualityFollowsTheUnderlyingValue()
    {
        NodeId a = 5u;
        NodeId b = new(5u);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(5u));
        Assert.Equal(5ul, (ulong)a);
        Assert.Equal("(NodeId: 5)", a.ToString());
    }

    [Fact]
    public void NodeIdGetNextIsUniqueAndIncreasing()
    {
        var first = (ulong)NodeId.GetNext();
        var second = (ulong)NodeId.GetNext();

        Assert.NotEqual(0ul, first); // 0 is reserved as "no node"
        Assert.True(second > first);
    }

    [Fact]
    public void NodeIdGetNextIsUniqueAcrossThreads()
    {
        const int perThread = 10_000;
        var ids = new ulong[8][];
        Parallel.For(0, ids.Length, t =>
        {
            ids[t] = new ulong[perThread];
            for (var i = 0; i < perThread; i++)
                ids[t][i] = NodeId.GetNext();
        });

        var all = ids.SelectMany(x => x).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.DoesNotContain(0ul, all);
    }

    [Fact]
    public void NodesReceiveDistinctIds()
    {
        using var a = new Node();
        using var b = new Node();

        Assert.NotEqual(a.Id, b.Id);
    }
}

using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// The allocation gate for replication: with 100 replicated, interpolated nodes moving every frame over
/// <see cref="LoopbackTransport"/>, steady-state network ticks on the server and on the client allocate nothing
/// (change detection, snapshot encode/decode, acknowledgements, interpolation, RPCs).
/// </summary>
[Collection(nameof(SerialNetBufferPool))] // measures this thread's allocations; keep other buffer tests away
public sealed class ReplicationAllocationTests
{
    [Fact]
    public void SteadyStateNetworkTicksDoNotAllocate()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var boxes = new NetBox[100];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Server.SetAuthority(boxes[0], client.Api.LocalPeerId);
        net.Step(10);
        var mine = client.Find<NetBox>(boxes[0].NetworkId)!;
        Assert.Equal(100, net.Server.Stats.NetworkedNodes);

        var frame = 0;
        void Frame()
        {
            frame++;
            for (var i = 0; i < boxes.Length; i++)
            {
                var box = boxes[i];
                box.NetPosition += new Vector3(0.01f, 0, 0);
                box.NetRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, frame * 0.01f + i);
                if ((frame + i) % 30 == 0)
                    box.Score++;
            }

            // RPCs both ways, every few frames.
            if (frame % 10 == 0)
            {
                mine.RpcNudge(new Vector3(0, 0.01f, 0));
                boxes[1].RpcBump(1);
            }

            net.Step();
        }

        for (var i = 0; i < 120; i++) // warm-up: JIT, pools, list capacities, interpolation buffers
            Frame();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 240; i++)
            Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        // And it really replicated.
        Assert.True(net.Server.Stats.Snapshots > 150);
        Assert.True(client.Api.Stats.RpcsReceived > 20);
        Assert.True(net.Server.Stats.RpcsReceived > 20);
        Assert.Equal(boxes[0].Score, mine.Score);
        Assert.Equal(0, boxes[1].Bumps); // server-only RPCs run on clients
        Assert.True(client.Find<NetBox>(boxes[1].NetworkId)!.Bumps > 20);
        Assert.True(net.Server.Stats.LastSnapshotBytes > 100 * 28, $"snapshot {net.Server.Stats.LastSnapshotBytes} B");
    }
}

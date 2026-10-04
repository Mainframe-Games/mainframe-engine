using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// The unit-level allocation gate for the scene tree: a steady-state tick over 10 000 nodes (physics, process,
/// transform propagation and server sync notifications, groups, input) allocates nothing.
/// </summary>
public sealed class SceneTreeAllocationTests
{
    // One delegate for warm-up and measurement: a lambda call site allocates its cached delegate on first use.
    private static readonly Action<Node, int> NoOp = static (_, _) => { };

    [Fact]
    public void SteadyStateTickOverTenThousandNodesDoesNotAllocate()
    {
        var tree = new SceneTree();
        var scene = new Node3D { Name = "Scene" };
        for (var branch = 0; branch < 100; branch++)
        {
            var parent = new SpinnerNode3D { Name = $"Branch{branch}" };
            parent.AddToGroup("branches");
            scene.AddChild(parent);
            for (var leaf = 0; leaf < 99; leaf++)
            {
                Node child = (leaf % 3) switch
                {
                    0 => new NotifyingNode3D(),
                    1 => new CounterNode(),
                    _ => new Node3D { Position = new Vector3(leaf, 0, 0) },
                };
                parent.AddChild(child);
            }
        }

        tree.ChangeScene(scene);
        Assert.True(tree.NodeCount >= 10_000);

        var time = new GameTime { DeltaTime = 1f / 60f, FrameCount = 1 };
        var input = new InputEventMouseMotion();
        for (var i = 0; i < 30; i++) // warm-up: lists sorted, buffers sized, JIT done
        {
            tree.Tick(time);
            tree.PushInput(input);
            tree.CallGroup("branches", 0, NoOp);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
        {
            tree.Tick(time);
            tree.PushInput(input);
            tree.CallGroup("branches", 0, NoOp);
            _ = tree.GetNodesInGroup("branches").Count;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        tree.Shutdown();
    }

    [Fact]
    public void GetNodeDoesNotAllocate()
    {
        using var root = new PlainNode { Name = "Root" };
        Node current = root;
        for (var depth = 0; depth < 6; depth++)
        {
            for (var sibling = 0; sibling < 20; sibling++)
                current.AddChild(new PlainNode { Name = $"N{depth}_{sibling}" });
            current = current.GetChild(10);
        }

        NodePath path = "N0_10/N1_10/N2_10/N3_10/N4_10/N5_10";
        Assert.NotNull(root.GetNodeOrNull(path));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = root.GetNodeOrNull(path);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

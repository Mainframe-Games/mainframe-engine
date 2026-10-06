using System.Numerics;

namespace MainframeEngine.Tests.Scene;

public sealed class TweenTests
{
    private static (SceneTree Tree, Node2D Node) Setup()
    {
        var tree = new SceneTree();
        var node = new Node2D { Name = "N" };
        tree.Root.AddChild(node);
        return (tree, node);
    }

    private static void Run(SceneTree tree, float seconds, float dt = 1f / 60f)
    {
        for (var t = 0f; t < seconds - 1e-6f; t += dt)
            tree.Tick(new GameTime { DeltaTime = dt });
    }

    [Fact]
    public void SubPropertiesEaseLikeGodot()
    {
        var (tree, node) = Setup();
        node.Modulate = Vector4.One;
        node.CreateTween().TweenProperty(node, "modulate:a", 0f, 1.0); // default Linear (Godot: TRANS_LINEAR, EASE_IN_OUT)
        Run(tree, 0.5f);
        Assert.Equal(0.5f, node.Modulate.W, 3);
        Assert.Equal(1f, node.Modulate.X); // other components untouched

        var x = node.CreateTween();
        x.TweenProperty(node, "position:x", 100f, 1.0).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        Run(tree, 0.5f);
        Assert.Equal(100 * (1 - MathF.Cos(0.5f * MathF.PI / 2)), node.Position.X, 1); // Godot's Sine::in
        Run(tree, 1f);
        Assert.Equal(100f, node.Position.X);
        Assert.Equal(0, tree.TweenCount); // finished tweens leave the tree
    }

    [Fact]
    public void StepsChainCarryLeftoverTimeAndParallelWaitsForTheSlowest()
    {
        var (tree, node) = Setup();
        var log = new List<string>();
        var tween = node.CreateTween();
        tween.TweenInterval(0.25);
        tween.TweenCallback(() => log.Add("a"));
        tween.TweenProperty(node, "rotation", 1f, 0.5);
        tween.Parallel().TweenProperty(node, "scale", new Vector2(3, 3), 1.0);
        tween.TweenCallback(() => log.Add("b"));
        var finished = 0;
        tween.Finished += () => finished++;
        Run(tree, 0.3f, 0.05f); // 50 ms frames: a longer frame would drop physics steps and shorten the process delta (ADR 0114)
        Assert.Equal(["a"], log); // the interval's leftover already ran the next step
        Run(tree, 0.5f, 0.05f);
        Assert.Equal(1f, node.Rotation, 4);
        Assert.Equal(["a"], log); // the parallel scale still runs
        Run(tree, 1f, 0.05f);
        Assert.Equal(["a", "b"], log);
        Assert.Equal(new Vector2(3, 3), node.Scale);
        Assert.Equal(1, finished);
    }

    [Fact]
    public void ABoundTweenPausesWhileOutOfTheTreeAndDiesWithTheNode()
    {
        var (tree, node) = Setup();
        node.CreateTween().TweenProperty(node, "position:x", 10f, 1.0);
        Assert.Equal(1, tree.TweenCount);
        node.QueueFree();
        Run(tree, 0.1f);
        Run(tree, 0.1f);
        Assert.Equal(0, tree.TweenCount);
    }

    [Fact]
    public void LoopsRelativeAndMethodTweens()
    {
        var (tree, node) = Setup();
        var tween = node.CreateTween().SetLoops(3);
        tween.TweenProperty(node, "position:y", 10f, 0.1).AsRelative();
        Run(tree, 0.31f, 0.01f);
        Assert.Equal(30f, node.Position.Y, 2);

        var values = new List<float>();
        tree.CreateTween().TweenMethod(values.Add, 0f, 1f, 0.2).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        Run(tree, 0.3f, 0.05f);
        Assert.Equal(0.4375f, values[0], 3); // Quad::out at t/d = 0.25
        Assert.Equal(1f, values[^1]);
    }

    [Fact]
    public void EveryEquationHitsItsEnds()
    {
        foreach (var trans in Enum.GetValues<Tween.TransitionType>())
            foreach (var ease in Enum.GetValues<Tween.EaseType>())
            {
                Assert.Equal(0f, Tween.InterpolateValue(0, 1, 0, 1, trans, ease), 2);
                Assert.Equal(1f, Tween.InterpolateValue(0, 1, 1, 1, trans, ease), 2);
            }
    }
}

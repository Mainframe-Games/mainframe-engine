using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Keyframe animation (G1a, docs/design/animation.md): one update of a player animating 1 000 nodes, each with position,
/// rotation, a colour and a sub-component value track, playing alone and during a crossfade.
/// </summary>
[MemoryDiagnoser]
public class AnimationBenchmarks
{
    private AnimationPlayer _player = null!;
    private AnimationPlayer _blending = null!;

    public sealed class Animated : Node3D
    {
        [Export] public System.Drawing.Color Tint { get; set; } = System.Drawing.Color.White;

        [Export] public Vector2 Offset { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        _player = Build(blend: false);
        _blending = Build(blend: true);
    }

    private static AnimationPlayer Build(bool blend)
    {
        var tree = new SceneTree(new ServerRegistry());
        var scene = new Node3D { Name = "Scene" };
        tree.ChangeScene(scene);
        var a = new Animation { ResourceName = "a", Length = 2f, LoopMode = AnimationLoopMode.Linear };
        var b = new Animation { ResourceName = "b", Length = 2f, LoopMode = AnimationLoopMode.Linear };
        for (var i = 0; i < 1000; i++)
        {
            var path = $"N{i}";
            scene.AddChild(new Animated { Name = path });
            foreach (var animation in new[] { a, b })
            {
                animation.AddTrack(new Position3DTrack { Path = path, Times = [0f, 1f], Values = [Vector3.Zero, Vector3.One] });
                animation.AddTrack(new Rotation3DTrack { Path = path, Times = [0f, 1f], Values = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f)] });
                animation.AddTrack(new ValueTrack { Path = path, Property = "Tint", Components = 4, Times = [0f, 1f], Values = [1, 0, 0, 1, 0, 1, 0, 1] });
                animation.AddTrack(new ValueTrack { Path = path, Property = "Offset:y", Times = [0f, 1f], Values = [0f, 5f], Interpolation = AnimationInterpolation.Cubic });
            }
        }

        var player = new AnimationPlayer { Name = "Player", CallbackModeProcess = AnimationCallbackMode.Manual };
        var library = new AnimationLibrary();
        library.Animations.Add(a);
        library.Animations.Add(b);
        player.AddAnimationLibrary("", library);
        scene.AddChild(player);
        player.Play("a");
        if (blend)
            player.Play("b", customBlend: 1e6);
        player.Advance(0.1);
        return player;
    }

    [Benchmark]
    public void AnimationEvaluate1k() => _player.Advance(1.0 / 60);

    [Benchmark]
    public void AnimationEvaluate1kCrossfade() => _blending.Advance(1.0 / 60);
}

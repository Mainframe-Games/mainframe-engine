using System.Numerics;

namespace MainframeEngine.Tests.Nodes;

[Collection(nameof(Scene.SerialResources))] // swaps AssetDatabase.Current (process-wide)
public sealed class SpineSpriteTests : IDisposable
{
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public SpineSpriteTests() => AssetDatabase.Current = new AssetDatabase(AppContext.BaseDirectory);

    public void Dispose() => AssetDatabase.Current = _previous;

    private static SpineSkeletonDataResource SpineBoy() => new()
    {
        AtlasRes = "Content/Models/Spine/SpineBoy/spineboy-pro.atlas",
        SkeletonFileRes = "Content/Models/Spine/SpineBoy/spineboy-pro.json",
        DefaultMix = 0.1f,
        AnimationMixes = [new SpineAnimationMix { From = "walk", To = "run", Mix = 0.3f }],
    };

    [Fact]
    public void LoadsAtlasAndSkeletonAndAppliesTheMixes()
    {
        var data = SpineBoy();
        Assert.NotNull(data.SkeletonData.FindAnimation("walk"));
        var walk = data.SkeletonData.FindAnimation("walk");
        var run = data.SkeletonData.FindAnimation("run");
        Assert.Equal(0.3f, data.AnimationStateData.GetMix(walk, run));
        Assert.Equal(0.1f, data.AnimationStateData.GetMix(run, walk)); // default mix
    }

    [Fact]
    public void UpdatesInSpineGodotsOrderAndDrawsTexturedTrianglesYDown()
    {
        var tree = new SceneTree();
        var sprite = new SpineSprite { SkeletonDataRes = SpineBoy() };
        tree.Root.AddChild(sprite);
        var completed = new List<string>();
        sprite.AnimationCompleted += e => completed.Add(e.Animation.Name);
        sprite.AnimationState!.SetAnimation(0, "jump", false);
        for (var i = 0; i < 120; i++)
            sprite.UpdateSkeleton(1f / 60f);
        Assert.Contains("jump", completed);

        sprite.RunDraw();
        var list = sprite.DrawList;
        Assert.False(list.Commands.IsEmpty);
        Assert.All(list.Commands.ToArray(), c => Assert.NotNull(c.Texture));
        // Y-down: the head is above (negative Y) the feet on the canvas.
        var head = sprite.GetGlobalBoneTransform("head").Origin;
        var foot = sprite.GetGlobalBoneTransform("front-foot").Origin;
        Assert.True(head.Y < foot.Y, $"head {head} foot {foot}");
        Assert.Equal(sprite.Skeleton!.FindBone("head").AppliedPose.WorldX, head.X, 3);
        Assert.Equal(-sprite.Skeleton.FindBone("head").AppliedPose.WorldY, head.Y, 3);

        // An unknown bone is the sprite's own transform.
        sprite.Position = new Vector2(10, 20);
        Assert.Equal(new Vector2(10, 20), sprite.GetGlobalBoneTransform("no-such-bone").Origin);
        tree.Shutdown();
    }
}

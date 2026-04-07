using Spine;

namespace MainframeEngine;

public class SpineNode : Node3D
{
    public readonly Skeleton Skeleton;
    private readonly AnimationState _animation;
    private readonly SpineRenderer _spineRenderer;
    
    public float SpineScale { get; set; } = 0.02f;
    public float ZSpacing { get; set; } = 0.01f;

    public Skeleton.Physics UpdateType { get; set; } = Skeleton.Physics.None;

    public SpineNode(in IRenderer renderer, in SpineFolder folder)
    {
        var textureLoader = new SpineTextureLoader();
        var atlas = new Atlas(folder.AtlasPath, textureLoader);

        var json = new SkeletonJson(atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);
        Skeleton = new Skeleton(skeletonData);
        Skeleton.SetSkin(skeletonData.DefaultSkin);
        Skeleton.ScaleX = SpineScale;
        Skeleton.ScaleY = SpineScale;

        _spineRenderer = new SpineRenderer(renderer, Skeleton, atlas.Pages[0].pma, textureLoader);

        var animStateData = new AnimationStateData(skeletonData);
        _animation = new AnimationState(animStateData);
        SetAnimation(Skeleton.Data.Animations.Items[0].Name);
    }
    
    public override void Dispose()
    {
        _spineRenderer.Dispose();
        base.Dispose();
    }

    public void OnUpdate(in GameTime gameTime)
    {
        Skeleton.UpdateWorldTransform(UpdateType);
        _animation.Update(gameTime.DeltaTime);
        _animation.Apply(Skeleton);
    }

    public void OnRender(in ICamera camera)
    {
        _spineRenderer.Draw(
            ZSpacing,
            ModelMatrix,
            camera.ViewMatrix,
            camera.ProjectionMatrix);
    }

    public void SetAnimation(in string animationName)
    {
        var animation = Skeleton.Data.FindAnimation(animationName);
        _animation.AddAnimation(0, animation, true, 0);
    }

    public void FlipX(bool isFlipped)
    {
        var scaleXAbs = Math.Abs(SpineScale);
        Skeleton.ScaleX = isFlipped ? -scaleXAbs : scaleXAbs;
        Skeleton.ScaleY = SpineScale;
    }
}

public readonly struct SpineFolder(in string rootFolder)
{
    public readonly string Name = rootFolder.Split('/')[^1];
    public readonly string AtlasPath = Directory.GetFiles(rootFolder, "*.atlas")[0];
    public readonly string JsonPath = Directory.GetFiles(rootFolder, "*.json")[0];
}
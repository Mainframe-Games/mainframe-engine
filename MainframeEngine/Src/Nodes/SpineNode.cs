using System.Numerics;
using Silk.NET.Vulkan;
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

    public ExposedList<Animation> AllAnimations => Skeleton.Data.Animations;

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

        _spineRenderer = new SpineRenderer(renderer, Skeleton, atlas.Pages[0].pma, textureLoader, ShadowSystem);

        var animStateData = new AnimationStateData(skeletonData);
        _animation = new AnimationState(animStateData);
        SetAnimation(Skeleton.Data.Animations.Items[0].Name);
    }

    public override void Dispose()
    {
        _spineRenderer.Dispose();
        base.Dispose();
    }

    public override void OnUpdate(in GameTime gameTime)
    {
        base.OnUpdate(gameTime);
        Skeleton.UpdateWorldTransform(UpdateType);
        _animation.Update(gameTime.DeltaTime);
        _animation.Apply(Skeleton);
        // Build CPU vertex arrays here so they are ready for both the shadow pass
        // and the main render pass within the same frame.
        _spineRenderer.BuildVertices(ZSpacing, ModelMatrix);
    }

    public override void Draw(in ICamera camera, in LightEnvironment lights)
    {
        base.Draw(camera, lights);
        _spineRenderer.Draw(
            camera.ViewMatrix,
            camera.ProjectionMatrix,
            lights,
            camera.Position);
    }

    /// <summary>
    /// Call inside the <c>draw2D</c> callback of <see cref="ShadowSystem.RenderShadows"/>
    /// to cast directional and spot light shadows.
    /// </summary>
    public override void DrawShadow2D(in CommandBuffer cb)
    {
        base.DrawShadow2D(cb);
        _spineRenderer.DrawShadow2D(cb);
    }

    /// <summary>
    /// Call inside the <c>drawPoint</c> callback of <see cref="ShadowSystem.RenderShadows"/>
    /// to cast point light shadows.
    /// </summary>
    public override void DrawShadowPoint(in CommandBuffer cb, in Vector3 lightPos, in float lightRange)
    {
        base.DrawShadowPoint(cb, lightPos, lightRange);
        _spineRenderer.DrawShadowPoint(cb, lightPos, lightRange);
    }

    public void SetAnimation(in string animationName)
    {
        var animation = Skeleton.Data.FindAnimation(animationName);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (animation is null)
        {
            Log.Error($"Animation {animationName} not found. options: {string.Join(", ", AllAnimations.Select(a => a.Name))}");
            return;
        }
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

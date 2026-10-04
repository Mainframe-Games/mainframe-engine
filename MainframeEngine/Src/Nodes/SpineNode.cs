using System.Numerics;
using Silk.NET.Vulkan;
using Spine;

namespace MainframeEngine;

public class SpineNode : Node3D
{
    /// <summary>Skeleton-space to world scale used when none is set (SpineBoy is ~500 units tall).</summary>
    public const float DefaultSpineScale = 0.02f;

    public Skeleton Skeleton { get; }
    private readonly AnimationState _animation;
    private readonly SpineRenderer _spineRenderer;
    private readonly SpineTextureLoader _textureLoader;
    private float _spineScale = DefaultSpineScale;
    private bool _flipX;

    /// <summary>Uniform skeleton scale (skeleton units → node units). Applied to the skeleton immediately; keeps <see cref="FlipX"/>.</summary>
    public float SpineScale
    {
        get => _spineScale;
        set
        {
            _spineScale = value;
            ApplySkeletonScale();
        }
    }

    public float ZSpacing { get; set; } = 0.01f;

    public Skeleton.Physics UpdateType { get; set; } = Skeleton.Physics.None;

    public ExposedList<Animation> AllAnimations => Skeleton.Data.Animations;

    /// <summary>The animation playing on track 0, or null.</summary>
    public Animation? CurrentAnimation => _animation.GetCurrent(0)?.Animation;

    internal SpineRenderer SpineRenderer => _spineRenderer;
    internal SpineTextureLoader TextureLoader => _textureLoader;

    public SpineNode(in IRenderer renderer, in SpineFolder folder)
    {
        _textureLoader = new SpineTextureLoader();
        var atlas = new Atlas(folder.AtlasPath, _textureLoader);

        var json = new SkeletonJson(atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);
        Skeleton = new Skeleton(skeletonData);
        Skeleton.SetSkin(skeletonData.DefaultSkin);
        ApplySkeletonScale();

        _spineRenderer = new SpineRenderer(renderer, Skeleton, atlas.Pages[0].pma, _textureLoader, ShadowSystem);

        var animStateData = new AnimationStateData(skeletonData);
        _animation = new AnimationState(animStateData);
        if (Skeleton.Data.Animations.Count > 0)
            SetAnimation(Skeleton.Data.Animations.Items[0].Name);
    }

    public override void Dispose()
    {
        _spineRenderer.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Advances the animation and builds this frame's vertices. Spine's order: advance the animation
    /// state, apply it to the skeleton (local bone transforms), advance skeleton time (physics), then
    /// compute world transforms — so the drawn pose is this frame's, not last frame's.
    /// </summary>
    public override void OnUpdate(in GameTime gameTime)
    {
        base.OnUpdate(gameTime);
        _animation.Update(gameTime.DeltaTime);
        _animation.Apply(Skeleton);
        Skeleton.Update(gameTime.DeltaTime);
        Skeleton.UpdateWorldTransform(UpdateType);
        // Build CPU vertex arrays here so they are ready for both the shadow pass
        // and the main render pass within the same frame.
        _spineRenderer.BuildVertices(ZSpacing, ModelMatrix);
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        base.Draw(camera, lightEnvironment);
        _spineRenderer.Draw(
            camera.ViewMatrix,
            camera.ProjectionMatrix,
            lightEnvironment,
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

    /// <summary>Replaces whatever plays on track 0 with <paramref name="animationName"/> (looping), immediately.</summary>
    public void SetAnimation(in string animationName)
    {
        var animation = Skeleton.Data.FindAnimation(animationName);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (animation is null)
        {
            Log.Error($"Animation {animationName} not found. options: {string.Join(", ", AllAnimations.Select(a => a.Name))}");
            return;
        }
        _animation.SetAnimation(0, animation, true);
    }

    /// <summary>Queues <paramref name="animationName"/> on track 0 after the current one (looping).</summary>
    public void QueueAnimation(in string animationName, float delay = 0f)
    {
        var animation = Skeleton.Data.FindAnimation(animationName);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (animation is null)
        {
            Log.Error($"Animation {animationName} not found. options: {string.Join(", ", AllAnimations.Select(a => a.Name))}");
            return;
        }
        _animation.AddAnimation(0, animation, true, delay);
    }

    public void FlipX(bool isFlipped)
    {
        _flipX = isFlipped;
        ApplySkeletonScale();
    }

    private void ApplySkeletonScale()
    {
        var magnitude = Math.Abs(_spineScale);
        Skeleton.ScaleX = _flipX ? -magnitude : magnitude;
        Skeleton.ScaleY = _spineScale;
    }
}

public readonly struct SpineFolder(in string rootFolder)
{
    public readonly string Name = rootFolder.Split('/')[^1];
    public readonly string AtlasPath = Directory.GetFiles(rootFolder, "*.atlas")[0];
    public readonly string JsonPath = Directory.GetFiles(rootFolder, "*.json")[0];
}

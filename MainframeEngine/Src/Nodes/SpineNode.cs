using System.Numerics;
using Silk.NET.Vulkan;
using Spine;

namespace MainframeEngine;

/// <summary>
/// A Spine skeleton drawn lit and shadow-casting in 3D (the design's <c>SpineSprite3D</c>). Set
/// <see cref="Folder"/> (an exported Spine folder with one <c>.atlas</c> and one <c>.json</c>); the skeleton
/// data is loaded on the CPU on first use and the GPU renderer is created through the
/// <see cref="RenderServer"/> when the node enters a tree. Animation advances in <see cref="Node.OnProcess"/>.
/// </summary>
public class SpineNode : VisualInstance3D
{
    /// <summary>Skeleton-space to world scale used when none is set (SpineBoy is ~500 units tall).</summary>
    public const float DefaultSpineScale = 0.02f;

    private Skeleton? _skeleton;
    private AnimationState? _animation;
    private SpineRenderer? _spineRenderer;
    private SpineTextureLoader? _textureLoader;
    private bool _pma;
    private string _folder = string.Empty;
    private float _spineScale = DefaultSpineScale;
    private bool _flipX;

    /// <summary>A node to configure (set <see cref="Folder"/>) and add to a tree.</summary>
    public SpineNode()
    {
    }

    /// <summary>
    /// Tree-less use (examples, tests): loads <paramref name="folder"/> and creates the renderer immediately,
    /// without shadows (lit pipelines bind the fallback shadow set). Drive it with <see cref="Advance"/> and
    /// <see cref="Draw"/>.
    /// </summary>
    public SpineNode(in IRenderer renderer, in SpineFolder folder)
    {
        _folder = folder.Path;
        EnsureLoaded();
        _spineRenderer = new SpineRenderer(renderer, _skeleton!, _pma, _textureLoader!);
    }

    /// <summary>The skeleton (loads <see cref="Folder"/> on first access).</summary>
    public Skeleton Skeleton => EnsureLoaded();

    /// <summary>Folder holding the Spine export (<c>*.atlas</c> + <c>*.json</c>), e.g. <c>Content/Models/Spine/SpineBoy</c>.</summary>
    [Export(Directory = true)]
    public string Folder
    {
        get => _folder;
        set
        {
            if (string.Equals(_folder, value, StringComparison.Ordinal))
                return;
            if (_skeleton is not null)
                throw new InvalidOperationException("SpineNode.Folder cannot change once the skeleton is loaded.");
            _folder = value ?? string.Empty;
        }
    }

    /// <summary>Animation played (looping) when the skeleton loads; empty plays the skeleton's first animation.</summary>
    [Export]
    public string Animation { get; set; } = string.Empty;

    /// <summary>Uniform skeleton scale (skeleton units → node units). Applied to the skeleton immediately; keeps <see cref="FlipX"/>.</summary>
    [Export]
    public float SpineScale
    {
        get => _spineScale;
        set
        {
            _spineScale = value;
            ApplySkeletonScale();
        }
    }

    /// <summary>Depth offset between successive slots, to avoid z-fighting.</summary>
    [Export]
    public float ZSpacing { get; set; } = 0.01f;

    [Export]
    public Skeleton.Physics UpdateType { get; set; } = Skeleton.Physics.None;

    public ExposedList<Animation> AllAnimations => Skeleton.Data.Animations;

    /// <summary>The animation playing on track 0, or null.</summary>
    public Animation? CurrentAnimation => _animation?.GetCurrent(0)?.Animation;

    internal SpineRenderer? SpineRenderer => _spineRenderer;
    internal SpineTextureLoader? TextureLoader => _textureLoader;

    private Skeleton EnsureLoaded()
    {
        if (_skeleton is not null)
            return _skeleton;
        if (string.IsNullOrEmpty(_folder))
            throw new InvalidOperationException($"SpineNode '{Name}' has no Folder.");

        var folder = new SpineFolder(_folder);
        _textureLoader = new SpineTextureLoader();
        var atlas = new Atlas(folder.AtlasPath, _textureLoader);
        _pma = atlas.Pages[0].pma;

        var json = new SkeletonJson(atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);
        _skeleton = new Skeleton(skeletonData);
        _skeleton.SetSkin(skeletonData.DefaultSkin);
        ApplySkeletonScale();

        _animation = new AnimationState(new AnimationStateData(skeletonData));
        if (!string.IsNullOrEmpty(Animation))
            SetAnimation(Animation);
        else if (skeletonData.Animations.Count > 0)
            SetAnimation(skeletonData.Animations.Items[0].Name);
        return _skeleton;
    }

    protected override void InitializeRenderResources(RenderServer server)
    {
        base.InitializeRenderResources(server);
        EnsureLoaded();
        // A node built with the tree-less constructor already owns a renderer (its atlas pixels are uploaded and
        // released); keep it rather than building a second one from the released pixels.
        _spineRenderer ??= new SpineRenderer(server.Renderer, _skeleton!, _pma, _textureLoader!, server.Shadows);
    }

    protected override void ReleaseRenderResources()
    {
        _spineRenderer?.Dispose();
        _spineRenderer = null;
        base.ReleaseRenderResources();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // Tree-less nodes own a renderer the server does not know about.
            _spineRenderer?.Dispose();
            _spineRenderer = null;
        }
    }

    /// <summary>Advances the animation; tree-less hosts call this instead of the tree's process step.</summary>
    public void Advance(in GameTime gameTime) => OnProcess(gameTime);

    /// <summary>
    /// Advances the animation and builds this frame's vertices. Spine's order: advance the animation
    /// state, apply it to the skeleton (local bone transforms), advance skeleton time (physics), then
    /// compute world transforms — so the drawn pose is this frame's, not last frame's.
    /// </summary>
    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        var skeleton = EnsureLoaded();
        _animation!.Update(gameTime.DeltaTime);
        _animation.Apply(skeleton);
        skeleton.Update(gameTime.DeltaTime);
        skeleton.UpdateWorldTransform(UpdateType);
        // Build CPU vertex arrays here so they are ready for both the shadow pass
        // and the main render pass within the same frame.
        _spineRenderer?.BuildVertices(ZSpacing, ModelMatrix);
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        base.Draw(camera, lightEnvironment);
        _spineRenderer?.Draw(
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
        _spineRenderer?.DrawShadow2D(cb);
    }

    /// <summary>
    /// Call inside the <c>drawPoint</c> callback of <see cref="ShadowSystem.RenderShadows"/>
    /// to cast point light shadows.
    /// </summary>
    public override void DrawShadowPoint(in CommandBuffer cb, in Vector3 lightPos, in float lightRange)
    {
        base.DrawShadowPoint(cb, lightPos, lightRange);
        _spineRenderer?.DrawShadowPoint(cb, lightPos, lightRange);
    }

    /// <summary>
    /// Replaces whatever plays on track 0 with <paramref name="animationName"/> (looping), immediately. Before
    /// the skeleton loads this sets <see cref="Animation"/>.
    /// </summary>
    public void SetAnimation(in string animationName)
    {
        if (_skeleton is null)
        {
            Animation = animationName;
            return;
        }

        var animation = _skeleton.Data.FindAnimation(animationName);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (animation is null)
        {
            Log.Error($"Animation {animationName} not found. options: {string.Join(", ", AllAnimations.Select(a => a.Name))}");
            return;
        }
        _animation!.SetAnimation(0, animation, true);
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
        _animation!.AddAnimation(0, animation, true, delay);
    }

    public void FlipX(bool isFlipped)
    {
        _flipX = isFlipped;
        ApplySkeletonScale();
    }

    private void ApplySkeletonScale()
    {
        if (_skeleton is null)
            return;
        var magnitude = Math.Abs(_spineScale);
        _skeleton.ScaleX = _flipX ? -magnitude : magnitude;
        _skeleton.ScaleY = _spineScale;
    }
}

public readonly struct SpineFolder(in string rootFolder)
{
    public readonly string Path = rootFolder;
    public readonly string Name = rootFolder.Split('/')[^1];
    public readonly string AtlasPath = Directory.GetFiles(rootFolder, "*.atlas")[0];
    public readonly string JsonPath = Directory.GetFiles(rootFolder, "*.json")[0];
}

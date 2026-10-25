using System.Numerics;
using Silk.NET.Vulkan;
using Spine;

namespace MainframeEngine;

/// <summary>
/// A Spine skeleton drawn lit and shadow-casting in 3D (the design's <c>SpineSprite3D</c>). Set
/// <see cref="Folder"/> (an exported Spine folder with one <c>.atlas</c> and a binary <c>.skel</c> or a <c>.json</c>; see
/// <see cref="SpineFolder"/>); the skeleton
/// data is loaded on the CPU on first use and the GPU renderer is created through the
/// <see cref="RenderServer"/> when the node enters a tree. Animation advances in <see cref="Node.OnProcess"/>.
/// Shadows are alpha-tested against the atlas (<see cref="ShadowAlphaThreshold"/>) and <see cref="Billboard"/> turns the
/// skeleton toward each view's camera, shadows included (ADR 0204).
/// </summary>
[EditorIcon("bone")]
public class SpineNode : VisualInstance3D
{
    /// <summary>Skeleton-space to world scale used when none is set (SpineBoy is ~500 units tall).</summary>
    public const float DefaultSpineScale = 0.02f;

    /// <summary>The default <see cref="ShadowAlphaThreshold"/>: shadows keep the atlas pixels at least half opaque.</summary>
    public const float DefaultShadowAlphaThreshold = 0.5f;

    private Skeleton? _skeleton;
    private AnimationState? _animation;
    private SpineRenderer? _spineRenderer;
    private SpineTextureLoader? _textureLoader;
    private bool _pma;
    private string _folder = string.Empty;
    private float _spineScale = DefaultSpineScale;
    private bool _flipX;
    private Matrix4x4 _buildModel = Matrix4x4.Identity; // SpineScale × ModelMatrix of the last build, before billboarding
    private Matrix4x4 _shadowModel = Matrix4x4.Identity; // the pose the shadow passes cast from (PrepareShadowCasting)

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

    /// <summary>
    /// Folder holding the Spine export (<c>*.atlas</c> + <c>*.skel</c> or <c>*.json</c>, the <c>.skel</c> preferred), e.g.
    /// <c>Assets/Models/Spine/SpineBoy</c>.
    /// </summary>
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

    /// <summary>
    /// Uniform skeleton scale (skeleton units → node units); keeps <see cref="FlipX"/>. Applied to the drawn vertices,
    /// not to the skeleton: Spine solves IK with absolute epsilons, so a skeleton scaled to tiny world units (below
    /// ~0.01) resolves wrong poses. The skeleton only carries the flip signs.
    /// </summary>
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
    public Spine.Physics UpdateType { get; set; } = Spine.Physics.None;

    /// <summary>
    /// Shadows are cast where the atlas alpha × the slot's tint alpha reaches this value (an alpha test, as cutout
    /// materials do), so attachments cast their silhouettes and fading slots fade their shadows; 0 casts every
    /// attachment's whole triangles (the quads of region attachments). Casting stays double-sided.
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float ShadowAlphaThreshold { get; set; } = DefaultShadowAlphaThreshold;

    /// <summary>
    /// Faces the camera as <see cref="Sprite3D.Billboard"/> does (ADR 0199, on the CPU): <see cref="BillboardMode.FixedY"/>
    /// turns the skeleton about world Y and keeps it upright, <see cref="BillboardMode.Enabled"/> also tilts it to the
    /// view. The node's position and scale are kept and its rotation is ignored; <see cref="FlipX"/> still mirrors it.
    /// Shadows cast from the pose the main view sees.
    /// </summary>
    [Export]
    public BillboardMode Billboard { get; set; }

    public ExposedList<Spine.Animation> AllAnimations => Skeleton.Data.Animations;

    /// <summary>The animation playing on track 0, or null.</summary>
    public Spine.Animation? CurrentAnimation => _animation is { Tracks.Count: > 0 } state ? state.Tracks.Items[0]?.Animation : null;

    /// <summary>
    /// The skeleton's animation state (loads <see cref="Folder"/> on first access): tracks, mixes and its own
    /// <c>TimeScale</c>. Its listeners are raised as this node's events, on the main thread during <see cref="Node.OnProcess"/>.
    /// </summary>
    public AnimationState AnimationState
    {
        get
        {
            EnsureLoaded();
            return _animation!;
        }
    }

    [Signal] public event Action<TrackEntry>? AnimationStarted;
    [Signal] public event Action<TrackEntry>? AnimationInterrupted;
    [Signal] public event Action<TrackEntry>? AnimationEnded;

    /// <summary>Raised at the end of each loop of a looping entry, and once at the end of a one-shot.</summary>
    [Signal] public event Action<TrackEntry>? AnimationCompleted;

    [Signal] public event Action<TrackEntry>? AnimationDisposed;

    /// <summary>Raised for each event keyed on a playing clip (e.g. SpineBoy's <c>footstep</c> on <c>walk</c>).</summary>
    [Signal] public event Action<TrackEntry, Spine.Event>? AnimationEvent;

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

        var skeletonData = folder.ReadSkeletonData(atlas);
        _skeleton = new Skeleton(skeletonData);
        _skeleton.SetSkin(skeletonData.DefaultSkin);
        ApplySkeletonScale();

        // Wired once, so the per-frame update allocates nothing for them.
        var state = new AnimationState(new AnimationStateData(skeletonData));
        state.Start += e => AnimationStarted?.Invoke(e);
        state.Interrupt += e => AnimationInterrupted?.Invoke(e);
        state.End += e => AnimationEnded?.Invoke(e);
        state.Complete += e => AnimationCompleted?.Invoke(e);
        state.Dispose += e => AnimationDisposed?.Invoke(e);
        state.Event += (e, ev) => AnimationEvent?.Invoke(e, ev);
        _animation = state;
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
        // SpineScale scales x/y only, so ZSpacing stays in node units.
        var spineScale = Math.Abs(_spineScale);
        _buildModel = Matrix4x4.CreateScale(spineScale, spineScale, 1f) * ModelMatrix;
        _shadowModel = _buildModel;
        _spineRenderer?.BuildVertices(ZSpacing, _buildModel, ShadowAlphaThreshold);
    }

    /// <summary>
    /// The model matrix drawn for a view with <paramref name="view"/> matrix: the built one (<see cref="SpineScale"/> ×
    /// <see cref="Node3D.ModelMatrix"/>) turned by <see cref="Billboard"/>.
    /// </summary>
    internal Matrix4x4 ViewModel(in Matrix4x4 view) => MainframeEngine.Billboard.Face(_buildModel, Billboard, view);

    /// <summary>The pose the next shadow passes cast from (set by <see cref="PrepareShadowCasting"/>).</summary>
    internal Matrix4x4 ShadowModel => _shadowModel;

    internal override void PrepareShadowCasting(ICamera? camera)
    {
        base.PrepareShadowCasting(camera);
        _shadowModel = camera is null ? _buildModel : ViewModel(camera.ViewMatrix);
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        base.Draw(camera, lightEnvironment);
        _spineRenderer?.Draw(
            ViewModel(camera.ViewMatrix),
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
        _spineRenderer?.DrawShadow2D(cb, _shadowModel);
    }

    /// <summary>
    /// Call inside the <c>drawPoint</c> callback of <see cref="ShadowSystem.RenderShadows"/>
    /// to cast point light shadows.
    /// </summary>
    public override void DrawShadowPoint(in CommandBuffer cb, in Vector3 lightPos, in float lightRange)
    {
        base.DrawShadowPoint(cb, lightPos, lightRange);
        _spineRenderer?.DrawShadowPoint(cb, _shadowModel, lightPos, lightRange);
    }

    /// <summary>
    /// Replaces whatever plays on <paramref name="track"/> with <paramref name="animationName"/>, immediately, and returns
    /// its entry (set its <c>TimeScale</c> to fit the clip to a duration, its <c>MixDuration</c>, …); null for an unknown
    /// animation. Before <see cref="Folder"/> is set this only sets <see cref="Animation"/> (played looping on track 0 when
    /// the skeleton loads) and returns null.
    /// </summary>
    public TrackEntry? SetAnimation(in string animationName, bool loop = true, int track = 0)
    {
        if (_skeleton is null && string.IsNullOrEmpty(_folder))
        {
            Animation = animationName;
            return null;
        }

        var animation = FindAnimation(animationName);
        return animation is null ? null : AnimationState.SetAnimation(track, animation, loop);
    }

    /// <summary>
    /// Queues <paramref name="animationName"/> on <paramref name="track"/> after the current entry and returns its entry
    /// (null for an unknown animation). A <paramref name="delay"/> of 0 or less starts it when the previous one completes
    /// (minus the mix), plus the delay; a positive one is seconds after the previous entry starts (Spine's rule).
    /// </summary>
    public TrackEntry? AddAnimation(in string animationName, bool loop, float delay = 0f, int track = 0)
    {
        var animation = FindAnimation(animationName);
        return animation is null ? null : AnimationState.AddAnimation(track, animation, loop, delay);
    }

    /// <summary>Queues <paramref name="animationName"/> on track 0 after the current one (looping).</summary>
    public void QueueAnimation(in string animationName, float delay = 0f) => AddAnimation(animationName, loop: true, delay);

    private Spine.Animation? FindAnimation(string animationName)
    {
        var animation = Skeleton.Data.FindAnimation(animationName);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (animation is null)
            Log.Error($"Animation {animationName} not found. options: {string.Join(", ", AllAnimations.Select(a => a.Name))}");
        return animation;
    }

    /// <summary>Mirrors the skeleton horizontally (its <c>ScaleX</c> sign), billboarded or not.</summary>
    public void FlipX(bool isFlipped)
    {
        _flipX = isFlipped;
        ApplySkeletonScale();
    }

    private void ApplySkeletonScale()
    {
        if (_skeleton is null)
            return;
        _skeleton.ScaleX = _flipX ? -1f : 1f;
        _skeleton.ScaleY = _spineScale < 0f ? -1f : 1f;
    }
}

/// <summary>
/// An exported Spine folder: one <c>.atlas</c> (with its pages) and the skeleton, a binary <c>.skel</c> or a <c>.json</c>.
/// When both are there the <c>.skel</c> wins (smaller and faster to load; Spine's advice for shipping).
/// </summary>
public readonly struct SpineFolder(in string rootFolder)
{
    public readonly string Path = rootFolder;
    public readonly string Name = rootFolder.Split('/')[^1];
    public readonly string AtlasPath = Find(rootFolder, "*.atlas") ?? throw new FileNotFoundException($"Spine folder '{rootFolder}' has no .atlas.");

    /// <summary>The skeleton export: the folder's <c>.skel</c>, else its <c>.json</c>.</summary>
    public readonly string SkeletonPath = Find(rootFolder, "*.skel") ?? Find(rootFolder, "*.json")
        ?? throw new FileNotFoundException($"Spine folder '{rootFolder}' has no .skel or .json skeleton.");

    /// <summary>The folder's <c>.json</c> export, or empty when it only has a <c>.skel</c> (see <see cref="SkeletonPath"/>).</summary>
    public readonly string JsonPath = Find(rootFolder, "*.json") ?? string.Empty;

    /// <summary>True when <see cref="SkeletonPath"/> is a binary <c>.skel</c>.</summary>
    public bool IsBinary => SkeletonPath.EndsWith(".skel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the skeleton data with the reader its format needs.</summary>
    public SkeletonData ReadSkeletonData(Atlas atlas) => IsBinary
        ? new SkeletonBinary(atlas).ReadSkeletonData(SkeletonPath)
        : new SkeletonJson(atlas).ReadSkeletonData(SkeletonPath);

    // The first match by name, so a folder loads the same file on every OS.
    private static string? Find(string folder, string pattern)
    {
        var files = Directory.GetFiles(ContentPaths.Resolve(folder), pattern);
        if (files.Length == 0)
            return null;
        Array.Sort(files, StringComparer.Ordinal);
        return files[0];
    }
}

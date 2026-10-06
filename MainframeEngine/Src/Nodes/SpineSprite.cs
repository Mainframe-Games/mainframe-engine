using System.Numerics;
using Spine;

namespace MainframeEngine;

/// <summary>An animation pair's crossfade time (spine-godot's <c>SpineAnimationMix</c>).</summary>
[EditorIcon("arrows-right-left")]
public sealed class SpineAnimationMix : Resource
{
    [Export]
    public string From { get; set; } = "";

    [Export]
    public string To { get; set; } = "";

    /// <summary>Seconds.</summary>
    [Export]
    public float Mix { get; set; }
}

/// <summary>
/// A Spine skeleton and its atlas (spine-godot's <c>SpineSkeletonDataResource</c>): the <c>.atlas</c> (its pages load
/// through <see cref="ResourceLoader"/>, so their <c>.meta</c> import settings apply), a binary <c>.skel</c> or a
/// <c>.json</c> export (Spine 4.3), the default crossfade and per-pair mixes. Loaded on first use and shared by every
/// <see cref="SpineSprite"/> using it.
/// </summary>
[EditorIcon("bone")]
public sealed class SpineSkeletonDataResource : Resource
{
    private SkeletonData? _data;
    private AnimationStateData? _stateData;
    private Atlas? _atlas;

    /// <summary>The <c>.atlas</c> file (project path).</summary>
    [Export(File = "*.atlas")]
    public string AtlasRes { get; set; } = "";

    /// <summary>The skeleton export, <c>.skel</c> or <c>.json</c> (project path).</summary>
    [Export(File = "*.skel,*.json")]
    public string SkeletonFileRes { get; set; } = "";

    /// <summary>Crossfade between any two animations without their own mix, seconds.</summary>
    [Export]
    public float DefaultMix { get; set; }

    [Export]
    public List<SpineAnimationMix> AnimationMixes { get; set; } = [];

    /// <summary>The skeleton data (loads atlas and skeleton on first use; throws when they are missing or invalid).</summary>
    public SkeletonData SkeletonData
    {
        get
        {
            if (_data is not null)
                return _data;
            var assets = AssetDatabase.Current;
            _atlas = new Atlas(assets.ToAbsolutePath(AtlasRes), new CanvasTextureLoader());
            var skeletonPath = assets.ToAbsolutePath(SkeletonFileRes);
            _data = skeletonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? new SkeletonJson(_atlas).ReadSkeletonData(skeletonPath)
                : new SkeletonBinary(_atlas).ReadSkeletonData(skeletonPath);
            return _data;
        }
    }

    /// <summary>The mixes for a new <see cref="AnimationState"/> (shared).</summary>
    public AnimationStateData AnimationStateData
    {
        get
        {
            if (_stateData is not null)
                return _stateData;
            var state = new AnimationStateData(SkeletonData) { DefaultMix = DefaultMix };
            foreach (var mix in AnimationMixes)
                if (SkeletonData.FindAnimation(mix.From) is { } from && SkeletonData.FindAnimation(mix.To) is { } to)
                    state.SetMix(from, to, mix.Mix);
                else
                    Log.Warning($"[Spine] '{ResourcePath ?? ResourceName}': mix {mix.From} → {mix.To} names an unknown animation.");
            return _stateData = state;
        }
    }

    // Atlas pages → engine textures, so the canvas samples them like any Sprite2D texture (straight, as spine-godot draws
    // a 2D sprite: a premultiplied page is blended as it is).
    private sealed class CanvasTextureLoader : TextureLoader
    {
        public void Load(AtlasPage page, string path)
        {
            // Through the loader (its .meta import settings) when the page is in the project, else straight from the file.
            var project = AssetDatabase.Current.ToProjectPath(path);
            var texture = ResourceLoader.Exists(project) ? ResourceLoader.Load<Texture2D>(project) : Texture2D.FromFile(path);
            page.rendererObject = texture;
            page.width = texture.Width;
            page.height = texture.Height;
        }

        public void Unload(object texture)
        {
        }
    }
}

/// <summary>When a <see cref="SpineSprite"/> advances (spine-godot's <c>update_mode</c>).</summary>
public enum SpineSpriteUpdateMode : byte
{
    Process,
    Physics,

    /// <summary>Only through <see cref="SpineSprite.UpdateSkeleton"/>.</summary>
    Manual,
}

/// <summary>
/// A Spine skeleton drawn on the canvas (spine-godot's 2D <c>SpineSprite</c>, Spine 4.3). Each update runs spine-godot's
/// order — animation state update, apply, skeleton update and world transforms with physics — then the attachments are
/// drawn as textured triangles in draw order (skeleton × slot × attachment tint, clipping attachments applied). The
/// skeleton is computed Y-up and mirrored into the canvas's Y-down space, which is what spine-godot's global
/// <c>Bone::setYDown(true)</c> does without touching the 3D <see cref="SpineNode"/>. Every slot draws with the sprite's
/// own material; non-normal slot blend modes are not supported yet (logged once).
/// </summary>
[EditorIcon("bone", Family = EditorIconFamily.Space2D)]
public class SpineSprite : Node2D, ISpineGeometrySink
{
    private readonly SpineGeometry _geometry = new();
    private SpineSkeletonDataResource? _dataRes;
    private Vector2[] _points = new Vector2[256];
    private Vector2[] _uvs = new Vector2[256];
    private Vector4[] _colors = new Vector4[256];
    private bool _warnedBlend;

    [Export]
    public SpineSkeletonDataResource? SkeletonDataRes
    {
        get => _dataRes;
        set
        {
            if (ReferenceEquals(_dataRes, value))
                return;
            _dataRes = value;
            Skeleton = null;
            AnimationState = null;
        }
    }

    [Export]
    public SpineSpriteUpdateMode UpdateMode { get; set; }

    /// <summary>Multiplies the delta of every update (spine-godot's <c>time_scale</c>).</summary>
    [Export]
    public float TimeScale { get; set; } = 1f;

    /// <summary>The skeleton (null until <see cref="SkeletonDataRes"/> is set and loaded).</summary>
    public Skeleton? Skeleton { get; private set; }

    public AnimationState? AnimationState { get; private set; }

    [Signal] public event Action<TrackEntry>? AnimationStarted;
    [Signal] public event Action<TrackEntry>? AnimationInterrupted;
    [Signal] public event Action<TrackEntry>? AnimationEnded;
    [Signal] public event Action<TrackEntry>? AnimationCompleted;
    [Signal] public event Action<TrackEntry>? AnimationDisposed;
    [Signal] public event Action<TrackEntry, Event>? AnimationEvent;

    /// <summary>Raised after the world transforms change each update (spine-godot's <c>world_transforms_changed</c>).</summary>
    [Signal] public event Action? WorldTransformsChanged;

    /// <summary>Creates the skeleton and animation state now (otherwise on entering the tree or the first update).</summary>
    public bool EnsureSkeleton()
    {
        if (Skeleton is not null)
            return true;
        if (_dataRes is null)
            return false;
        var data = _dataRes.SkeletonData;
        Skeleton = new Skeleton(data);
        Skeleton.SetupPose();
        Skeleton.UpdateWorldTransform(Spine.Physics.None);
        var state = new AnimationState(_dataRes.AnimationStateData);
        state.Start += e => AnimationStarted?.Invoke(e);
        state.Interrupt += e => AnimationInterrupted?.Invoke(e);
        state.End += e => AnimationEnded?.Invoke(e);
        state.Complete += e => AnimationCompleted?.Invoke(e);
        state.Dispose += e => AnimationDisposed?.Invoke(e);
        state.Event += (e, ev) => AnimationEvent?.Invoke(e, ev);
        AnimationState = state;
        if (!_warnedBlend)
            foreach (var slot in data.Slots)
                if (slot.BlendMode != Spine.BlendMode.Normal)
                {
                    Log.Warning($"[Spine] '{Name}': slot '{slot.Name}' uses blend mode {slot.BlendMode}; SpineSprite draws every slot with the normal (mix) blend.");
                    _warnedBlend = true;
                    break;
                }

        QueueRedraw();
        return true;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        EnsureSkeleton();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        if (UpdateMode == SpineSpriteUpdateMode.Process)
            UpdateSkeleton(gameTime.DeltaTime);
    }

    protected override void OnPhysicsProcess(float delta)
    {
        base.OnPhysicsProcess(delta);
        if (UpdateMode == SpineSpriteUpdateMode.Physics)
            UpdateSkeleton(delta);
    }

    /// <summary>Advances the animation and pose by <paramref name="delta"/> seconds (× <see cref="TimeScale"/>) and redraws.</summary>
    public void UpdateSkeleton(float delta)
    {
        if (!EnsureSkeleton())
            return;
        var dt = delta * TimeScale;
        AnimationState!.Update(dt);
        AnimationState.Apply(Skeleton!);
        Skeleton!.Update(dt);
        Skeleton.UpdateWorldTransform(Spine.Physics.Update);
        WorldTransformsChanged?.Invoke();
        QueueRedraw();
    }

    /// <summary>
    /// A bone's transform on the canvas (spine-godot's <c>get_global_bone_transform</c>): its world axes and position in the
    /// sprite's Y-down space, times the sprite's global transform; the sprite's own transform for an unknown bone.
    /// </summary>
    public Transform2D GetGlobalBoneTransform(string boneName)
    {
        if (!EnsureSkeleton() || Skeleton!.FindBone(boneName) is not { } bone)
            return GlobalTransform;
        var pose = bone.AppliedPose;
        // Y-up world matrix M; the Y-down frame spine-godot reports is F·M·F (F = diag(1, −1)), origin F·world.
        var local = new Transform2D(new Vector2(pose.A, -pose.C), new Vector2(-pose.B, pose.D), new Vector2(pose.WorldX, -pose.WorldY));
        return GlobalTransform * local;
    }

    protected override void OnDraw()
    {
        if (Skeleton is null)
            return;
        _geometry.Build(Skeleton, this);
    }

    void ISpineGeometrySink.Add(in SpineDrawItem item)
    {
        var vertices = item.Vertices;
        var count = vertices.Length / 2;
        if (count > _points.Length)
        {
            var size = Math.Max(count, _points.Length * 2);
            Array.Resize(ref _points, size);
            Array.Resize(ref _uvs, size);
            Array.Resize(ref _colors, size);
        }

        var uvs = item.Uvs;
        for (var i = 0; i < count; i++)
        {
            _points[i] = new Vector2(vertices[i * 2], -vertices[i * 2 + 1]);
            _uvs[i] = new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
            _colors[i] = item.Color;
        }

        DrawTriangles(_points.AsSpan(0, count), item.Triangles, _colors.AsSpan(0, count), _uvs.AsSpan(0, count),
            item.Region.page.rendererObject as Texture2D);
    }
}

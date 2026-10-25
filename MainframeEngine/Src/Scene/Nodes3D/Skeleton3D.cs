using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A 3D bone hierarchy (Godot 4's <c>Skeleton3D</c>, ADR 0207): bones are data, not nodes. Each bone has a name, a parent
/// (−1 for roots; parents come before their children) and a rest transform relative to its parent; at run time each has a
/// pose (position, rotation, scale relative to its parent) that an <see cref="AnimationPlayer"/> or code sets. Skinned
/// <see cref="MeshInstance3D"/>s whose <see cref="MeshInstance3D.Skeleton"/> names this node deform with the pose, and
/// <see cref="BoneAttachment3D"/> children follow a bone.
/// </summary>
/// <remarks>
/// <para>Global poses (bone → skeleton space) are recomputed once when read after a change, parents first. The bone
/// arrays are what a scene saves (the rest pose); poses are not saved and start at the rest.</para>
/// <para>Setting a pose inside a tree queues one deferred update per frame, which moves the attachments and raises
/// <see cref="SkeletonUpdated"/>; it allocates nothing.</para>
/// </remarks>
[EditorIcon("bone", Family = EditorIconFamily.Space3D)]
public class Skeleton3D : Node3D
{
    private static readonly Action<object?> DeferredUpdate = static state => ((Skeleton3D)state!).FlushPose();

    private string[] _names = [];
    private int[] _parents = [];
    private Transform3D[] _rests = [];
    private Vector3[] _posePosition = [];
    private Quaternion[] _poseRotation = [];
    private Vector3[] _poseScale = [];
    private Matrix4x4[] _global = [];
    private bool _bonesDirty = true;
    private bool _globalDirty = true;
    private bool _updateQueued;
    private readonly List<BoneAttachment3D> _attachments = [];

    /// <summary>The bone names (unique; bone tracks and skins find bones by name).</summary>
    [Export]
    public string[] BoneNames
    {
        get => _names;
        set
        {
            _names = value ?? [];
            _bonesDirty = true;
        }
    }

    /// <summary>Each bone's parent index, −1 for a root. A parent comes before its children.</summary>
    [Export]
    public int[] BoneParents
    {
        get => _parents;
        set
        {
            _parents = value ?? [];
            _bonesDirty = true;
        }
    }

    /// <summary>Each bone's rest (bind) transform relative to its parent bone (or the skeleton for roots).</summary>
    [Export]
    public Transform3D[] BoneRests
    {
        get => _rests;
        set
        {
            _rests = value ?? [];
            _bonesDirty = true;
        }
    }

    /// <summary>Bumped whenever a pose or the bone set changes (skinned meshes and tools compare it).</summary>
    public int PoseVersion { get; private set; }

    /// <summary>Bumped when the bones themselves change (names, parents, rests): skins re-match their bones.</summary>
    public int BonesVersion { get; private set; }

    /// <summary>Raised after the pose changed, once per frame at the end of process (Godot's <c>skeleton_updated</c>).</summary>
    [Signal]
    public event Action? SkeletonUpdated;

    /// <summary>The number of bones.</summary>
    public int GetBoneCount()
    {
        EnsureBones();
        return _names.Length;
    }

    /// <summary>The bone named <paramref name="name"/>, or −1.</summary>
    public int FindBone(string name) => FindBone(name.AsSpan());

    /// <summary>The bone named <paramref name="name"/>, or −1 (no allocation).</summary>
    public int FindBone(ReadOnlySpan<char> name)
    {
        var names = _names;
        for (var i = 0; i < names.Length; i++)
            if (name.SequenceEqual(names[i]))
                return i;
        return -1;
    }

    public string GetBoneName(int bone) => _names[bone];

    public int GetBoneParent(int bone)
    {
        EnsureBones();
        return _parents[bone];
    }

    public Transform3D GetBoneRest(int bone)
    {
        EnsureBones();
        return _rests[bone];
    }

    /// <summary>
    /// Appends a bone (code-built skeletons; imports set the arrays). <paramref name="parent"/> must be an existing bone or
    /// −1. Returns the new bone's index.
    /// </summary>
    public int AddBone(string name, int parent, Transform3D rest)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureBones();
        if (parent < -1 || parent >= _names.Length)
            throw new ArgumentOutOfRangeException(nameof(parent), $"Bone parent {parent} does not exist.");
        BoneNames = [.. _names, name];
        BoneParents = [.. _parents, parent];
        BoneRests = [.. _rests, rest];
        EnsureBones();
        return _names.Length - 1;
    }

    public Vector3 GetBonePosePosition(int bone)
    {
        EnsureBones();
        return _posePosition[bone];
    }

    public Quaternion GetBonePoseRotation(int bone)
    {
        EnsureBones();
        return _poseRotation[bone];
    }

    public Vector3 GetBonePoseScale(int bone)
    {
        EnsureBones();
        return _poseScale[bone];
    }

    /// <summary>The bone's pose relative to its parent.</summary>
    public Transform3D GetBonePose(int bone)
    {
        EnsureBones();
        return Transform3D.FromTrs(_posePosition[bone], _poseRotation[bone], _poseScale[bone]);
    }

    public void SetBonePosePosition(int bone, Vector3 position)
    {
        EnsureBones();
        _posePosition[bone] = position;
        PoseChanged();
    }

    public void SetBonePoseRotation(int bone, Quaternion rotation)
    {
        EnsureBones();
        _poseRotation[bone] = rotation;
        PoseChanged();
    }

    public void SetBonePoseScale(int bone, Vector3 scale)
    {
        EnsureBones();
        _poseScale[bone] = scale;
        PoseChanged();
    }

    /// <summary>Sets the bone's whole pose relative to its parent (decomposed into position, rotation and scale).</summary>
    public void SetBonePose(int bone, Transform3D pose)
    {
        EnsureBones();
        pose.Decompose(out _posePosition[bone], out _poseRotation[bone], out _poseScale[bone]);
        PoseChanged();
    }

    /// <summary>Puts every bone back at its rest (Godot's <c>reset_bone_poses</c>).</summary>
    public void ResetBonePoses()
    {
        EnsureBones();
        for (var i = 0; i < _names.Length; i++)
            _rests[i].Decompose(out _posePosition[i], out _poseRotation[i], out _poseScale[i]);
        PoseChanged();
    }

    /// <summary>The bone's posed transform in skeleton space (Godot's <c>get_bone_global_pose</c>).</summary>
    public Transform3D GetBoneGlobalPose(int bone) => Transform3D.FromMatrix4x4(GetBoneGlobalPoseMatrix(bone));

    /// <summary>The bone's rest transform in skeleton space (Godot's <c>get_bone_global_rest</c>).</summary>
    public Transform3D GetBoneGlobalRest(int bone)
    {
        EnsureBones();
        var global = _rests[bone];
        for (var parent = _parents[bone]; parent >= 0; parent = _parents[parent])
            global = _rests[parent] * global;
        return global;
    }

    /// <summary>The bone's posed transform in skeleton space as a row-vector matrix.</summary>
    internal Matrix4x4 GetBoneGlobalPoseMatrix(int bone)
    {
        EnsureGlobals();
        return _global[bone];
    }

    /// <summary>Every bone's posed skeleton-space matrix (valid until the pose changes).</summary>
    internal ReadOnlySpan<Matrix4x4> GlobalPoses
    {
        get
        {
            EnsureGlobals();
            return _global;
        }
    }

    /// <summary>
    /// Moves the attachments and raises <see cref="SkeletonUpdated"/> now (Godot's <c>force_update_all_bone_transforms</c>);
    /// normally that happens once at the end of the frame's process.
    /// </summary>
    public void ForceUpdatePose() => FlushPose();

    internal void AddAttachment(BoneAttachment3D attachment)
    {
        if (!_attachments.Contains(attachment))
            _attachments.Add(attachment);
    }

    internal void RemoveAttachment(BoneAttachment3D attachment) => _attachments.Remove(attachment);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (_updateQueued)
        {
            _updateQueued = false;
            PoseChanged(); // queued outside a tree: queue it on this one
        }
    }

    private void PoseChanged()
    {
        _globalDirty = true;
        PoseVersion++;
        if (_updateQueued)
            return;
        _updateQueued = true;
        Tree?.CallDeferred(DeferredUpdate, this);
    }

    private void FlushPose()
    {
        _updateQueued = false;
        EnsureGlobals();
        var attachments = _attachments;
        for (var i = 0; i < attachments.Count; i++)
            attachments[i].Follow(this);
        SkeletonUpdated?.Invoke();
    }

    private void EnsureGlobals()
    {
        EnsureBones();
        if (!_globalDirty)
            return;
        _globalDirty = false;
        var parents = _parents;
        for (var i = 0; i < _names.Length; i++)
        {
            var local = Matrix4x4.CreateScale(_poseScale[i]) * Matrix4x4.CreateFromQuaternion(_poseRotation[i]);
            local.Translation = _posePosition[i];
            var parent = parents[i];
            _global[i] = parent >= 0 ? local * _global[parent] : local;
        }
    }

    // Sizes the pose arrays to the bones (after the bone arrays were assigned) and resets the poses to the rests.
    private void EnsureBones()
    {
        if (!_bonesDirty)
            return;
        _bonesDirty = false;
        var count = _names.Length;
        if (_parents.Length != count)
            Array.Resize(ref _parents, count);
        if (_rests.Length != count)
        {
            var old = _rests.Length;
            Array.Resize(ref _rests, count);
            for (var i = old; i < count; i++)
                _rests[i] = Transform3D.Identity;
        }

        for (var i = 0; i < count; i++)
        {
            if (_parents[i] >= i || _parents[i] < -1)
            {
                Log.Error($"[Skeleton3D] '{Name}': bone {i} ('{_names[i]}') has parent {_parents[i]}; parents must come before their children. It is a root now.");
                _parents[i] = -1;
            }
        }

        _posePosition = new Vector3[count];
        _poseRotation = new Quaternion[count];
        _poseScale = new Vector3[count];
        _global = new Matrix4x4[count];
        for (var i = 0; i < count; i++)
            _rests[i].Decompose(out _posePosition[i], out _poseRotation[i], out _poseScale[i]);
        BonesVersion++;
        PoseChanged();
    }
}

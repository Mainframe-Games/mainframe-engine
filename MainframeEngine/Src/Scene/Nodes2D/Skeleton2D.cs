using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The root of a 2D bone hierarchy (Godot's <c>Skeleton2D</c>, ADR 0202): its <see cref="Bone2D"/> children, and theirs,
/// are its bones, numbered in tree order. A skinned <see cref="Polygon2D"/> whose <see cref="Polygon2D.Skeleton"/> names it
/// deforms with them and redraws whenever a bone moves. Godot's modification stack (look-at, IK) is not supported: drive
/// bones yourself, or from physics with <see cref="RemoteTransform2D"/>.
/// </summary>
/// <remarks>
/// This is the Godot-compatible bone container. G17's Spine-like skeletal animation node is named
/// <c>SkeletonSprite2D</c> in its proposal (docs/design/future/skeleton-2d.md).
/// </remarks>
[EditorIcon("bone", Family = EditorIconFamily.Space2D)]
public class Skeleton2D : Node2D
{
    private readonly List<Bone2D> _bones = [];
    private readonly List<Polygon2D> _dependents = [];
    private bool _bonesDirty = true;

    /// <summary>Bumped whenever a bone (or the skeleton) moves or a bone is added or removed.</summary>
    public int PoseVersion { get; private set; }

    /// <summary>The number of bones.</summary>
    public int GetBoneCount()
    {
        EnsureBones();
        return _bones.Count;
    }

    /// <summary>Bone <paramref name="index"/> in tree order.</summary>
    public Bone2D GetBone(int index)
    {
        EnsureBones();
        return _bones[index];
    }

    internal int IndexOf(Bone2D bone)
    {
        EnsureBones();
        return _bones.IndexOf(bone);
    }

    internal void OnBonesChanged()
    {
        _bonesDirty = true;
        foreach (var polygon in _dependents)
            polygon.OnSkeletonBonesChanged(); // bone paths may now resolve to other nodes
        OnPoseChanged();
    }

    internal void OnPoseChanged()
    {
        PoseVersion++;
        foreach (var polygon in _dependents)
            polygon.QueueRedraw();
    }

    internal void AddDependent(Polygon2D polygon)
    {
        if (!_dependents.Contains(polygon))
            _dependents.Add(polygon);
    }

    internal void RemoveDependent(Polygon2D polygon) => _dependents.Remove(polygon);

    // Tree order: each child bone, then its own bones (Godot numbers bones the same way).
    private void EnsureBones()
    {
        if (!_bonesDirty)
            return;
        _bonesDirty = false;
        _bones.Clear();
        Collect(this);
    }

    private void Collect(Node parent)
    {
        for (var i = 0; i < parent.ChildCount; i++)
        {
            if (parent.GetChild(i) is not Bone2D bone)
                continue;
            _bones.Add(bone);
            Collect(bone);
        }
    }
}

/// <summary>
/// A bone of a <see cref="Skeleton2D"/> (Godot's <c>Bone2D</c>): a <see cref="Node2D"/> whose parent is the skeleton or
/// another bone. <see cref="Rest"/> is its bind pose (relative to its parent); a skinned <see cref="Polygon2D"/> moves its
/// vertices by how far the bone has moved from it. Moving a bone, by code or from physics through a
/// <see cref="RemoteTransform2D"/>, redraws the skeleton's polygons in the same frame.
/// </summary>
[EditorIcon("bone", Family = EditorIconFamily.Space2D)]
public class Bone2D : Node2D
{
    private Skeleton2D? _skeleton;

    public Bone2D()
    {
        TrackGlobalTransformChanges(true);
    }

    /// <summary>The bind pose relative to the parent bone or skeleton (Godot's <c>rest</c>).</summary>
    [Export]
    public Transform2D Rest { get; set; } = Transform2D.Identity;

    /// <summary>
    /// Derive <see cref="Length"/> and <see cref="BoneAngle"/> from the first child bone, when there is one (Godot's
    /// default).
    /// </summary>
    [Export]
    public bool AutocalculateLengthAndAngle { get; set; } = true;

    /// <summary>The bone's length in pixels (Godot's default 16). With autocalculation: the distance to the first child bone.</summary>
    [Export(Range = "0,10000,0.1")]
    public float Length
    {
        get => AutocalculateLengthAndAngle && FirstChildBone() is { } child ? child.Position.Length() : field;
        set;
    } = 16f;

    /// <summary>The bone's direction in radians, relative to its rotation. With autocalculation: toward the first child bone.</summary>
    [Export]
    public float BoneAngle
    {
        get => AutocalculateLengthAndAngle && FirstChildBone() is { } child ? MathF.Atan2(child.Position.Y, child.Position.X) : field;
        set;
    }

    /// <summary>The skeleton this bone belongs to (null outside one).</summary>
    public Skeleton2D? Skeleton => _skeleton;

    /// <summary>This bone's index in its skeleton (−1 outside one).</summary>
    public int GetIndexInSkeleton() => _skeleton?.IndexOf(this) ?? -1;

    /// <summary>Puts the bone at its rest pose (Godot's <c>apply_rest</c>).</summary>
    public void ApplyRest() => Transform = Rest;

    /// <summary>The rest pose relative to the skeleton: the parent bones' rests composed with this one (Godot's <c>get_skeleton_rest</c>).</summary>
    public Transform2D GetSkeletonRest() => Parent is Bone2D parent ? parent.GetSkeletonRest() * Rest : Rest;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Node? node = Parent;
        while (node is Bone2D)
            node = node.Parent;
        _skeleton = node as Skeleton2D;
        _skeleton?.OnBonesChanged();
    }

    protected override void OnExitTree()
    {
        _skeleton?.OnBonesChanged();
        _skeleton = null;
        base.OnExitTree();
    }

    private protected override void OnGlobalTransformInvalidated() => _skeleton?.OnPoseChanged();

    private Bone2D? FirstChildBone()
    {
        for (var i = 0; i < ChildCount; i++)
            if (GetChild(i) is Bone2D bone)
                return bone;
        return null;
    }
}

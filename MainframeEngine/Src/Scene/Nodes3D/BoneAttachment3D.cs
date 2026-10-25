namespace MainframeEngine;

/// <summary>
/// Follows one bone of its parent <see cref="Skeleton3D"/> (Godot's <c>BoneAttachment3D</c>, ADR 0207): its transform is
/// set to the bone's skeleton-space pose whenever the skeleton's pose changes, so children (a tool in the hand, a torch
/// light, a hat) ride on the bone. It must be a direct child of the skeleton.
/// </summary>
/// <remarks>Its own transform is overwritten while it follows a bone; offset children instead.</remarks>
[EditorIcon("link", Family = EditorIconFamily.Space3D)]
public class BoneAttachment3D : Node3D
{
    private Skeleton3D? _skeleton;
    private int _resolvedBonesVersion = -1;
    private int _bone = -1;

    /// <summary>The bone followed, by name (Godot's <c>bone_name</c>).</summary>
    [Export]
    public string BoneName
    {
        get;
        set
        {
            field = value ?? "";
            _resolvedBonesVersion = -1;
            if (_skeleton is not null)
                Follow(_skeleton);
        }
    } = "";

    /// <summary>The followed bone's index in the skeleton, or −1 when the name matches no bone (or there is no skeleton).</summary>
    public int BoneIdx
    {
        get
        {
            if (_skeleton is { } skeleton)
                Resolve(skeleton);
            return _skeleton is null ? -1 : _bone;
        }
    }

    /// <summary>The parent skeleton, when the parent is one.</summary>
    public Skeleton3D? GetSkeleton() => _skeleton;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _skeleton = Parent as Skeleton3D;
        _resolvedBonesVersion = -1;
        if (_skeleton is null)
        {
            Log.Warning($"[BoneAttachment3D] '{Name}' is not a child of a Skeleton3D; it does not follow a bone.");
            return;
        }

        _skeleton.AddAttachment(this);
        Follow(_skeleton);
    }

    protected override void OnExitTree()
    {
        _skeleton?.RemoveAttachment(this);
        _skeleton = null;
        base.OnExitTree();
    }

    internal void Follow(Skeleton3D skeleton)
    {
        Resolve(skeleton);
        if (_bone < 0)
            return;
        var pose = skeleton.GetBoneGlobalPose(_bone);
        if (pose != Transform)
            Transform = pose;
    }

    private void Resolve(Skeleton3D skeleton)
    {
        skeleton.GetBoneCount(); // sizes the bones (and bumps BonesVersion) before the version is read
        if (_resolvedBonesVersion == skeleton.BonesVersion)
            return;
        _resolvedBonesVersion = skeleton.BonesVersion;
        _bone = skeleton.FindBone(BoneName);
        if (_bone < 0 && BoneName.Length > 0)
            Log.Warning($"[BoneAttachment3D] '{Name}': skeleton '{skeleton.Name}' has no bone '{BoneName}'.");
    }
}

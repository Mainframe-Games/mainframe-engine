using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// How a skinned mesh's joints map to a skeleton's bones (Godot's <c>Skin</c>, ADR 0207): joint <c>i</c> of a surface's
/// <see cref="MeshSurface.Joints"/> is the bone named <see cref="BindNames"/>[i], and <see cref="BindPoses"/>[i] is its
/// inverse bind matrix (mesh space at bind time → bone space). A <see cref="MeshInstance3D"/> without a skin binds joint
/// <c>i</c> to bone <c>i</c> with the inverse of the bone's global rest.
/// </summary>
[EditorIcon("bone")]
public sealed class Skin : Resource
{
    /// <summary>Joints a skin (and a surface's <see cref="MeshSurface.Joints"/>) may use.</summary>
    public const int MaxJoints = 256;

    private string[] _names = [];
    private Transform3D[] _poses = [];

    /// <summary>The bone each joint binds to, by name (matched when the mesh first draws, and when the bones change).</summary>
    [Export]
    public string[] BindNames
    {
        get => _names;
        set
        {
            _names = value ?? [];
            Version++;
        }
    }

    /// <summary>Each joint's inverse bind matrix: mesh space → bone space at bind time.</summary>
    [Export]
    public Transform3D[] BindPoses
    {
        get => _poses;
        set
        {
            _poses = value ?? [];
            Version++;
        }
    }

    /// <summary>Joints in the skin.</summary>
    public int BindCount => _names.Length;

    /// <summary>Bumped when the binds change.</summary>
    public int Version { get; private set; } = 1;

    /// <summary>Appends a joint bound to <paramref name="bone"/> with inverse bind matrix <paramref name="pose"/>.</summary>
    public void AddNamedBind(string bone, Transform3D pose)
    {
        BindNames = [.. _names, bone];
        BindPoses = [.. _poses, pose];
    }
}

/// <summary>
/// The CPU side of skinning one <see cref="MeshInstance3D"/> (ADR 0207): matches its <see cref="Skin"/>'s joints to the
/// skeleton's bones (on change only), builds the joint palette (inverse bind × bone pose × skeleton → mesh) and the posed
/// bounds from per-joint bind-space boxes. The renderer uploads the palette for the compute pre-skinning pass; tests use
/// <see cref="SkinPoint"/> as the reference. Allocates only when the mesh, skin or bones change.
/// </summary>
internal sealed class SkinState
{
    private Skin? _skin;
    private int _skinVersion = -1;
    private Skeleton3D? _skeleton;
    private int _bonesVersion = -1;
    private Mesh? _mesh;
    private int _meshVersion = -1;
    private int[] _jointBones = [];
    private Matrix4x4[] _inverseBinds = [];
    private Aabb[] _jointBoxes = [];
    private Aabb _unweighted = Aabb.Empty;

    /// <summary>Joint matrices (row vectors, mesh-space bind vertex → mesh-space posed vertex), one per joint.</summary>
    public Matrix4x4[] Palette = [];

    /// <summary>Joints in <see cref="Palette"/> (the skin's, or the skeleton's bones without a skin).</summary>
    public int JointCount { get; private set; }

    /// <summary>The posed mesh-space bounds from the last <see cref="Update"/>.</summary>
    public Aabb Bounds { get; private set; } = Aabb.Empty;

    /// <summary>Frame stamp for the renderer (palette built once per frame).</summary>
    public ulong PreparedFrame;

    /// <summary>
    /// Rebuilds the palette and bounds for the current pose. Returns false when the mesh cannot be skinned with this
    /// skeleton (no joints, a joint index outside the skin).
    /// </summary>
    public bool Update(Mesh mesh, Skin? skin, Skeleton3D skeleton, in Matrix4x4 skeletonToMesh)
    {
        var boneCount = skeleton.GetBoneCount();
        if (!ReferenceEquals(skin, _skin) || (skin?.Version ?? 0) != _skinVersion || !ReferenceEquals(skeleton, _skeleton) ||
            skeleton.BonesVersion != _bonesVersion)
            Bind(skin, skeleton, boneCount);
        var meshVersion = mesh.Version;
        if (!ReferenceEquals(mesh, _mesh) || meshVersion != _meshVersion)
        {
            _mesh = mesh;
            _meshVersion = meshVersion;
            BuildJointBoxes(mesh);
        }

        if (JointCount == 0)
            return false;

        var poses = skeleton.GlobalPoses;
        var bounds = _unweighted;
        for (var j = 0; j < JointCount; j++)
        {
            var bone = _jointBones[j];
            var boneToMesh = (bone >= 0 ? poses[bone] : Matrix4x4.Identity) * skeletonToMesh;
            Palette[j] = _inverseBinds[j] * boneToMesh;
            if (!_jointBoxes[j].IsEmpty)
                bounds = bounds.Merge(_jointBoxes[j].Transform(boneToMesh));
        }

        Bounds = bounds;
        return true;
    }

    /// <summary>Skins one bind-pose point with the current <see cref="Palette"/> (the compute shader's maths, for tests).</summary>
    public Vector3 SkinPoint(Vector3 position, ReadOnlySpan<int> joints, Vector4 weights)
    {
        var sum = weights.X + weights.Y + weights.Z + weights.W;
        if (sum <= 0f)
            return position;
        var result = Vector3.Zero;
        for (var k = 0; k < 4; k++)
        {
            var w = k switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
            if (w > 0f && (uint)joints[k] < (uint)JointCount)
                result += Vector3.Transform(position, Palette[joints[k]]) * w;
        }

        return result;
    }

    private void Bind(Skin? skin, Skeleton3D skeleton, int boneCount)
    {
        _skin = skin;
        _skinVersion = skin?.Version ?? 0;
        _skeleton = skeleton;
        _bonesVersion = skeleton.BonesVersion;
        _meshVersion = -1; // joint boxes depend on the inverse binds
        var count = skin?.BindCount ?? boneCount;
        JointCount = count;
        _jointBones = new int[count];
        _inverseBinds = new Matrix4x4[count];
        Palette = new Matrix4x4[count];
        for (var j = 0; j < count; j++)
        {
            if (skin is not null)
            {
                var bone = skeleton.FindBone(skin.BindNames[j]);
                if (bone < 0)
                    Log.Warning($"[Skin] joint '{skin.BindNames[j]}' has no bone in skeleton '{skeleton.Name}'; it stays at its bind pose.");
                _jointBones[j] = bone;
                _inverseBinds[j] = j < skin.BindPoses.Length ? skin.BindPoses[j].ToMatrix4x4() : Matrix4x4.Identity;
                if (bone < 0)
                    _inverseBinds[j] = Matrix4x4.Identity;
            }
            else
            {
                _jointBones[j] = j;
                Matrix4x4.Invert(skeleton.GetBoneGlobalRest(j).ToMatrix4x4(), out _inverseBinds[j]);
            }
        }
    }

    // Each joint's bind-space box: every vertex it moves, in the joint's bone space. A posed vertex is a weighted mean of
    // its joints' transforms of it, so the union of the posed joint boxes contains it.
    private void BuildJointBoxes(Mesh mesh)
    {
        _jointBoxes = new Aabb[JointCount];
        Array.Fill(_jointBoxes, Aabb.Empty);
        _unweighted = Aabb.Empty;
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            var positions = surface.Positions;
            var joints = surface.Joints;
            var weights = surface.Weights;
            var skinned = surface.IsSkinned;
            for (var v = 0; v < positions.Length; v++)
            {
                var p = positions[v];
                var w = skinned ? weights[v] : default;
                if (!skinned || w.X + w.Y + w.Z + w.W <= 0f)
                {
                    _unweighted = _unweighted.Encapsulate(p);
                    continue;
                }

                for (var k = 0; k < 4; k++)
                {
                    var weight = k switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
                    var joint = joints[v * 4 + k];
                    if (weight > 0f && (uint)joint < (uint)JointCount)
                        _jointBoxes[joint] = _jointBoxes[joint].Encapsulate(Vector3.Transform(p, _inverseBinds[joint]));
                }
            }
        }
    }
}

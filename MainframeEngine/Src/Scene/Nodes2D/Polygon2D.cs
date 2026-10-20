using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A filled, optionally textured polygon on the canvas (Godot's <c>Polygon2D</c>, ADR 0202), drawn as Godot's
/// <c>polygon_2d.cpp</c> draws it. Without <see cref="Polygons"/>, the outline (all points but the last
/// <see cref="InternalVertexCount"/>) is ear-clipped; with them, each index list is triangulated on its own and internal
/// vertices take part. <see cref="UV"/> is in texture pixels, through <see cref="TextureOffset"/>,
/// <see cref="TextureRotation"/> and <see cref="TextureScale"/>. With a <see cref="Skeleton"/> and bones it is skinned on
/// the CPU: each vertex follows its four heaviest bones (weights normalised, as Godot does) by how far they moved from
/// their rest pose. It redraws when a bone moves, in the same frame, and steady frames allocate nothing.
/// </summary>
[EditorIcon("polygon", Family = EditorIconFamily.Space2D)]
public class Polygon2D : Node2D
{
    private const int Influences = 4;

    private int[] _indices = [];
    private bool _trianglesDirty = true;
    private bool _skinDirty = true;
    private Vector2[] _points = [];
    private Vector2[] _uvs = [];
    private Vector4[] _colors = [];
    private int[] _vertexBones = [];       // Influences per vertex: index into the bone entries, −1 = none
    private float[] _vertexWeights = [];   // Influences per vertex, normalised
    private Bone2D?[] _bones = [];         // resolved BonePaths
    private Transform2D[] _skin = [];      // per bone entry: polygon-local rest → posed
    private Skeleton2D? _skeleton;

    public Polygon2D()
    {
        TrackGlobalTransformChanges(true); // moving the polygon against its skeleton changes the skinned shape
    }

    /// <summary>The points (Godot's <c>polygon</c>): the outline, then <see cref="InternalVertexCount"/> internal vertices.</summary>
    [Export]
    public Vector2[] Polygon
    {
        get;
        set
        {
            field = value ?? [];
            _trianglesDirty = _skinDirty = true;
            QueueRedraw();
        }
    } = [];

    /// <summary>Texture coordinates in texture pixels, one per point; empty: the points themselves.</summary>
    [Export]
    public Vector2[] UV
    {
        get;
        set
        {
            field = value ?? [];
            QueueRedraw();
        }
    } = [];

    /// <summary>The fill (multiplied with the texture), used when <see cref="VertexColors"/> does not have one colour per point.</summary>
    [Export]
    public Color Color
    {
        get;
        set
        {
            field = value;
            QueueRedraw();
        }
    } = Colors.White;

    /// <summary>A colour per point (Godot's <c>vertex_colors</c>); any other length uses <see cref="Color"/>.</summary>
    [Export]
    public Color[] VertexColors
    {
        get;
        set
        {
            field = value ?? [];
            QueueRedraw();
        }
    } = [];

    /// <summary>Added to every point (pixels).</summary>
    [Export]
    public Vector2 Offset
    {
        get;
        set
        {
            field = value;
            QueueRedraw();
        }
    }

    [Export]
    public Texture2D? Texture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            if (field is not null)
                field.Changed -= QueueRedraw;
            field = value;
            if (field is not null)
                field.Changed += QueueRedraw;
            QueueRedraw();
        }
    }

    [Export]
    public Vector2 TextureOffset
    {
        get;
        set
        {
            field = value;
            QueueRedraw();
        }
    }

    [Export]
    public Vector2 TextureScale
    {
        get;
        set
        {
            field = value;
            QueueRedraw();
        }
    } = Vector2.One;

    /// <summary>Radians.</summary>
    [Export]
    public float TextureRotation
    {
        get;
        set
        {
            field = value;
            QueueRedraw();
        }
    }

    /// <summary>Index lists, each a polygon triangulated on its own (Godot's <c>polygons</c>); empty: the outline.</summary>
    [Export]
    public int[][] Polygons
    {
        get;
        set
        {
            field = value ?? [];
            _trianglesDirty = true;
            QueueRedraw();
        }
    } = [];

    /// <summary>How many of the last points are internal: left out of the outline, used by <see cref="Polygons"/> and skinning.</summary>
    [Export(Range = "0,100000,1")]
    public int InternalVertexCount
    {
        get;
        set
        {
            field = Math.Max(0, value);
            _trianglesDirty = true;
            QueueRedraw();
        }
    }

    /// <summary>The <see cref="Skeleton2D"/> that deforms the polygon (relative to this node); empty: not skinned.</summary>
    [ExportGroup("Skeleton")]
    [Export(NodeType = typeof(Skeleton2D))]
    public NodePath Skeleton
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            DetachSkeleton();
            _skinDirty = true;
            QueueRedraw();
        }
    }

    /// <summary>Each bone's path, relative to <see cref="Skeleton"/> (Godot's <c>bones</c>, with <see cref="BoneWeights"/>).</summary>
    [Export]
    public NodePath[] BonePaths
    {
        get;
        set
        {
            field = value ?? [];
            _skinDirty = true;
            QueueRedraw();
        }
    } = [];

    /// <summary>Each bone's weight per point (an array as long as <see cref="Polygon"/>; others are ignored).</summary>
    [Export]
    public float[][] BoneWeights
    {
        get;
        set
        {
            field = value ?? [];
            _skinDirty = true;
            QueueRedraw();
        }
    } = [];

    public int GetBoneCount() => Math.Min(BonePaths.Length, BoneWeights.Length);

    public NodePath GetBonePath(int index) => BonePaths[index];

    public float[] GetBoneWeights(int index) => BoneWeights[index];

    /// <summary>Adds a bone and its per-point weights (Godot's <c>add_bone</c>).</summary>
    public void AddBone(NodePath path, float[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var count = GetBoneCount();
        BonePaths = [.. BonePaths.AsSpan(0, count), path];
        BoneWeights = [.. BoneWeights.AsSpan(0, count), weights];
    }

    public void SetBonePath(int index, NodePath path)
    {
        BonePaths[index] = path;
        _skinDirty = true;
        QueueRedraw();
    }

    public void SetBoneWeights(int index, float[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        BoneWeights[index] = weights;
        _skinDirty = true;
        QueueRedraw();
    }

    public void EraseBone(int index)
    {
        BonePaths = [.. BonePaths.AsSpan(0, index), .. BonePaths.AsSpan(index + 1)];
        BoneWeights = [.. BoneWeights.AsSpan(0, index), .. BoneWeights.AsSpan(index + 1)];
    }

    public void ClearBones()
    {
        BonePaths = [];
        BoneWeights = [];
    }

    protected override void OnExitTree()
    {
        DetachSkeleton();
        base.OnExitTree();
    }

    internal void OnSkeletonBonesChanged() => _skinDirty = true;

    private protected override void OnGlobalTransformInvalidated()
    {
        if (_skeleton is not null)
            QueueRedraw();
    }

    protected override void OnDraw()
    {
        var source = Polygon;
        var count = source.Length;
        if (count < 3)
            return;
        EnsureTriangles(source);
        if (_indices.Length == 0)
            return;
        EnsureBuffers(count);

        for (var i = 0; i < count; i++)
            _points[i] = source[i] + Offset;
        var texture = Texture;
        if (texture is { Width: > 0, Height: > 0 })
        {
            // Godot: Transform2D(rotation, offset).scale(scale), then divided by the texture size.
            var uv = UV.Length == count ? UV : _points;
            var (sin, cos) = MathF.SinCos(TextureRotation);
            var size = new Vector2(texture.Width, texture.Height);
            for (var i = 0; i < count; i++)
            {
                var p = uv[i];
                var rotated = new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos) + TextureOffset;
                _uvs[i] = rotated * TextureScale / size;
            }
        }

        var colors = VertexColors;
        for (var i = 0; i < count; i++)
            _colors[i] = colors.Length == count ? colors[i] : Color;

        Skin(count);
        DrawTriangles(_points.AsSpan(0, count), _indices, _colors.AsSpan(0, count), texture is null ? default : _uvs.AsSpan(0, count), texture);
    }

    // Godot's polygon_2d.cpp: no polygons → the outline only (internal vertices dropped); polygons → each its own fan.
    private void EnsureTriangles(Vector2[] source)
    {
        if (!_trianglesDirty)
            return;
        _trianglesDirty = false;
        var polygons = Polygons;
        if (polygons.Length == 0)
        {
            var outline = Math.Max(0, source.Length - InternalVertexCount);
            _indices = outline >= 3 ? CanvasPrimitives.Triangulate(source.AsSpan(0, outline)) ?? [] : [];
            return;
        }

        var indices = new List<int>();
        var scratch = new List<Vector2>();
        foreach (var polygon in polygons)
        {
            if (polygon is null || polygon.Length < 3)
                continue;
            scratch.Clear();
            var valid = true;
            foreach (var index in polygon)
            {
                if ((uint)index >= (uint)source.Length)
                {
                    valid = false;
                    break;
                }

                scratch.Add(source[index]);
            }

            if (!valid || CanvasPrimitives.Triangulate(scratch.ToArray()) is not { } local)
                continue;
            foreach (var i in local)
                indices.Add(polygon[i]);
        }

        _indices = [.. indices];
    }

    private void EnsureBuffers(int count)
    {
        if (_points.Length >= count)
            return;
        _points = new Vector2[count];
        _uvs = new Vector2[count];
        _colors = new Vector4[count];
    }

    // CPU skinning (Godot does it on the GPU with the same maths): in skeleton space a vertex moves by
    // Σ wᵢ (boneᵢ · restᵢ⁻¹); R takes polygon-local points to skeleton space and back.
    private void Skin(int count)
    {
        var skeleton = ResolveSkeleton();
        if (skeleton is null)
            return;
        EnsureSkin(skeleton, count);
        var entries = _bones.Length;
        if (entries == 0)
            return;

        var skeletonInverse = skeleton.GlobalTransform.AffineInverse();
        var toSkeleton = skeletonInverse * GlobalTransform;
        var fromSkeleton = toSkeleton.AffineInverse();
        for (var e = 0; e < entries; e++)
        {
            _skin[e] = _bones[e] is { IsInsideTree: true } bone
                ? fromSkeleton * (skeletonInverse * bone.GlobalTransform) * bone.GetSkeletonRest().AffineInverse() * toSkeleton
                : Transform2D.Identity;
        }

        for (var i = 0; i < count; i++)
        {
            var b = i * Influences;
            if (_vertexBones[b] < 0)
                continue; // no weights: left where it is
            var rest = _points[i];
            var skinned = Vector2.Zero;
            for (var k = 0; k < Influences; k++)
            {
                var entry = _vertexBones[b + k];
                if (entry < 0)
                    break;
                skinned += _skin[entry].TransformPoint(rest) * _vertexWeights[b + k];
            }

            _points[i] = skinned;
        }
    }

    // Per vertex, the four heaviest bones and their weights normalised (Godot's Polygon2D), rebuilt on change only.
    private void EnsureSkin(Skeleton2D skeleton, int count)
    {
        if (!_skinDirty && _vertexBones.Length == count * Influences)
            return;
        _skinDirty = false;
        var entries = GetBoneCount();
        _bones = new Bone2D?[entries];
        _skin = new Transform2D[entries];
        for (var e = 0; e < entries; e++)
            _bones[e] = skeleton.GetNodeOrNull(BonePaths[e]) as Bone2D;

        _vertexBones = new int[count * Influences];
        _vertexWeights = new float[count * Influences];
        Array.Fill(_vertexBones, -1);
        for (var e = 0; e < entries; e++)
        {
            var weights = BoneWeights[e];
            if (_bones[e] is null || weights is null || weights.Length != count)
                continue;
            for (var i = 0; i < count; i++)
            {
                var w = weights[i];
                if (!(w > 0f))
                    continue;
                var b = i * Influences;
                for (var k = 0; k < Influences; k++)
                {
                    if (_vertexBones[b + k] >= 0 && w <= _vertexWeights[b + k])
                        continue;
                    for (var m = Influences - 1; m > k; m--)
                    {
                        _vertexBones[b + m] = _vertexBones[b + m - 1];
                        _vertexWeights[b + m] = _vertexWeights[b + m - 1];
                    }

                    _vertexBones[b + k] = e;
                    _vertexWeights[b + k] = w;
                    break;
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            var b = i * Influences;
            var total = 0f;
            for (var k = 0; k < Influences && _vertexBones[b + k] >= 0; k++)
                total += _vertexWeights[b + k];
            if (total <= 0f)
                continue;
            for (var k = 0; k < Influences && _vertexBones[b + k] >= 0; k++)
                _vertexWeights[b + k] /= total;
        }
    }

    private Skeleton2D? ResolveSkeleton()
    {
        if (_skeleton is { IsInsideTree: true })
            return _skeleton;
        DetachSkeleton();
        if (Skeleton.IsEmpty || GetNodeOrNull(Skeleton) is not Skeleton2D skeleton)
            return null;
        _skeleton = skeleton;
        skeleton.AddDependent(this);
        _skinDirty = true;
        return skeleton;
    }

    private void DetachSkeleton()
    {
        _skeleton?.RemoveDependent(this);
        _skeleton = null;
    }
}

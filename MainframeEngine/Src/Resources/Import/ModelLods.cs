using System.Numerics;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>
/// G6.4: authored levels of detail in model files. Sibling mesh nodes named <c>&lt;name&gt;_LOD0</c> …
/// <c>&lt;name&gt;_LOD7</c> (the Unity/Unreal convention, any case) merge into one <see cref="MeshInstance3D"/> named
/// <c>&lt;name&gt;</c> at LOD0's place, whose mesh's surfaces carry the other nodes' surfaces (matched by index, moved into
/// LOD0's space) as <see cref="MeshLod"/> levels with their own geometry.
/// </summary>
internal static partial class ModelLods
{
    /// <summary>A level's default error: 1 % of the LOD0 mesh's bounds radius, doubling per level.</summary>
    public const float DefaultFirstError = 0.01f;

    [GeneratedRegex(@"^(?<base>.+?)[_ .-]LOD(?<level>[0-7])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LodName();

    /// <summary>The error fraction (of the bounds radius) of <paramref name="level"/> ≥ 1.</summary>
    public static float ErrorFraction(IReadOnlyList<float>? errors, int level) =>
        errors is { } list && level - 1 < list.Count && list[level - 1] > 0f && float.IsFinite(list[level - 1])
            ? list[level - 1]
            : DefaultFirstError * MathF.Pow(2f, level - 1);

    /// <summary>
    /// Merges every group of <c>_LODn</c> siblings under <paramref name="root"/>. Returns the number of groups merged.
    /// Levels below LOD0's are dropped with a warning; skinned meshes (with a <see cref="MeshInstance3D.Skin"/>) are left
    /// alone.
    /// </summary>
    public static int Merge(Node root, IReadOnlyList<float>? errors, string where)
    {
        var merged = 0;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            merged += MergeChildren(parent, errors, where);
            for (var i = 0; i < parent.ChildCount; i++)
                stack.Push(parent.GetChild(i));
        }

        return merged;
    }

    private static int MergeChildren(Node parent, IReadOnlyList<float>? errors, string where)
    {
        Dictionary<string, MeshInstance3D?[]>? groups = null;
        for (var i = 0; i < parent.ChildCount; i++)
        {
            var child = parent.GetChild(i);
            if (child is not MeshInstance3D { Mesh: not null, Skin: null } mesh || LodName().Match(child.Name) is not { Success: true } match)
                continue;
            groups ??= new Dictionary<string, MeshInstance3D?[]>(StringComparer.Ordinal);
            var key = match.Groups["base"].Value;
            if (!groups.TryGetValue(key, out var levels))
                groups[key] = levels = new MeshInstance3D?[MeshLod.MaxLevels];
            var level = match.Groups["level"].Value[0] - '0';
            if (levels[level] is not null)
                Log.Warning($"[Import] {where}: two nodes are level {level} of '{key}'; '{child.Name}' is ignored.");
            else
                levels[level] = mesh;
        }

        if (groups is null)
            return 0;
        var merged = 0;
        foreach (var (name, levels) in groups)
        {
            if (levels[0] is not { } lod0)
            {
                Log.Warning($"[Import] {where}: '{name}' has LOD levels but no {name}_LOD0; they stay separate nodes.");
                continue;
            }

            MergeGroup(name, lod0, levels, errors, where);
            merged++;
        }

        return merged;
    }

    private static void MergeGroup(string name, MeshInstance3D lod0, MeshInstance3D?[] levels, IReadOnlyList<float>? errors, string where)
    {
        var baseMesh = lod0.Mesh!;
        var radius = baseMesh.Bounds.Extents.Length();
        var toLod0 = Matrix4x4.Invert(lod0.Transform.ToMatrix4x4(), out var inverse) ? inverse : Matrix4x4.Identity;
        var mesh = new ArrayMesh { ResourceName = string.IsNullOrEmpty(baseMesh.ResourceName) ? name : baseMesh.ResourceName };
        var surfaceLods = new List<MeshLod>[baseMesh.SurfaceCount];
        for (var s = 0; s < surfaceLods.Length; s++)
            surfaceLods[s] = [];

        var nextLevel = 1;
        for (var level = 1; level < levels.Length; level++)
        {
            if (levels[level] is not { } node)
                continue;
            if (level != nextLevel)
                Log.Warning($"[Import] {where}: '{name}' skips level {nextLevel}; {node.Name} becomes level {nextLevel}.");
            var toBase = node.Transform.ToMatrix4x4() * toLod0;
            var lodMesh = node.Mesh!;
            var error = ErrorFraction(errors, nextLevel) * radius;
            for (var s = 0; s < surfaceLods.Length; s++)
            {
                // A level keeps a surface's chain only while every earlier level had that surface.
                if (s < lodMesh.SurfaceCount && surfaceLods[s].Count == nextLevel - 1)
                    surfaceLods[s].Add(new MeshLod(error, Transformed(lodMesh.GetSurface(s), toBase)));
            }

            if (lodMesh.SurfaceCount != baseMesh.SurfaceCount)
                Log.Warning($"[Import] {where}: {node.Name} has {lodMesh.SurfaceCount} surfaces and {lod0.Name} {baseMesh.SurfaceCount}; levels pair them by index.");
            if (node.ChildCount > 0)
                Log.Warning($"[Import] {where}: the children of {node.Name} are dropped with it (only LOD0's children stay).");
            node.Parent!.RemoveChild(node);
            node.Free();
            nextLevel++;
        }

        for (var s = 0; s < surfaceLods.Length; s++)
        {
            var surface = Copy(baseMesh.GetSurface(s));
            surface.Lods = [.. surfaceLods[s]];
            mesh.AddSurface(surface);
        }

        lod0.Mesh = mesh;
        lod0.Name = name;
    }

    // A copy of the surface's geometry in another node's space (normals by the inverse transpose).
    private static MeshSurface Transformed(MeshSurface source, in Matrix4x4 transform)
    {
        var copy = Copy(source);
        if (transform.IsIdentity)
            return copy;
        var positions = new Vector3[source.Positions.Length];
        for (var i = 0; i < positions.Length; i++)
            positions[i] = Vector3.Transform(source.Positions[i], transform);
        copy.Positions = positions;
        if (source.Normals.Length != 0)
        {
            var normalMatrix = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
            var normals = new Vector3[source.Normals.Length];
            for (var i = 0; i < normals.Length; i++)
            {
                var n = Vector3.TransformNormal(source.Normals[i], normalMatrix);
                normals[i] = n.LengthSquared() > 0f ? Vector3.Normalize(n) : source.Normals[i];
            }

            copy.Normals = normals;
        }

        return copy;
    }

    private static MeshSurface Copy(MeshSurface source) => new(source.Positions, source.Normals, source.UVs, source.Indices, source.Material)
    {
        ResourceName = source.ResourceName,
        Colors = source.Colors,
        Custom0 = source.Custom0,
        Custom1 = source.Custom1,
        Custom2 = source.Custom2,
    };
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>Geometry helpers shared by the mesh generators and importers.</summary>
public static class MeshGeometry
{
    /// <summary>Area-weighted smooth vertex normals for a triangle list (zero-area vertices get +Y).</summary>
    public static Vector3[] ComputeSmoothNormals(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices)
    {
        var normals = new Vector3[positions.Length];
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length)
                continue;
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]); // length = 2 × area
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            var length = normals[i].Length();
            normals[i] = length > 1e-12f ? normals[i] / length : Vector3.UnitY;
        }

        return normals;
    }
}

/// <summary>
/// Accumulates one surface's vertices and triangles. Triangles added with <see cref="AddTriangle"/> are oriented
/// so their face normal agrees with the vertex normals (counter-clockwise when seen from the side the normals
/// point to), which keeps every generator's winding consistent with the engine's front-face convention.
/// </summary>
internal sealed class MeshBuilder
{
    private readonly List<Vector3> _positions = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector2> _uvs = [];
    private readonly List<int> _indices = [];

    public int VertexCount => _positions.Count;

    public int AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        _positions.Add(position);
        _normals.Add(normal);
        _uvs.Add(uv);
        return _positions.Count - 1;
    }

    /// <summary>True when vertices <paramref name="a"/> and <paramref name="b"/> share a position (a pole).</summary>
    public bool IsDegenerate(int a, int b) => Vector3.DistanceSquared(_positions[a], _positions[b]) < 1e-12f;

    /// <summary>Adds a triangle, swapping two corners when its winding disagrees with the vertex normals.</summary>
    public void AddTriangle(int a, int b, int c)
    {
        var face = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
        var normal = _normals[a] + _normals[b] + _normals[c];
        if (Vector3.Dot(face, normal) < 0f)
            (b, c) = (c, b);
        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
    }

    /// <summary>Two triangles over the quad a-b-c-d (any consistent corner order).</summary>
    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>Builds the surface; <paramref name="flipFaces"/> reverses the winding and the normals.</summary>
    public MeshSurface Build(Material? material, bool flipFaces)
    {
        var positions = _positions.ToArray();
        var normals = _normals.ToArray();
        var indices = _indices.ToArray();
        if (flipFaces)
        {
            for (var i = 0; i < normals.Length; i++)
                normals[i] = -normals[i];
            for (var t = 0; t + 2 < indices.Length; t += 3)
                (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        }

        return new MeshSurface(positions, normals, _uvs.ToArray(), indices, material);
    }
}

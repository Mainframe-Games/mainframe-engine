using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Pond and lake surfaces from a terrain's water layer (G8c.1, ADR 0159): per chunk, the render triangles (same diagonal
/// rule) with any wet vertex, at the stored height (the water surface) minus <see cref="ShoreDrop"/>, so a dry shore
/// vertex's bank stands just above the water and the shoreline is a clean cut. Plain C#, no tree.
/// </summary>
/// <remarks>
/// Vertices are shared (smooth); normals point up; UV = (0.5, 0) so <see cref="WaterMaterial3D"/>'s ribbon-edge foam
/// stays off; <see cref="MeshSurface.Custom0"/> = (column depth in metres, no flow, no foam). Chunk-local positions, like
/// the terrain's chunk meshes.
/// </remarks>
internal static class TerrainWaterMesh
{
    /// <summary>How far below the stored height the water surface sits (Realistic; Faceted uses twice as much).</summary>
    public const float ShoreDrop = 0.02f;

    /// <summary>True when any vertex of chunk (<paramref name="cx"/>, <paramref name="cz"/>), edges included, has water.</summary>
    public static bool ChunkIsWet(TerrainData data, int cx, int cz)
    {
        var water = data.WaterPixels;
        var stride = data.VerticesPerSide;
        var q = data.ChunkQuads;
        for (var j = cz * q; j <= (cz + 1) * q; j++)
            for (var i = cx * q; i <= (cx + 1) * q; i++)
                if (water[(j * stride + i) * 4] != 0)
                    return true;
        return false;
    }

    /// <summary>The chunk's water surface, or null when no triangle of it is wet.</summary>
    public static MeshSurface? Build(TerrainData data, int cx, int cz)
    {
        if (!ChunkIsWet(data, cx, cz))
            return null;
        var water = data.WaterPixels;
        var heights = data.Heights;
        var stride = data.VerticesPerSide;
        var q = data.ChunkQuads;
        var row = q + 1;
        var spacing = data.VertexSpacing;
        var drop = data.Profile == TerrainProfile.Faceted ? 2f * ShoreDrop : ShoreDrop;
        var depthPerUnit = data.MaxWaterDepth / 255f;

        var remap = new int[row * row];
        Array.Fill(remap, -1);
        var indices = new List<int>();
        var positions = new List<Vector3>();
        var custom = new List<Vector4>();
        Span<int> corners = stackalloc int[6];
        Span<int> quad = stackalloc int[4];
        for (var b = 0; b < q; b++)
            for (var a = 0; a < q; a++)
            {
                var i = cx * q + a;
                var j = cz * q + b;
                quad[0] = b * row + a;
                quad[1] = quad[0] + 1;
                quad[2] = quad[0] + row;
                quad[3] = quad[2] + 1;
                TerrainGrid.QuadCorners(TerrainGrid.IsDiagonalB(data.Diagonal, i, j), corners);
                for (var t = 0; t < 6; t += 3)
                {
                    var wet = false;
                    for (var c = 0; c < 3; c++)
                        wet |= Depth(water, stride, cx * q, cz * q, row, quad[corners[t + c]]) != 0;
                    if (!wet)
                        continue;
                    for (var c = 0; c < 3; c++)
                    {
                        var local = quad[corners[t + c]];
                        if (remap[local] < 0)
                        {
                            var la = local % row;
                            var lb = local / row;
                            var k = (cz * q + lb) * stride + cx * q + la;
                            remap[local] = positions.Count;
                            positions.Add(new Vector3(la * spacing, heights[k] - drop, lb * spacing));
                            custom.Add(new Vector4(water[k * 4] * depthPerUnit, 0f, 0f, 0f));
                        }

                        indices.Add(remap[local]);
                    }
                }
            }

        if (indices.Count == 0)
            return null;
        var normals = new Vector3[positions.Count];
        Array.Fill(normals, Vector3.UnitY);
        var uvs = new Vector2[positions.Count];
        Array.Fill(uvs, new Vector2(0.5f, 0f));
        return new MeshSurface([.. positions], normals, uvs, [.. indices]) { Custom0 = [.. custom] };
    }

    private static byte Depth(byte[] water, int stride, int i0, int j0, int row, int local) =>
        water[((j0 + local / row) * stride + i0 + local % row) * 4];
}

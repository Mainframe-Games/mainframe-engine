using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace MainframeEngine;

/// <summary>
/// G3.12: bakes a <see cref="NavigationMesh"/> for a <see cref="NavigationRegion3D"/> with Recast (DotRecast, ADR 0264; the
/// only code besides <see cref="DetourMesh3D"/> that calls it). The source geometry — <see cref="MeshInstance3D"/>
/// surfaces and <see cref="StaticBody3D"/> collision shapes (boxes, spheres, capsules, cylinders, convex hulls, concave
/// faces, height maps) as the mesh's settings choose — is collected as one triangle soup in the region's local space
/// (counter-clockwise from outside, as the engine winds them: Recast reads upward normals as floor), then voxelised, split
/// into regions, traced and polygonised with the mesh's settings, and written as one Detour tile. Main thread; allocates.
/// </summary>
internal static class RecastBaker
{
    private const string Prefix = "dtmd1:";
    private const int RingSegments = 16;

    public static bool Bake(NavigationRegion3D region, NavigationMesh mesh, out Vector3[] vertices, out int[][] polygons,
        out string detourData, out string? error)
    {
        vertices = [];
        polygons = [];
        detourData = "";
        var soup = new TriangleSoup();
        Parse(region, mesh, soup);
        if (soup.TriangleCount == 0)
        {
            error = "there is no source geometry (meshes or static collision under the region, or in its group)";
            return false;
        }

        var nvp = Math.Clamp(mesh.VerticesPerPolygon, 3, 6);
        var geom = new RcSampleInputGeomProvider(soup.Vertices.ToArray(), soup.Indices.ToArray());
        // Recast rasterises surfaces only: the air inside a closed shape would be walkable floor. Each solid primitive's
        // footprint is marked unwalkable from its bottom to just below its lowest top (its top and a ramp stay walkable).
        foreach (var (footprint, bottom, top) in soup.Solids)
            if (top - mesh.CellHeight > bottom - mesh.CellHeight)
                geom.AddConvexVolume(footprint, bottom - mesh.CellHeight, top - mesh.CellHeight, new RcAreaModification(0));
        var partition = mesh.SamplePartitionType switch
        {
            NavigationMeshPartition.Monotone => RcPartition.MONOTONE,
            NavigationMeshPartition.Layers => RcPartition.LAYERS,
            _ => RcPartition.WATERSHED,
        };
        var config = new RcConfig(partition, mesh.CellSize, mesh.CellHeight, mesh.AgentMaxSlope, mesh.AgentHeight,
            mesh.AgentRadius, mesh.AgentMaxClimb, (int)mesh.RegionMinSize, (int)mesh.RegionMergeSize, mesh.EdgeMaxLength,
            mesh.EdgeMaxError, nvp, mesh.DetailSampleDistance, mesh.DetailSampleMaxError, mesh.FilterLowHangingObstacles,
            mesh.FilterLedgeSpans, mesh.FilterWalkableLowHeightSpans, new RcAreaModification(0x3f), true);
        var min = geom.GetMeshBoundsMin();
        var max = geom.GetMeshBoundsMax();
        var filter = mesh.FilterBakingAabb;
        if (filter.Size.X > 0f && filter.Size.Y > 0f && filter.Size.Z > 0f)
        {
            min = RcVec3f.Max(min, new RcVec3f(filter.Min.X, filter.Min.Y, filter.Min.Z));
            max = RcVec3f.Min(max, new RcVec3f(filter.Max.X, filter.Max.Y, filter.Max.Z));
            if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            {
                error = "the baking AABB holds no geometry";
                return false;
            }
        }

        RcBuilderResult result;
        try
        {
            result = new RcBuilder().Build(geom, new RcBuilderConfig(config, min, max), false);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            error = $"Recast failed: {e.Message}";
            return false;
        }

        var poly = result.Mesh;
        var detail = result.MeshDetail;
        if (poly is null || poly.npolys == 0)
        {
            error = "nothing is walkable for these agent settings";
            return false;
        }

        // Godot's API view: vertices in metres and each polygon's vertex list.
        vertices = new Vector3[poly.nverts];
        for (var i = 0; i < poly.nverts; i++)
            vertices[i] = new Vector3(poly.bmin.X + poly.verts[i * 3] * poly.cs, poly.bmin.Y + poly.verts[i * 3 + 1] * poly.ch,
                poly.bmin.Z + poly.verts[i * 3 + 2] * poly.cs);
        polygons = new int[poly.npolys][];
        for (var p = 0; p < poly.npolys; p++)
        {
            var count = 0;
            while (count < poly.nvp && poly.polys[p * 2 * poly.nvp + count] != RcRecast.RC_MESH_NULL_IDX)
                count++;
            var indices = new int[count];
            for (var k = 0; k < count; k++)
                indices[k] = poly.polys[p * 2 * poly.nvp + k];
            polygons[p] = indices;
            poly.flags[p] = 1;
        }

        var parameters = new DtNavMeshCreateParams
        {
            verts = poly.verts, vertCount = poly.nverts, polys = poly.polys, polyAreas = poly.areas, polyFlags = poly.flags,
            polyCount = poly.npolys, nvp = poly.nvp, detailMeshes = detail?.meshes, detailVerts = detail?.verts,
            detailVertsCount = detail?.nverts ?? 0, detailTris = detail?.tris, detailTriCount = detail?.ntris ?? 0,
            walkableHeight = mesh.AgentHeight, walkableRadius = mesh.AgentRadius, walkableClimb = mesh.AgentMaxClimb,
            bmin = poly.bmin, bmax = poly.bmax, cs = config.Cs, ch = config.Ch, buildBvTree = true,
        };
        var data = DtNavMeshBuilder.CreateNavMeshData(parameters);
        if (data is null)
        {
            error = "Detour could not build the tile (too many vertices for one tile?)";
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, true);
        detourData = Prefix + Convert.ToBase64String(stream.ToArray());
        error = null;
        return true;
    }

    /// <summary>Reads <see cref="NavigationMesh.DetourData"/> back into a Detour tile (null when empty or corrupt).</summary>
    public static DtMeshData? Read(string detourData, int maxVertsPerPoly, out string? error)
    {
        error = null;
        if (detourData.Length == 0)
            return null;
        if (!detourData.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "the Detour data has no dtmd1: prefix";
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(detourData[Prefix.Length..]);
            var buffer = new RcByteBuffer(bytes);
            buffer.Order(RcByteOrder.LITTLE_ENDIAN);
            return new DtMeshDataReader().Read(buffer, maxVertsPerPoly);
        }
        catch (Exception e) when (e is FormatException or IOException or IndexOutOfRangeException or ArgumentException)
        {
            error = $"the Detour data is corrupt ({e.Message})";
            return null;
        }
    }

    private static void Parse(NavigationRegion3D region, NavigationMesh mesh, TriangleSoup soup)
    {
        var toLocal = region.GlobalTransform.AffineInverse();
        var meshes = mesh.ParsedGeometryType != NavigationParsedGeometryType.StaticColliders;
        var colliders = mesh.ParsedGeometryType != NavigationParsedGeometryType.MeshInstances;
        void Visit(Node node, bool recurse)
        {
            switch (node)
            {
                case MeshInstance3D { Mesh: { } m } instance when meshes:
                    soup.AddMesh(m, toLocal * instance.GlobalTransform);
                    break;
                case StaticBody3D body when colliders && (body.CollisionLayer & mesh.CollisionMask) != 0:
                    foreach (var child in body.Children)
                        if (child is CollisionShape3D { Disabled: false, Shape: { } shape } owner)
                            soup.AddShape(shape, toLocal * owner.GlobalTransform);
                    break;
            }

            if (!recurse)
                return;
            foreach (var child in node.Children)
                Visit(child, true);
        }

        if (mesh.SourceGeometryMode == NavigationSourceGeometryMode.RootNodeChildren || region.Tree is not { } tree)
        {
            foreach (var child in region.Children)
                Visit(child, true);
            return;
        }

        foreach (var node in tree.GetNodesInGroup(mesh.SourceGroupName))
            Visit(node, mesh.SourceGeometryMode == NavigationSourceGeometryMode.GroupsWithChildren);
    }

    // Triangles in the region's local space, as Recast's flat arrays.
    private sealed class TriangleSoup
    {
        public readonly List<float> Vertices = [];
        public readonly List<int> Indices = [];
        public readonly List<(float[] Footprint, float Bottom, float Top)> Solids = [];

        public int TriangleCount => Indices.Count / 3;

        private int Vertex(Vector3 v)
        {
            Vertices.Add(v.X);
            Vertices.Add(v.Y);
            Vertices.Add(v.Z);
            return Vertices.Count / 3 - 1;
        }

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, in Transform3D t)
        {
            Indices.Add(Vertex(t.TransformPoint(a)));
            Indices.Add(Vertex(t.TransformPoint(b)));
            Indices.Add(Vertex(t.TransformPoint(c)));
        }

        public void AddMesh(Mesh mesh, in Transform3D t)
        {
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                var positions = surface.Positions;
                var indices = surface.Indices;
                if (indices.Length >= 3)
                {
                    for (var i = 0; i + 2 < indices.Length; i += 3)
                        if (indices[i] < positions.Length && indices[i + 1] < positions.Length && indices[i + 2] < positions.Length)
                            Triangle(positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]], t);
                }
                else
                {
                    for (var i = 0; i + 2 < positions.Length; i += 3)
                        Triangle(positions[i], positions[i + 1], positions[i + 2], t);
                }
            }
        }

        public void AddShape(Shape3D shape, in Transform3D t)
        {
            switch (shape)
            {
                case BoxShape3D box:
                    Box(box.Size * 0.5f, t);
                    break;
                case SphereShape3D sphere:
                    Capsule(sphere.Radius, 0f, t);
                    Solid(t, sphere.Radius, 0f, 0f);
                    break;
                case CapsuleShape3D capsule:
                {
                    var half = MathF.Max(0f, capsule.Height * 0.5f - capsule.Radius);
                    Capsule(capsule.Radius, half, t);
                    Solid(t, capsule.Radius, half, half);
                    break;
                }
                case CylinderShape3D cylinder:
                    Cylinder(cylinder.Radius, cylinder.Height * 0.5f, t);
                    Solid(t, cylinder.Radius, cylinder.Height * 0.5f, cylinder.Height * 0.5f);
                    break;
                case ConvexPolygonShape3D convex:
                    Hull(convex.Points, t);
                    break;
                case ConcavePolygonShape3D concave:
                {
                    var faces = concave.Faces;
                    for (var i = 0; i + 2 < faces.Length; i += 3)
                        Triangle(faces[i], faces[i + 1], faces[i + 2], t);
                    break;
                }
                case HeightMapShape3D map:
                {
                    var faces = map.GetFaces();
                    for (var i = 0; i + 2 < faces.Length; i += 3)
                        Triangle(faces[i], faces[i + 1], faces[i + 2], t);
                    break;
                }
            }
        }

        private void Box(Vector3 h, in Transform3D t)
        {
            Span<Vector3> c =
            [
                new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z),
                new(-h.X, h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z),
            ];
            // The solid inside: up to the lowest corner of the face that looks most upwards (a flat top, or a ramp's surface).
            Span<Vector3> world = stackalloc Vector3[8];
            for (var i = 0; i < 8; i++)
                world[i] = t.TransformPoint(c[i]);
            ReadOnlySpan<int> faces = [4, 7, 6, 5, 0, 1, 2, 3, 3, 2, 6, 7, 1, 0, 4, 5, 2, 1, 5, 6, 0, 3, 7, 4];
            var bestUp = float.NegativeInfinity;
            var top = 0f;
            for (var f = 0; f < 6; f++)
            {
                var a = world[faces[f * 4]];
                var up = Vector3.Normalize(Vector3.Cross(world[faces[f * 4 + 1]] - a, world[faces[f * 4 + 2]] - a)).Y;
                if (up <= bestUp)
                    continue;
                bestUp = up;
                top = MathF.Min(MathF.Min(a.Y, world[faces[f * 4 + 1]].Y), MathF.Min(world[faces[f * 4 + 2]].Y, world[faces[f * 4 + 3]].Y));
            }

            AddSolid(world, top);
            // Each face counter-clockwise seen from outside.
            Quad(c[4], c[7], c[6], c[5], t); // top
            Quad(c[0], c[1], c[2], c[3], t); // bottom
            Quad(c[3], c[2], c[6], c[7], t); // +z
            Quad(c[1], c[0], c[4], c[5], t); // -z
            Quad(c[2], c[1], c[5], c[6], t); // +x
            Quad(c[0], c[3], c[7], c[4], t); // -x
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, in Transform3D t)
        {
            Triangle(a, b, c, t);
            Triangle(a, c, d, t);
        }

        // A round solid along its local Y: its footprint from rings round the axis; its lowest top is `topRise` above the
        // centre when it stands upright (a cylinder's cap, a capsule's upper equator), else the centre.
        private void Solid(in Transform3D t, float radius, float half, float topRise)
        {
            Span<Vector3> points = stackalloc Vector3[RingSegments * 2 + 2];
            var n = 0;
            for (var s = 0; s < RingSegments; s++)
            {
                var theta = MathF.Tau * s / RingSegments;
                var p = new Vector3(MathF.Cos(theta) * radius, 0f, MathF.Sin(theta) * radius);
                points[n++] = t.TransformPoint(p + new Vector3(0f, half, 0f));
                points[n++] = t.TransformPoint(p - new Vector3(0f, half, 0f));
            }

            points[n++] = t.TransformPoint(new Vector3(0f, half + radius, 0f));
            points[n++] = t.TransformPoint(new Vector3(0f, -half - radius, 0f));
            var axis = Vector3.Normalize(t.TransformDirection(Vector3.UnitY));
            var centre = t.TransformPoint(Vector3.Zero).Y;
            AddSolid(points[..n], axis.Y > 0.9f ? t.TransformPoint(new Vector3(0f, topRise, 0f)).Y : centre);
        }

        // The footprint (the XZ convex hull of the points, as Recast's x, y, z triplets) and height range of a solid.
        private void AddSolid(ReadOnlySpan<Vector3> points, float top)
        {
            var bottom = float.PositiveInfinity;
            var flat = new List<Vector2>(points.Length);
            foreach (var p in points)
            {
                bottom = MathF.Min(bottom, p.Y);
                flat.Add(new Vector2(p.X, p.Z));
            }

            var hull = Hull2D(flat);
            if (hull.Count < 3 || !(top > bottom))
                return;
            var footprint = new float[hull.Count * 3];
            for (var i = 0; i < hull.Count; i++)
            {
                footprint[i * 3] = hull[i].X;
                footprint[i * 3 + 1] = bottom;
                footprint[i * 3 + 2] = hull[i].Y;
            }

            Solids.Add((footprint, bottom, top));
        }

        // Andrew's monotone chain (sorted by x, then z): the convex hull, counter-clockwise.
        private static List<Vector2> Hull2D(List<Vector2> points)
        {
            points.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            var hull = new List<Vector2>(points.Count + 1);
            for (var pass = 0; pass < 2; pass++)
            {
                var start = hull.Count;
                for (var k = 0; k < points.Count; k++)
                {
                    var p = points[pass == 0 ? k : points.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        // A sphere (half = 0) or capsule along Y: rings of latitude, the upper hemisphere raised by `half`.
        private void Capsule(float radius, float half, in Transform3D t)
        {
            const int rings = 8;
            Span<Vector3> previous = stackalloc Vector3[RingSegments];
            Span<Vector3> current = stackalloc Vector3[RingSegments];
            for (var r = 0; r <= rings; r++)
            {
                var phi = MathF.PI * r / rings;
                var y = MathF.Cos(phi) * radius + (r <= rings / 2 ? half : -half);
                var ring = MathF.Sin(phi) * radius;
                for (var s = 0; s < RingSegments; s++)
                {
                    var theta = MathF.Tau * s / RingSegments;
                    current[s] = new Vector3(MathF.Cos(theta) * ring, y, MathF.Sin(theta) * ring);
                }

                if (r > 0)
                    Band(previous, current, t);
                if (r == rings / 2 && half > 0f)
                {
                    // The straight middle: the same ring lowered by 2·half.
                    current.CopyTo(previous);
                    for (var s = 0; s < RingSegments; s++)
                        current[s] -= new Vector3(0f, 2f * half, 0f);
                    Band(previous, current, t);
                }

                current.CopyTo(previous);
            }
        }

        private void Cylinder(float radius, float half, in Transform3D t)
        {
            Span<Vector3> top = stackalloc Vector3[RingSegments];
            Span<Vector3> bottom = stackalloc Vector3[RingSegments];
            for (var s = 0; s < RingSegments; s++)
            {
                var theta = MathF.Tau * s / RingSegments;
                top[s] = new Vector3(MathF.Cos(theta) * radius, half, MathF.Sin(theta) * radius);
                bottom[s] = top[s] with { Y = -half };
            }

            Band(top, bottom, t);
            for (var s = 1; s + 1 < RingSegments; s++)
            {
                Triangle(top[0], top[s + 1], top[s], t);       // up
                Triangle(bottom[0], bottom[s], bottom[s + 1], t); // down
            }
        }

        // Quads between two rings (upper then lower), facing outwards.
        private void Band(ReadOnlySpan<Vector3> upper, ReadOnlySpan<Vector3> lower, in Transform3D t)
        {
            for (var s = 0; s < RingSegments; s++)
            {
                var n = (s + 1) % RingSegments;
                Triangle(upper[s], upper[n], lower[n], t);
                Triangle(upper[s], lower[n], lower[s], t);
            }
        }

        // A convex hull by brute force (a face for every triple with all points on its inner side): fine for the few points
        // a collision hull has.
        private void Hull(ReadOnlySpan<Vector3> points, in Transform3D t)
        {
            var n = points.Length;
            if (n < 4 || n > 128)
                return;
            for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            for (var k = j + 1; k < n; k++)
            {
                var normal = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
                if (normal.LengthSquared() < 1e-12f)
                    continue;
                bool above = false, below = false;
                for (var m = 0; m < n && !(above && below); m++)
                {
                    var d = Vector3.Dot(normal, points[m] - points[i]);
                    if (d > 1e-5f)
                        above = true;
                    else if (d < -1e-5f)
                        below = true;
                }

                if (above && below)
                    continue;
                if (above)
                    Triangle(points[i], points[k], points[j], t); // flip so the normal points out
                else
                    Triangle(points[i], points[j], points[k], t);
            }
        }
    }
}

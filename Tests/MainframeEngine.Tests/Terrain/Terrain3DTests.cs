using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>A <see cref="Terrain3D"/> in a headless tree with single-threaded physics.</summary>
public sealed class Terrain3DTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private Terrain3D AddTerrain(Vector3 position = default, int size = 32, float spacing = 1f, int chunk = 16)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, size, spacing, chunk);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        var terrain = _h.Add(new Terrain3D { Name = "Terrain", Data = data, Position = position });
        terrain.Owner = _h.Scene;
        return terrain;
    }

    private static IEnumerable<TerrainChunk3D> Chunks(Terrain3D terrain) => terrain.Children.OfType<TerrainChunk3D>();

    [Fact]
    public void ChunksAreInternalUnsavedChildrenWithLods()
    {
        var terrain = AddTerrain();
        Assert.Equal(2, terrain.ChunksPerSide);
        Assert.Equal(4, terrain.LodLevels); // 16 quads: every 1st, 2nd, 4th, 8th vertex
        Assert.Equal(16, Chunks(terrain).Count());
        Assert.Equal(4, terrain.Children.OfType<StaticBody3D>().Count());
        Assert.All(terrain.Children, child => Assert.Null(child.Owner));
        Assert.Equal(4, Chunks(terrain).Count(c => c.Visible)); // one level per chunk

        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_h.Scene));
        Assert.DoesNotContain("TerrainChunk3D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticBody3D", json, StringComparison.Ordinal);
        Assert.Contains("Terrain3D", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkMeshesUseChunkLocalPositionsAndTerrainUvs()
    {
        var terrain = AddTerrain();
        var chunk = terrain.GetChunkNode(1, 1, 0);
        Assert.Equal(new Vector3(16, 0, 16), chunk.Position);
        var surface = chunk.Mesh!.GetSurface(0);
        Assert.Equal(new Vector3(0, terrain.Data!.BedHeights[16 * 33 + 16], 0), surface.Positions[0]);
        Assert.Equal(new Vector2(0.5f, 0.5f), surface.UVs[0]);
        Assert.Equal(new Vector2(1f, 1f), surface.UVs[16 * 17 + 16]);
        Assert.Equal(terrain.Data.Grid.VertexNormal(terrain.Data.BedHeights, 16, 16), surface.Normals[0]);
        Assert.Same(Terrain3D.DefaultMaterial, chunk.GetRenderMaterial(chunk.Mesh, 0));
        var material = new StandardMaterial3D();
        terrain.Material = material;
        Assert.Same(material, chunk.GetRenderMaterial(chunk.Mesh, 0));
    }

    [Fact]
    public void CollisionFacesEqualTheLodZeroRenderTrianglesBitForBit()
    {
        var terrain = AddTerrain();
        for (var cz = 0; cz < 2; cz++)
            for (var cx = 0; cx < 2; cx++)
            {
                var surface = terrain.GetChunkNode(cx, cz, 0).Mesh!.GetSurface(0);
                var faces = terrain.GetChunkShape(cx, cz)!.Faces;
                Assert.Equal(16 * 16 * 6, faces.Length);
                for (var k = 0; k < faces.Length; k++)
                    Assert.Equal(surface.Positions[surface.Indices[k]], faces[k]);
                Assert.Equal(terrain.GetChunkNode(cx, cz, 0).Position, terrain.GetChunkBody(cx, cz)!.Position);
            }
    }

    [Fact]
    public void HeightAtMatchesTheDrawnTriangles()
    {
        var origin = new Vector3(-10, 3, 4);
        var terrain = AddTerrain(origin);
        var rng = new Random(8);
        for (var n = 0; n < 2000; n++)
        {
            var x = origin.X + (float)rng.NextDouble() * 32;
            var z = origin.Z + (float)rng.NextDouble() * 32;
            Assert.Equal(DrawnHeight(terrain, x, z), terrain.HeightAt(x, z), 1e-4f);
        }
    }

    /// <summary>The height of the LOD 0 render triangle under (x, z), found by testing every grid triangle of its chunk.</summary>
    private static float DrawnHeight(Terrain3D terrain, float x, float z)
    {
        var local = new Vector2(x - terrain.GlobalPosition.X, z - terrain.GlobalPosition.Z);
        var cx = Math.Min((int)(local.X / 16), 1);
        var cz = Math.Min((int)(local.Y / 16), 1);
        var node = terrain.GetChunkNode(cx, cz, 0);
        var surface = node.Mesh!.GetSurface(0);
        var p = local - new Vector2(node.Position.X, node.Position.Z);
        for (var k = 0; k < 16 * 16 * 6; k += 3)
        {
            var a = surface.Positions[surface.Indices[k]];
            var b = surface.Positions[surface.Indices[k + 1]];
            var c = surface.Positions[surface.Indices[k + 2]];
            var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            var w0 = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Z)) / d;
            var w1 = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Z)) / d;
            var w2 = 1 - w0 - w1;
            if (w0 >= -1e-5f && w1 >= -1e-5f && w2 >= -1e-5f)
                return terrain.GlobalPosition.Y + w0 * a.Y + w1 * b.Y + w2 * c.Y;
        }

        throw new InvalidOperationException("no triangle under the point");
    }

    [Fact]
    public void RaycastAgreesWithHeightAtInWorldSpace()
    {
        var origin = new Vector3(5, -2, -7);
        var terrain = AddTerrain(origin);
        var rng = new Random(13);
        var hits = 0;
        for (var n = 0; n < 300; n++)
        {
            var from = origin + new Vector3((float)rng.NextDouble() * 32, 20, (float)rng.NextDouble() * 32);
            var dir = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, -1, (float)rng.NextDouble() - 0.5f));
            if (!terrain.Raycast(from, dir, 100, out var hit))
            {
                // A miss is real: no triangle of the map is hit (brute force).
                var grid = terrain.Data!.Grid;
                for (var j = 0; j < grid.Quads; j++)
                    for (var i = 0; i < grid.Quads; i++)
                        Assert.False(grid.RaycastQuad(terrain.Data.BedHeights, i, j, from - origin, dir, out var t, out _) && t <= 100);
                continue;
            }

            hits++;
            Assert.Equal(terrain.HeightAt(hit.Position.X, hit.Position.Z), hit.Position.Y, 1e-3f);
            Assert.Equal(Vector3.Distance(from, hit.Position), hit.Distance, 1e-3f);
            Assert.True(Vector3.Distance(terrain.NormalAt(hit.Position.X, hit.Position.Z), hit.Normal) < 1e-3f ||
                        IsOnAnEdge(hit.Position - origin));
        }

        Assert.True(hits > 200, $"only {hits} hits");
        Assert.False(terrain.Raycast(origin + new Vector3(10, 20, 10), Vector3.UnitY, 100, out _));

        static bool IsOnAnEdge(Vector3 local) =>
            MathF.Abs(local.X - MathF.Round(local.X)) < 1e-2f || MathF.Abs(local.Z - MathF.Round(local.Z)) < 1e-2f ||
            MathF.Abs(local.X - MathF.Floor(local.X) + local.Z - MathF.Floor(local.Z) - 1) < 1e-2f ||
            MathF.Abs(local.X - MathF.Floor(local.X) - (local.Z - MathF.Floor(local.Z))) < 1e-2f;
    }

    [Fact]
    public void AnEditRebuildsOnlyTheTouchedChunksAndRaisesChanged()
    {
        var terrain = AddTerrain();
        var changes = new List<TerrainChange>();
        terrain.Changed += changes.Add;
        var versions = Chunks(terrain).ToDictionary(c => c, c => c.Mesh!.GetSurface(0).Version);
        var faces = terrain.GetChunkShape(1, 1)!.Faces;

        // Vertices (20..21, 22..23): inside chunk (1, 1) only, one vertex away from its edges.
        terrain.SetHeights(new Rect2I(20, 22, 2, 2), [9f, 9f, 9f, 9f]);

        var change = Assert.Single(changes);
        Assert.Equal(TerrainLayers.Height, change.Layers);
        Assert.Equal(new Rect2I(1, 1, 1, 1), change.Chunks);
        Assert.Equal(new Rect2I(19, 21, 3, 3), change.Cells);
        Assert.False(change.FromUndo);
        foreach (var (chunk, version) in versions)
        {
            var touched = chunk.ChunkX == 1 && chunk.ChunkZ == 1;
            Assert.Equal(touched, chunk.Mesh!.GetSurface(0).Version != version);
        }

        Assert.Equal(terrain.Data!.QuantizeHeight(9f), terrain.HeightAt(20, 22));
        Assert.Same(faces, terrain.GetChunkShape(1, 1)!.Faces); // rewritten in place
        Assert.Contains(faces, f => f == new Vector3(4, terrain.Data.QuantizeHeight(9f), 6));

        // A vertex on the shared edge x = 16 rebuilds both chunks; its normal neighbours too.
        changes.Clear();
        terrain.SetHeights(new Rect2I(16, 3, 1, 1), [0f]);
        Assert.Equal(new Rect2I(0, 0, 2, 1), Assert.Single(changes).Chunks);

        // Paint changes no mesh.
        changes.Clear();
        versions = Chunks(terrain).ToDictionary(c => c, c => c.Mesh!.GetSurface(0).Version);
        terrain.PaintSurface(new Vector2(25, 25), 2, 3);
        Assert.Equal(TerrainLayers.Surface, Assert.Single(changes).Layers);
        Assert.Equal(new Rect2I(1, 1, 1, 1), changes[0].Chunks);
        Assert.All(versions, kv => Assert.Equal(kv.Value, kv.Key.Mesh!.GetSurface(0).Version));
        Assert.Equal(3, terrain.SurfaceAt(25, 25));
    }

    [Fact]
    public void EditingTheDataDirectlyAlsoRebuilds()
    {
        var terrain = AddTerrain();
        var raised = 0;
        terrain.Changed += _ => raised++;
        terrain.Data!.SetHeightsFrom((_, _) => 1f);
        Assert.Equal(1, raised);
        Assert.Equal(terrain.Data.QuantizeHeight(1f), terrain.GetChunkNode(0, 0, 2).Mesh!.GetSurface(0).Positions[5].Y);
    }

    [Fact]
    public void LodLevelsAreTheirRealErrorsAndSelectionFollowsTheCamera()
    {
        var terrain = AddTerrain();
        var data = terrain.Data!;
        for (var level = 1; level < terrain.LodLevels; level++)
        {
            // Error recomputed independently: the coarse level's triangles sampled at every LOD 0 vertex.
            var step = 1 << level;
            var expected = 0f;
            var grid = new TerrainGrid(16 / step * 2, step, TerrainDiagonal.Checkerboard);
            var coarse = new float[(grid.Quads + 1) * (grid.Quads + 1)];
            for (var j = 0; j <= grid.Quads; j++)
                for (var i = 0; i <= grid.Quads; i++)
                    coarse[j * (grid.Quads + 1) + i] = data.BedHeights[j * step * 33 + i * step];
            for (var j = 0; j <= 16; j++)
                for (var i = 0; i <= 16; i++)
                    expected = MathF.Max(expected, MathF.Abs(data.BedHeights[j * 33 + i] - grid.HeightAt(coarse, i, j)));
            Assert.Equal(expected, terrain.GetChunkLodError(0, 0, level), 1e-4f);
            Assert.True(expected > 0);
        }

        terrain.UpdateLods(new Vector3(8, 10, 8)); // over chunk (0, 0)
        Assert.Equal(0, terrain.GetChunkLod(0, 0));
        terrain.UpdateLods(new Vector3(8, 10, 1e6f)); // very far away
        Assert.Equal(terrain.LodLevels - 1, terrain.GetChunkLod(0, 0));
        Assert.True(terrain.GetChunkNode(0, 0, terrain.LodLevels - 1).Visible);
        Assert.False(terrain.GetChunkNode(0, 0, 0).Visible);

        // A higher bias keeps finer levels further away.
        var distance = terrain.GetChunkLodError(0, 0, 1) * Terrain3D.LodPixelsPerRadian * 1.5f;
        terrain.UpdateLods(new Vector3(8, 10, 16 + distance));
        var coarseAtBias1 = terrain.GetChunkLod(0, 1);
        terrain.LodBias = 4f;
        terrain.UpdateLods(new Vector3(8, 10, 16 + distance));
        Assert.True(terrain.GetChunkLod(0, 1) < coarseAtBias1 || coarseAtBias1 == 0);
    }

    [Fact]
    public void EveryLevelHasASkirtBelowItsInnerEdges()
    {
        var terrain = AddTerrain();
        var depth = 1f; // one quad below the worst level
        for (var level = 1; level < terrain.LodLevels; level++)
            depth = MathF.Max(depth, terrain.GetChunkLodError(1, 1, level) + 1f);
        for (var level = 0; level < terrain.LodLevels; level++)
        {
            // Chunk (1, 1) of 2 × 2: edges 0 (z = 16) and 2 (x = 16) face neighbours, 1 and 3 are the map border.
            var surface = terrain.GetChunkNode(1, 1, level).Mesh!.GetSurface(0);
            var n = (16 >> level) + 1;
            Assert.Equal(n * n + 4 * n, surface.VertexCount);
            Assert.Equal((n - 1) * (n - 1) * 6 + 4 * (n - 1) * 12, surface.IndexCount);
            for (var e = 0; e < n; e++)
            {
                Assert.Equal(surface.Positions[e] - new Vector3(0, depth, 0), surface.Positions[n * n + e]);                    // edge 0
                Assert.Equal(surface.Positions[(n - 1) * n + e], surface.Positions[n * n + n + e]);                              // edge 1: border
                Assert.Equal(surface.Positions[e * n] - new Vector3(0, depth, 0), surface.Positions[n * n + 2 * n + e]);        // edge 2
                Assert.Equal(surface.Positions[e * n + n - 1], surface.Positions[n * n + 3 * n + e]);                           // edge 3: border
                Assert.Equal(surface.Normals[e], surface.Normals[n * n + e]);
            }
        }
    }

    [Fact]
    public void UndoAndRedoRestoreTheLayersBitExact()
    {
        var terrain = AddTerrain();
        var before = terrain.Data!.Heights.ToArray();
        var faces = terrain.GetChunkShape(0, 0)!.Faces.ToArray();

        terrain.Edit.Begin(TerrainLayers.Height | TerrainLayers.Surface);
        terrain.SetHeights(new Rect2I(2, 2, 3, 1), [5f, 6f, 7f]);
        terrain.SetHeights(new Rect2I(15, 15, 2, 2), [1f, 1f, 1f, 1f]); // the corner of all four chunks
        Assert.Equal(faces, terrain.GetChunkShape(0, 0)!.Faces); // collision waits for End
        terrain.PaintSurface(new Vector2(3, 3), 1.5f, 2);
        var record = terrain.Edit.End();
        Assert.False(terrain.Edit.IsActive);
        Assert.NotEqual(faces, terrain.GetChunkShape(0, 0)!.Faces);
        Assert.Equal(4, record.Tiles.Count(t => t.Layer == TerrainLayers.Height));
        Assert.Equal(2, record.Tiles.Count(t => t.Layer == TerrainLayers.Surface)); // splat 0 and 1 of chunk (0, 0)
        var after = terrain.Data.Heights.ToArray();

        var undoChanges = new List<TerrainChange>();
        terrain.Changed += undoChanges.Add;
        record.Undo();
        Assert.Equal(before, terrain.Data.Heights.ToArray());
        Assert.Equal(faces, terrain.GetChunkShape(0, 0)!.Faces);
        Assert.Equal(0, terrain.SurfaceAt(3, 3));
        Assert.All(undoChanges, c => Assert.True(c.FromUndo));

        record.Redo();
        Assert.Equal(after, terrain.Data.Heights.ToArray());
        Assert.Equal(2, terrain.SurfaceAt(3, 3));
    }

    [Fact]
    public void ReplacingDataRebuildsAndRemovingItFreesTheChunks()
    {
        var terrain = AddTerrain();
        var old = Chunks(terrain).First();
        terrain.Data = TerrainData.Create(TerrainProfile.Realistic, 64, 1f, 16);
        Assert.Equal(4, terrain.ChunksPerSide);
        Assert.Equal(64, Chunks(terrain).Count());
        Assert.False(old.IsInsideTree);
        terrain.Data = null;
        Assert.Empty(terrain.Children);
    }

    [Fact]
    public void FacetedTerrainsBuildOneLevelWithoutSkirts()
    {
        var data = TerrainData.Create(TerrainProfile.Faceted, 64, 2f);
        var terrain = _h.Add(new Terrain3D { Data = data });
        Assert.Equal(1, terrain.LodLevels);
        var surface = terrain.GetChunkNode(0, 0, 0).Mesh!.GetSurface(0);
        Assert.Equal(17 * 17, surface.VertexCount);
        Assert.Equal(16 * 16 * 6, surface.IndexCount);
        terrain.PaintSurface(new Vector2(10.5f, 10.5f), 0.2f, 7);
        Assert.Equal(7, terrain.SurfaceAt(10.5f, 10.5f));
    }

    [Fact]
    public void WaterDepthIsInterpolatedAndTheSurfaceIsHeightPlusDepth()
    {
        var terrain = AddTerrain();
        terrain.SetHeightsFrom((_, _) => 4f);
        terrain.SetWaterDepthFrom((x, _) => x <= 10 ? 1.2f : 0f);
        Assert.Equal(1.2f, terrain.WaterDepthAt(5, 5), 1e-4f);
        Assert.Equal(0f, terrain.WaterDepthAt(20, 5));
        Assert.Equal(0.6f, terrain.WaterDepthAt(10.5f, 5.0f), 1e-4f);
        Assert.Equal(terrain.Data!.QuantizeHeight(4f), terrain.HeightAt(5, 5) + terrain.WaterDepthAt(5, 5), 1e-4f);
        Assert.Equal(Terrain3D.WaterSurface, terrain.SurfaceAt(5, 5));
        Assert.Equal(0, terrain.SurfaceAt(5, 5, ignoreWater: true));
        Assert.Equal(0, terrain.SurfaceAt(20, 5));
    }

    [Fact]
    public void ACharacterCapsuleStandsExactlyOnTheTerrain()
    {
        var terrain = AddTerrain();
        var character = _h.Add(new WalkerCharacter3D { Name = "Walker", Position = new Vector3(13.3f, 20, 9.6f) });
        character.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        _h.RunSeconds(3f);

        Assert.True(character.IsOnFloor());
        // The capsule's lower sphere touches the drawn surface: its distance to the terrain is the radius plus the
        // character's 1 cm safe margin.
        var center = character.GlobalPosition - new Vector3(0, 0.5f, 0);
        var gap = DistanceToSurface(terrain, center) - 0.4f;
        Assert.InRange(gap, 0f, 0.025f);
    }

    /// <summary>The distance from <paramref name="p"/> to the terrain surface, sampled on a 5 mm grid within 1 m.</summary>
    private static float DistanceToSurface(Terrain3D terrain, Vector3 p)
    {
        var best = float.MaxValue;
        for (var dz = -1f; dz <= 1f; dz += 0.005f)
            for (var dx = -1f; dx <= 1f; dx += 0.005f)
            {
                var x = p.X + dx;
                var z = p.Z + dz;
                best = MathF.Min(best, Vector3.Distance(p, new Vector3(x, terrain.HeightAt(x, z), z)));
            }

        return best;
    }

    [Fact]
    public void ASphereRestsAtHeightPlusRadius()
    {
        var terrain = AddTerrain();
        terrain.SetHeightsFrom((x, z) => 2f + 0.01f * x);
        var ball = _h.Add(new RigidBody3D { Name = "Ball", Position = new Vector3(20, 8, 20) });
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        _h.RunSeconds(4f);
        Assert.InRange(DistanceToSurface(terrain, ball.GlobalPosition) - 0.5f, -0.02f, 0.02f);
    }

    [Fact]
    public void PhysicsRaysHitTheTerrainWhereRaycastDoes()
    {
        var terrain = AddTerrain();
        _h.Run(2);
        var from = new Vector3(11.2f, 30, 7.9f);
        Assert.True(terrain.Raycast(from, -Vector3.UnitY, 100, out var hit));
        Assert.True(_h.Query.RayCast(from, from - new Vector3(0, 100, 0), out var physics));
        Assert.Equal(hit.Position.Y, physics.Position.Y, 1e-3f);
        Assert.Same(terrain.GetChunkBody(0, 0), physics.Collider);
    }
}

using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>Terrain queries, LOD selection and steady-state frames with a character walking on it allocate nothing.</summary>
[Collection(nameof(SerialAllocationGates))]
public sealed class TerrainAllocationTests
{
    [Fact]
    public void RepeatedQueriesAllocateNothing()
    {
        using var h = new PhysicsHarness3D();
        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        data.SetWaterDepthFrom((x, _) => x < 4 ? 0.5f : 0f);
        var terrain = h.Add(new Terrain3D { Data = data, Position = new Vector3(-32, 0, -32) });
        var sink = 0f;

        void Queries()
        {
            for (var n = 0; n < 2000; n++)
            {
                var x = -32 + n * 0.031f;
                var z = -32 + n * 0.017f;
                sink += terrain.HeightAt(x, z) + terrain.NormalAt(x, z).Y + terrain.SmoothNormalAt(x, z).Y;
                sink += terrain.SurfaceAt(x, z) + terrain.LayerWeightAt(0, x, z) + terrain.WaterDepthAt(x, z) + terrain.UserChannelAt(1, x, z);
                if (terrain.Raycast(new Vector3(x, 30, z), new Vector3(0.3f, -1, 0.2f), 200, out var hit))
                    sink += hit.Distance;
                sink += terrain.Contains(x, z) ? 1 : 0;
            }

            for (var n = 0; n < 50; n++)
                terrain.UpdateLods(new Vector3(n * 3f, 10, n * 2f));
        }

        Queries(); // warm up
        Assert.Equal(0, AllocationGate.SmallestWindow(Queries));
        Assert.True(float.IsFinite(sink));
    }

    [Fact]
    public void SteadyStateFramesWithACharacterOnTheTerrainAllocateNothing()
    {
        using var h = new PhysicsHarness3D();
        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 1f, 16);
        data.SetHeightsFrom(TerrainDataTests.Hills);
        h.Add(new Terrain3D { Data = data });
        var camera = h.Add(new Camera3D { Position = new Vector3(10, 20, 10) });
        var walker = new WalkerCharacter3D { Position = new Vector3(20, 15, 20), Walk = new Vector3(0.6f, 0, 0.4f) };
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        h.Add(walker);
        h.Run(120);

        var frame = 0;
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 120; i++, frame++)
            {
                camera.Position = new Vector3(32 + 40 * MathF.Sin(frame * 0.05f), 25, 32 + 40 * MathF.Cos(frame * 0.05f));
                h.Run(1);
            }
        }));
        Assert.True(walker.Moves > 200);
    }
}

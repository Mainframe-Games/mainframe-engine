using System.Diagnostics;
using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// ADR 0202: Godot's <c>Polygon2D</c> (triangulation, UVs, CPU skinning), <c>Skeleton2D</c>/<c>Bone2D</c> and
/// <c>RemoteTransform2D</c>, including bones driven by physics in the same frame.
/// </summary>
public sealed class Polygon2DSkinningTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());

    public void Dispose() => _tree.Shutdown();

    private static Vector2[] Drawn(CanvasItem item)
    {
        var vertices = item.DrawList.Vertices;
        var result = new Vector2[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
            result[i] = vertices[i].Position;
        return result;
    }

    private static void Near(Vector2 expected, Vector2 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector2.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private T Add<T>(T node, Node? parent = null) where T : Node
    {
        (parent ?? _tree.Root).AddChild(node);
        return node;
    }

    [Fact]
    public void ATexturedSquareDrawsTwoTrianglesWithPixelUvsAndVertexColours()
    {
        var polygon = Add(new Polygon2D
        {
            Polygon = [new(0, 0), new(40, 0), new(40, 20), new(0, 20)],
            UV = [new(0, 0), new(64, 0), new(64, 32), new(0, 32)],
            Texture = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]),
            VertexColors = [Colors.Red, Colors.Green, Colors.Blue, Colors.White],
            Offset = new Vector2(5, 5),
        });
        _tree.FlushCanvasRedraws();

        Assert.Equal(6, polygon.DrawList.IndexCount);
        var vertices = polygon.DrawList.Vertices;
        Assert.Equal(new Vector2(5, 5), vertices[0].Position);
        Assert.Equal(new Vector2(45, 25), vertices[2].Position);
        Assert.Equal(new Vector2(1, 1), vertices[2].Uv); // texture pixels → 0..1
        Assert.Equal(new Vector2(1, 0), vertices[1].Uv);
        Assert.Equal((Vector4)Colors.Blue, vertices[2].Color);
    }

    [Fact]
    public void InternalVerticesAreLeftOutOfTheOutlineButPolygonsUseThem()
    {
        // A square and its centre as an internal vertex.
        var polygon = Add(new Polygon2D
        {
            Polygon = [new(0, 0), new(20, 0), new(20, 20), new(0, 20), new(10, 10)],
            InternalVertexCount = 1,
        });
        _tree.FlushCanvasRedraws();
        Assert.Equal(6, polygon.DrawList.IndexCount); // the outline: two triangles, centre unused

        polygon.Polygons = [[0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]];
        _tree.FlushCanvasRedraws();
        Assert.Equal(12, polygon.DrawList.IndexCount); // four fans meeting at the centre
    }

    // A strip from x = 0 to 100 at y 0..10: left points follow bone A, right points bone B, the middle both.
    private (Skeleton2D Skeleton, Bone2D A, Bone2D B, Polygon2D Polygon) Rig(Vector2 polygonPosition, float[]? middleWeightsA = null)
    {
        var skeleton = Add(new Skeleton2D { Name = "Skeleton", Position = new Vector2(200, 100) });
        var a = Add(new Bone2D { Name = "A", Rest = Transform2D.Identity }, skeleton);
        var b = Add(new Bone2D { Name = "B", Position = new Vector2(50, 0), Rest = Transform2D.FromTrs(new Vector2(50, 0), 0, Vector2.One) }, a);
        // The polygon is a sibling of the skeleton, placed so its points sit along the bones in skeleton space.
        var polygon = Add(new Polygon2D
        {
            Name = "Strip",
            Position = polygonPosition,
            Polygon = [new(0, 0), new(50, 0), new(100, 0), new(100, 10), new(50, 10), new(0, 10)],
            Skeleton = "../Skeleton",
        });
        var middle = middleWeightsA ?? [0.5f, 0.5f];
        polygon.AddBone("A", [1, middle[0], 0, 0, middle[0], 1]);
        polygon.AddBone("A/B", [0, middle[1], 1, 1, middle[1], 0]);
        return (skeleton, a, b, polygon);
    }

    [Fact]
    public void AtRestNothingMovesAndTurningABoneMovesTheVerticesWeightedToIt()
    {
        var (skeleton, _, b, polygon) = Rig(new Vector2(200, 95)); // skeleton space: points at y −5..5
        _tree.FlushCanvasRedraws();
        var rest = Drawn(polygon);
        Assert.Equal(polygon.Polygon, rest);

        b.Rotation = MathF.PI / 2; // turn B a quarter about its joint (x = 50 along the strip)
        _tree.FlushCanvasRedraws();
        var bent = Drawn(polygon);
        Near(new Vector2(0, 0), bent[0]); // A only: unchanged
        Near(new Vector2(0, 10), bent[5]);
        // B only: (100, 0) in polygon space is (100, −5) in skeleton space, 50 along B; turned to (50 + 5, 50).
        Near(new Vector2(55, 55), bent[2]);
        Near(new Vector2(45, 55), bent[3]);
        // Half and half: the average of staying and turning.
        Near((new Vector2(50, 0) + new Vector2(55, 5)) / 2, bent[1]);
        Assert.True(skeleton.PoseVersion > 0);
    }

    [Fact]
    public void WeightsAreNormalisedAndOnlyTheFourHeaviestCount()
    {
        // Snake Oil's baked weights sum to 3: they skin as if normalised.
        var (_, _, bTriple, triple) = Rig(new Vector2(200, 95), [1.5f, 1.5f]);
        bTriple.Rotation = 0.7f;
        _tree.FlushCanvasRedraws();
        var tripled = Drawn(triple);
        _tree.Root.RemoveChild(triple);
        _tree.Root.RemoveChild(bTriple.Skeleton!);

        var (_, _, bHalf, half) = Rig(new Vector2(200, 95));
        bHalf.Rotation = 0.7f;
        _tree.FlushCanvasRedraws();
        Near(Drawn(half)[1], tripled[1]);

        // Five bones on one vertex: the lightest is dropped, the other four renormalised; unweighted vertices stay put.
        var skeleton = Add(new Skeleton2D { Name = "Many" });
        for (var i = 0; i < 5; i++)
            Add(new Bone2D { Name = $"B{i}" }, skeleton);
        var polygon = Add(new Polygon2D { Polygon = [new(0, 0), new(10, 0), new(0, 10)], Skeleton = "../Many" });
        for (var i = 0; i < 5; i++)
            polygon.AddBone($"B{i}", [i == 0 ? 0.01f : 1f, 0, 0]);
        skeleton.GetBone(0).Position = new Vector2(1000, 0); // the dropped bone: moving it changes nothing
        for (var i = 1; i < 5; i++)
            skeleton.GetBone(i).Position = new Vector2(8, 0);
        _tree.FlushCanvasRedraws();
        var drawn = Drawn(polygon);
        Near(new Vector2(8, 0), drawn[0]);
        Near(new Vector2(10, 0), drawn[1]);
        Near(new Vector2(0, 10), drawn[2]);
    }

    [Fact]
    public void BonesAreNumberedInTreeOrderAndRestsCompose()
    {
        var skeleton = Add(new Skeleton2D());
        var root = Add(new Bone2D { Name = "Root", Rest = Transform2D.FromTrs(new Vector2(10, 0), 0, Vector2.One) }, skeleton);
        var arm = Add(new Bone2D { Name = "Arm", Position = new Vector2(0, 5), Rotation = MathF.PI / 2, Rest = Transform2D.FromTrs(new Vector2(0, 5), MathF.PI / 2, Vector2.One) }, root);
        var tail = Add(new Bone2D { Name = "Tail" }, skeleton);
        Assert.Equal(3, skeleton.GetBoneCount());
        Assert.Equal([root, arm, tail], Enumerable.Range(0, 3).Select(skeleton.GetBone));
        Assert.Equal(1, arm.GetIndexInSkeleton());
        Near(new Vector2(10, 5), arm.GetSkeletonRest().Origin);
        Assert.Equal(5f, root.Length, 4); // autocalculated: the distance to the first child bone
        Assert.Equal(MathF.PI / 2, root.BoneAngle, 4);

        arm.Position = new Vector2(3, 3);
        arm.ApplyRest();
        Assert.Equal(arm.Rest, arm.Transform);
    }

    [Fact]
    public void RemoteTransformCopiesGlobalOrLocalAndOnlyWhatItIsAskedTo()
    {
        var follower = Add(new Node2D { Name = "Follower", Position = new Vector2(7, 7), Rotation = 0.3f });
        var parent = Add(new Node2D { Name = "Parent", Position = new Vector2(100, 0) });
        var remote = Add(new RemoteTransform2D { Name = "Remote", Position = new Vector2(5, 5), Rotation = 1f, RemotePath = "../../Follower" }, parent);
        _tree.Tick(default); // the push happens in the frame's transform notifications
        Near(new Vector2(105, 5), follower.GlobalPosition);
        Assert.Equal(1f, follower.Rotation, 4);

        remote.UpdateRotation = false;
        follower.Rotation = 0.3f;
        remote.Position = new Vector2(10, 0);
        _tree.Tick(default);
        Near(new Vector2(110, 0), follower.GlobalPosition);
        Assert.Equal(0.3f, follower.Rotation, 4);

        remote.UseGlobalCoordinates = false; // local: copies (10, 0) as the follower's own position
        Near(new Vector2(10, 0), follower.Position);
    }

    [Fact]
    public void JointsAndShapesRoundTripThroughScenes()
    {
        var root = new Node2D { Name = "Root" };
        var skeleton = new Skeleton2D { Name = "Skeleton" };
        var bone = new Bone2D { Name = "Bone", Rest = Transform2D.FromTrs(new Vector2(3, 4), 0.5f, Vector2.One), Length = 30, AutocalculateLengthAndAngle = false, BoneAngle = 0.25f };
        var polygon = new Polygon2D
        {
            Name = "Flesh",
            Polygon = [new(0, 0), new(10, 0), new(10, 10), new(5, 5)],
            UV = [new(0, 0), new(16, 0), new(16, 16), new(8, 8)],
            VertexColors = [Colors.Red, Colors.Red, Colors.Red, Colors.Red],
            Polygons = [[0, 1, 3], [1, 2, 3]],
            InternalVertexCount = 1,
            Skeleton = "../Skeleton",
            TextureScale = new Vector2(2, 2),
        };
        polygon.AddBone("Bone", [1, 0.5f, 0, 0.25f]);
        var remote = new RemoteTransform2D { Name = "Remote", RemotePath = "../Skeleton/Bone", UpdateScale = false };
        root.AddChild(skeleton);
        skeleton.AddChild(bone);
        root.AddChild(polygon);
        root.AddChild(remote);
        foreach (var node in new Node[] { skeleton, bone, polygon, remote })
            node.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var flesh = copy.GetNode<Polygon2D>("Flesh");
        Assert.Equal(polygon.Polygon, flesh.Polygon);
        Assert.Equal(polygon.UV, flesh.UV);
        Assert.Equal(polygon.VertexColors, flesh.VertexColors);
        Assert.Equal([[0, 1, 3], [1, 2, 3]], flesh.Polygons);
        Assert.Equal(1, flesh.InternalVertexCount);
        Assert.Equal("../Skeleton", flesh.Skeleton.ToString());
        Assert.Equal(1, flesh.GetBoneCount());
        Assert.Equal("Bone", flesh.GetBonePath(0).ToString());
        Assert.Equal([1, 0.5f, 0, 0.25f], flesh.GetBoneWeights(0));
        Assert.Equal(new Vector2(2, 2), flesh.TextureScale);
        var boneCopy = copy.GetNode<Bone2D>("Skeleton/Bone");
        Assert.Equal(bone.Rest, boneCopy.Rest);
        Assert.Equal((false, 30f, 0.25f), (boneCopy.AutocalculateLengthAndAngle, boneCopy.Length, boneCopy.BoneAngle));
        var remoteCopy = copy.GetNode<RemoteTransform2D>("Remote");
        Assert.Equal("../Skeleton/Bone", remoteCopy.RemotePath.ToString());
        Assert.False(remoteCopy.UpdateScale);
        root.Free();
        copy.Free();
    }

    [Fact]
    public void SteadySkinnedRedrawsAllocateNothingAndFourBenchPolygonsStayUnderBudget()
    {
        // Snake Oil's prep bench: four soft bodies, ~600 vertices and 25 bones each.
        const int bones = 25, columns = 30, rows = 20;
        var polygons = new List<Polygon2D>();
        var allBones = new List<Bone2D>();
        for (var p = 0; p < 4; p++)
        {
            var skeleton = Add(new Skeleton2D { Name = $"Skeleton{p}", Position = new Vector2(p * 400, 0) });
            for (var b = 0; b < bones; b++)
            {
                var at = new Vector2(b % 5 * 60, b / 5 * 40);
                allBones.Add(Add(new Bone2D { Name = $"B{b}", Position = at, Rest = Transform2D.FromTrs(at, 0, Vector2.One) }, skeleton));
            }

            var points = new Vector2[columns * rows];
            for (var i = 0; i < points.Length; i++)
                points[i] = new Vector2(i % columns * 10, i / columns * 10);
            var polygon = Add(new Polygon2D
            {
                Name = $"Body{p}",
                Position = new Vector2(p * 400, 0),
                Polygon = points,
                InternalVertexCount = points.Length - 4,
                Skeleton = $"../Skeleton{p}",
                Texture = Texture2D.FromPixels(4, 4, new byte[64]),
            });
            // Outline = the first four points (a quad); every vertex weighted to its three nearest bones.
            points[1] = new Vector2(290, 0);
            points[2] = new Vector2(290, 190);
            points[3] = new Vector2(0, 190);
            polygon.Polygon = points;
            var weights = new float[bones][];
            for (var b = 0; b < bones; b++)
                weights[b] = new float[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var nearest = Enumerable.Range(0, bones).OrderBy(b => Vector2.Distance(points[i], new Vector2(b % 5 * 60, b / 5 * 40))).Take(3).ToArray();
                foreach (var b in nearest)
                    weights[b][i] = 1f;
            }

            for (var b = 0; b < bones; b++)
                polygon.AddBone($"B{b}", weights[b]);
            polygons.Add(polygon);
        }

        void Frame(int f)
        {
            foreach (var bone in allBones)
                bone.Rotation = f * 0.01f; // every bone moves: every polygon re-skins
            _tree.FlushCanvasRedraws();
        }

        for (var f = 0; f < 10; f++)
            Frame(f);
        Assert.All(polygons, p => Assert.Equal(columns * rows, p.DrawList.VertexCount));
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var f = 10; f < 40; f++)
                Frame(f);
        }));

        var watch = Stopwatch.StartNew();
        for (var f = 40; f < 140; f++)
            Frame(f);
        var perFrame = watch.Elapsed.TotalMilliseconds / 100;
        TestContext.Current.SendDiagnosticMessage($"4 skinned polygons ({columns * rows} vertices, {bones} bones): {perFrame:F3} ms per frame");
        Assert.True(perFrame < 5, $"{perFrame} ms per frame"); // the budget is 1 ms on the dev Mac (Release); this is a guard
    }
}

/// <summary>Bones driven by rigid bodies through <see cref="RemoteTransform2D"/> show the bodies' pose in the same frame.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class PhysicsDrivenSkinningTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ARigidBodyMovesItsBoneAndTheSkinnedPolygonInTheSameFrame()
    {
        var skeleton = _h.Add(new Skeleton2D { Name = "Skeleton" });
        var bone = new Bone2D { Name = "Bone", Position = new Vector2(0, -500), Rest = Transform2D.FromTrs(new Vector2(0, -500), 0, Vector2.One) };
        skeleton.AddChild(bone);
        var body = _h.AddBox(new Vector2(0, -500), size: 20, name: "Body");
        var remote = new RemoteTransform2D { Name = "Remote" };
        body.AddChild(remote);
        remote.RemotePath = "../../Skeleton/Bone";
        var polygon = _h.Add(new Polygon2D { Name = "Flesh", Polygon = [new(-10, -510), new(10, -510), new(10, -490), new(-10, -490)], Skeleton = "../Skeleton" });
        polygon.AddBone("Bone", [1, 1, 1, 1]);

        for (var frame = 0; frame < 30; frame++)
        {
            _h.Run(1); // physics, interpolation, transform notifications (the remote's push)
            _h.Tree.FlushCanvasRedraws(); // what the canvas server does next in the same frame
            var drawn = polygon.DrawList.Vertices[0].Position;
            var expected = body.GlobalTransform.TransformPoint(new Vector2(-10, -10)); // the body's pose as rendered now
            Assert.True(Vector2.Distance(expected, drawn) < 1e-3f, $"frame {frame}: drawn {drawn}, body corner {expected}");
        }

        Assert.True(body.Position.Y > -480, "the body fell");
    }
}

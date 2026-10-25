using System.Numerics;
using MainframeEngine.Tests.Scene;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Animation;

/// <summary>
/// ADR 0207/0208: the generated Blender-style character imports to a <see cref="Skeleton3D"/>, a skinned
/// <see cref="MeshInstance3D"/> with a <see cref="Skin"/>, and an <see cref="AnimationPlayer"/> with its eight clips (loop
/// modes and events from the glTF extras and the <c>.meta</c>); the clips evaluate and the CPU skinning reference follows.
/// </summary>
/// <remarks>Needs the Assimp native library.</remarks>
[Collection(nameof(SerialResources))]
public sealed class SkinnedImportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-skinned-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private const string ModelPath = "Assets/Models/Character/" + SkinnedCharacter.File;

    public SkinnedImportTests()
    {
        SkinnedCharacter.Write(Path.Combine(_project, "Assets", "Models", "Character"));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Node3D Instantiate()
    {
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        return scene.Instantiate<Node3D>();
    }

    [Fact]
    public void TheCommittedCharacterMatchesTheGenerator()
    {
        if (Environment.GetEnvironmentVariable("UPDATE_TEST_ASSETS") == "1")
            SkinnedCharacter.Write(Path.Combine(TestPaths.RepositoryRoot(), "Tests", "Assets", "Models", "Character"));
        var committed = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "Character");
        var generated = Path.Combine(_project, "Assets", "Models", "Character");
        foreach (var file in new[] { SkinnedCharacter.File, SkinnedCharacter.File + ".meta" })
            Assert.True(File.ReadAllBytes(Path.Combine(committed, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(generated, file))),
                $"{file} differs from the generator: run it with UPDATE_TEST_ASSETS=1 (SkinnedCharacter.Write into Tests/Assets/Models/Character).");
    }

    [Fact]
    public void ACharacterImportsToASkeletonASkinnedMeshAndAPlayerWithEightClips()
    {
        var root = Instantiate();

        var skeleton = root.GetNode<Skeleton3D>("Armature");
        Assert.Equal(SkinnedCharacter.BoneCount, skeleton.GetBoneCount());
        Assert.Equal(0, skeleton.FindBone("Hips"));
        Assert.Equal(-1, skeleton.GetBoneParent(0));
        var hand = skeleton.FindBone(SkinnedCharacter.HandBone);
        Assert.Equal("LowerArm.R", skeleton.GetBoneName(skeleton.GetBoneParent(hand)));
        AssertNear(new Vector3(0, 1, 0), skeleton.GetBoneRest(0).Origin);
        AssertNear(new Vector3(-0.72f, 1.52f, 0), skeleton.GetBoneGlobalRest(hand).Origin); // shoulder 0.06 + 0.12 + 0.28 + 0.26 along −X
        Assert.Equal(1.52f, skeleton.GetBoneGlobalPose(hand).Origin.Y, 3);

        var body = skeleton.GetNode<MeshInstance3D>("Body");
        Assert.Same(skeleton, body.GetSkeleton());
        Assert.Equal(Transform3D.Identity, body.Transform);
        var mesh = Assert.IsType<ArrayMesh>(body.Mesh);
        Assert.Equal(2, mesh.SurfaceCount);
        Assert.True(mesh.IsSkinned);
        var triangles = 0;
        for (var s = 0; s < mesh.SurfaceCount; s++)
        {
            var surface = mesh.GetSurface(s);
            Assert.True(surface.IsSkinned);
            surface.Validate();
            triangles += surface.IndexCount / 3;
            foreach (var w in surface.Weights)
                Assert.Equal(1f, w.X + w.Y + w.Z + w.W, 3);
        }

        Assert.Equal(SkinnedCharacter.TriangleCount, triangles);
        Assert.InRange(triangles, 5000, 7000);
        var skin = Assert.IsType<Skin>(body.Skin);
        Assert.Equal(SkinnedCharacter.BoneCount, skin.BindCount);
        Assert.All(skin.BindNames, name => Assert.True(skeleton.FindBone(name) >= 0, name));

        var player = root.GetNode<AnimationPlayer>("AnimationPlayer");
        var names = new List<string>();
        player.GetAnimationNames(names);
        Assert.Equal(SkinnedCharacter.Clips.Order(), names.Order());
        Assert.Equal(AnimationLoopMode.Linear, player.GetAnimation("run")!.LoopMode);
        Assert.Equal(AnimationLoopMode.Linear, player.GetAnimation("idle")!.LoopMode);
        Assert.Equal(AnimationLoopMode.Linear, player.GetAnimation("craft")!.LoopMode); // from the .meta clips
        Assert.Equal(AnimationLoopMode.None, player.GetAnimation("swing")!.LoopMode);
        Assert.Equal(0.66f, player.GetAnimation("run")!.Length, 2);
        Assert.Equal([("HammerFist", SkinnedCharacter.SwingEventTime)], player.GetAnimation("swing")!.GetEvents().ToArray());
        Assert.Equal(["Step", "Step"], player.GetAnimation("run")!.GetEvents().Select(e => e.Name));
        Assert.Equal([("Tap", 0.5f)], player.GetAnimation("craft")!.GetEvents().ToArray()); // .meta events
        var track = player.GetAnimation("run")!.Tracks.OfType<Rotation3DTrack>().First(t => t.Bone == "UpperLeg.L");
        Assert.Equal("Armature", track.Path.ToString());
    }

    [Fact]
    public void ClipsEvaluateOnTheBonesAndTheSkinFollowsThePose()
    {
        var root = Instantiate();
        var skeleton = root.GetNode<Skeleton3D>("Armature");
        var body = skeleton.GetNode<MeshInstance3D>("Body");
        var player = root.GetNode<AnimationPlayer>("AnimationPlayer");
        var surface = body.Mesh!.GetSurface(0);

        // At rest the palette is the identity: skinned vertices are the bind-pose ones.
        var state = new SkinState();
        Assert.True(state.Update(body.Mesh!, body.Skin, skeleton, Matrix4x4.Identity));
        for (var v = 0; v < surface.VertexCount; v += 97)
            AssertNear(surface.Positions[v], state.SkinPoint(surface.Positions[v], surface.Joints.AsSpan(v * 4, 4), surface.Weights[v]));
        var restBounds = state.Bounds;

        // Swing at its top: the right hand is above the head.
        player.Play("swing");
        player.Seek(0.3, update: true);
        var hand = skeleton.FindBone(SkinnedCharacter.HandBone);
        Assert.True(skeleton.GetBoneGlobalPose(hand).Origin.Y > 1.8f, $"hand at {skeleton.GetBoneGlobalPose(hand).Origin}");
        Assert.True(state.Update(body.Mesh!, body.Skin, skeleton, Matrix4x4.Identity));
        Assert.True(state.Bounds.Max.Y > restBounds.Max.Y + 0.1f, "the posed bounds grow with the raised arm");

        // Every skinned vertex lies inside the posed bounds (they are what culling uses).
        var inside = true;
        for (var s = 0; s < body.Mesh!.SurfaceCount; s++)
        {
            var current = body.Mesh.GetSurface(s);
            for (var v = 0; v < current.VertexCount; v++)
                inside &= Contains(state.Bounds, state.SkinPoint(current.Positions[v], current.Joints.AsSpan(v * 4, 4), current.Weights[v]), 1e-4f);
        }

        Assert.True(inside);
    }

    [Fact]
    public void TheImportScaleScalesTheSkinnedCharacter()
    {
        var root = ModelImporter.ImportNodes(ModelPath, new ModelImportSettings { Scale = 0.5f });
        var skeleton = root.GetNode<Skeleton3D>("Armature");
        var hand = skeleton.FindBone(SkinnedCharacter.HandBone);
        var tree = new SceneTree();
        tree.Root.AddChild(root);
        var world = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(hand);
        Assert.Equal(0.76f, world.Origin.Y, 3);
        tree.Shutdown();
    }

    [Fact]
    public void ClipSettingsRoundTripThroughTheMeta()
    {
        var settings = new ModelImportSettings
        {
            Clips =
            [
                new ModelClipSettings { Name = "run", Loop = AnimationLoopMode.PingPong, Rename = "jog" },
                new ModelClipSettings { Name = "swing", Events = [new ModelClipEvent("Hit", 0.25f)] },
            ],
        };
        var read = ModelImportSettings.FromMeta(new AssetMeta { Importer = "model", Settings = settings.ToMetaSettings() });
        Assert.Equal(settings, read);
        Assert.Equal(AnimationLoopMode.PingPong, read.FindClip("run")!.Loop);
        Assert.Equal("Hit", read.FindClip("swing")!.Events![0].Name);

        var root = ModelImporter.ImportNodes(ModelPath, settings);
        var player = root.GetNode<AnimationPlayer>("AnimationPlayer");
        Assert.Null(player.GetAnimation("run"));
        Assert.Equal(AnimationLoopMode.PingPong, player.GetAnimation("jog")!.LoopMode);
        Assert.Equal([("Hit", 0.25f)], player.GetAnimation("swing")!.GetEvents().ToArray());
    }

    [Fact]
    public void ClipNamesLoseTheArmaturePrefixAndLoopSuffixes()
    {
        Assert.Equal("run", ModelImporter.CleanClipName("Armature|run", 0, out var loop));
        Assert.False(loop);
        Assert.Equal("walk", ModelImporter.CleanClipName("walk-loop", 0, out loop));
        Assert.True(loop);
        Assert.Equal("idle", ModelImporter.CleanClipName("Rig|idle_cycle", 0, out loop));
        Assert.True(loop);
        Assert.Equal("Animation3", ModelImporter.CleanClipName("", 3, out _));
    }

    [Fact]
    public void AnimationsCanBeLeftOut()
    {
        var root = ModelImporter.ImportNodes(ModelPath, new ModelImportSettings { ImportAnimations = false });
        Assert.Null(root.GetNodeOrNull("AnimationPlayer"));
        Assert.NotNull(root.GetNodeOrNull<Skeleton3D>("Armature"));
    }

    private static bool Contains(in Aabb box, Vector3 p, float epsilon) =>
        p.X >= box.Min.X - epsilon && p.Y >= box.Min.Y - epsilon && p.Z >= box.Min.Z - epsilon &&
        p.X <= box.Max.X + epsilon && p.Y <= box.Max.Y + epsilon && p.Z <= box.Max.Z + epsilon;

    private static void AssertNear(Vector3 expected, Vector3 actual, float epsilon = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < epsilon, $"expected {expected}, got {actual}");
}

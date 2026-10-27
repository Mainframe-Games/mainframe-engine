using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>ADR 0207 in the editor: the skeletal animation nodes' configuration warnings and inspector icons.</summary>
public sealed class AnimationNodesTests
{
    [Fact]
    public void BoneAttachmentsAnimationPlayersAndSkinnedMeshesWarnWhenMisconfigured()
    {
        var root = new Node3D { Name = "Root" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("Hand", -1, Transform3D.Identity);
        root.AddChild(skeleton);

        var loose = new BoneAttachment3D { Name = "Loose", BoneName = "Hand" };
        root.AddChild(loose);
        Assert.Contains("child of a Skeleton3D", NodeWarnings.For(loose));
        var wrong = new BoneAttachment3D { Name = "Wrong", BoneName = "Foot" };
        skeleton.AddChild(wrong);
        Assert.Contains("no bone 'Foot'", NodeWarnings.For(wrong));
        var right = new BoneAttachment3D { Name = "Right", BoneName = "Hand" };
        skeleton.AddChild(right);
        Assert.Null(NodeWarnings.For(right));

        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        Assert.Contains("No animations", NodeWarnings.For(player));
        var library = new AnimationLibrary();
        library.Add("wave", new MainframeEngine.Animation());
        player.AddAnimationLibrary("", library);
        Assert.Null(NodeWarnings.For(player));

        // Root motion (ADR 0210): from a skeleton, with a bone it has, on the physics step.
        player.RootMotionTrack = "Loose"; // a BoneAttachment3D
        Assert.Contains("is not a Skeleton3D", NodeWarnings.For(player));
        player.RootMotionTrack = "Skeleton";
        player.RootMotionBone = "Hips";
        Assert.Contains("no bone 'Hips'", NodeWarnings.For(player));
        Assert.Contains("set CallbackModeProcess to Physics", NodeWarnings.For(player));
        player.RootMotionBone = "Hand";
        player.CallbackModeProcess = AnimationCallbackMode.Physics;
        Assert.Null(NodeWarnings.For(player));

        var surface = new MeshSurface([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [0, 1, 2])
        {
            Joints = new int[12],
            Weights = [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX],
        };
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        var orphan = new MeshInstance3D { Name = "Orphan", Mesh = mesh };
        root.AddChild(orphan); // its Skeleton path ("..") is the root, not a skeleton
        Assert.Contains("bind pose", NodeWarnings.For(orphan));
        var bound = new MeshInstance3D { Name = "Bound", Mesh = mesh };
        skeleton.AddChild(bound);
        Assert.Null(NodeWarnings.For(bound));
        root.Dispose();
    }

    [Fact]
    public void AnimationPropertiesHaveSemanticIcons()
    {
        Assert.Equal("bone", PropertyIcons.SemanticIcon("BoneName"));
        Assert.Equal("movie", PropertyIcons.SemanticIcon("Libraries"));
        Assert.Equal("gauge", PropertyIcons.SemanticIcon("SpeedScale"));
        Assert.Equal("repeat", PropertyIcons.SemanticIcon("LoopMode"));
        Assert.Equal("run", PropertyIcons.SemanticIcon("RootMotionTrack"));
        Assert.Equal("bone", PropertyIcons.SemanticIcon("RootMotionBone"));
        Assert.Equal("arrow-up", PropertyIcons.SemanticIcon("RootMotionVertical"));
    }
}

namespace MainframeEngine.Editor;

/// <summary>
/// Configuration warnings of a node (Godot's <c>_get_configuration_warnings</c>), shown as a warning badge on its scene
/// tree row with the text as tooltip: a type that is not loaded, a collision shape outside a collision object or
/// without a shape, a collision object without shapes, a mesh instance without a mesh (or a skinned one without its skeleton),
/// a bone attachment outside a skeleton or naming no bone, an animation player without animations (or with root motion
/// outside the physics callback mode, or from no skeleton), an audio player without a stream, a joint whose paths name no
/// body (ADR 0211).
/// </summary>
public static class NodeWarnings
{
    /// <summary>The warnings of <paramref name="node"/> joined by new lines, or null when there are none.</summary>
    public static string? For(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string? warnings = null;
        void Add(string text) => warnings = warnings is null ? text : warnings + "\n" + text;

        switch (node)
        {
            case MissingNode missing:
                Add($"Type {missing.OriginalType} is not loaded: its properties are kept and written back unchanged.");
                break;
            case CollisionShape3D shape3D:
                if (node.Parent is not CollisionObject3D)
                    Add("A CollisionShape3D only works as a child of a 3D body or area (StaticBody3D, RigidBody3D, CharacterBody3D, Area3D).");
                if (shape3D.Shape is null)
                    Add("No Shape: create one in the inspector.");
                break;
            case CollisionShape2D shape2D:
                if (node.Parent is not CollisionObject2D)
                    Add("A CollisionShape2D only works as a child of a 2D body or area (StaticBody2D, RigidBody2D, CharacterBody2D, Area2D).");
                if (shape2D.Shape is null)
                    Add("No Shape: create one in the inspector.");
                break;
            case CollisionObject3D:
                if (!HasChild<CollisionShape3D>(node))
                    Add("No collision shape: add a CollisionShape3D child so it can collide.");
                break;
            case CollisionObject2D:
                if (!HasChild<CollisionShape2D>(node))
                    Add("No collision shape: add a CollisionShape2D child so it can collide.");
                break;
            case MeshInstance3D { Mesh: null }:
                Add("No Mesh: nothing is drawn.");
                break;
            case MeshInstance3D { Mesh.IsSkinned: true } skinned when skinned.GetSkeleton() is null:
                Add("Skinned mesh without a Skeleton3D at its Skeleton path: drawn in its bind pose.");
                break;
            case BoneAttachment3D attachment:
                if (node.Parent is not Skeleton3D skeleton)
                    Add("A BoneAttachment3D follows a bone only as a child of a Skeleton3D.");
                else if (skeleton.FindBone(attachment.BoneName) < 0)
                    Add(attachment.BoneName.Length == 0 ? "No BoneName: pick the bone to follow." : $"The skeleton has no bone '{attachment.BoneName}'.");
                break;
            case Joint3D joint3D:
                JointPaths(joint3D, joint3D.NodeA, joint3D.NodeB, typeof(PhysicsBody3D), Add);
                break;
            case Joint2D joint2D:
                JointPaths(joint2D, joint2D.NodeA, joint2D.NodeB, typeof(PhysicsBody2D), Add);
                break;
            case AnimationPlayer player:
                if (!HasAnimations(player))
                    Add("No animations: add an AnimationLibrary with animations to Libraries.");
                if (!player.RootMotionTrack.IsEmpty)
                {
                    var root = player.RootNode.IsEmpty ? player : player.GetNodeOrNull(player.RootNode);
                    if (root?.GetNodeOrNull(player.RootMotionTrack) is not Skeleton3D rootSkeleton)
                        Add($"RootMotionTrack '{player.RootMotionTrack}' is not a Skeleton3D: no root motion.");
                    else if (player.RootMotionBone.Length > 0 && rootSkeleton.FindBone(player.RootMotionBone) < 0)
                        Add($"The root motion skeleton has no bone '{player.RootMotionBone}'.");
                    if (player.CallbackModeProcess != AnimationCallbackMode.Physics)
                        Add("Root motion moves a body per physics step: set CallbackModeProcess to Physics.");
                }
                break;
            case AudioPlayer { Stream: null } or AudioPlayer2D { Stream: null } or AudioPlayer3D { Stream: null }:
                Add("No Stream: nothing plays.");
                break;
        }

        return warnings;
    }

    // A joint's paths: at least one set, each naming a physics body, and not the same one.
    private static void JointPaths(Node joint, NodePath a, NodePath b, Type bodyType, Action<string> add)
    {
        if (a.IsEmpty && b.IsEmpty)
        {
            add("NodeA and NodeB are empty: pick the bodies to join (one empty path joins the other body to the world).");
            return;
        }

        var bodyA = JointPath(joint, "NodeA", a, bodyType, add);
        var bodyB = JointPath(joint, "NodeB", b, bodyType, add);
        if (bodyA is not null && ReferenceEquals(bodyA, bodyB))
            add("NodeA and NodeB are the same body: a joint needs two.");
    }

    private static Node? JointPath(Node joint, string name, NodePath path, Type bodyType, Action<string> add)
    {
        if (path.IsEmpty)
            return null;
        switch (joint.GetNodeOrNull(path))
        {
            case null:
                add($"{name} '{path}' does not point at a node.");
                return null;
            case var node when !bodyType.IsInstanceOfType(node):
                add($"{name} '{path}' is a {node.GetType().Name}, not a {bodyType.Name}.");
                return null;
            case var node:
                return node;
        }
    }

    private static bool HasAnimations(AnimationPlayer player)
    {
        foreach (var library in player.Libraries)
            if (library is { Animations.Count: > 0 })
                return true;
        return false;
    }

    private static bool HasChild<T>(Node node)
        where T : Node
    {
        foreach (var child in node.Children)
            if (child is T)
                return true;
        return false;
    }
}

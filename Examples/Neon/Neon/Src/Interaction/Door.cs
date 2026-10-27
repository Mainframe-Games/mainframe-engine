using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// A door that opens and shuts with <c>interact</c>: this node stays where the apartment's model put the door (its
/// socket) and moves its <see cref="Leaf"/> child, which holds the door's meshes and collision box, so the collider moves
/// with the door. The move is eased over <see cref="SwingSeconds"/>; nothing allocates per frame.
/// </summary>
public abstract class DoorBase : Node3D, IInteractable
{
    private Node3D? _leaf;

    /// <summary>How long a full open or shut takes.</summary>
    [Export(Range = "0.05,3,0.05")] public float SwingSeconds { get; set; } = 0.7f;

    /// <summary>True when the door is (or is moving) open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The move, 0 (shut) to 1 (open).</summary>
    public float Openness { get; private set; }

    /// <summary>The moving part (created when first asked for): the door's meshes and its body go under it.</summary>
    public Node3D Leaf
    {
        get
        {
            if (_leaf is null)
            {
                _leaf = GetNodeOrNull<Node3D>("Leaf");
                if (_leaf is null)
                    AddChild(_leaf = new Node3D { Name = "Leaf" });
            }

            return _leaf;
        }
    }

    public string Verb => IsOpen ? Verbs.Close : Verbs.Open;

    public bool CanInteract => true;

    public void Interact() => Toggle();

    /// <summary>Opens a shut door or shuts an open one.</summary>
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>A box collider (on the world and interact layers) over the leaf's meshes, in the leaf's space.</summary>
    public SurfaceBody3D AddBody(Aabb bounds, string surface = "wood")
    {
        var body = new SurfaceBody3D { Name = "Body", Surface = surface, CollisionLayer = NeonLayers.World | NeonLayers.Interact };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.Max(bounds.Size, new Vector3(0.05f)) }, Position = bounds.Center });
        Leaf.AddChild(body);
        return body;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        var target = IsOpen ? 1f : 0f;
        if (Openness == target)
            return;
        var step = gameTime.DeltaTime / MathF.Max(SwingSeconds, 1e-3f);
        Openness = target > Openness ? MathF.Min(Openness + step, 1f) : MathF.Max(Openness - step, 0f);
        Apply(Leaf, SmoothStep(Openness));
    }

    /// <summary>Places the leaf at <paramref name="eased"/> (0 shut, 1 open).</summary>
    protected abstract void Apply(Node3D leaf, float eased);

    internal static float SmoothStep(float t) => t * t * (3f - 2f * t);
}

/// <summary>A hinged door: the node stands on the hinge axis and the leaf turns about its Y.</summary>
public sealed class Door : DoorBase
{
    /// <summary>The leaf's turn when open, in degrees (its sign picks the way the door swings).</summary>
    [Export(Range = "-180,180,1")] public float OpenDegrees { get; set; } = 100f;

    protected override void Apply(Node3D leaf, float eased) => leaf.RotationDegrees = new Vector3(0f, OpenDegrees * eased, 0f);
}

/// <summary>A sliding glass door (the balcony's): the leaf slides along the node's X axis.</summary>
public sealed class SlidingDoor : DoorBase
{
    public SlidingDoor() => SwingSeconds = 0.9f;

    /// <summary>How far the leaf slides when open, metres along X (its sign picks the way).</summary>
    [Export(Range = "-5,5,0.01")] public float SlideDistance { get; set; } = -1.4f;

    protected override void Apply(Node3D leaf, float eased) => leaf.Position = new Vector3(SlideDistance * eased, 0f, 0f);
}

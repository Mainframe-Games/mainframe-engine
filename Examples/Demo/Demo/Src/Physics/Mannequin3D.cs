using MainframeEngine;

namespace Demo;

/// <summary>
/// A capsule mannequin (a <see cref="Ragdoll3D"/>): its parts stand kinematic until a body that is not one of them (a
/// crate) touches one, then it falls as a ragdoll. <see cref="ResetPose"/> stands it up again.
/// </summary>
public sealed class Mannequin3D : Ragdoll3D
{
    private readonly List<RigidBody3D> _parts = [];
    private readonly List<Transform3D> _poses = [];

    /// <summary>How many times it has been knocked down.</summary>
    public int Falls { get; private set; }

    protected override void OnReady()
    {
        base.OnReady();
        GetParts(_parts);
        foreach (var part in _parts)
        {
            _poses.Add(part.Transform);
            part.BodyEntered += OnHit;
        }

        SimulationStarted += () => Falls++;
    }

    /// <summary>Stops the ragdoll and puts every part back where it was authored.</summary>
    public void ResetPose()
    {
        StopSimulation();
        for (var i = 0; i < _parts.Count; i++)
            _parts[i].Teleport(GlobalTransform * _poses[i]);
    }

    private void OnHit(Node3D body)
    {
        if (!Simulating && body is RigidBody3D && body.Parent != this)
            StartSimulation();
    }
}

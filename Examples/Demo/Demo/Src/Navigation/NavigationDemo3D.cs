using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Navigation 3D scene's controller: a left click on the floor sends every chaser to that point, each to its own spot
/// on a ring round it (a ray from the camera through the click, against the physics).
/// </summary>
public sealed class NavigationDemo3D : Node3D
{
    private readonly List<NavChaser3D> _chasers = [];
    private Node3D? _marker;

    /// <summary>The last target (metres, on the floor).</summary>
    public Vector3 Target { get; private set; }

    public IReadOnlyList<NavChaser3D> Chasers => _chasers;

    public NavigationGrid3D? Grid { get; private set; }

    /// <summary>Chasers within reach of their spot.</summary>
    public int Arrived
    {
        get
        {
            var count = 0;
            foreach (var chaser in _chasers)
                if (chaser.Agent is { } agent && agent.IsTargetReached())
                    count++;
            return count;
        }
    }

    protected override void OnReady()
    {
        var scene = Parent!;
        Grid = scene.GetNodeOrNull<NavigationGrid3D>("Grid");
        _marker = scene.GetNodeOrNull<Node3D>("Target");
        if (scene.GetNodeOrNull("Chasers") is { } chasers)
            foreach (var child in chasers.Children)
                if (child is NavChaser3D chaser)
                    _chasers.Add(chaser);
        SetTarget(_marker is { } marker ? new Vector3(marker.GlobalPosition.X, 0f, marker.GlobalPosition.Z) : Vector3.Zero);
    }

    protected override void OnExitTree()
    {
        _chasers.Clear();
        _marker = null;
        Grid = null;
        base.OnExitTree();
    }

    /// <summary>Sends every chaser to <paramref name="target"/>: the first on it, the others on a 0.9 m ring.</summary>
    public void SetTarget(Vector3 target)
    {
        Target = target;
        if (_marker is { } marker)
            marker.GlobalPosition = new Vector3(target.X, 0.05f, target.Z);
        for (var i = 0; i < _chasers.Count; i++)
        {
            if (_chasers[i].Agent is not { } agent)
                continue;
            var offset = Vector3.Zero;
            if (i > 0)
            {
                var angle = (i - 1) * MathF.Tau / (_chasers.Count - 1);
                offset = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * 0.9f;
            }

            agent.TargetPosition = target + offset;
        }
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click
            || GetViewport()?.ActiveCamera3D is not { } camera || GetWorld3D() is not { } world)
            return;
        var pixel = click.Position * (Tree!.Servers.Get<UiServer>()?.PixelScale ?? 1f);
        var from = camera.ProjectRayOrigin(pixel);
        var to = from + camera.ProjectRayNormal(pixel) * 200f;
        if (world.DirectSpaceState.RayCast(from, to, out var hit))
            SetTarget(new Vector3(hit.Position.X, 0f, hit.Position.Z));
    }
}

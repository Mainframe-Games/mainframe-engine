using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Navigation Mesh 2D scene's controller: a left click sends every chaser to the clicked point (each to its own spot
/// round it); <see cref="Rebake"/> bakes both regions again, and <see cref="PondCarved"/> switches whether the pond's
/// obstacle carves the hall's mesh (and re-bakes it). Counts arrivals and ferry crossings.
/// </summary>
public sealed class NavigationMeshDemo2D : Node2D
{
    private readonly List<NavChaser> _chasers = [];
    private NavigationRegion2D? _hall;
    private NavigationRegion2D? _bank;
    private NavigationObstacle2D? _pond;
    private Node2D? _marker;

    /// <summary>The last target (pixels).</summary>
    public Vector2 Target { get; private set; }

    public IReadOnlyList<NavChaser> Chasers => _chasers;

    public NavigationRegion2D? Hall => _hall;

    public NavigationRegion2D? Bank => _bank;

    /// <summary>Ferry crossings so far (the chasers' <see cref="NavigationAgent2D.LinkReached"/> signals).</summary>
    public int Crossings { get; private set; }

    /// <summary>The last bake's time for both regions (milliseconds).</summary>
    public double LastBakeMilliseconds { get; private set; }

    /// <summary>Chasers within reach of the target.</summary>
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

    /// <summary>Whether the pond is carved out of the hall's mesh (off: the chasers wade through it).</summary>
    public bool PondCarved
    {
        get => _pond?.AffectNavigationMesh ?? false;
        set
        {
            if (_pond is not { } pond || pond.AffectNavigationMesh == value)
                return;
            pond.AffectNavigationMesh = value;
            Rebake();
        }
    }

    /// <summary>Every chaser's agents avoid each other (G3.7).</summary>
    public bool Avoidance
    {
        get => _chasers.Count > 0 && _chasers[0].Agent is { AvoidanceEnabled: true };
        set
        {
            foreach (var chaser in _chasers)
                if (chaser.Agent is { } agent)
                    agent.AvoidanceEnabled = value;
        }
    }

    protected override void OnReady()
    {
        var scene = Parent!;
        _hall = scene.GetNodeOrNull<NavigationRegion2D>("Hall");
        _bank = scene.GetNodeOrNull<NavigationRegion2D>("Bank");
        _pond = _hall?.GetNodeOrNull<NavigationObstacle2D>("Pond");
        _marker = scene.GetNodeOrNull<Node2D>("Target");
        if (scene.GetNodeOrNull("Chasers") is { } chasers)
            foreach (var child in chasers.Children)
                if (child is NavChaser chaser)
                {
                    _chasers.Add(chaser);
                    if (chaser.GetNodeOrNull<NavigationAgent2D>("Agent") is { } agent)
                        agent.LinkReached += OnLinkReached;
                }

        SetTarget(_marker?.GlobalPosition ?? Vector2.Zero);
    }

    protected override void OnExitTree()
    {
        foreach (var chaser in _chasers)
            if (chaser.GetNodeOrNull<NavigationAgent2D>("Agent") is { } agent)
                agent.LinkReached -= OnLinkReached;
        _chasers.Clear();
        _hall = _bank = null;
        _pond = null;
        _marker = null;
        base.OnExitTree();
    }

    private void OnLinkReached(NavigationLinkReached2D link) => Crossings++;

    /// <summary>Bakes both regions from the scene again (the time shows in the panel).</summary>
    public void Rebake()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _hall?.Bake();
        _bank?.Bake();
        LastBakeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Sends every chaser to <paramref name="target"/>, each to its own spot (rings of 40 and 70 px).</summary>
    public void SetTarget(Vector2 target)
    {
        Target = target;
        if (_marker is { } marker)
            marker.GlobalPosition = target;
        for (var i = 0; i < _chasers.Count; i++)
            if (_chasers[i].Agent is { } agent)
                agent.TargetPosition = target + Spot(i, _chasers.Count);
    }

    private static Vector2 Spot(int i, int count)
    {
        if (i == 0)
            return Vector2.Zero;
        var inner = Math.Min(5, count - 1);
        var (index, ring, radius) = i <= inner ? (i - 1, inner, 32f) : (i - 1 - inner, count - 1 - inner, 56f);
        var angle = index * MathF.Tau / ring + (radius > 40f ? MathF.PI / ring : 0f);
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click)
            SetTarget(CanvasInput.WindowToCanvas(this, click.Position));
    }
}

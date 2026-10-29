using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Navigation 2D scene's controller: a left click sends every chaser to the clicked point (spread over a small ring so
/// they do not pile up), or with <see cref="AddWalls"/> toggles a wall block on the clicked cell — a static body for the
/// physics and <see cref="NavigationGrid2D.SetCellSolid"/> for the grid, which makes every agent repath at the next sync.
/// The maze's own walls cannot be removed.
/// </summary>
public sealed class NavigationDemo2D : Node2D
{
    private readonly Dictionary<Vector2I, StaticBody2D> _added = [];
    private readonly List<NavChaser> _chasers = [];
    private NavigationGrid2D? _grid;
    private Node2D? _marker;
    private Node? _walls;

    /// <summary>Clicks toggle wall cells instead of moving the target.</summary>
    public bool AddWalls { get; set; }

    /// <summary>The last target (pixels).</summary>
    public Vector2 Target { get; private set; }

    public IReadOnlyList<NavChaser> Chasers => _chasers;

    public NavigationGrid2D? Grid => _grid;

    /// <summary>How many walls clicks added.</summary>
    public int AddedWalls => _added.Count;

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

    protected override void OnReady()
    {
        var scene = Parent!;
        _grid = scene.GetNodeOrNull<NavigationGrid2D>("Grid");
        _marker = scene.GetNodeOrNull<Node2D>("Target");
        _walls = scene.GetNodeOrNull("Walls");
        if (scene.GetNodeOrNull("Chasers") is { } chasers)
            foreach (var child in chasers.Children)
                if (child is NavChaser chaser)
                    _chasers.Add(chaser);
        SetTarget(_marker?.GlobalPosition ?? Vector2.Zero);
    }

    protected override void OnExitTree()
    {
        _chasers.Clear();
        _added.Clear();
        _grid = null;
        _marker = null;
        _walls = null;
        base.OnExitTree();
    }

    /// <summary>Sends every chaser to <paramref name="target"/> (each to its own spot on a 28 px ring around it).</summary>
    public void SetTarget(Vector2 target)
    {
        Target = target;
        if (_marker is { } marker)
            marker.GlobalPosition = target;
        for (var i = 0; i < _chasers.Count; i++)
        {
            if (_chasers[i].Agent is not { } agent)
                continue;
            var angle = i * MathF.Tau / _chasers.Count;
            agent.TargetPosition = target + (i == 0 ? Vector2.Zero : new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 28f);
        }
    }

    /// <summary>Adds a wall block on <paramref name="cell"/>, or removes one a click added; false for the maze's walls and the outside.</summary>
    public bool ToggleWall(Vector2I cell)
    {
        if (_grid is not { } grid || _walls is not { } walls || !grid.Grid.IsInBounds(cell))
            return false;
        if (_added.Remove(cell, out var existing))
        {
            existing.QueueFree();
            grid.SetCellSolid(cell, false);
            return true;
        }

        if (grid.Grid.IsPointSolid(cell))
            return false;
        var block = Navigation2DScene.AddWall(walls, walls, $"Block_{cell.X}_{cell.Y}", new Rect2I(cell, 1, 1));
        block.Owner = null;
        foreach (var child in block.Children)
            child.Owner = null; // runtime blocks are not part of the scene file
        _added.Add(cell, block);
        grid.SetCellSolid(cell, true);
        return true;
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click)
            return;
        var at = CanvasInput.WindowToCanvas(this, click.Position);
        if (AddWalls && _grid is { } grid)
            ToggleWall(grid.CellAt(at));
        else
            SetTarget(at);
    }
}

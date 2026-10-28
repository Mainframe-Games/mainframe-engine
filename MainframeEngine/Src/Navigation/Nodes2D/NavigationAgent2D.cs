using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Path following for the <see cref="Node2D"/> it is a child of (Godot's <c>NavigationAgent2D</c>). It moves nothing:
/// the body's own <see cref="Node.OnPhysicsProcess"/> asks <see cref="GetNextPathPosition"/> where to go and moves (for
/// example <see cref="CharacterBody2D.MoveAndSlide"/>). Paths are computed lazily inside that call — when
/// <see cref="TargetPosition"/> moved (by more than <see cref="TargetRepathDistance"/>), when the map changed, when the
/// parent is farther than <see cref="PathMaxDistance"/> from its path segment, or after <see cref="RequestRepath"/> — so an
/// idle agent costs nothing. Its path buffer grows when a longer path appears and is reused after that: steady state
/// allocates nothing. Signals fire on the main thread from the caller's physics process.
/// </summary>
[EditorIcon("route", Family = EditorIconFamily.Space2D)]
public class NavigationAgent2D : Node
{
    private readonly NavigationPath2D _path = new(32);
    private Vector2 _target;
    private Vector2 _pathTarget;   // the target the current path was computed for
    private bool _targetSubmitted;
    private bool _hasPathTarget;
    private bool _repath;
    private bool _finished = true;
    private bool _targetReached;
    private bool _lastWaypointReached;
    private int _index;
    private uint _iteration;
    private NavigationMap2D? _map;
    private bool _warnedParent;

    /// <summary>Where the agent should go (pixels). Setting it starts navigating (a repath, unless within <see cref="TargetRepathDistance"/>).</summary>
    [Export]
    public Vector2 TargetPosition
    {
        get => _target;
        set
        {
            _target = value;
            if (TargetRepathDistance > 0f && _hasPathTarget && Vector2.Distance(value, _pathTarget) <= TargetRepathDistance)
                return; // keep the current path (or the arrival) while the target only wiggles
            RequestRepath();
        }
    }

    /// <summary>Within this distance of the current path point the agent moves on to the next one (Godot's 20 px).</summary>
    [Export(Range = "0.1,1000,0.1")]
    public float PathDesiredDistance { get; set; } = 20f;

    /// <summary>Within this distance of the target it counts as reached (Godot's 10 px).</summary>
    [Export(Range = "0.1,1000,0.1")]
    public float TargetDesiredDistance { get; set; } = 10f;

    /// <summary>Farther than this from the current path segment (pushed, teleported) the agent repaths.</summary>
    [Export(Range = "10,1000,1")]
    public float PathMaxDistance { get; set; } = 100f;

    /// <summary>
    /// New: a target that moved less than this from the one the path was computed for keeps the path (chasers that set the
    /// target every tick do not search every tick). 0 = Godot's behaviour: every change repaths.
    /// </summary>
    [Export(Range = "0,1000,0.1")]
    public float TargetRepathDistance { get; set; }

    /// <summary>The navigation layers whose regions the agent's paths may use.</summary>
    [Export(Flags = true)]
    public uint NavigationLayers { get; set; } = 1;

    /// <summary>How the cell path is simplified.</summary>
    [Export]
    public NavigationPathPostprocessing PathPostprocessing { get; set; } = NavigationPathPostprocessing.CorridorFunnel;

    /// <summary>Draws this agent's path even while the server's debug draw is off.</summary>
    [Export]
    public bool DebugEnabled { get; set; }

    /// <summary>A new path was computed.</summary>
    [Signal] public event Action? PathChanged;

    /// <summary>The agent came within <see cref="PathDesiredDistance"/> of a path point.</summary>
    [Signal] public event Action<NavigationWaypoint2D>? WaypointReached;

    /// <summary>The agent came within <see cref="TargetDesiredDistance"/> of the target (once per target).</summary>
    [Signal] public event Action? TargetReached;

    /// <summary>The path ended: the target was reached, or the last point of a path to an unreachable target (once).</summary>
    [Signal] public event Action? NavigationFinished;

    /// <summary>The velocity to move with, raised by <see cref="SetVelocity"/> (the same velocity until avoidance, G3.7).</summary>
    [Signal] public event Action<Vector2>? VelocityComputed;

    /// <summary>The map the agent navigates (its parent's world's; null outside the tree).</summary>
    public NavigationMap2D? Map => _map;

    /// <summary>The path point to move towards; the parent's position when there is no path. Repaths and advances waypoints.</summary>
    public Vector2 GetNextPathPosition()
    {
        var origin = Origin();
        UpdateNavigation(origin);
        return _path.Count == 0 ? origin : _path.Points[Math.Min(_index, _path.Count - 1)];
    }

    /// <summary>True once the path ended (or before any target was set).</summary>
    public bool IsNavigationFinished()
    {
        UpdateNavigation(Origin());
        return _finished;
    }

    internal bool IsNavigationFinishedNoUpdate => _finished;

    /// <summary>True once the agent came within <see cref="TargetDesiredDistance"/> of the target.</summary>
    public bool IsTargetReached() => _targetReached;

    /// <summary>Whether the path's end is within <see cref="TargetDesiredDistance"/> of the target (a partial path's is not).</summary>
    public bool IsTargetReachable() => Vector2.Distance(GetFinalPosition(), _target) <= TargetDesiredDistance;

    /// <summary>The parent's distance to the target.</summary>
    public float DistanceToTarget() => Vector2.Distance(Origin(), _target);

    /// <summary>The last point of the current path (the parent's position when there is none).</summary>
    public Vector2 GetFinalPosition() => _path.Count == 0 ? Origin() : _path.Points[^1];

    /// <summary>The current path (pixels).</summary>
    public ReadOnlySpan<Vector2> GetCurrentNavigationPath() => _path.Points;

    /// <summary>The index of the path point the agent is heading to.</summary>
    public int GetCurrentNavigationPathIndex() => _index;

    /// <summary>The current path's result (status, cells, region).</summary>
    public NavigationPath2D CurrentPath => _path;

    /// <summary>Computes a new path to <see cref="TargetPosition"/> at the next <see cref="GetNextPathPosition"/> and navigates again.</summary>
    public void RequestRepath()
    {
        _targetSubmitted = true;
        _repath = true;
        _finished = false;
        _targetReached = false;
        _lastWaypointReached = false;
    }

    /// <summary>Raises <see cref="VelocityComputed"/> with <paramref name="velocity"/> (pass-through until avoidance, G3.7).</summary>
    public void SetVelocity(Vector2 velocity) => VelocityComputed?.Invoke(velocity);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _map = NavigationServer2D.For(Tree!).MapOf(this);
        _map.AddAgent(this);
    }

    protected override void OnExitTree()
    {
        _map?.RemoveAgent(this);
        _map = null;
        base.OnExitTree();
    }

    private Vector2 Origin()
    {
        if (Parent is Node2D parent)
            return parent.GlobalPosition;
        if (!_warnedParent)
        {
            _warnedParent = true;
            Log.Warning($"[Navigation] '{Name}' is not the child of a Node2D: it navigates from (0, 0).");
        }

        return Vector2.Zero;
    }

    private void UpdateNavigation(Vector2 origin)
    {
        if (!_targetSubmitted || _map is not { } map)
            return;

        var reload = _repath || map.IterationId != _iteration;
        if (!reload && _index > 0 && _index < _path.Count && !_finished)
        {
            var p = ClosestOnSegment(origin, _path.Points[_index - 1], _path.Points[_index]);
            if (Vector2.Distance(origin, p) >= PathMaxDistance)
                reload = true;
        }

        if (reload)
            Repath(map, origin);
        if (_path.Count == 0 || _finished)
            return;

        if (Vector2.Distance(origin, _target) < TargetDesiredDistance)
        {
            AdvanceWaypoints(origin);
            ReachTarget();
            Finish();
        }
        else
        {
            AdvanceWaypoints(origin);
            if (_lastWaypointReached && !IsTargetReachable())
                Finish();
        }
    }

    private void Repath(NavigationMap2D map, Vector2 origin)
    {
        _repath = false;
        _iteration = map.IterationId;
        _pathTarget = _target;
        _hasPathTarget = true;
        map.QueryPath(new NavigationPathQuery2D(origin, _target, NavigationLayers, PathPostprocessing), _path);
        _iteration = _path.MapIteration;
        _index = 0;
        _lastWaypointReached = false;
        _finished = false;
        if (_path.Count == 0)
        {
            // Nowhere to go yet (no region here, or the map has not synced its regions): stand still, and try again when the
            // map changes. No signal: navigation has not ended.
            _finished = true;
            return;
        }

        PathChanged?.Invoke();
    }

    private void AdvanceWaypoints(Vector2 origin)
    {
        if (_lastWaypointReached)
            return;
        while (_index < _path.Count && Vector2.Distance(origin, _path.Points[_index]) < PathDesiredDistance)
        {
            WaypointReached?.Invoke(new NavigationWaypoint2D(_path.Points[_index], _index, _path.Cells[_index]));
            if (_index == _path.Count - 1)
            {
                _lastWaypointReached = true;
                break;
            }

            _index++;
        }
    }

    private void ReachTarget()
    {
        if (_targetReached)
            return;
        _targetReached = true;
        TargetReached?.Invoke();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _targetSubmitted = false; // Godot: nothing updates until a new target (a later map change does not restart it)
        NavigationFinished?.Invoke();
    }

    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = Vector2.Dot(ab, ab);
        if (length <= 0f)
            return a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / length, 0f, 1f);
        return a + ab * t;
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.11: path following for the <see cref="Node3D"/> it is a child of (Godot's <c>NavigationAgent3D</c>), with the 2D
/// agent's rules: it moves nothing; the body's <see cref="Node.OnPhysicsProcess"/> asks <see cref="GetNextPathPosition"/>
/// where to go. Paths are computed lazily inside that call — when <see cref="TargetPosition"/> moved (by more than
/// <see cref="TargetRepathDistance"/>), when the map changed, when the body is farther than <see cref="PathMaxDistance"/>
/// from its path segment, or after <see cref="RequestRepath"/>. Distances are measured from the body's origin lowered by
/// <see cref="PathHeightOffset"/> (its height above the navigation surface), and path positions are handed back raised by
/// it, so a capsule whose origin is 1 m above the floor steers level. Path buffers are reused: 0 bytes per tick.
/// </summary>
[EditorIcon("route", Family = EditorIconFamily.Space3D)]
public class NavigationAgent3D : Node
{
    private readonly NavigationPath3D _path = new(32);
    private Vector3 _target;
    private Vector3 _pathTarget;
    private bool _targetSubmitted;
    private bool _hasPathTarget;
    private bool _repath;
    private bool _finished = true;
    private bool _targetReached;
    private bool _lastWaypointReached;
    private int _index;
    private uint _iteration;
    private NavigationMap3D? _map;
    private bool _warnedParent;

    /// <summary>Where the agent should go (metres). Setting it starts navigating.</summary>
    [Export]
    public Vector3 TargetPosition
    {
        get => _target;
        set
        {
            _target = value;
            if (TargetRepathDistance > 0f && _hasPathTarget && Vector3.Distance(value, _pathTarget) <= TargetRepathDistance)
                return;
            RequestRepath();
        }
    }

    /// <summary>Within this distance of the current path point the agent moves on (Godot's 1 m).</summary>
    [Export(Range = "0.01,100,0.01")]
    public float PathDesiredDistance { get; set; } = 1f;

    /// <summary>Within this distance of the target it counts as reached (Godot's 1 m).</summary>
    [Export(Range = "0.01,100,0.01")]
    public float TargetDesiredDistance { get; set; } = 1f;

    /// <summary>Farther than this from the current path segment the agent repaths (Godot's 5 m).</summary>
    [Export(Range = "0.1,100,0.01")]
    public float PathMaxDistance { get; set; } = 5f;

    /// <summary>A target that moved less than this keeps the current path (0 = every change repaths).</summary>
    [Export(Range = "0,100,0.01")]
    public float TargetRepathDistance { get; set; }

    /// <summary>The body origin's height above the navigation surface (metres; Godot's <c>path_height_offset</c>).</summary>
    [Export(Range = "-10,10,0.01")]
    public float PathHeightOffset { get; set; }

    /// <summary>The navigation layers whose regions the agent's paths may use.</summary>
    [Export(Flags = true)]
    public uint NavigationLayers { get; set; } = 1;

    /// <summary>How the path is simplified.</summary>
    [Export]
    public NavigationPathPostprocessing PathPostprocessing { get; set; } = NavigationPathPostprocessing.CorridorFunnel;

    /// <summary>Draws this agent's path even while the server's debug draw is off.</summary>
    [Export]
    public bool DebugEnabled { get; set; }

    /// <summary>A new path was computed.</summary>
    [Signal] public event Action? PathChanged;

    /// <summary>The agent came within <see cref="PathDesiredDistance"/> of a path point.</summary>
    [Signal] public event Action<NavigationWaypoint3D>? WaypointReached;

    /// <summary>The agent came within <see cref="TargetDesiredDistance"/> of the target (once per target).</summary>
    [Signal] public event Action? TargetReached;

    /// <summary>The path ended (once).</summary>
    [Signal] public event Action? NavigationFinished;

    /// <summary>The velocity given to <see cref="SetVelocity"/>, at once (3D avoidance is not built yet).</summary>
    [Signal] public event Action<Vector3>? VelocityComputed;

    /// <summary>The map the agent navigates (its parent's world's; null outside the tree).</summary>
    public NavigationMap3D? Map => _map;

    /// <summary>The path point to move towards, raised by <see cref="PathHeightOffset"/>; the body's position when there is no path.</summary>
    public Vector3 GetNextPathPosition()
    {
        var origin = Origin();
        UpdateNavigation(origin);
        if (_path.Count == 0)
            return Body();
        return _path.Points[Math.Min(_index, _path.Count - 1)] + new Vector3(0f, PathHeightOffset, 0f);
    }

    public bool IsNavigationFinished()
    {
        UpdateNavigation(Origin());
        return _finished;
    }

    internal bool IsNavigationFinishedNoUpdate => _finished;

    public bool IsTargetReached() => _targetReached;

    /// <summary>Whether the path's end is within <see cref="TargetDesiredDistance"/> of the target (on the surface).</summary>
    public bool IsTargetReachable() => Vector3.Distance(GetFinalPosition(), _target) <= TargetDesiredDistance;

    public float DistanceToTarget() => Vector3.Distance(Origin(), _target);

    /// <summary>The last point of the current path (on the navigation surface; the body's position when there is none).</summary>
    public Vector3 GetFinalPosition() => _path.Count == 0 ? Origin() : _path.Points[^1];

    /// <summary>The current path (on the navigation surface).</summary>
    public ReadOnlySpan<Vector3> GetCurrentNavigationPath() => _path.Points;

    public int GetCurrentNavigationPathIndex() => _index;

    public NavigationPath3D CurrentPath => _path;

    public void RequestRepath()
    {
        _targetSubmitted = true;
        _repath = true;
        _finished = false;
        _targetReached = false;
        _lastWaypointReached = false;
    }

    /// <summary>Raises <see cref="VelocityComputed"/> with <paramref name="velocity"/> (no 3D avoidance yet).</summary>
    public void SetVelocity(Vector3 velocity) => VelocityComputed?.Invoke(velocity);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _map = NavigationServer3D.For(Tree!).MapOf(this);
        _map.AddAgent(this);
    }

    protected override void OnExitTree()
    {
        _map?.RemoveAgent(this);
        _map = null;
        base.OnExitTree();
    }

    private Vector3 Body()
    {
        if (Parent is Node3D parent)
            return parent.GlobalPosition;
        if (!_warnedParent)
        {
            _warnedParent = true;
            Log.Warning($"[Navigation] '{Name}' is not the child of a Node3D: it navigates from (0, 0, 0).");
        }

        return Vector3.Zero;
    }

    // The body's origin on the navigation surface.
    private Vector3 Origin() => Body() - new Vector3(0f, PathHeightOffset, 0f);

    private void UpdateNavigation(Vector3 origin)
    {
        if (!_targetSubmitted || _map is not { } map)
            return;

        var reload = _repath || map.IterationId != _iteration;
        if (!reload && _index > 0 && _index < _path.Count && !_finished)
        {
            var p = ClosestOnSegment(origin, _path.Points[_index - 1], _path.Points[_index]);
            if (Vector3.Distance(origin, p) >= PathMaxDistance)
                reload = true;
        }

        if (reload)
            Repath(map, origin);
        if (_path.Count == 0 || _finished)
            return;

        AdvanceWaypoints(origin);
        if (Vector3.Distance(origin, _target) < TargetDesiredDistance)
        {
            ReachTarget();
            Finish();
        }
        else if (_lastWaypointReached && !IsTargetReachable())
        {
            Finish();
        }
    }

    private void Repath(NavigationMap3D map, Vector3 origin)
    {
        _repath = false;
        _pathTarget = _target;
        _hasPathTarget = true;
        map.QueryPath(new NavigationPathQuery3D(origin, _target, NavigationLayers, PathPostprocessing), _path);
        _iteration = _path.MapIteration;
        _index = 0;
        _lastWaypointReached = false;
        _finished = false;
        if (_path.Count == 0)
        {
            _finished = true; // nowhere to go yet: stand, retry when the map changes
            return;
        }

        PathChanged?.Invoke();
    }

    private void AdvanceWaypoints(Vector3 origin)
    {
        if (_lastWaypointReached)
            return;
        while (_index < _path.Count && Vector3.Distance(origin, _path.Points[_index]) < PathDesiredDistance)
        {
            WaypointReached?.Invoke(new NavigationWaypoint3D(_path.Points[_index], _index, _path.Cells[_index]));
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
        _targetSubmitted = false;
        NavigationFinished?.Invoke();
    }

    private static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var length = Vector3.Dot(ab, ab);
        if (length <= 0f)
            return a;
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / length, 0f, 1f);
        return a + ab * t;
    }
}

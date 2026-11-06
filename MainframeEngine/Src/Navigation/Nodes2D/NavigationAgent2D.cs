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

    /// <summary>
    /// The velocity to move with: with <see cref="AvoidanceEnabled"/>, the safe velocity the server computed from the one
    /// given to <see cref="SetVelocity"/>, raised after the physics step of the same tick (before the next tick's
    /// <see cref="Node.OnPhysicsProcess"/>); without avoidance, that velocity at once.
    /// </summary>
    [Signal] public event Action<Vector2>? VelocityComputed;

    /// <summary>
    /// Local avoidance (G3.7, ORCA): <see cref="SetVelocity"/> hands the preferred velocity to the server, and
    /// <see cref="VelocityComputed"/> returns a velocity that keeps <see cref="Radius"/> clear of other agents and
    /// obstacles for the time horizons. Godot's default: off.
    /// </summary>
    [ExportGroup("Avoidance")]
    [Export]
    public bool AvoidanceEnabled { get; set; }

    /// <summary>The agent's avoidance radius (pixels; Godot's 10).</summary>
    [Export(Range = "0.1,500,0.1")]
    public float Radius { get; set; } = 10f;

    /// <summary>Other agents and radius obstacles within this distance (centre to centre) are considered (Godot's 500 px).</summary>
    [Export(Range = "0.1,100000,0.1")]
    public float NeighborDistance { get; set; } = 500f;

    /// <summary>At most this many of the nearest neighbours are considered (Godot's 10).</summary>
    [Export(Range = "1,10000,1")]
    public int MaxNeighbors { get; set; } = 10;

    /// <summary>How far ahead (seconds) the agent avoids other agents (Godot's 1).</summary>
    [Export(Range = "0.01,10,0.01")]
    public float TimeHorizonAgents { get; set; } = 1f;

    /// <summary>
    /// How far ahead (seconds) it avoids obstacle polygons (0.5; Godot's default 0 would divide by zero in ORCA's obstacle
    /// lines, so the floor is 0.01).
    /// </summary>
    [Export(Range = "0.01,10,0.01")]
    public float TimeHorizonObstacles { get; set; } = 0.5f;

    /// <summary>The safe velocity's speed limit (pixels per second; Godot's 100).</summary>
    [Export(Range = "0.01,100000,0.01")]
    public float MaxSpeed { get; set; } = 100f;

    /// <summary>The avoidance layers this agent is on (others with a matching mask avoid it).</summary>
    [Export(Flags = true)]
    public uint AvoidanceLayers { get; set; } = 1;

    /// <summary>The avoidance layers of the agents and obstacles this agent avoids.</summary>
    [Export(Flags = true)]
    public uint AvoidanceMask { get; set; } = 1;

    /// <summary>
    /// 0–1 (Godot's 1): an agent ignores agents of lower priority, which then make way for it alone (equal priorities
    /// share the avoidance half and half).
    /// </summary>
    [Export(Range = "0,1,0.001")]
    public float AvoidancePriority { get; set; } = 1f;

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

    /// <summary>
    /// The velocity the parent wants to move with this tick (call it from <see cref="Node.OnPhysicsProcess"/>). With
    /// <see cref="AvoidanceEnabled"/> in a tree it is submitted to the server, which raises <see cref="VelocityComputed"/>
    /// with the safe velocity after the physics step; an agent that submits nothing in a tick stands still for the others
    /// and gets no signal. Otherwise <see cref="VelocityComputed"/> is raised now with <paramref name="velocity"/>.
    /// </summary>
    public void SetVelocity(Vector2 velocity)
    {
        if (!AvoidanceEnabled || _map is null)
        {
            VelocityComputed?.Invoke(velocity);
            return;
        }

        SubmittedVelocity = velocity;
        VelocitySubmitted = true;
    }

    /// <summary>
    /// Replaces the velocity the other agents predict this one with (ORCA's current velocity: the last safe velocity),
    /// for example after a teleport or a knock-back. Godot's <c>set_velocity_forced</c>.
    /// </summary>
    public void SetVelocityForced(Vector2 velocity) => SafeVelocity = velocity;

    /// <summary>The last safe velocity the server computed (zero while not avoiding).</summary>
    public Vector2 SafeVelocity { get; internal set; }

    /// <summary>The velocity last given to <see cref="SetVelocity"/> with avoidance on.</summary>
    public Vector2 SubmittedVelocity { get; private set; }

    internal bool VelocitySubmitted { get; set; }

    internal void DispatchSafeVelocity() => VelocityComputed?.Invoke(SafeVelocity);

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
        VelocitySubmitted = false;
        SafeVelocity = Vector2.Zero;
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

using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>Who has the view: the walking player, the fly-over, a reference shot or the free camera.</summary>
public enum ForestCameraMode
{
    Player,
    FlyOver,
    Shot,
    FreeFly,
}

/// <summary>
/// The Forest's cameras (ADR 0180), switched from the pause menu's Cameras page: the player's own, a cinematic
/// <see cref="FlyOver"/> along <see cref="ForestBenchmark.Spline"/> at a steady speed (looping, or back to the player at
/// the end), the reference shots R1–R8 (<see cref="ValleyLayout.Shots"/>, with their photo lenses) and a
/// <see cref="FreeFlyCamera"/>. Outside <see cref="ForestCameraMode.Player"/> the player is disabled (it stands still),
/// and so is everything while <see cref="MenuOpen"/>. In a shot, left and right (move left/right) cycle the shots.
/// Per frame it only moves the fly-over camera: nothing allocates.
/// </summary>
public sealed class ForestCameras : Node
{
    /// <summary>The fly-over's speed along the spline.</summary>
    public const float FlyOverSpeed = 7f;

    /// <summary>How far ahead on the spline the fly-over looks.</summary>
    public const float LookAhead = 14f;

    private const int SamplesPerSegment = 32;

    private readonly Vector3[] _points = new Vector3[ForestBenchmark.Spline.Length];
    private float[] _lengths = [];
    private Camera3D? _cinematic;
    private FreeFlyCamera? _free;
    private float _distance;
    private Vector3 _lookTarget;
    private bool _menuOpen;
    private bool _resolved;

    /// <summary>The player whose camera is the default view.</summary>
    public FirstPersonController? Player { get; set; }

    /// <summary>Ground height at world (x, z): the valley's terrain (null: flat at 0).</summary>
    public Func<float, float, float>? GroundHeight { get; set; }

    /// <summary>Capture the mouse for the player and the free camera (off with <c>++ --no-capture</c>).</summary>
    public bool CaptureMouse { get; set; } = true;

    /// <summary>Loop the fly-over (default); otherwise it hands the view back to the player at the end.</summary>
    public bool Loop { get; set; } = true;

    public ForestCameraMode Mode { get; private set; }

    /// <summary>The reference shot shown (0-based), in <see cref="ForestCameraMode.Shot"/>.</summary>
    public int ShotIndex { get; private set; }

    /// <summary>Metres flown along the fly-over in this loop.</summary>
    public float FlyOverDistance => _distance;

    /// <summary>The fly-over's length (one loop), metres.</summary>
    public float FlyOverLength => _lengths.Length > 0 ? _lengths[^1] : 0f;

    /// <summary>The camera of the current cinematic mode (fly-over and shots), or null.</summary>
    public Camera3D? CinematicCamera => _cinematic;

    public FreeFlyCamera? FreeCamera => _free;

    /// <summary>Raised when the mode changes (the menu refreshes its Cameras page).</summary>
    public event Action? ModeChanged;

    /// <summary>
    /// The pause menu is open: the player and the free camera stop, the mouse is free. Closing it gives control back to
    /// the current mode.
    /// </summary>
    public bool MenuOpen
    {
        get => _menuOpen;
        set
        {
            _menuOpen = value;
            ApplyControl();
        }
    }

    /// <summary>Flies the benchmark spline as a cinematic camera, from its start.</summary>
    public void StartFlyOver()
    {
        if (Tree is null)
            return;
        Resolve();
        _distance = 0f;
        var camera = EnsureCinematic();
        camera.Fov = 55f;
        camera.Attributes = null;
        Place(camera, 0f, snap: true);
        SetMode(ForestCameraMode.FlyOver);
    }

    /// <summary>Shows reference shot <paramref name="index"/> (0-based, wraps).</summary>
    public void ShowShot(int index)
    {
        if (Tree is null)
            return;
        var shots = ValleyLayout.Shots;
        index = ((index % shots.Length) + shots.Length) % shots.Length;
        var shot = shots[index];
        var (eye, target) = ForestDev.ShotPose(shot, Height);
        var camera = EnsureCinematic();
        camera.Fov = shot.Fov;
        camera.Attributes = ForestDev.PhotoLens(shot);
        camera.Position = eye;
        camera.LookAt(target);
        ShotIndex = index;
        SetMode(ForestCameraMode.Shot);
    }

    /// <summary>A free camera starting from the current view.</summary>
    public void StartFreeFly()
    {
        if (Tree is null)
            return;
        var from = GetViewport()?.ActiveCamera3D;
        if (_free is null)
        {
            _free = new FreeFlyCamera { Name = "FreeCamera", Near = 0.05f, Far = 1200f, Fov = 60f };
            AddChild(_free);
        }

        if (from is not null)
            _free.SetPose(from.GlobalPosition, from.GlobalForward);
        if (Player is not null)
        {
            _free.MouseSensitivity = Player.MouseSensitivity;
            _free.InvertY = Player.InvertY;
        }

        SetMode(ForestCameraMode.FreeFly);
    }

    /// <summary>Back to walking.</summary>
    public void ReturnToPlayer() => SetMode(ForestCameraMode.Player);

    protected override void OnProcess(in GameTime gameTime)
    {
        switch (Mode)
        {
            case ForestCameraMode.FlyOver when _cinematic is { } camera && _lengths.Length > 0:
                _distance += FlyOverSpeed * gameTime.DeltaTime;
                if (_distance >= FlyOverLength)
                {
                    if (!Loop)
                    {
                        ReturnToPlayer();
                        return;
                    }

                    _distance -= FlyOverLength;
                }

                Place(camera, gameTime.DeltaTime, snap: false);
                break;
            case ForestCameraMode.Shot when !_menuOpen && Tree is { } tree:
                if (tree.Input.IsActionJustPressed("move_right"))
                    ShowShot(ShotIndex + 1);
                else if (tree.Input.IsActionJustPressed("move_left"))
                    ShowShot(ShotIndex - 1);
                break;
        }
    }

    private void SetMode(ForestCameraMode mode)
    {
        Mode = mode;
        Camera3D? view = mode switch
        {
            ForestCameraMode.Player => Player?.Camera,
            ForestCameraMode.FreeFly => _free,
            _ => _cinematic,
        };
        if (view is not null)
            view.Current = true;
        Tree?.Servers.Render?.ResetTemporalHistory(); // a cut: no TAA history from the old view
        ApplyControl();
        ModeChanged?.Invoke();
    }

    // Who processes input, and the mouse: free in the menu, captured to walk or fly, hidden over a cinematic.
    private void ApplyControl()
    {
        if (Player is { } player)
            player.ProcessMode = Mode == ForestCameraMode.Player && !_menuOpen ? ProcessMode.Inherit : ProcessMode.Disabled;
        if (_free is { } free)
            free.ProcessMode = Mode == ForestCameraMode.FreeFly && !_menuOpen ? ProcessMode.Inherit : ProcessMode.Disabled;
        if (Tree is not { } tree)
            return;
        tree.Input.MouseMode = _menuOpen
            ? MouseMode.Visible
            : Mode switch
            {
                ForestCameraMode.Player or ForestCameraMode.FreeFly => CaptureMouse ? MouseMode.Captured : MouseMode.Visible,
                _ => MouseMode.Hidden,
            };
    }

    private Camera3D EnsureCinematic()
    {
        if (_cinematic is null)
        {
            _cinematic = new Camera3D { Name = "CinematicCamera", Near = 0.05f, Far = 1200f, Fov = 55f };
            AddChild(_cinematic);
        }

        return _cinematic;
    }

    private float Height(float x, float z) => GroundHeight?.Invoke(x, z) ?? 0f;

    // The spline over the ground and the water, and its arc length (a closed loop: the last point flies back to the first).
    private void Resolve()
    {
        if (_resolved)
            return;
        _resolved = true;
        ForestBenchmark.ResolveSpline(Height, Tree?.Root.World3D.Water, _points);
        var segments = _points.Length;
        _lengths = new float[segments * SamplesPerSegment + 1];
        var previous = PointAt(0f);
        for (var i = 1; i < _lengths.Length; i++)
        {
            var p = PointAt((float)i / SamplesPerSegment);
            _lengths[i] = _lengths[i - 1] + Vector3.Distance(previous, p);
            previous = p;
        }
    }

    /// <summary>A point on the closed Catmull-Rom loop through the spline at <paramref name="t"/> (0 … points).</summary>
    private Vector3 PointAt(float t)
    {
        var n = _points.Length;
        var i = (int)MathF.Floor(t);
        var f = t - i;
        i = ((i % n) + n) % n;
        var p0 = _points[(i + n - 1) % n];
        var p1 = _points[i];
        var p2 = _points[(i + 1) % n];
        var p3 = _points[(i + 2) % n];
        var f2 = f * f;
        var f3 = f2 * f;
        return 0.5f * (2f * p1 + (-p0 + p2) * f + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f2 + (-p0 + 3f * p1 - 3f * p2 + p3) * f3);
    }

    /// <summary>The spline parameter at arc length <paramref name="distance"/> (wraps).</summary>
    private float ParameterAt(float distance)
    {
        var total = FlyOverLength;
        if (total <= 0f)
            return 0f;
        distance %= total;
        if (distance < 0f)
            distance += total;
        int lo = 0, hi = _lengths.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (_lengths[mid] <= distance)
                lo = mid;
            else
                hi = mid;
        }

        var span = _lengths[hi] - _lengths[lo];
        var f = span > 1e-6f ? (distance - _lengths[lo]) / span : 0f;
        return (lo + f) / SamplesPerSegment;
    }

    /// <summary>The fly-over's camera position at <paramref name="distance"/> metres along it.</summary>
    public Vector3 FlyOverPosition(float distance)
    {
        Resolve();
        return PointAt(ParameterAt(distance));
    }

    private void Place(Camera3D camera, float delta, bool snap)
    {
        var position = PointAt(ParameterAt(_distance));
        var ahead = PointAt(ParameterAt(_distance + LookAhead));
        var target = ahead + new Vector3(0f, -0.15f * LookAhead, 0f);
        // Ease the look so the turns at the spline's points stay smooth.
        _lookTarget = snap ? target : Vector3.Lerp(_lookTarget, target, 1f - MathF.Exp(-2.5f * delta));
        camera.Position = position;
        if (Vector3.DistanceSquared(_lookTarget, position) > 1e-4f)
            camera.LookAt(_lookTarget);
    }
}

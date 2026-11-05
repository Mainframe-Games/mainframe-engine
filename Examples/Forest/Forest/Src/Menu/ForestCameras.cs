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
/// <see cref="FlyOver"/> along <see cref="ForestBenchmark.Spline"/> at a steady speed (a closed <see cref="Curve3D"/>
/// through its points, sampled by arc length: G13.8; looping, or back to the player at the end), the reference shots R1–R8 (<see cref="ValleyLayout.Shots"/>, with their photo lenses) and a
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

    /// <summary>
    /// The least height the fly-over keeps above the ground or the water (the meadow's grass and the pond's reeds reach
    /// about a metre). The spline's points stand higher, but between them the curve can sag into rising ground (the
    /// first leg, north from the trailhead) or skim the pond: there the path is lifted, eased over ±half a segment.
    /// </summary>
    public const float MinClearance = 1.5f;

    /// <summary>Distance between the rail's baked points (and lift samples), metres.</summary>
    private const float RailInterval = 0.25f;

    private readonly Vector3[] _points = new Vector3[ForestBenchmark.Spline.Length];
    private readonly Curve3D _rail = new() { BakeInterval = RailInterval };
    private float[] _lift = [];
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
    public float FlyOverLength => _resolved ? _rail.GetBakedLength() : 0f;

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
            case ForestCameraMode.FlyOver when _cinematic is { } camera && FlyOverLength > 0:
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

    // The spline over the ground and the water as a closed rail (a uniform Catmull-Rom loop, as before), and the lift.
    private void Resolve()
    {
        if (_resolved)
            return;
        _resolved = true;
        var water = Tree?.Root.World3D.Water;
        ForestBenchmark.ResolveSpline(Height, water, _points);
        _rail.SetSmoothPoints(_points, closed: true);
        _lift = Lift(water);
    }

    /// <summary>
    /// How far each of the rail's baked points is lifted to keep <see cref="MinClearance"/> over the ground and the water:
    /// what each needs, widened by a running maximum over ±half a segment and then averaged over as much, so the lift is
    /// never less than a point needs and eases in and out instead of following every bump.
    /// </summary>
    private float[] Lift(WaterQueries? water)
    {
        var baked = _rail.GetBakedPoints();
        var samples = baked.Length - 1; // closed: the last baked point is the first
        var need = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var p = baked[i];
            var ground = Height(p.X, p.Z);
            var surface = water?.SurfaceHeightAt(new Vector3(p.X, ground, p.Z)) ?? float.NaN;
            if (float.IsFinite(surface))
                ground = MathF.Max(ground, surface);
            need[i] = MathF.Max(0f, ground + MinClearance - p.Y);
        }

        var window = Math.Max(1, (int)(_rail.GetBakedLength() / _points.Length / 2f / RailInterval));
        var widened = new float[samples];
        for (var i = 0; i < samples; i++)
            for (var k = -window; k <= window; k++)
                widened[i] = MathF.Max(widened[i], need[((i + k) % samples + samples) % samples]);
        var lift = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var sum = 0f;
            for (var k = -window; k <= window; k++)
                sum += widened[((i + k) % samples + samples) % samples];
            lift[i] = sum / (2 * window + 1);
        }

        return lift;
    }

    /// <summary>The rail at <paramref name="distance"/> metres (wraps), lifted clear of the ground.</summary>
    private Vector3 PointAt(float distance)
    {
        var d = _rail.WrapOffset(distance);
        var p = _rail.SampleBaked(d, cubic: true);
        if (_lift.Length == 0)
            return p;
        var u = d / _rail.GetBakedLength() * _lift.Length;
        var i = Math.Clamp((int)u, 0, _lift.Length - 1);
        p.Y += float.Lerp(_lift[i], _lift[(i + 1) % _lift.Length], u - i);
        return p;
    }

    /// <summary>The fly-over's camera position at <paramref name="distance"/> metres along it.</summary>
    public Vector3 FlyOverPosition(float distance)
    {
        Resolve();
        return PointAt(distance);
    }

    private void Place(Camera3D camera, float delta, bool snap)
    {
        var position = PointAt(_distance);
        var ahead = PointAt(_distance + LookAhead);
        var target = ahead + new Vector3(0f, -0.15f * LookAhead, 0f);
        // Ease the look so the turns at the spline's points stay smooth.
        _lookTarget = snap ? target : Vector3.Lerp(_lookTarget, target, 1f - MathF.Exp(-2.5f * delta));
        camera.Position = position;
        if (Vector3.DistanceSquared(_lookTarget, position) > 1e-4f)
            camera.LookAt(_lookTarget);
    }
}

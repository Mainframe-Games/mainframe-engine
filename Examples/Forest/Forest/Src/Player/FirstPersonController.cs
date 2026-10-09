using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's first-person walker (docs/design/future/forest-showcase.md): a capsule <see cref="CharacterBody3D"/> with
/// its origin at the feet, a <c>Head</c> <see cref="Node3D"/> at eye height (top − 0.12 m) holding the
/// <see cref="Camera3D"/> (<c>Head/Camera</c>). Missing children are created (unowned) when it is ready.
/// </summary>
/// <remarks>
/// <para><b>Look:</b> mouse motion while <see cref="InputState.MouseMode"/> is captured (per event, in
/// <see cref="Node.OnInput"/>), and the right stick (<c>look_*</c> actions, 0.15 radial deadzone, squared response) in
/// <see cref="Node.OnProcess"/>. Yaw and pitch both live on the head, not the body: the body's transform is physics
/// interpolated (ADR 0024), so a yaw written there between steps would be overwritten by the render pose. The mouse is
/// captured at start (<see cref="CaptureMouse"/>); <c>pause</c> (Escape / Start) releases it and a click recaptures it
/// (with a <see cref="PauseMenu"/>, the menu opens instead and disables the controller while it is open).</para>
/// <para><b>Move:</b> <c>move_*</c> in the head's yaw frame, accelerating to walk, sprint (<c>sprint</c> held or
/// <c>sprint_toggle</c>) or crouch (<c>crouch</c> held or <c>crouch_toggle</c>) speed, scaled by
/// <see cref="WaterQueries.WadeSpeedScale"/> and pushed by the water's flow; gravity, a jump with coyote time and a jump
/// buffer, then <see cref="CharacterBody3D.MoveAndSlide"/>. Deeper than <see cref="MaxWadeImmersion"/> is a soft wall.</para>
/// <para><b>Head bob and footsteps:</b> the walked distance drives a vertical bob at the stride frequency and a sideways
/// one at half of it, and raises <see cref="Footstep"/> once per <see cref="StrideLength"/> with the surface under the
/// feet: <c>"water"</c> when wading, else the floor collider's <see cref="IFootstepSurface"/>, else
/// <see cref="SurfaceResolver"/>, else <see cref="DefaultSurface"/>.</para>
/// <para>Nothing here allocates per frame.</para>
/// </remarks>
public class FirstPersonController : CharacterBody3D
{
    /// <summary>Radial deadzone of the look stick.</summary>
    public const float LookDeadzone = 0.15f;

    /// <summary>Eye height below the top of the capsule, in metres.</summary>
    public const float EyeBelowTop = 0.12f;

    private const float CrouchTransitionSeconds = 0.15f;

    /// <summary>Seconds off the floor before touching down again counts as a landing footstep.</summary>
    public const float LandingAirTime = 0.2f;

    private readonly List<CollisionObject3D> _overlaps = new(8);
    private CapsuleShape3D? _standProbe;
    private Node3D? _head;
    private Camera3D? _camera;
    private CollisionShape3D? _shape;
    private CapsuleShape3D? _capsule;
    private float _yaw;
    private float _pitch;
    private float _headHeight;
    private float _coyote;
    private float _jumpBuffer;
    private bool _crouching;
    private bool _crouchToggled;
    private bool _sprintToggled;
    private bool _wasOnFloor;
    private float _airTime = 1f;
    private float _distance;
    private Vector3 _horizontal;
    private float _bobAmplitude;
    private float _appliedAspect;
    private float _appliedFov;
    private bool _captureOnReady = true;

    // ---------------------------------------------------------------------------------------------
    // Movement
    // ---------------------------------------------------------------------------------------------

    /// <summary>Walk speed in m/s.</summary>
    [Export(Range = "0.5,10,0.1")] public float WalkSpeed { get; set; } = 2.5f;

    [Export(Range = "0.5,20,0.1")] public float SprintSpeed { get; set; } = 5f;

    [Export(Range = "0.1,10,0.1")] public float CrouchSpeed { get; set; } = 1.3f;

    /// <summary>Acceleration towards the target speed on the floor, m/s².</summary>
    [Export(Range = "1,100,0.5")] public float Acceleration { get; set; } = 14f;

    /// <summary>Fraction of <see cref="Acceleration"/> available in the air.</summary>
    [Export(Range = "0,1,0.05")] public float AirControl { get; set; } = 0.3f;

    /// <summary>Upward speed of a jump (≈ 0.9 m at 9.81 m/s²).</summary>
    [Export(Range = "0,20,0.1")] public float JumpVelocity { get; set; } = 4.2f;

    [Export(Range = "0,50,0.01")] public float Gravity { get; set; } = 9.81f;

    /// <summary>Seconds after leaving a ledge a jump still works; also how long a jump press is buffered before landing.</summary>
    [Export(Range = "0,1,0.01")] public float CoyoteTime { get; set; } = 0.1f;

    [Export(Range = "0.5,3,0.01")] public float StandingHeight { get; set; } = 1.75f;

    [Export(Range = "0.5,3,0.01")] public float CrouchHeight { get; set; } = 1.1f;

    [Export(Range = "0.1,1,0.01")] public float Radius { get; set; } = 0.3f;

    // ---------------------------------------------------------------------------------------------
    // Look
    // ---------------------------------------------------------------------------------------------

    /// <summary>Degrees per mouse count.</summary>
    [Export(Range = "0.01,1,0.01")] public float MouseSensitivity { get; set; } = 0.1f;

    /// <summary>Degrees per second at full right-stick deflection.</summary>
    [Export(Range = "10,500,1")] public float GamepadLookSpeed { get; set; } = 140f;

    [Export] public bool InvertY { get; set; }

    /// <summary>Horizontal field of view in degrees; the camera's vertical FOV follows from the viewport's aspect.</summary>
    [Export(Range = "60,110,1")] public float HorizontalFov { get; set; } = 90f;

    /// <summary>Capture the mouse when ready (the <c>++ --no-capture</c> game argument turns it off).</summary>
    [Export] public bool CaptureMouse { get; set; } = true;

    /// <summary>
    /// <c>pause</c> (Escape / Start) releases the captured mouse (default). Off when a pause menu owns the action
    /// (<see cref="PauseMenu"/> opens on it and releases the mouse itself).
    /// </summary>
    public bool ReleaseMouseOnPause { get; set; } = true;

    // ---------------------------------------------------------------------------------------------
    // Head bob, footsteps, water
    // ---------------------------------------------------------------------------------------------

    /// <summary>Head bob on or off (off for players prone to motion sickness).</summary>
    [Export] public bool HeadBob { get; set; } = true;

    /// <summary>Vertical bob in metres at walk speed (sideways is half).</summary>
    [Export(Range = "0,0.2,0.001")] public float HeadBobAmount { get; set; } = 0.035f;

    /// <summary>Metres walked per footstep.</summary>
    [Export(Range = "0.2,3,0.01")] public float StrideLength { get; set; } = 0.75f;

    /// <summary>Surface name when nothing under the feet names one.</summary>
    [Export] public string DefaultSurface { get; set; } = "default";

    /// <summary>Water column under the feet from which footsteps are <c>"water"</c>, in metres.</summary>
    [Export(Range = "0,1,0.01")] public float WadeFootstepDepth { get; set; } = 0.05f;

    /// <summary>Immersion in metres the player cannot go beyond (no swimming): deeper water is a soft wall.</summary>
    [Export(Range = "0.2,3,0.05")] public float MaxWadeImmersion { get; set; } = 1.2f;

    /// <summary>How much of the water's flow pushes the player.</summary>
    [Export(Range = "0,1,0.01")] public float FlowPush { get; set; } = 0.3f;

    /// <summary>Raised once per stride on the floor (and on landing) with the surface name under the feet.</summary>
    [Signal] public event Action<string>? Footstep;

    /// <summary>
    /// Names the surface at a feet position when the floor collider is not an <see cref="IFootstepSurface"/> (the
    /// content wave sets it to <c>Terrain3D.SurfaceAt</c>'s layer name); null result: <see cref="DefaultSurface"/>.
    /// </summary>
    public Func<Vector3, string?>? SurfaceResolver { get; set; }

    // ---------------------------------------------------------------------------------------------
    // State (read-only)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Look yaw in degrees (0 looks along −Z, positive turns left).</summary>
    public float YawDegrees => float.RadiansToDegrees(_yaw);

    /// <summary>Look pitch in degrees (positive looks up), within ±89°.</summary>
    public float PitchDegrees => float.RadiansToDegrees(_pitch);

    public bool IsCrouching => _crouching;

    public bool IsSprinting { get; private set; }

    /// <summary>Immersion of the feet in water at the last physics step (≤ 0 when dry).</summary>
    public float Immersion { get; private set; }

    /// <summary>The wading speed multiplier at the last physics step (1 when dry).</summary>
    public float WadeScale { get; private set; } = 1f;

    /// <summary>Metres walked on the floor (drives the head bob and footsteps).</summary>
    public float DistanceWalked => _distance;

    public int FootstepCount { get; private set; }

    public string? LastFootstepSurface { get; private set; }

    /// <summary>The camera's head-bob offset from the head (metres).</summary>
    public Vector3 HeadBobOffset => _camera?.Position ?? Vector3.Zero;

    public Node3D? Head => _head;

    public Camera3D? Camera => _camera;

    /// <summary>The camera's vertical FOV in degrees for a horizontal FOV at an aspect ratio (width / height).</summary>
    public static float VerticalFov(float horizontalDegrees, float aspect) =>
        float.RadiansToDegrees(2f * MathF.Atan(MathF.Tan(float.DegreesToRadians(horizontalDegrees) * 0.5f) / MathF.Max(aspect, 1e-3f)));

    /// <summary>The look stick's response: a 0.15 radial deadzone, then a squared curve (0..1 magnitude).</summary>
    public static Vector2 LookCurve(Vector2 stick)
    {
        var length = stick.Length();
        if (length <= LookDeadzone)
            return Vector2.Zero;
        var scaled = MathF.Min((length - LookDeadzone) / (1f - LookDeadzone), 1f);
        return stick / length * (scaled * scaled);
    }

    /// <summary>Turns the view by <paramref name="yawDegrees"/> (positive: left) and <paramref name="pitchDegrees"/> (positive: up).</summary>
    public void AddLook(float yawDegrees, float pitchDegrees)
    {
        _yaw += float.DegreesToRadians(yawDegrees);
        _yaw = MathF.IEEERemainder(_yaw, MathF.Tau);
        _pitch = Math.Clamp(_pitch + float.DegreesToRadians(pitchDegrees), float.DegreesToRadians(-89f), float.DegreesToRadians(89f));
        ApplyLook();
    }

    /// <summary>Applies persisted player settings (FOV, sensitivity, invert Y, pad look speed, head bob).</summary>
    public void ApplySettings(ForestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HorizontalFov = Math.Clamp(settings.HorizontalFov, 60f, 110f);
        MouseSensitivity = settings.MouseSensitivity;
        InvertY = settings.InvertY;
        GamepadLookSpeed = settings.GamepadLookSpeed;
        HeadBob = settings.HeadBob;
    }

    protected override void OnReady()
    {
        base.OnReady();
        EnsureChildren();
        // The head's authored rotation is the starting view.
        var euler = _head!.RotationDegrees;
        _yaw = float.DegreesToRadians(euler.Y);
        _pitch = Math.Clamp(float.DegreesToRadians(euler.X), float.DegreesToRadians(-89f), float.DegreesToRadians(89f));
        _headHeight = StandingHeight - EyeBelowTop;
        ApplyShape(StandingHeight);
        ApplyLook();
        PlaceHead();
        UpdateFov();
        if (GameHost.Project is not null)
        {
            ApplySettings(ForestSettings.LoadOrDefault(ForestSettings.DefaultPath));
            _captureOnReady = !GameHost.UserArgs.Contains("--no-capture");
        }

        if (CaptureMouse && _captureOnReady && Tree is { } tree)
            tree.Input.MouseMode = MouseMode.Captured;
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        if (Tree is not { } tree)
            return;
        switch (inputEvent)
        {
            case InputEventMouseMotion motion when tree.Input.MouseMode == MouseMode.Captured:
                AddLook(-motion.Relative.X * MouseSensitivity, -motion.Relative.Y * MouseSensitivity * (InvertY ? -1f : 1f));
                break;
            case InputEventMouseButton { Pressed: true } when CaptureMouse && tree.Input.MouseMode != MouseMode.Captured:
                tree.Input.MouseMode = MouseMode.Captured;
                break;
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Tree is not { } tree)
            return;
        var input = tree.Input;
        var delta = gameTime.DeltaTime;
        if (ReleaseMouseOnPause && input.IsActionJustPressed("pause") && input.MouseMode == MouseMode.Captured)
            input.MouseMode = MouseMode.Visible;

        var stick = LookCurve(input.GetVector("look_left", "look_right", "look_up", "look_down"));
        if (stick != Vector2.Zero)
            AddLook(-stick.X * GamepadLookSpeed * delta, -stick.Y * GamepadLookSpeed * delta * (InvertY ? -1f : 1f));

        // Crouch: the head eases to the new eye height.
        var targetHead = (_crouching ? CrouchHeight : StandingHeight) - EyeBelowTop;
        var rate = MathF.Abs(StandingHeight - CrouchHeight) / CrouchTransitionSeconds;
        _headHeight = MoveToward(_headHeight, targetHead, rate * delta);

        // Head bob: amplitude follows the horizontal speed and eases out when stopping.
        var velocity = GetRealVelocity();
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        var targetAmplitude = HeadBob && IsOnFloor() ? HeadBobAmount * MathF.Min(speed / MathF.Max(WalkSpeed, 0.1f), 1.5f) : 0f;
        _bobAmplitude += (targetAmplitude - _bobAmplitude) * (1f - MathF.Exp(-10f * delta));
        if (!HeadBob)
            _bobAmplitude = 0f;
        PlaceHead();
        UpdateFov();
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (Tree is not { } tree)
            return;
        var input = tree.Input;
        var move = input.GetVector("move_left", "move_right", "move_forward", "move_back");

        // Crouch (held or toggled); standing up needs headroom.
        if (input.IsActionJustPressed("crouch_toggle"))
            _crouchToggled = !_crouchToggled;
        var wantCrouch = input.IsActionPressed("crouch") ^ _crouchToggled;
        if (wantCrouch && !_crouching)
            SetCrouching(true);
        else if (!wantCrouch && _crouching && CanStand())
            SetCrouching(false);

        // Sprint (held, or toggled until the stick is released).
        if (input.IsActionJustPressed("sprint_toggle"))
            _sprintToggled = !_sprintToggled;
        if (move.LengthSquared() < 0.01f)
            _sprintToggled = false;
        IsSprinting = (input.IsActionPressed("sprint") || _sprintToggled) && !_crouching && move.LengthSquared() > 0.01f;
        var speed = _crouching ? CrouchSpeed : IsSprinting ? SprintSpeed : WalkSpeed;

        // Wish direction in the head's yaw frame (yaw 0 looks along −Z).
        var sin = MathF.Sin(_yaw);
        var cos = MathF.Cos(_yaw);
        var forward = new Vector3(-sin, 0, -cos);
        var right = new Vector3(cos, 0, -sin);
        var wish = right * move.X - forward * move.Y;

        // Water: wading slows, flow pushes, too deep is a soft wall.
        var feet = GlobalPosition;
        var water = GetWorld3D()?.Water;
        var target = wish * speed;
        Immersion = water?.ImmersionAt(feet) ?? 0f;
        WadeScale = WaterQueries.WadeSpeedScale(Immersion);
        if (water is not null)
        {
            target *= WadeScale;
            if (target.LengthSquared() > 1e-6f)
            {
                var probe = feet + Vector3.Normalize(target) * (Radius + 0.15f);
                var ahead = MathF.Max(water.ImmersionAt(probe), water.WaterDepthAt(probe));
                if (ahead > MaxWadeImmersion && ahead > Immersion)
                    target = Vector3.Zero;
            }

            target += water.FlowAt(feet) * FlowPush;
        }

        // Accelerate the intended horizontal velocity towards the target. It is kept here rather than read back from
        // Velocity: MoveAndSlide removes the part going into a slope, which would bleed speed on every uphill step.
        var onFloor = IsOnFloor();
        _horizontal = MoveToward(_horizontal, target, Acceleration * (onFloor ? 1f : AirControl) * delta);

        // Gravity, coyote time and the jump buffer.
        _coyote = onFloor ? CoyoteTime : _coyote - delta;
        _jumpBuffer = input.IsActionJustPressed("jump") ? CoyoteTime : _jumpBuffer - delta;
        var motion = _horizontal;
        var vertical = Velocity.Y;
        var jumped = false;
        if (_jumpBuffer > 0 && _coyote > 0 && !_crouching)
        {
            jumped = true;
            vertical = JumpVelocity;
            _jumpBuffer = 0;
            _coyote = 0;
        }
        else if (onFloor)
        {
            // Walk along the floor plane at the intended speed (Godot's floor_constant_speed).
            vertical = 0f;
            var normal = GetFloorNormal();
            var intended = _horizontal.Length();
            if (normal.Y > 0.1f && intended > 1e-4f)
            {
                var tangent = _horizontal - normal * Vector3.Dot(_horizontal, normal);
                var length = tangent.Length();
                if (length > 1e-6f)
                    motion = tangent * (intended / length);
                vertical = motion.Y;
            }
        }
        else
        {
            vertical -= Gravity * delta;
        }

        Velocity = new Vector3(motion.X, vertical, motion.Z);
        MoveAndSlide();
        var moved = GetPositionDelta();

        // Uneven ground: walking up one triangle's plane leaves the body just above the next, flatter one, and
        // MoveAndSlide does not snap a body moving up. Stay on the floor when it is within the snap length below.
        if (onFloor && !jumped && !IsOnFloor() && FloorSnapLength > 0f)
            SnapToFloor(delta);

        // Walls stop the intent going into them.
        if (IsOnWall())
        {
            var wall = GetWallNormal();
            var flat = new Vector3(wall.X, 0, wall.Z);
            var flatLength = flat.Length();
            if (flatLength > 1e-4f)
            {
                flat /= flatLength;
                var into = Vector3.Dot(_horizontal, flat);
                if (into < 0)
                    _horizontal -= flat * into;
            }
        }

        // Footsteps: one per stride walked on the floor, and one on landing.
        var nowOnFloor = IsOnFloor();
        if (nowOnFloor)
        {
            var step = MathF.Sqrt(moved.X * moved.X + moved.Z * moved.Z);
            var before = (int)(_distance / StrideLength);
            _distance += step;
            // A landing is a step only after a real fall or jump: bumpy ground loses the floor for a frame or two.
            if ((int)(_distance / StrideLength) != before || (!_wasOnFloor && _airTime > LandingAirTime))
                RaiseFootstep();
            _airTime = 0f;
        }
        else
        {
            _airTime += delta;
        }

        _wasOnFloor = nowOnFloor;
    }

    // A second, downward move of up to FloorSnapLength when a ray finds walkable floor that close below the feet.
    private void SnapToFloor(float delta)
    {
        if (GetWorld3D() is not { } world)
            return;
        var feet = GlobalPosition;
        var up = new Vector3(0f, 0.05f, 0f);
        if (!world.DirectSpaceState.RayCast(feet + up, feet - new Vector3(0f, FloorSnapLength, 0f), out var hit, CollisionMask, this) ||
            hit.Normal.Y < MathF.Cos(FloorMaxAngle))
            return;
        var keep = Velocity;
        Velocity = new Vector3(0f, -(feet.Y - hit.Position.Y + 0.02f) / delta, 0f);
        MoveAndSlide();
        Velocity = new Vector3(keep.X, 0f, keep.Z);
    }

    /// <summary>The surface name under the feet (see the type remarks for the order).</summary>
    public string ResolveSurface()
    {
        var feet = GlobalPosition;
        if (GetWorld3D() is not { } world)
            return DefaultSurface;
        if (world.Water.WaterDepthAt(feet) >= WadeFootstepDepth)
            return "water";
        if (world.DirectSpaceState.RayCast(feet + new Vector3(0, 0.2f, 0), feet - new Vector3(0, 0.4f, 0), out var hit, exclude: this)
            && hit.Collider is IFootstepSurface surface)
            return surface.GetFootstepSurface(hit.Position);
        return SurfaceResolver?.Invoke(feet) ?? DefaultSurface;
    }

    private void RaiseFootstep()
    {
        var surface = ResolveSurface();
        FootstepCount++;
        LastFootstepSurface = surface;
        Footstep?.Invoke(surface);
    }

    private bool CanStand()
    {
        if (GetWorld3D() is not { } world)
            return true;
        // A standing capsule a little above the floor and a little thinner than the body: anything there blocks.
        _standProbe ??= new CapsuleShape3D();
        _standProbe.Radius = Radius * 0.95f;
        _standProbe.Height = MathF.Max(StandingHeight - 0.1f, _standProbe.Radius * 2f);
        var at = Transform3D.FromTrs(GlobalPosition + new Vector3(0, 0.05f + _standProbe.Height * 0.5f, 0), Quaternion.Identity, Vector3.One);
        _overlaps.Clear();
        return world.DirectSpaceState.IntersectShape(_standProbe, at, _overlaps, CollisionMask, this) == 0;
    }

    private void SetCrouching(bool crouching)
    {
        _crouching = crouching;
        ApplyShape(crouching ? CrouchHeight : StandingHeight);
    }

    private void ApplyShape(float height)
    {
        if (_capsule is null || _shape is null)
            return;
        _capsule.Radius = Radius;
        _capsule.Height = MathF.Max(height, Radius * 2f);
        _shape.Position = new Vector3(0, _capsule.Height * 0.5f, 0);
    }

    private void ApplyLook()
    {
        if (_head is not null)
            _head.Rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
    }

    private void PlaceHead()
    {
        if (_head is null)
            return;
        _head.Position = new Vector3(0, _headHeight, 0);
        if (_camera is null)
            return;
        var phase = _distance / StrideLength * MathF.PI;
        // Lowest at each footstep (whole strides), sideways at half the stride frequency.
        var y = -_bobAmplitude * (0.5f + 0.5f * MathF.Cos(2f * phase));
        var x = _bobAmplitude * 0.5f * MathF.Sin(phase);
        _camera.Position = new Vector3(x, y, 0);
    }

    private void UpdateFov()
    {
        if (_camera is null)
            return;
        var size = GetViewport()?.Size ?? Vector2.Zero;
        var aspect = size.X > 0 && size.Y > 0 ? size.X / size.Y : 16f / 9f;
        if (aspect == _appliedAspect && HorizontalFov == _appliedFov)
            return;
        _appliedAspect = aspect;
        _appliedFov = HorizontalFov;
        _camera.Fov = VerticalFov(HorizontalFov, aspect);
    }

    private void EnsureChildren()
    {
        _shape = GetNodeOrNull<CollisionShape3D>("Shape");
        if (_shape is null)
        {
            _shape = new CollisionShape3D { Name = "Shape" };
            AddChild(_shape);
        }

        if (_shape.Shape is not CapsuleShape3D capsule)
        {
            capsule = new CapsuleShape3D();
            _shape.Shape = capsule;
        }

        _capsule = capsule;
        _head = GetNodeOrNull<Node3D>("Head");
        if (_head is null)
        {
            _head = new Node3D { Name = "Head" };
            AddChild(_head);
        }

        _camera = _head.GetNodeOrNull<Camera3D>("Camera");
        if (_camera is null)
        {
            _camera = new Camera3D { Name = "Camera", Current = true, Near = 0.05f };
            _head.AddChild(_camera);
        }
    }

    private static float MoveToward(float from, float to, float maxDelta) =>
        MathF.Abs(to - from) <= maxDelta ? to : from + MathF.Sign(to - from) * maxDelta;

    private static Vector3 MoveToward(Vector3 from, Vector3 to, float maxDelta)
    {
        var d = to - from;
        var length = d.Length();
        return length <= maxDelta || length < 1e-6f ? to : from + d / length * maxDelta;
    }
}

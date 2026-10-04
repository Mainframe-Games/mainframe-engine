using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Project settings of the 3D physics server (<see cref="PhysicsServer3D"/>, Jitter2). Read when a physics space is
/// created; set them before nodes enter the tree (<see cref="EngineOptions.Physics3D"/>).
/// </summary>
public sealed class PhysicsSettings3D
{
    /// <summary>Gravity in m/s² (default 9.81 down, −Y).</summary>
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    /// <summary>Jitter2 sub-steps per fixed step (1 = no sub-stepping).</summary>
    public int SubstepCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;

    /// <summary>Constraint solver iterations per sub-step (Jitter2 default 6).</summary>
    public int SolverIterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 6;

    /// <summary>Velocity relaxation iterations per sub-step (Jitter2 default 4).</summary>
    public int RelaxationIterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 4;

    /// <summary>Lets resting bodies sleep (deactivate) until something wakes them.</summary>
    public bool AllowDeactivation { get; set; } = true;

    /// <summary>
    /// Steps on Jitter2's worker pool. Faster for large worlds; single-threaded steps are reproducible run to run
    /// (render tests turn this off). Physics is non-deterministic across machines either way (ADR 0020).
    /// </summary>
    public bool MultiThreaded { get; set; } = true;
}

/// <summary>
/// Project settings of the 2D physics server (<see cref="PhysicsServer2D"/>, Box2D.NET). 2D nodes work in pixels; the
/// server converts to Box2D's metres with <see cref="PixelsPerMeter"/> at its boundary.
/// </summary>
public sealed class PhysicsSettings2D
{
    /// <summary>Gravity in px/s² (default 980 down, −Y: 9.8 m/s² at 100 px/m). 2D space is Y-up.</summary>
    public Vector2 Gravity { get; set; } = new(0, -980f);

    /// <summary>
    /// Pixels per metre (default 100). Box2D is tuned for objects of 0.1–10 m, so a 100 px crate is 1 m inside
    /// the solver.
    /// </summary>
    public float PixelsPerMeter
    {
        get;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "PixelsPerMeter must be positive and finite.");
            field = value;
        }
    } = 100f;

    /// <summary>Box2D sub-steps per fixed step (Box2D recommends 4).</summary>
    public int SubstepCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 4;

    /// <summary>Lets resting bodies sleep.</summary>
    public bool AllowSleep { get; set; } = true;

    /// <summary>Continuous collision against static geometry for dynamic bodies (Box2D default on).</summary>
    public bool EnableContinuous { get; set; } = true;
}

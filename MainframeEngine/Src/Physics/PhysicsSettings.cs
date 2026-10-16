using System.Numerics;

namespace MainframeEngine;

/// <summary>The 3D physics library a space runs on (ADR 0192). Jolt arrives in B3; until then selecting it logs once and runs Jitter2.</summary>
public enum PhysicsBackendKind
{
    /// <summary>Jitter2 2.9 (pure C#, every platform).</summary>
    Jitter2,

    /// <summary>Jolt 5.5 through BRUTAL (B3).</summary>
    Jolt,
}

/// <summary>
/// Project settings of the 3D physics server (<see cref="PhysicsServer3D"/>): the shared keys plus one group per backend
/// (<see cref="Jitter2"/>, <see cref="Jolt"/>). Read when a physics space is created (<see cref="MultiThreaded"/> at every
/// step); set them before nodes enter the tree (<see cref="EngineOptions.Physics3D"/>).
/// </summary>
public sealed class PhysicsSettings3D
{
    private readonly Jitter2PhysicsSettings _jitter2 = new();

    /// <summary>The library new spaces run on (default Jitter2; <c>physics.3d.backend</c>).</summary>
    public PhysicsBackendKind Backend
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a physics backend.");
            field = value;
        }
    }

    /// <summary>Gravity in m/s² (default 9.81 down, −Y).</summary>
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    /// <summary>Sub-steps per fixed step (1 = no sub-stepping).</summary>
    public int SubstepCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;

    /// <summary>Lets resting bodies sleep (deactivate) until something wakes them.</summary>
    public bool AllowDeactivation { get; set; } = true;

    /// <summary>Steps on the library's worker threads (faster for large worlds).</summary>
    public bool MultiThreaded { get; set; } = true;

    /// <summary>
    /// A deterministic solve instead of the (faster) regular one, so the same build on the same machine produces identical
    /// results run to run — for golden-image tests and replays. It is not a cross-platform lockstep guarantee (ADR 0020).
    /// Read when a space is created.
    /// </summary>
    public bool Deterministic { get; set; }

    /// <summary>What only Jitter2 understands (<c>physics.3d.jitter2</c>).</summary>
    public Jitter2PhysicsSettings Jitter2 => _jitter2;

    /// <summary>What only Jolt understands (<c>physics.3d.jolt</c>; used from B3).</summary>
    public JoltPhysicsSettings Jolt { get; } = new();

    /// <summary>Same as <see cref="Jitter2PhysicsSettings.SolverIterations"/> (kept from before the backend groups).</summary>
    public int SolverIterations
    {
        get => _jitter2.SolverIterations;
        set => _jitter2.SolverIterations = value;
    }

    /// <summary>Same as <see cref="Jitter2PhysicsSettings.RelaxationIterations"/> (kept from before the backend groups).</summary>
    public int RelaxationIterations
    {
        get => _jitter2.RelaxationIterations;
        set => _jitter2.RelaxationIterations = value;
    }
}

/// <summary>Jitter2-only 3D settings (<c>physics.3d.jitter2</c>).</summary>
public sealed class Jitter2PhysicsSettings
{
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
}

/// <summary>Jolt-only 3D settings (<c>physics.3d.jolt</c>): parsed and saved in B2, used by the Jolt backend from B3.</summary>
public sealed class JoltPhysicsSettings
{
    /// <summary>Velocity solver steps per sub-step (Jolt default 10).</summary>
    public int VelocitySteps { get; set => field = AtLeastOne(value); } = 10;

    /// <summary>Position solver steps per sub-step (Jolt default 2).</summary>
    public int PositionSteps { get; set => field = AtLeastOne(value); } = 2;

    /// <summary>Bodies one space can hold.</summary>
    public int MaxBodies { get; set => field = AtLeastOne(value); } = 65536;

    /// <summary>Broad-phase pairs per step.</summary>
    public int MaxBodyPairs { get; set => field = AtLeastOne(value); } = 65536;

    /// <summary>Contact constraints per step.</summary>
    public int MaxContactConstraints { get; set => field = AtLeastOne(value); } = 10240;

    /// <summary>Per-space temporary allocator, in MB.</summary>
    public int TempAllocatorMB { get; set => field = AtLeastOne(value); } = 16;

    /// <summary>Contact events recorded per step for monitors and areas (doubled on overflow).</summary>
    public int ContactEventCapacity { get; set => field = AtLeastOne(value); } = 4096;

    private static int AtLeastOne(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }
}

/// <summary>
/// Project settings of the 2D physics server (<see cref="PhysicsServer2D"/>, Box2D.NET). 2D nodes work in pixels; the
/// server converts to Box2D's metres with <see cref="PixelsPerMeter"/> at its boundary.
/// </summary>
public sealed class PhysicsSettings2D
{
    /// <summary>Gravity in px/s² (default 980 down, +Y: 9.8 m/s² at 100 px/m). 2D space is Y-down, as in Godot.</summary>
    public Vector2 Gravity { get; set; } = new(0, 980f);

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

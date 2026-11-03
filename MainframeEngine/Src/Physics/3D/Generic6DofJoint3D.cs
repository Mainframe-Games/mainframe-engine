using System.Numerics;
using static MainframeEngine.SixDofMath;

namespace MainframeEngine;

/// <summary>How a <see cref="Generic6DofJoint3D"/> treats one axis.</summary>
public enum JointAxisMode
{
    /// <summary>The axis moves freely (a motor or spring may still drive it).</summary>
    Free,

    /// <summary>The axis moves between its lower and upper limits.</summary>
    Limited,

    /// <summary>The axis keeps its authored offset (0).</summary>
    Locked,
}

/// <summary>
/// A joint whose three linear and three angular axes are each free, limited or locked, with a motor and a spring per
/// axis (Godot's <c>Generic6DOFJoint3D</c>, G9.10b). The axes are the joint's local X, Y and Z; offsets are body B
/// relative to body A, measured from the authored pose: positions along frame A's axes, and the rotation's XYZ Euler
/// angles (so the Y angle's limits are within −90°…90°). The default locks every axis: a weld. Everything is live.
/// </summary>
/// <remarks>
/// Built on the engine's own Jitter2 constraints (ADR 0234): locks and limits use Jitter2's softness and bias like the
/// other joints; springs are soft rows in the solver (frequency and damping ratio, independent of mass); motors are
/// velocity rows capped by their force or torque.
/// </remarks>
[EditorIcon("arrows-move", Family = EditorIconFamily.Physics)]
public class Generic6DofJoint3D : Joint3D
{

    /// <summary>How the linear X axis moves.</summary>
    [ExportGroup("Linear X")]
    [Export]
    public JointAxisMode LinearXMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest offset along X in metres, when <see cref="LinearXMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearXLower
    {
        get;
        set => Live(ref field, value);
    } = -1f;

    /// <summary>The highest offset along X in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearXUpper
    {
        get;
        set => Live(ref field, value);
    } = 1f;

    /// <summary>Drive the X offset at <see cref="LinearXMotorTargetVelocity"/>.</summary>
    [Export]
    public bool LinearXMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed along X in metres per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float LinearXMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most force the X motor applies (N).</summary>
    [Export(Range = "0,100000,0.1")]
    public float LinearXMotorMaxForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the X offset toward <see cref="LinearXSpringTarget"/>.</summary>
    [Export]
    public bool LinearXSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The X spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float LinearXSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The X spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float LinearXSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The offset along X the spring pulls toward, in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearXSpringTarget
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>How the linear Y axis moves.</summary>
    [ExportGroup("Linear Y")]
    [Export]
    public JointAxisMode LinearYMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest offset along Y in metres, when <see cref="LinearYMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearYLower
    {
        get;
        set => Live(ref field, value);
    } = -1f;

    /// <summary>The highest offset along Y in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearYUpper
    {
        get;
        set => Live(ref field, value);
    } = 1f;

    /// <summary>Drive the Y offset at <see cref="LinearYMotorTargetVelocity"/>.</summary>
    [Export]
    public bool LinearYMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed along Y in metres per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float LinearYMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most force the Y motor applies (N).</summary>
    [Export(Range = "0,100000,0.1")]
    public float LinearYMotorMaxForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the Y offset toward <see cref="LinearYSpringTarget"/>.</summary>
    [Export]
    public bool LinearYSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The Y spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float LinearYSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The Y spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float LinearYSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The offset along Y the spring pulls toward, in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearYSpringTarget
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>How the linear Z axis moves.</summary>
    [ExportGroup("Linear Z")]
    [Export]
    public JointAxisMode LinearZMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest offset along Z in metres, when <see cref="LinearZMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearZLower
    {
        get;
        set => Live(ref field, value);
    } = -1f;

    /// <summary>The highest offset along Z in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearZUpper
    {
        get;
        set => Live(ref field, value);
    } = 1f;

    /// <summary>Drive the Z offset at <see cref="LinearZMotorTargetVelocity"/>.</summary>
    [Export]
    public bool LinearZMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed along Z in metres per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float LinearZMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most force the Z motor applies (N).</summary>
    [Export(Range = "0,100000,0.1")]
    public float LinearZMotorMaxForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the Z offset toward <see cref="LinearZSpringTarget"/>.</summary>
    [Export]
    public bool LinearZSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The Z spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float LinearZSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The Z spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float LinearZSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The offset along Z the spring pulls toward, in metres.</summary>
    [Export(Range = "-1000,1000,0.001")]
    public float LinearZSpringTarget
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>How the angular X axis turns.</summary>
    [ExportGroup("Angular X")]
    [Export]
    public JointAxisMode AngularXMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest X angle in degrees, when <see cref="AngularXMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularXLowerDegrees
    {
        get => float.RadiansToDegrees(AngularXLower);
        set => AngularXLower = float.DegreesToRadians(value);
    }

    /// <summary>The highest X angle in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularXUpperDegrees
    {
        get => float.RadiansToDegrees(AngularXUpper);
        set => AngularXUpper = float.DegreesToRadians(value);
    }

    /// <summary>The lowest X angle in radians (default −45°).</summary>
    public float AngularXLower
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = -MathF.PI / 4;

    /// <summary>The highest X angle in radians (default 45°).</summary>
    public float AngularXUpper
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = MathF.PI / 4;

    /// <summary>Drive the X angle at <see cref="AngularXMotorTargetVelocity"/>.</summary>
    [Export]
    public bool AngularXMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed about X in radians per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float AngularXMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most torque the X motor applies (N·m).</summary>
    [Export(Range = "0,100000,0.1")]
    public float AngularXMotorMaxTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the X angle toward <see cref="AngularXSpringTarget"/>.</summary>
    [Export]
    public bool AngularXSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The X spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float AngularXSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The X spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float AngularXSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The X angle the spring pulls toward, in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularXSpringTargetDegrees
    {
        get => float.RadiansToDegrees(AngularXSpringTarget);
        set => AngularXSpringTarget = float.DegreesToRadians(value);
    }

    /// <summary>The X angle the spring pulls toward, in radians.</summary>
    public float AngularXSpringTarget
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    }

    /// <summary>How the angular Y axis turns.</summary>
    [ExportGroup("Angular Y")]
    [Export]
    public JointAxisMode AngularYMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest Y angle in degrees, when <see cref="AngularYMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-90,90,0.1")]
    public float AngularYLowerDegrees
    {
        get => float.RadiansToDegrees(AngularYLower);
        set => AngularYLower = float.DegreesToRadians(value);
    }

    /// <summary>The highest Y angle in degrees.</summary>
    [Export(Range = "-90,90,0.1")]
    public float AngularYUpperDegrees
    {
        get => float.RadiansToDegrees(AngularYUpper);
        set => AngularYUpper = float.DegreesToRadians(value);
    }

    /// <summary>The lowest Y angle in radians (default −45°).</summary>
    public float AngularYLower
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI / 2, MathF.PI / 2));
    } = -MathF.PI / 4;

    /// <summary>The highest Y angle in radians (default 45°).</summary>
    public float AngularYUpper
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI / 2, MathF.PI / 2));
    } = MathF.PI / 4;

    /// <summary>Drive the Y angle at <see cref="AngularYMotorTargetVelocity"/>.</summary>
    [Export]
    public bool AngularYMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed about Y in radians per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float AngularYMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most torque the Y motor applies (N·m).</summary>
    [Export(Range = "0,100000,0.1")]
    public float AngularYMotorMaxTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the Y angle toward <see cref="AngularYSpringTarget"/>.</summary>
    [Export]
    public bool AngularYSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The Y spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float AngularYSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The Y spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float AngularYSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The Y angle the spring pulls toward, in degrees.</summary>
    [Export(Range = "-90,90,0.1")]
    public float AngularYSpringTargetDegrees
    {
        get => float.RadiansToDegrees(AngularYSpringTarget);
        set => AngularYSpringTarget = float.DegreesToRadians(value);
    }

    /// <summary>The Y angle the spring pulls toward, in radians.</summary>
    public float AngularYSpringTarget
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI / 2, MathF.PI / 2));
    }

    /// <summary>How the angular Z axis turns.</summary>
    [ExportGroup("Angular Z")]
    [Export]
    public JointAxisMode AngularZMode
    {
        get;
        set => Live(ref field, value);
    } = JointAxisMode.Locked;

    /// <summary>The lowest Z angle in degrees, when <see cref="AngularZMode"/> is <see cref="JointAxisMode.Limited"/>.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularZLowerDegrees
    {
        get => float.RadiansToDegrees(AngularZLower);
        set => AngularZLower = float.DegreesToRadians(value);
    }

    /// <summary>The highest Z angle in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularZUpperDegrees
    {
        get => float.RadiansToDegrees(AngularZUpper);
        set => AngularZUpper = float.DegreesToRadians(value);
    }

    /// <summary>The lowest Z angle in radians (default −45°).</summary>
    public float AngularZLower
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = -MathF.PI / 4;

    /// <summary>The highest Z angle in radians (default 45°).</summary>
    public float AngularZUpper
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = MathF.PI / 4;

    /// <summary>Drive the Z angle at <see cref="AngularZMotorTargetVelocity"/>.</summary>
    [Export]
    public bool AngularZMotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed about Z in radians per second.</summary>
    [Export(Range = "-100,100,0.01")]
    public float AngularZMotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most torque the Z motor applies (N·m).</summary>
    [Export(Range = "0,100000,0.1")]
    public float AngularZMotorMaxTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull the Z angle toward <see cref="AngularZSpringTarget"/>.</summary>
    [Export]
    public bool AngularZSpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The Z spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float AngularZSpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The Z spring's damping ratio (1: critically damped).</summary>
    [Export(Range = "0,10,0.01")]
    public float AngularZSpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The Z angle the spring pulls toward, in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float AngularZSpringTargetDegrees
    {
        get => float.RadiansToDegrees(AngularZSpringTarget);
        set => AngularZSpringTarget = float.DegreesToRadians(value);
    }

    /// <summary>The Z angle the spring pulls toward, in radians.</summary>
    public float AngularZSpringTarget
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    }

    /// <summary>An axis's mode (0 = X, 1 = Y, 2 = Z).</summary>
    public JointAxisMode GetLinearMode(int axis) => axis switch { 0 => LinearXMode, 1 => LinearYMode, _ => LinearZMode };

    /// <summary>An axis's mode (0 = X, 1 = Y, 2 = Z).</summary>
    public JointAxisMode GetAngularMode(int axis) => axis switch { 0 => AngularXMode, 1 => AngularYMode, _ => AngularZMode };

    /// <summary>Where body B's anchor is, in metres along the joint's axes on body A (0 at the authored pose).</summary>
    public Vector3 GetLinearOffset()
    {
        if (Record is not { HasJoint: true } record)
            return Vector3.Zero;
        record.GetFrameA(out var a, out var rotationA);
        record.GetFrameB(out var b, out _);
        return Vector3.Transform(b - a, Quaternion.Conjugate(rotationA));
    }

    /// <summary>Body B's rotation relative to body A as XYZ Euler angles in radians (0 at the authored pose).</summary>
    public Vector3 GetAngularOffset()
    {
        if (Record is not { HasJoint: true } record)
            return Vector3.Zero;
        record.GetFrameA(out _, out var rotationA);
        record.GetFrameB(out _, out var rotationB);
        return EulerXYZ(Relative(rotationA, rotationB));
    }

    /// <summary><see cref="GetAngularOffset"/> in degrees.</summary>
    public Vector3 GetAngularOffsetDegrees()
    {
        var angles = GetAngularOffset();
        return new Vector3(float.RadiansToDegrees(angles.X), float.RadiansToDegrees(angles.Y), float.RadiansToDegrees(angles.Z));
    }

    /// <summary>A linear axis's limits in metres.</summary>
    private (float Lower, float Upper) LinearLimits(int axis) => axis switch
    {
        0 => (LinearXLower, LinearXUpper),
        1 => (LinearYLower, LinearYUpper),
        _ => (LinearZLower, LinearZUpper),
    };

    /// <summary>An angular axis's limits in radians.</summary>
    private (float Lower, float Upper) AngularLimits(int axis) => axis switch
    {
        0 => (AngularXLower, AngularXUpper),
        1 => (AngularYLower, AngularYUpper),
        _ => (AngularZLower, AngularZUpper),
    };

    // How far an offset is past what its mode allows (0 when free or inside).
    private static float Excess(JointAxisMode mode, float value, float lower, float upper) => mode switch
    {
        JointAxisMode.Locked => MathF.Abs(value),
        JointAxisMode.Limited => MathF.Max(0f, MathF.Max(MathF.Min(lower, upper) - value, value - MathF.Max(lower, upper))),
        _ => 0f,
    };

    /// <summary>Stretched: a locked or limited axis more than 5 cm or 5° past what it allows.</summary>
    internal override bool IsStretched(JointRecord3D record, Vector3 anchorA, Vector3 anchorB)
    {
        var linear = GetLinearOffset();
        var angular = GetAngularOffset();
        for (var i = 0; i < 3; i++)
        {
            var (lower, upper) = LinearLimits(i);
            if (Excess(GetLinearMode(i), linear[i], lower, upper) > 0.05f)
                return true;
            (lower, upper) = AngularLimits(i);
            if (Excess(GetAngularMode(i), angular[i], lower, upper) > float.DegreesToRadians(5f))
                return true;
        }

        return false;
    }

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        const float axisLength = 0.15f, tick = 0.03f, radius = 0.2f;
        var angles = GetAngularOffset();
        for (var i = 0; i < 3; i++)
        {
            var axis = Axis(i);
            var u = Axis((i + 1) % 3);
            var v = Axis((i + 2) % 3);
            lines.AddLine(frame, Vector3.Zero, axis * axisLength, color);
            if (GetLinearMode(i) == JointAxisMode.Limited)
            {
                var (lower, upper) = LinearLimits(i);
                lines.AddLine(frame, axis * lower, axis * upper, color);
                lines.AddLine(frame, axis * lower - u * tick, axis * lower + u * tick, color);
                lines.AddLine(frame, axis * upper - u * tick, axis * upper + u * tick, color);
            }

            if (GetAngularMode(i) == JointAxisMode.Limited)
            {
                // About axis i, from +u toward +v (right-handed): the limit arc, its ends, and the current angle.
                var (lower, upper) = AngularLimits(i);
                (lower, upper) = (MathF.Min(lower, upper), MathF.Max(lower, upper));
                DrawArc(lines, frame, u, v, radius, lower, upper, color);
                lines.AddLine(frame, Vector3.Zero, (u * MathF.Cos(lower) + v * MathF.Sin(lower)) * radius, color);
                lines.AddLine(frame, Vector3.Zero, (u * MathF.Cos(upper) + v * MathF.Sin(upper)) * radius, color);
                lines.AddLine(frame, Vector3.Zero, (u * MathF.Cos(angles[i]) + v * MathF.Sin(angles[i])) * radius * 1.3f, color);
            }
        }
    }

    internal override void CreateConstraints(JointRecord3D record) => record.Space.CreateSixDof(record);

    internal override void ApplyLive(JointRecord3D record)
    {
        if (record.SixDofLinear is { IsValid: true } linear)
        {
            ref var data = ref linear.Data;
            data.Modes = Modes(LinearXMode, LinearYMode, LinearZMode);
            SetLimit(ref data.Rows[0], LinearXLower, LinearXUpper);
            SetLimit(ref data.Rows[1], LinearYLower, LinearYUpper);
            SetLimit(ref data.Rows[2], LinearZLower, LinearZUpper);
        }

        if (record.SixDofAngular is { IsValid: true } angular)
        {
            ref var data = ref angular.Data;
            data.Modes = Modes(AngularXMode, AngularYMode, AngularZMode);
            SetLimit(ref data.Rows[0], AngularXLower, AngularXUpper);
            SetLimit(ref data.Rows[1], AngularYLower, AngularYUpper);
            SetLimit(ref data.Rows[2], AngularZLower, AngularZUpper);
        }

        if (record.SixDofLinearDrive is { IsValid: true } linearDrive)
        {
            ref var data = ref linearDrive.Data;
            data.Flags = DriveFlags(LinearXMotorEnabled, LinearYMotorEnabled, LinearZMotorEnabled, LinearXSpringEnabled && LinearXSpringFrequency > 0f,
                LinearYSpringEnabled && LinearYSpringFrequency > 0f, LinearZSpringEnabled && LinearZSpringFrequency > 0f);
            SetDrive(ref data.Rows[0], LinearXMotorTargetVelocity, LinearXMotorMaxForce, LinearXSpringTarget, LinearXSpringFrequency, LinearXSpringDampingRatio);
            SetDrive(ref data.Rows[1], LinearYMotorTargetVelocity, LinearYMotorMaxForce, LinearYSpringTarget, LinearYSpringFrequency, LinearYSpringDampingRatio);
            SetDrive(ref data.Rows[2], LinearZMotorTargetVelocity, LinearZMotorMaxForce, LinearZSpringTarget, LinearZSpringFrequency, LinearZSpringDampingRatio);
        }

        if (record.SixDofAngularDrive is { IsValid: true } angularDrive)
        {
            ref var data = ref angularDrive.Data;
            data.Flags = DriveFlags(AngularXMotorEnabled, AngularYMotorEnabled, AngularZMotorEnabled, AngularXSpringEnabled && AngularXSpringFrequency > 0f,
                AngularYSpringEnabled && AngularYSpringFrequency > 0f, AngularZSpringEnabled && AngularZSpringFrequency > 0f);
            SetDrive(ref data.Rows[0], AngularXMotorTargetVelocity, AngularXMotorMaxTorque, AngularXSpringTarget, AngularXSpringFrequency,
                AngularXSpringDampingRatio);
            SetDrive(ref data.Rows[1], AngularYMotorTargetVelocity, AngularYMotorMaxTorque, AngularYSpringTarget, AngularYSpringFrequency,
                AngularYSpringDampingRatio);
            SetDrive(ref data.Rows[2], AngularZMotorTargetVelocity, AngularZMotorMaxTorque, AngularZSpringTarget, AngularZSpringFrequency,
                AngularZSpringDampingRatio);
        }
    }

    private static uint Modes(JointAxisMode x, JointAxisMode y, JointAxisMode z) => Set(Set(Set(0u, 0, (uint)x), 1, (uint)y), 2, (uint)z);

    private static void SetLimit(ref SixDofLimitRow row, float lower, float upper)
    {
        row.Lower = lower;
        row.Upper = upper;
    }

    private static uint DriveFlags(bool motorX, bool motorY, bool motorZ, bool springX, bool springY, bool springZ) =>
        (motorX ? 1u : 0u) | (motorY ? 2u : 0u) | (motorZ ? 4u : 0u) | (springX ? 8u : 0u) | (springY ? 16u : 0u) | (springZ ? 32u : 0u);

    private static void SetDrive(ref SixDofDriveRow row, float velocity, float maxForce, float target, float frequency, float dampingRatio)
    {
        row.MotorVelocity = velocity;
        row.MotorMaxForce = maxForce;
        row.SpringTarget = target;
        row.SpringFrequency = frequency;
        row.SpringDampingRatio = dampingRatio;
    }
}

namespace MainframeEngine;

/// <summary>
/// Surface properties shared by static and rigid bodies (Godot's <c>PhysicsMaterial</c>): friction and bounce. A
/// <see cref="Resource"/>, so one material can be shared and saved. Bodies without one use the defaults.
/// </summary>
public sealed class PhysicsMaterial : Resource
{
    /// <summary>Default friction for bodies without a material.</summary>
    public const float DefaultFriction = 0.5f;

    /// <summary>Coulomb friction coefficient (0 = ice).</summary>
    [Export(Range = "0,2,0.01")]
    public float Friction
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (field == value)
                return;
            field = value;
            EmitChanged();
        }
    } = DefaultFriction;

    /// <summary>Restitution (0 = no bounce, 1 = perfectly elastic).</summary>
    [Export(Range = "0,1,0.01")]
    public float Bounce
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (field == value)
                return;
            field = value;
            EmitChanged();
        }
    }
}

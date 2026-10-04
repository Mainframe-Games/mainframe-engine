namespace MainframeEngine;

/// <summary>
/// A node's subscription to a resource's <see cref="Resource.Changed"/> event while it is inside the tree: tracks
/// whether it is subscribed, so swapping the resource or leaving the tree never leaks or doubles the handler.
/// </summary>
internal sealed class ResourceSubscription<T>(Action onChanged) where T : Resource
{
    private bool _active;
    private bool _subscribed;

    public T? Value { get; private set; }

    /// <summary>Replaces the resource; returns false if it was already the value.</summary>
    public bool Set(T? value)
    {
        if (ReferenceEquals(Value, value))
            return false;
        Unsubscribe();
        Value = value;
        if (_active)
            Subscribe();
        return true;
    }

    /// <summary>The owner entered the tree.</summary>
    public void Activate()
    {
        _active = true;
        Subscribe();
    }

    /// <summary>The owner left the tree.</summary>
    public void Deactivate()
    {
        _active = false;
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed || Value is null)
            return;
        Value.Changed += onChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || Value is null)
            return;
        Value.Changed -= onChanged;
        _subscribed = false;
    }
}

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

namespace MainframeEngine;

/// <summary>How an <see cref="EditValue"/> changes a value.</summary>
public enum EditOperation : byte
{
    /// <summary>Replace it (<c>20</c>).</summary>
    Set,

    /// <summary>Add to it (<c>"+=5"</c>, <c>"-=10"</c>).</summary>
    Add,

    /// <summary>Multiply it (<c>"*1.3"</c>).</summary>
    Scale,
}

/// <summary>One value of an edit script: absolute, relative or scaled.</summary>
public readonly record struct EditValue(EditOperation Operation, float Amount)
{
    public float ApplyTo(float value) => Operation switch
    {
        EditOperation.Add => value + Amount,
        EditOperation.Scale => value * Amount,
        _ => Amount,
    };
}

/// <summary>The joint angles an edit changes; null components stay.</summary>
public readonly record struct EditAngles(EditValue? Flex = null, EditValue? Abduct = null, EditValue? Twist = null)
{
    public JointAngles ApplyTo(JointAngles angles) => new(
        Flex?.ApplyTo(angles.Flex) ?? angles.Flex,
        Abduct?.ApplyTo(angles.Abduct) ?? angles.Abduct,
        Twist?.ApplyTo(angles.Twist) ?? angles.Twist);
}

/// <summary>The hips values an edit changes; null components stay.</summary>
public readonly record struct EditHips(EditValue? X = null, EditValue? Y = null, EditValue? Z = null, EditValue? Yaw = null,
    EditValue? Pitch = null, EditValue? Roll = null)
{
    public bool MovesPosition => X is not null || Y is not null || Z is not null;

    public bool Turns => Yaw is not null || Pitch is not null || Roll is not null;

    public HipsPose ApplyTo(HipsPose hips) => new(
        new System.Numerics.Vector3(X?.ApplyTo(hips.Offset.X) ?? hips.Offset.X, Y?.ApplyTo(hips.Offset.Y) ?? hips.Offset.Y,
            Z?.ApplyTo(hips.Offset.Z) ?? hips.Offset.Z),
        Yaw?.ApplyTo(hips.Yaw) ?? hips.Yaw, Pitch?.ApplyTo(hips.Pitch) ?? hips.Pitch, Roll?.ApplyTo(hips.Roll) ?? hips.Roll);
}

/// <summary>
/// One edit of a <see cref="PoseEditScript"/>: at a key (<see cref="Time"/>), at a new key (<see cref="Insert"/>), or on
/// every key in <see cref="Range"/>.
/// </summary>
public sealed record PoseEdit
{
    /// <summary>The JSON path of this edit (<c>edits[2]</c>), for errors found when it is applied.</summary>
    public required string Path { get; init; }

    /// <summary>The key time to edit; null with <see cref="Range"/>.</summary>
    public float? Time { get; init; }

    /// <summary>Every key time from <c>From</c> to <c>To</c> (inclusive); null with <see cref="Time"/>.</summary>
    public (float From, float To)? Range { get; init; }

    /// <summary>Add a key at <see cref="Time"/> (sampled from the clip, then edited) instead of editing an existing one.</summary>
    public bool Insert { get; init; }

    public EditHips? Hips { get; init; }

    public IReadOnlyDictionary<HumanoidBone, EditAngles> Joints { get; init; } = new Dictionary<HumanoidBone, EditAngles>();

    /// <summary>Apply each named side bone's edit to its mirror too, where the edit does not name the mirror.</summary>
    public bool Mirror { get; init; }

    /// <summary><see cref="IkCommandKind.Pin"/> (hold where the clip had it) or <see cref="IkCommandKind.Target"/> per effector.</summary>
    public IReadOnlyDictionary<IkEffector, IkCommand> Ik { get; init; } = new Dictionary<IkEffector, IkCommand>();

    /// <summary>A new ease for the segment after each edited key, on every track keyed there.</summary>
    public float? Ease { get; init; }

    /// <summary>A new apex for the hips' rise over <see cref="Range"/> (metres above take-off).</summary>
    public EditValue? ArcHeight { get; init; }
}

/// <summary>
/// An edit script (G1c): changes on top of a clip's current keys, hand fixes included (keys are the truth). Applied by
/// <see cref="PoseEditor"/>. Input only, like a <see cref="PoseScript"/>.
/// </summary>
public sealed record PoseEditScript
{
    /// <summary>The rigged model (project path).</summary>
    public required string Model { get; init; }

    /// <summary>The library <c>.mfres</c> that holds the base clip and receives the result (project path).</summary>
    public required string Library { get; init; }

    /// <summary>The clip edited.</summary>
    public required string Base { get; init; }

    /// <summary>The result's name: <see cref="Base"/> to change it, another name to write a variant.</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<PoseEdit> Edits { get; init; }
}

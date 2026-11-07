namespace MainframeEngine;

/// <summary>Where a <see cref="HumanoidMap"/> entry came from.</summary>
public enum HumanoidMapSource : byte
{
    /// <summary>The rig bone's name matched a table (VRM, Mixamo, Unreal, Blender/Rigify, generic).</summary>
    Name,

    /// <summary>The skeleton's shape: the bone sits where that canonical bone does (chains from the hips).</summary>
    Structure,

    /// <summary>The model's <c>.mfmeta</c> <c>humanoid</c> setting.</summary>
    Override,
}

/// <summary>One mapped bone: the canonical bone, the rig bone, where the match came from and how sure it is (0–1).</summary>
public readonly record struct HumanoidMapEntry(HumanoidBone Bone, string RigBone, HumanoidMapSource Source, float Confidence);

/// <summary>What <see cref="HumanoidMap.Detect"/> found: every entry, the rig bones left unmapped, what is missing, notes.</summary>
public sealed class HumanoidMapReport
{
    /// <summary>The mapped bones in canonical order.</summary>
    public required IReadOnlyList<HumanoidMapEntry> Entries { get; init; }

    /// <summary>Rig bones no canonical bone maps to (ends, twist and helper bones, extras), in skeleton order.</summary>
    public required IReadOnlyList<string> UnmappedRigBones { get; init; }

    /// <summary>Required canonical bones (<see cref="HumanoidBones.IsRequired"/>) the rig has no bone for.</summary>
    public required IReadOnlyList<HumanoidBone> MissingRequired { get; init; }

    /// <summary>Things a person may want to fix with an override: conflicts, side mismatches, unknown override names.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>True when every required bone is mapped.</summary>
    public bool IsComplete => MissingRequired.Count == 0;
}

/// <summary>
/// Which rig bone each <see cref="HumanoidBone"/> is (G1c, ADR 0281). Immutable. <see cref="Detect"/> builds one from a
/// skeleton (names first, then structure, overrides from the model's <c>.mfmeta</c> winning); <see cref="HumanoidRig"/> binds it
/// to a skeleton's rest pose.
/// </summary>
public sealed class HumanoidMap
{
    private readonly string?[] _rigBones = new string?[HumanoidBones.Count];

    public HumanoidMap(IReadOnlyDictionary<HumanoidBone, string> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        foreach (var (bone, rigBone) in bones)
            if (!string.IsNullOrEmpty(rigBone))
                _rigBones[(int)bone] = rigBone;
    }

    /// <summary>The rig bone's name for <paramref name="bone"/>, or null when it is unmapped.</summary>
    public string? Get(HumanoidBone bone) => _rigBones[(int)bone];

    /// <summary>True when <paramref name="bone"/> is mapped.</summary>
    public bool Has(HumanoidBone bone) => _rigBones[(int)bone] is not null;

    /// <summary>The mapped bones (canonical order), as stored in a <c>.mfmeta</c> <c>humanoid</c> setting.</summary>
    public IReadOnlyDictionary<HumanoidBone, string> ToDictionary()
    {
        var map = new Dictionary<HumanoidBone, string>();
        foreach (var bone in HumanoidBones.All)
            if (_rigBones[(int)bone] is { } rigBone)
                map[bone] = rigBone;
        return map;
    }

    /// <summary>
    /// Maps <paramref name="skeleton"/>'s bones to canonical bones: bone names first (known prefixes such as
    /// <c>mixamorig:</c> stripped, sides from <c>Left</c>/<c>L</c>/<c>.L</c>/<c>_l</c> tokens), then the skeleton's structure for
    /// what the names miss, then <paramref name="overrides"/> (canonical bone name → rig bone name, from the <c>.mfmeta</c>),
    /// which win. <paramref name="report"/> lists every entry's source, unmapped rig bones, missing bones and notes.
    /// </summary>
    public static HumanoidMap Detect(Skeleton3D skeleton, IReadOnlyDictionary<string, string>? overrides,
        out HumanoidMapReport report)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return HumanoidDetector.Detect(skeleton, overrides, out report);
    }
}

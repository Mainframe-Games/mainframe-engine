using System.Numerics;

namespace MainframeEngine;

/// <summary>The kinds of <see cref="ShapeDescription3D"/>, one per <see cref="Shape3D"/> resource type.</summary>
internal enum ShapeKind3D : byte
{
    Box,
    Sphere,
    Capsule,
    Cylinder,
    ConvexHull,
    ConcaveMesh,
    HeightMap,
}

/// <summary>
/// A library-neutral shape: what <see cref="Shape3D.Describe"/> hands to a <see cref="PhysicsBackend3D"/>. Sizes are the
/// resource's own; <see cref="Linear"/> (rotation × scale, the body's scale baked in) and <see cref="Translation"/> place it
/// in its body's frame. <see cref="Points"/> is the resource's array (never copied): the hull points, or 3 vertices per
/// triangle for <see cref="ShapeKind3D.ConcaveMesh"/> and <see cref="ShapeKind3D.HeightMap"/> (the height map's triangle
/// list; <see cref="Resource"/> has its samples). Backends cache native shapes by (<see cref="Resource"/>,
/// <see cref="ChangeCount"/>, baked scale).
/// </summary>
internal readonly record struct ShapeDescription3D
{
    public ShapeKind3D Kind { get; init; }

    public Shape3D Resource { get; init; }

    /// <summary><see cref="Shape3D.ChangeCount"/> when described.</summary>
    public int ChangeCount { get; init; }

    /// <summary>Box: full extents.</summary>
    public Vector3 Size { get; init; }

    /// <summary>Sphere, capsule, cylinder.</summary>
    public float Radius { get; init; }

    /// <summary>Capsule (caps included) and cylinder: total height.</summary>
    public float Height { get; init; }

    public Vector3[]? Points { get; init; }

    public Basis Linear { get; init; }

    public Vector3 Translation { get; init; }

    /// <summary>The capsule's cylindrical part (as <see cref="CapsuleShape3D.MidHeight"/>).</summary>
    public float MidHeight => MathF.Max(0, Height - 2 * Radius);
}

using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// A curve the curve tool edits (G13, docs/design/paths.md#editor): its points and handles in the node's local space and
/// the node's transform, in 3D coordinates (2D curves use z = 0), so one tool serves <see cref="Path2D"/> now and
/// <see cref="Path3D"/> later. Handles are relative to their point, as in the curve resources.
/// </summary>
public interface ICurveTarget
{
    /// <summary>The node that holds the curve.</summary>
    Node Node { get; }

    /// <summary>The curve resource (undo actions reference it, never the tool).</summary>
    Resource Curve { get; }

    /// <summary>The curve's exported point storage (<c>Points</c>), set by undo.</summary>
    ExportPropertyInfo PointsProperty { get; }

    /// <summary>The curve's exported <c>Closed</c> flag.</summary>
    ExportPropertyInfo ClosedProperty { get; }

    int PointCount { get; }

    bool Closed { get; }

    Vector3 GetPosition(int index);

    void SetPosition(int index, Vector3 position);

    Vector3 GetIn(int index);

    void SetIn(int index, Vector3 @in);

    Vector3 GetOut(int index);

    void SetOut(int index, Vector3 @out);

    void AddPoint(Vector3 position, Vector3 @in, Vector3 @out, int index);

    void RemovePoint(int index);

    void Reverse();

    /// <summary>Local → the view's world space (2D: canvas pixels, z = 0).</summary>
    Vector3 ToGlobal(Vector3 local);

    /// <summary>The view's world space → local.</summary>
    Vector3 ToLocal(Vector3 global);

    /// <summary>The curve has a per-point tilt (roll about the curve, radians): 3D curves.</summary>
    bool HasTilt => false;

    float GetTilt(int index) => 0f;

    void SetTilt(int index, float tilt)
    {
    }

    /// <summary>The curve has per-point width and depth (a <see cref="River3D"/>'s banks and channel): the width and depth handles.</summary>
    bool HasWidth => false;

    /// <summary>The full width at point <paramref name="index"/> (metres).</summary>
    float GetWidth(int index) => 0f;

    void SetWidth(int index, float width)
    {
    }

    /// <summary>The centreline depth at point <paramref name="index"/> (metres).</summary>
    float GetDepth(int index) => 0f;

    void SetDepth(int index, float depth)
    {
    }

    /// <summary>
    /// The curve's frame at point <paramref name="index"/> in world space, before tilt: the unit tangent and the up vector
    /// the tilt rolls (the tilt ring's axis and zero). 2D curves: along the curve and the screen's −Z.
    /// </summary>
    void FrameAt(int index, out Vector3 tangent, out Vector3 up)
    {
        tangent = Vector3.UnitX;
        up = Vector3.UnitY;
    }
}

/// <summary>A <see cref="Path2D"/>'s <see cref="Curve2D"/> as an <see cref="ICurveTarget"/>.</summary>
public sealed class Curve2DTarget : ICurveTarget
{
    private static ExportPropertyInfo? _points;
    private static ExportPropertyInfo? _closed;

    public Curve2DTarget(Path2D path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        Curve2D = path.Curve ?? throw new ArgumentException("The path has no curve.", nameof(path));
    }

    public Path2D Path { get; }

    public Curve2D Curve2D { get; }

    public Node Node => Path;

    public Resource Curve => Curve2D;

    public ExportPropertyInfo PointsProperty => _points ??= Find("Points");

    public ExportPropertyInfo ClosedProperty => _closed ??= Find("Closed");

    public int PointCount => Curve2D.PointCount;

    public bool Closed => Curve2D.Closed;

    public Vector3 GetPosition(int index) => new(Curve2D.GetPointPosition(index), 0f);

    public void SetPosition(int index, Vector3 position) => Curve2D.SetPointPosition(index, Flat(position));

    public Vector3 GetIn(int index) => new(Curve2D.GetPointIn(index), 0f);

    public void SetIn(int index, Vector3 @in) => Curve2D.SetPointIn(index, Flat(@in));

    public Vector3 GetOut(int index) => new(Curve2D.GetPointOut(index), 0f);

    public void SetOut(int index, Vector3 @out) => Curve2D.SetPointOut(index, Flat(@out));

    public void AddPoint(Vector3 position, Vector3 @in, Vector3 @out, int index) => Curve2D.AddPoint(Flat(position), Flat(@in), Flat(@out), index);

    public void RemovePoint(int index) => Curve2D.RemovePoint(index);

    public void Reverse() => Curve2D.Reverse();

    public Vector3 ToGlobal(Vector3 local) => new(Path.GlobalTransform.TransformPoint(Flat(local)), 0f);

    public Vector3 ToLocal(Vector3 global) => new(Path.GlobalTransform.AffineInverse().TransformPoint(Flat(global)), 0f);

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    private static ExportPropertyInfo Find(string name) =>
        TypeRegistry.GetNearest(typeof(Curve2D))?.FindProperty(name)
        ?? throw new InvalidOperationException($"Curve2D.{name} is not registered.");
}

/// <summary>
/// A <see cref="Curve3D"/> on a 3D node as an <see cref="ICurveTarget"/>: a <see cref="Path3D"/>'s (G13.6, with tilt) or a
/// <see cref="River3D"/>'s (G8c.4, with width and depth).
/// </summary>
public sealed class Curve3DTarget : ICurveTarget
{
    private static ExportPropertyInfo? _points;
    private static ExportPropertyInfo? _closed;

    public Curve3DTarget(Path3D path)
        : this(path, path?.Curve ?? throw new ArgumentException("The path has no curve.", nameof(path)))
    {
    }

    public Curve3DTarget(Node3D owner, Curve3D curve)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Path = owner;
        Curve3D = curve ?? throw new ArgumentNullException(nameof(curve));
    }

    /// <summary>The node holding the curve (a <see cref="Path3D"/> or a <see cref="River3D"/>).</summary>
    public Node3D Path { get; }

    public Curve3D Curve3D { get; }

    public Node Node => Path;

    public bool HasWidth => Path is River3D;

    public float GetWidth(int index) => Curve3D.GetPointWidth(index);

    public void SetWidth(int index, float width) => Curve3D.SetPointWidth(index, width);

    public float GetDepth(int index) => Curve3D.GetPointDepth(index);

    public void SetDepth(int index, float depth) => Curve3D.SetPointDepth(index, depth);

    public Resource Curve => Curve3D;

    public ExportPropertyInfo PointsProperty => _points ??= Find("Points");

    public ExportPropertyInfo ClosedProperty => _closed ??= Find("Closed");

    public int PointCount => Curve3D.PointCount;

    public bool Closed => Curve3D.Closed;

    public bool HasTilt => Path is Path3D; // a river's sections stay level

    public Vector3 GetPosition(int index) => Curve3D.GetPointPosition(index);

    public void SetPosition(int index, Vector3 position) => Curve3D.SetPointPosition(index, position);

    public Vector3 GetIn(int index) => Curve3D.GetPointIn(index);

    public void SetIn(int index, Vector3 @in) => Curve3D.SetPointIn(index, @in);

    public Vector3 GetOut(int index) => Curve3D.GetPointOut(index);

    public void SetOut(int index, Vector3 @out) => Curve3D.SetPointOut(index, @out);

    public float GetTilt(int index) => Curve3D.GetPointTilt(index);

    public void SetTilt(int index, float tilt) => Curve3D.SetPointTilt(index, tilt);

    public void AddPoint(Vector3 position, Vector3 @in, Vector3 @out, int index) => Curve3D.AddPoint(position, @in, @out, index);

    public void RemovePoint(int index) => Curve3D.RemovePoint(index);

    public void Reverse() => Curve3D.Reverse();

    public Vector3 ToGlobal(Vector3 local) => Path.GlobalTransform.TransformPoint(local);

    public Vector3 ToLocal(Vector3 global) => Path.GlobalTransform.AffineInverse().TransformPoint(global);

    public void FrameAt(int index, out Vector3 tangent, out Vector3 up)
    {
        var offset = Curve3D.GetClosestOffset(Curve3D.GetPointPosition(index));
        var basis = Path.GlobalTransform.Basis;
        tangent = Normalized(basis.Transform(Curve3D.SampleBakedTangent(offset)), Vector3.UnitZ);
        up = Normalized(basis.Transform(Curve3D.SampleBakedUpVector(offset)), Vector3.UnitY);
    }

    private static Vector3 Normalized(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;

    private static ExportPropertyInfo Find(string name) =>
        TypeRegistry.GetNearest(typeof(Curve3D))?.FindProperty(name)
        ?? throw new InvalidOperationException($"Curve3D.{name} is not registered.");
}

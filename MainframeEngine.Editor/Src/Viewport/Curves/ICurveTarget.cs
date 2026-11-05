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

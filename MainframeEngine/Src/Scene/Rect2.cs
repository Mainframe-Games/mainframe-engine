using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// An axis-aligned 2D rectangle (Godot's <c>Rect2</c>): <see cref="Position"/> is the top-left corner in Y-down 2D
/// space, <see cref="Size"/> may be negative until <see cref="Abs"/>. Used by the canvas API.
/// </summary>
public struct Rect2 : IEquatable<Rect2>
{
    public Vector2 Position;
    public Vector2 Size;

    public Rect2(Vector2 position, Vector2 size)
    {
        Position = position;
        Size = size;
    }

    public Rect2(float x, float y, float width, float height)
        : this(new Vector2(x, y), new Vector2(width, height))
    {
    }

    /// <summary>The far corner (<see cref="Position"/> + <see cref="Size"/>).</summary>
    public readonly Vector2 End => Position + Size;

    public readonly float Area => Size.X * Size.Y;

    /// <summary>The same rectangle with a non-negative size.</summary>
    public readonly Rect2 Abs() => new(Vector2.Min(Position, Position + Size), Vector2.Abs(Size));

    /// <summary>True when <paramref name="point"/> lies inside (the far edges excluded, as in Godot).</summary>
    public readonly bool HasPoint(Vector2 point) =>
        point.X >= Position.X && point.Y >= Position.Y && point.X < Position.X + Size.X && point.Y < Position.Y + Size.Y;

    /// <summary>True when the rectangles overlap (touching edges count only with <paramref name="includeBorders"/>).</summary>
    public readonly bool Intersects(Rect2 other, bool includeBorders = false)
    {
        if (includeBorders)
            return Position.X <= other.Position.X + other.Size.X && Position.X + Size.X >= other.Position.X &&
                   Position.Y <= other.Position.Y + other.Size.Y && Position.Y + Size.Y >= other.Position.Y;
        return Position.X < other.Position.X + other.Size.X && Position.X + Size.X > other.Position.X &&
               Position.Y < other.Position.Y + other.Size.Y && Position.Y + Size.Y > other.Position.Y;
    }

    /// <summary>The smallest rectangle containing both.</summary>
    public readonly Rect2 Merge(Rect2 other)
    {
        var min = Vector2.Min(Position, other.Position);
        var max = Vector2.Max(End, other.End);
        return new Rect2(min, max - min);
    }

    /// <summary>The rectangle grown by <paramref name="by"/> on every side.</summary>
    public readonly Rect2 Grow(float by) => new(Position - new Vector2(by), Size + new Vector2(by * 2f));

    /// <summary>The smallest rectangle containing this one and <paramref name="point"/>.</summary>
    public readonly Rect2 Expand(Vector2 point)
    {
        var min = Vector2.Min(Position, point);
        var max = Vector2.Max(End, point);
        return new Rect2(min, max - min);
    }

    /// <summary>The axis-aligned bounds of this rectangle after <paramref name="transform"/>.</summary>
    public readonly Rect2 Transformed(in Transform2D transform)
    {
        var a = transform.TransformPoint(Position);
        var b = transform.TransformPoint(new Vector2(Position.X + Size.X, Position.Y));
        var c = transform.TransformPoint(Position + Size);
        var d = transform.TransformPoint(new Vector2(Position.X, Position.Y + Size.Y));
        var min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
        var max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
        return new Rect2(min, max - min);
    }

    public readonly bool Equals(Rect2 other) => Position == other.Position && Size == other.Size;

    public override readonly bool Equals(object? obj) => obj is Rect2 other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Position, Size);

    public static bool operator ==(Rect2 left, Rect2 right) => left.Equals(right);

    public static bool operator !=(Rect2 left, Rect2 right) => !left.Equals(right);

    public override readonly string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[P: ({Position.X}, {Position.Y}), S: ({Size.X}, {Size.Y})]");
}

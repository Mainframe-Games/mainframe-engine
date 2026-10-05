using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>One canvas vertex: position (item-local while recorded, canvas pixels once culled), UV and colour.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CanvasVertex
{
    public Vector2 Position;
    public Vector2 Uv;

    /// <summary>Vertex colour times the command's modulate (gamma-space values; may exceed 1).</summary>
    public Vector4 Color;

    public CanvasVertex(Vector2 position, Vector2 uv, Vector4 color)
    {
        Position = position;
        Uv = uv;
        Color = color;
    }

    public const int SizeInBytes = 32;
}

/// <summary>How a draw command's indices are assembled.</summary>
public enum CanvasPrimitive : byte
{
    Triangles,
    Lines,
}

/// <summary>
/// A run of indices in a <see cref="CanvasDrawList"/> sharing a texture and a primitive type. <see cref="Tile"/> forces
/// repeat sampling (Godot's <c>CANVAS_RECT_TILE</c>, from <c>draw_texture_rect(..., tile: true)</c>).
/// </summary>
public readonly record struct CanvasDrawCommand(Texture2D? Texture, CanvasPrimitive Primitive, int FirstIndex, int IndexCount, bool Tile = false);

/// <summary>
/// The commands a <see cref="CanvasItem"/> recorded in its last draw (Godot's per-item command list): vertices in the
/// item's local space (the draw transform of <see cref="CanvasItem.DrawSetTransform(Vector2, float, Vector2)"/> already
/// applied), indices, and commands that group them by texture. Rebuilt only when the item is redrawn.
/// </summary>
public sealed class CanvasDrawList
{
    private CanvasVertex[] _vertices = [];
    private uint[] _indices = [];
    private CanvasDrawCommand[] _commands = [];
    private Vector2 _min, _max;

    public int VertexCount { get; private set; }

    public int IndexCount { get; private set; }

    public int CommandCount { get; private set; }

    public ReadOnlySpan<CanvasVertex> Vertices => _vertices.AsSpan(0, VertexCount);

    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, IndexCount);

    public ReadOnlySpan<CanvasDrawCommand> Commands => _commands.AsSpan(0, CommandCount);

    /// <summary>True when nothing was recorded.</summary>
    public bool IsEmpty => CommandCount == 0;

    /// <summary>Local bounds of every vertex (Godot's item rect), empty when nothing was recorded.</summary>
    public Rect2 Bounds => IsEmpty ? default : new Rect2(_min, _max - _min);

    /// <summary>The transform applied to the positions of commands added from now on (draw_set_transform).</summary>
    public Transform2D DrawTransform { get; set; } = Transform2D.Identity;

    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
        CommandCount = 0;
        DrawTransform = Transform2D.Identity;
        _min = new Vector2(float.MaxValue);
        _max = new Vector2(float.MinValue);
    }

    /// <summary>Appends vertices (positions are transformed by <see cref="DrawTransform"/>) and returns the first one's index.</summary>
    public int AddVertices(ReadOnlySpan<CanvasVertex> vertices)
    {
        Ensure(ref _vertices, VertexCount + vertices.Length);
        var first = VertexCount;
        var transform = DrawTransform;
        var identity = transform == Transform2D.Identity;
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            if (!identity)
                v.Position = transform.TransformPoint(v.Position);
            _min = Vector2.Min(_min, v.Position);
            _max = Vector2.Max(_max, v.Position);
            _vertices[first + i] = v;
        }

        VertexCount += vertices.Length;
        return first;
    }

    /// <summary>Appends indices (relative to <paramref name="baseVertex"/>) under <paramref name="texture"/>.</summary>
    public void AddIndices(Texture2D? texture, CanvasPrimitive primitive, int baseVertex, ReadOnlySpan<int> indices, bool tile = false)
    {
        if (indices.IsEmpty)
            return;
        Ensure(ref _indices, IndexCount + indices.Length);
        var firstIndex = IndexCount;
        for (var i = 0; i < indices.Length; i++)
            _indices[firstIndex + i] = (uint)(baseVertex + indices[i]);
        IndexCount += indices.Length;

        // Consecutive commands with the same texture and primitive merge into one.
        if (CommandCount > 0)
        {
            ref var last = ref _commands[CommandCount - 1];
            if (ReferenceEquals(last.Texture, texture) && last.Primitive == primitive && last.Tile == tile && last.FirstIndex + last.IndexCount == firstIndex)
            {
                last = last with { IndexCount = last.IndexCount + indices.Length };
                return;
            }
        }

        Ensure(ref _commands, CommandCount + 1);
        _commands[CommandCount++] = new CanvasDrawCommand(texture, primitive, firstIndex, indices.Length, tile);
    }

    /// <summary>Adds a quad (corners in order a, b, c, d; triangles a-b-c and a-c-d, as Godot's rect instances).</summary>
    public void AddQuad(Texture2D? texture, in CanvasVertex a, in CanvasVertex b, in CanvasVertex c, in CanvasVertex d)
    {
        Span<CanvasVertex> v = [a, b, c, d];
        var first = AddVertices(v);
        AddIndices(texture, CanvasPrimitive.Triangles, first, [0, 1, 2, 0, 2, 3]);
    }

    private static void Ensure<T>(ref T[] array, int count)
    {
        if (array.Length >= count)
            return;
        Array.Resize(ref array, Math.Max(count, Math.Max(16, array.Length * 2)));
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>The sampler a canvas batch uses: filter × repeat (Godot's canvas texture state).</summary>
public enum CanvasSampler : byte
{
    LinearClamp,
    NearestClamp,
    LinearRepeat,
    NearestRepeat,
    LinearMirror,
    NearestMirror,
}

/// <summary>
/// One draw of the canvas renderer: a run of indices sharing texture, sampler, material, blend, primitive and canvas
/// modulate. Batches of items whose material has a custom vertex function keep local vertices and carry the model
/// (item global) and canvas transforms; all others are pre-transformed to target pixels (identity transforms).
/// </summary>
public struct CanvasBatch
{
    public int FirstIndex;
    public int IndexCount;
    public Texture2D? Texture;
    public CanvasSampler Sampler;
    public CanvasPrimitive Primitive;
    public Material? Material;
    public CanvasBlendMode Blend;
    public Vector4 CanvasModulate;
    public Transform2D Model;
    public Transform2D CanvasTransform;
    public bool LocalVertices;

    /// <summary>The frame lights (<see cref="CanvasFrame.Lights"/> indices, one bit each) that light this batch.</summary>
    public uint LightMask;
}

/// <summary>One 2D light of the frame in the light block's layout (canvas_lights.slang).</summary>
public struct CanvasLightData
{
    public Vector4 MatrixX;
    public Vector4 MatrixY;
    public Vector4 Color;
    public uint Blend;
    public Texture2D Texture;
}

/// <summary>
/// The canvas server's output for one frame: every visible canvas of a viewport flattened into one vertex/index stream
/// and an ordered batch list, in target pixels (origin top-left, Y down). Reused frame to frame; no allocation once
/// warmed up.
/// </summary>
/// <summary>
/// One render target's share of a <see cref="CanvasFrame"/>: a 2D <see cref="SubViewport"/>'s target (drawn first, in
/// order) or the main canvas layer (<see cref="Viewport"/> null, always last).
/// </summary>
public struct CanvasPass
{
    public SubViewport? Viewport;

    /// <summary>A clip-children group's pass (its own target the canvas's size), drawn before the main canvas.</summary>
    public CanvasItem? ClipOwner;
    public int FirstBatch;
    public int BatchCount;
    public Vector2 Size;
    public Vector4? ClearColor;
}

public sealed class CanvasFrame
{
    private readonly List<CanvasPass> _passes = new(8);
    private int _passStart;
    private CanvasVertex[] _vertices = new CanvasVertex[4096];
    private uint[] _indices = new uint[8192];
    private readonly List<CanvasBatch> _batches = new(256);

    public int VertexCount { get; private set; }

    public int IndexCount { get; private set; }

    public ReadOnlySpan<CanvasVertex> Vertices => _vertices.AsSpan(0, VertexCount);

    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, IndexCount);

    public List<CanvasBatch> Batches => _batches;

    /// <summary>Most lights one frame carries (the shader's light block).</summary>
    public const int MaxLights = 8;

    private readonly List<CanvasLightData> _lights = new(MaxLights);

    /// <summary>The frame's 2D lights; batches refer to them by bit.</summary>
    public List<CanvasLightData> Lights => _lights;

    /// <summary>The frame's targets in draw order: the 2D sub-viewports, then the main canvas.</summary>
    public List<CanvasPass> Passes => _passes;

    /// <summary>Starts a pass: batches appended until <see cref="EndPass"/> draw into it (and never merge across passes).</summary>
    public void BeginPass(SubViewport? viewport, Vector2 size, Vector4? clearColor, CanvasItem? clipOwner = null)
    {
        _passStart = _batches.Count;
        _passes.Add(new CanvasPass { Viewport = viewport, ClipOwner = clipOwner, FirstBatch = _passStart, Size = size, ClearColor = clearColor });
    }

    /// <summary>Closes the pass started by <see cref="BeginPass"/>.</summary>
    public void EndPass()
    {
        var pass = _passes[^1];
        pass.BatchCount = _batches.Count - pass.FirstBatch;
        _passes[^1] = pass;
        _passStart = _batches.Count;
    }

    /// <summary>The target size in pixels this frame was built for.</summary>
    public Vector2 TargetSize { get; set; }

    /// <summary>Opaque background the canvas layer is cleared to, or null for transparent (draw over the 3D scene).</summary>
    public Vector4? ClearColor { get; set; }

    /// <summary>Canvas items culled into this frame (statistics).</summary>
    public int ItemCount { get; private set; }

    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
        ItemCount = 0;
        _batches.Clear();
        _passes.Clear();
        _passStart = 0;
        _lights.Clear();
    }

    /// <summary>Appends the commands of every culled item (in order) under one canvas's modulate and transform.</summary>
    public void Append(List<CulledCanvasItem> items, Vector4 canvasModulate, in Transform2D canvasTransform, in Transform2D canvasInverse) =>
        Append(items, canvasModulate, canvasTransform, canvasInverse, null, 0);

    /// <summary>
    /// As above, with the canvas's <paramref name="lights"/> (frame light indices <paramref name="lightBase"/> onwards): each
    /// item gets the bits of the lights that affect its light mask and z.
    /// </summary>
    public void Append(List<CulledCanvasItem> items, Vector4 canvasModulate, in Transform2D canvasTransform, in Transform2D canvasInverse,
        IReadOnlyList<PointLight2D>? lights, int lightBase)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var culled = items[i];
            var list = culled.Item.DrawList;
            var commands = list.Commands;
            if (commands.IsEmpty)
                continue;
            ItemCount++;
            if (culled.ClipComposite)
            {
                AppendClipComposite(culled, list, canvasModulate);
                continue;
            }
            var local = culled.Material is ShaderMaterial { Shader.HasVertexFunction: true };
            var model = local ? canvasInverse * culled.Transform : Transform2D.Identity;
            var baseVertex = VertexCount;

            // Vertices: transformed to target pixels (or kept local) and modulated, once per item.
            var src = list.Vertices;
            EnsureVertices(VertexCount + src.Length);
            var transform = culled.Transform;
            var modulate = culled.Modulate;
            for (var v = 0; v < src.Length; v++)
            {
                var vertex = src[v];
                if (!local)
                    vertex.Position = transform.TransformPoint(vertex.Position);
                vertex.Color *= modulate;
                _vertices[VertexCount + v] = vertex;
            }

            VertexCount += src.Length;

            var blend = culled.Material is CanvasItemMaterial cim ? cim.BlendMode : culled.Material is ShaderMaterial sm ? sm.Shader?.BlendMode ?? CanvasBlendMode.Mix : CanvasBlendMode.Mix;
            if (culled.ClipGroup is { } owner && !ReferenceEquals(owner, culled.Item))
                blend = CanvasBlendMode.Atop;   // inside a clip-children group: only where the owner drew
            var lightMask = 0u;
            if (lights is not null)
                for (var l = 0; l < lights.Count && lightBase + l < MaxLights; l++)
                    if (lights[l].Affects(culled.Item.LightMask, culled.Z))
                        lightMask |= 1u << (lightBase + l);
            var indices = list.Indices;
            for (var c = 0; c < commands.Length; c++)
            {
                var command = commands[c];
                var sampler = SamplerFor(culled.Filter, command.Tile ? CanvasTextureRepeat.Enabled : culled.Repeat);
                var firstIndex = IndexCount;
                EnsureIndices(IndexCount + command.IndexCount);
                for (var k = 0; k < command.IndexCount; k++)
                    _indices[IndexCount + k] = (uint)baseVertex + indices[command.FirstIndex + k];
                IndexCount += command.IndexCount;

                if (!local && _batches.Count > _passStart)
                {
                    var last = _batches[^1];
                    if (!last.LocalVertices && ReferenceEquals(last.Texture, command.Texture) && last.Sampler == sampler &&
                        last.Primitive == command.Primitive && ReferenceEquals(last.Material, culled.Material) && last.Blend == blend &&
                        last.CanvasModulate == canvasModulate && last.LightMask == lightMask && last.FirstIndex + last.IndexCount == firstIndex)
                    {
                        last.IndexCount += command.IndexCount;
                        _batches[^1] = last;
                        continue;
                    }
                }

                _batches.Add(new CanvasBatch
                {
                    FirstIndex = firstIndex,
                    IndexCount = command.IndexCount,
                    Texture = command.Texture,
                    Sampler = sampler,
                    Primitive = command.Primitive,
                    Material = culled.Material,
                    Blend = blend,
                    CanvasModulate = canvasModulate,
                    Model = model,
                    CanvasTransform = local ? canvasTransform : Transform2D.Identity,
                    LocalVertices = local,
                    LightMask = lightMask,
                });
            }
        }
    }

    // The group target is already modulated and lit; the composite only places it (Godot's clip_children material keeps the
    // group's colours under the owner's alpha; the target holds premultiplied owner + clipped children).
    private static readonly CanvasItemMaterial ClipCompositeMaterial = new() { BlendMode = CanvasBlendMode.PremultAlpha, LightMode = CanvasLightMode.Unshaded };

    /// <summary>A clip-children owner's composite: its drawn bounds (target pixels) textured with its group target.</summary>
    private void AppendClipComposite(in CulledCanvasItem culled, CanvasDrawList list, Vector4 canvasModulate)
    {
        var rect = list.Bounds.Transformed(culled.Transform);
        var size = _passes[^1].Size;
        if (size.X <= 0 || size.Y <= 0 || rect.Size.X <= 0 || rect.Size.Y <= 0)
            return;
        var texture = Texture2D.ForClipGroup(culled.Item, size);
        EnsureVertices(VertexCount + 4);
        EnsureIndices(IndexCount + 6);
        var baseVertex = (uint)VertexCount;
        var p0 = rect.Position;
        var p1 = rect.Position + rect.Size;
        ReadOnlySpan<Vector2> corners = [p0, new Vector2(p1.X, p0.Y), p1, new Vector2(p0.X, p1.Y)];
        for (var v = 0; v < 4; v++)
            _vertices[VertexCount + v] = new CanvasVertex { Position = corners[v], Uv = corners[v] / size, Color = Vector4.One };
        VertexCount += 4;
        ReadOnlySpan<uint> quad = [0, 1, 2, 2, 3, 0];
        var firstIndex = IndexCount;
        for (var k = 0; k < 6; k++)
            _indices[IndexCount + k] = baseVertex + quad[k];
        IndexCount += 6;
        _batches.Add(new CanvasBatch
        {
            FirstIndex = firstIndex,
            IndexCount = 6,
            Texture = texture,
            Sampler = CanvasSampler.LinearClamp,
            Primitive = CanvasPrimitive.Triangles,
            Material = ClipCompositeMaterial,
            Blend = CanvasBlendMode.PremultAlpha,
            CanvasModulate = canvasModulate,
            Model = Transform2D.Identity,
            CanvasTransform = Transform2D.Identity,
        });
    }

    /// <summary>
    /// Debug line segments (world positions; z ignored) as one unshaded line-list batch in target pixels, through
    /// <paramref name="transform"/> (canvas → target).
    /// </summary>
    internal void AppendLines(ReadOnlySpan<DebugLineVertex> vertices, in Transform2D transform)
    {
        if (vertices.Length < 2)
            return;
        var count = vertices.Length & ~1;
        EnsureVertices(VertexCount + count);
        EnsureIndices(IndexCount + count);
        var baseVertex = (uint)VertexCount;
        var firstIndex = IndexCount;
        for (var i = 0; i < count; i++)
        {
            var p = vertices[i].Position;
            _vertices[VertexCount + i] = new CanvasVertex { Position = transform.TransformPoint(new Vector2(p.X, p.Y)), Color = vertices[i].Color };
            _indices[IndexCount + i] = baseVertex + (uint)i;
        }

        VertexCount += count;
        IndexCount += count;
        _batches.Add(new CanvasBatch
        {
            FirstIndex = firstIndex,
            IndexCount = count,
            Sampler = CanvasSampler.LinearClamp,
            Primitive = CanvasPrimitive.Lines,
            Material = LinesMaterial,
            Blend = CanvasBlendMode.Mix,
            CanvasModulate = Vector4.One,
            Model = Transform2D.Identity,
            CanvasTransform = Transform2D.Identity,
        });
    }

    private static readonly CanvasItemMaterial LinesMaterial = new() { LightMode = CanvasLightMode.Unshaded };

    /// <summary>The sampler for a resolved filter and repeat (ParentNode resolves to linear / disabled).</summary>
    public static CanvasSampler SamplerFor(CanvasTextureFilter filter, CanvasTextureRepeat repeat)
    {
        var nearest = filter == CanvasTextureFilter.Nearest;
        return repeat switch
        {
            CanvasTextureRepeat.Enabled => nearest ? CanvasSampler.NearestRepeat : CanvasSampler.LinearRepeat,
            CanvasTextureRepeat.Mirror => nearest ? CanvasSampler.NearestMirror : CanvasSampler.LinearMirror,
            _ => nearest ? CanvasSampler.NearestClamp : CanvasSampler.LinearClamp,
        };
    }

    private void EnsureVertices(int count)
    {
        if (_vertices.Length < count)
            Array.Resize(ref _vertices, Math.Max(count, _vertices.Length * 2));
    }

    private void EnsureIndices(int count)
    {
        if (_indices.Length < count)
            Array.Resize(ref _indices, Math.Max(count, _indices.Length * 2));
    }
}

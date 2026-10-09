using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Mist and spray as a few camera-facing soft cards (G8e.6, ADR 0173): the cheap stand-in for G6.3's
/// <c>GpuParticles3D</c> at the foot of a fall. <see cref="Count"/> cards with centres scattered (deterministically, from
/// <see cref="Seed"/>) in a box of half-size <see cref="Extents"/> around the node; each rises, grows and fades over its
/// own phase of <see cref="SprayMaterial3D.Cycle"/>, drifting by up to <see cref="Drift"/> metres. Drawn with a
/// <see cref="SprayMaterial3D"/>, soft against the scene where the view has a scene copy (refracting water). Casts no
/// shadows. <see cref="River3D"/> places one at every fall's foot.
/// </summary>
/// <remarks>
/// The generated mesh holds four vertices per card at the card's centre (the vertex shader billboards them): the normal
/// stream is the corner (±1, ±1) and the card size, <see cref="MeshSurface.Custom0"/> the phase, rise scale and drift.
/// Its bounds (<see cref="GeometryInstance3D.CustomAabb"/>) cover the cards at full size and height. Not saved: it is
/// rebuilt from the exports.
/// </remarks>
[EditorIcon("ripple", Family = EditorIconFamily.Space3D)]
public class SprayCards3D : GeometryInstance3D
{
    private ArrayMesh? _mesh;
    private MeshSurface? _surface;
    private SprayMaterial3D? _defaultMaterial;
    private bool _dirty = true;

    /// <summary>Cards.</summary>
    [Export(Range = "1,64,1")]
    public int Count
    {
        get;
        set
        {
            value = Math.Clamp(value, 1, 64);
            if (field == value) return;
            field = value;
            _dirty = true;
        }
    } = 8;

    /// <summary>Half-size in metres of the box (node space) the cards' centres are scattered in.</summary>
    [Export]
    public Vector3 Extents
    {
        get;
        set
        {
            value = Vector3.Max(value, Vector3.Zero);
            if (field == value) return;
            field = value;
            _dirty = true;
        }
    } = new(1.5f, 0.4f, 1f);

    /// <summary>A card's size in metres at the start of its cycle.</summary>
    [Export(Range = "0.05,20,0.05")]
    public float CardSize
    {
        get;
        set
        {
            value = MathF.Max(value, 0.05f);
            if (field == value) return;
            field = value;
            _dirty = true;
        }
    } = 1.6f;

    /// <summary>The farthest a card drifts sideways over its cycle, metres.</summary>
    [Export(Range = "0,10,0.05")]
    public float Drift
    {
        get;
        set
        {
            value = MathF.Max(value, 0f);
            if (field == value) return;
            field = value;
            _dirty = true;
        }
    } = 0.6f;

    /// <summary>Seed of the cards' placement, phases and drift.</summary>
    [Export]
    public int Seed
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _dirty = true;
        }
    } = 1;

    /// <summary>The cards' material (null: a default <see cref="SprayMaterial3D"/>).</summary>
    [Export]
    public SprayMaterial3D? Material
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            RenderStamp++;
            _dirty = true; // the bounds follow the material's rise and growth
        }
    }

    /// <summary>The generated card mesh (rebuilt when an export changed).</summary>
    public ArrayMesh? CardMesh
    {
        get
        {
            Build();
            return _mesh;
        }
    }

    internal override Mesh? GetRenderMesh()
    {
        Build();
        return _mesh;
    }

    internal override Material GetRenderMaterial(Mesh mesh, int surface) => MaterialOverride ?? Material ?? DefaultMaterial;

    private SprayMaterial3D DefaultMaterial => _defaultMaterial ??= new SprayMaterial3D { ResourceName = "Spray (default)" };

    private void Build()
    {
        if (!_dirty && _mesh is not null)
            return;
        _dirty = false;
        var count = Count;
        var positions = new Vector3[count * 4];
        var corners = new Vector3[count * 4];
        var uvs = new Vector2[count * 4];
        var custom = new Vector4[count * 4];
        var indices = new int[count * 6];
        var state = (uint)Seed * 2654435761u + 0x9E3779B9u;
        var extents = Extents;
        for (var c = 0; c < count; c++)
        {
            var centre = new Vector3((Next(ref state) * 2f - 1f) * extents.X, (Next(ref state) * 2f - 1f) * extents.Y,
                (Next(ref state) * 2f - 1f) * extents.Z);
            var size = CardSize * (0.75f + 0.5f * Next(ref state));
            var phase = (c + Next(ref state) * 0.5f) / count; // spread over the cycle
            var rise = 0.6f + 0.6f * Next(ref state);
            var angle = Next(ref state) * MathF.Tau;
            var drift = Drift * (0.3f + 0.7f * Next(ref state));
            var parameters = new Vector4(phase, rise, MathF.Cos(angle) * drift, MathF.Sin(angle) * drift);
            for (var k = 0; k < 4; k++)
            {
                var x = (k & 1) == 0 ? -1f : 1f;
                var y = k < 2 ? -1f : 1f;
                var v = c * 4 + k;
                positions[v] = centre;
                corners[v] = new Vector3(x, y, size);
                uvs[v] = new Vector2(x * 0.5f + 0.5f, 0.5f - y * 0.5f);
                custom[v] = parameters;
            }

            var i = c * 6;
            var b = c * 4;
            indices[i] = b;
            indices[i + 1] = b + 1;
            indices[i + 2] = b + 2;
            indices[i + 3] = b + 1;
            indices[i + 4] = b + 3;
            indices[i + 5] = b + 2;
        }

        if (_mesh is null || _surface is null)
        {
            _surface = new MeshSurface();
            _mesh = new ArrayMesh();
            _mesh.AddSurface(_surface);
        }

        _surface.Positions = positions;
        _surface.Normals = corners;
        _surface.UVs = uvs;
        _surface.Indices = indices;
        _surface.Custom0 = custom;

        // Every card at its largest, its highest and drifted farthest (rotation about Y included).
        var material = Material ?? DefaultMaterial;
        var reach = CardSize * 1.25f * MathF.Max(material.Growth, 1f) * 0.75f + Drift;
        var horizontal = MathF.Sqrt(extents.X * extents.X + extents.Z * extents.Z) + reach;
        CustomAabb = new Aabb(new Vector3(-horizontal, -extents.Y - reach, -horizontal),
            new Vector3(horizontal, extents.Y + reach + material.Rise * 1.2f, horizontal));
    }

    // A small LCG in [0, 1): the same cards on every machine.
    private static float Next(ref uint state)
    {
        state = state * 1664525u + 1013904223u;
        return (state >> 8) / 16777216f;
    }
}

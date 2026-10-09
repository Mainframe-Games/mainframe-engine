using DrawingColor = System.Drawing.Color;
using System.Drawing;
using System.Numerics;

namespace MainframeEngine;


/// <summary>How a material treats alpha.</summary>
public enum AlphaMode : byte
{
    /// <summary>Alpha ignored; drawn in the opaque pass with depth writes.</summary>
    Opaque,

    /// <summary>Alpha test: fragments below <see cref="StandardMaterial3D.AlphaCutoff"/> are discarded (foliage, fences).</summary>
    Cutout,

    /// <summary>Alpha blending (straight alpha), drawn back to front after the opaque pass, no depth writes, casts no shadow.</summary>
    Blend,
}

/// <summary>Which faces are culled.</summary>
public enum CullMode : byte
{
    Back,
    Front,

    /// <summary>Nothing is culled (both sides drawn with the same normal; see <see cref="StandardMaterial3D.DoubleSided"/>).</summary>
    Disabled,
}

/// <summary>Lighting model of a <see cref="StandardMaterial3D"/>.</summary>
public enum ShadingMode : byte
{
    /// <summary>Blinn-Phong: ambient + every light (with shadows), specular highlights.</summary>
    BlinnPhong,

    /// <summary>No lighting: albedo (× texture) + emission.</summary>
    Unshaded,

    /// <summary>
    /// Physically based (ADR 0150): Cook-Torrance GGX + Lambert with <see cref="StandardMaterial3D.Metallic"/>,
    /// <see cref="StandardMaterial3D.Roughness"/> and <see cref="StandardMaterial3D.AmbientOcclusion"/>; ambient and
    /// reflections from the sky's image-based lighting (<see cref="WorldEnvironment.AmbientSource"/>,
    /// <see cref="WorldEnvironment.ReflectedLightSource"/>). Godot's per-pixel shading.
    /// </summary>
    Pbr,
}

/// <summary>
/// How a surface is shaded (Godot's <c>Material</c>). Materials are resources: share one between many meshes and
/// the renderer keeps one descriptor set (set 2) per material; instances drawn with the same material and mesh
/// are batched into one instanced draw.
/// </summary>
[EditorIcon("palette")]
public abstract class Material : Resource
{
    private int _version = 1;

    /// <summary>Changes whenever a property changes (the renderer re-uploads the parameters).</summary>
    public int Version => _version;

    /// <summary>Draw order among transparent materials at the same depth (higher draws later).</summary>
    [Export(Range = "-128,127,1")]
    public int RenderPriority
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>
    /// A material drawn after this one over the same surfaces (Godot's <c>next_pass</c>), for example an
    /// <see cref="OutlineMaterial3D"/>. Chains are followed up to <see cref="MaxPassChain"/> materials; a cycle stops there.
    /// </summary>
    [Export]
    public Material? NextPass
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Interlocked.Increment(ref _chainGeneration);
            Touch();
        }
    }

    /// <summary>The most materials one surface draws through <see cref="NextPass"/> links (a cycle stops here).</summary>
    public const int MaxPassChain = 8;

    private static int _chainGeneration;

    /// <summary>Bumped whenever any material's <see cref="NextPass"/> changes (the renderer re-resolves its chains).</summary>
    internal static int ChainGeneration => Volatile.Read(ref _chainGeneration);

    /// <summary>The fixed-function state the material needs (blending, culling, depth writes).</summary>
    public abstract MaterialRenderState RenderState { get; }

    /// <summary>Marks the material changed.</summary>
    protected void Touch()
    {
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// The pipeline-relevant state of a material: part of the pipeline-cache key, so every material with equal state
/// shares one <c>VkPipeline</c>.
/// </summary>
public readonly record struct MaterialRenderState(AlphaMode Alpha, CullMode Cull, bool DoubleSided)
{
    /// <summary>The cull mode actually used: double-sided materials cull nothing.</summary>
    public CullMode EffectiveCull => DoubleSided ? CullMode.Disabled : Cull;

    /// <summary>Blended materials do not write depth.</summary>
    public bool DepthWrite => Alpha != AlphaMode.Blend;

    /// <summary>Blended materials are drawn in the transparent pass, back to front.</summary>
    public bool IsTransparent => Alpha == AlphaMode.Blend;

    /// <summary>Whether instances cast shadows (blended surfaces do not).</summary>
    public bool CastsShadows => Alpha != AlphaMode.Blend;
}

/// <summary>
/// The engine's standard surface material: albedo colour × texture, optional tangent-space normal map, emission, alpha
/// modes (opaque, cutout, blend), culling and double-sided lighting, shaded Blinn-Phong (the default: specular strength
/// and shininess), unshaded, or PBR (<see cref="ShadingMode.Pbr"/>: metallic, roughness, ambient occlusion and an ORM
/// texture, ADR 0150). Colours are authored in sRGB, like every colour in the engine, and converted to linear for the
/// shader.
/// </summary>
[EditorIcon("palette")]
public sealed class StandardMaterial3D : Material
{
    /// <summary>The material used when a surface has none: white, lit, opaque.</summary>
    public static StandardMaterial3D Default { get; } = new() { ResourceName = "Default" };

    [ExportGroup("Albedo")]
    [Export]
    public DrawingColor AlbedoColor
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = DrawingColor.White;

    /// <summary>Multiplied with <see cref="AlbedoColor"/> (alpha too). Sampled as sRGB unless its import settings force linear.</summary>
    [Export]
    public Texture2D? AlbedoTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Tangent-space normal map (OpenGL/glTF convention: +Y up). Sampled as linear data.</summary>
    [ExportGroup("Normal map")]
    [Export]
    public Texture2D? NormalTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    [Export(Range = "0,4,0.01")]
    public float NormalScale
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    /// <summary>Strength of the white specular highlight (0 = none).</summary>
    [ExportGroup("Specular")]
    [Export(Range = "0,4,0.01")]
    public float Specular
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.3f;

    /// <summary>Blinn-Phong exponent: higher is a smaller, sharper highlight.</summary>
    [Export(Range = "1,1024,1")]
    public float Shininess
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 32f;

    /// <summary>How metallic the surface is (0 = dielectric, 1 = metal); <see cref="ShadingMode.Pbr"/> only (Godot's <c>metallic</c>).</summary>
    [ExportGroup("PBR")]
    [Export(Range = "0,1,0.01")]
    public float Metallic
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Perceptual roughness (0 = mirror, 1 = fully rough); <see cref="ShadingMode.Pbr"/> only (Godot's <c>roughness</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float Roughness
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    /// <summary>How much ambient light reaches the surface (1 = all); <see cref="ShadingMode.Pbr"/> only.</summary>
    [Export(Range = "0,1,0.01")]
    public float AmbientOcclusion
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    /// <summary>
    /// Packed occlusion (R), roughness (G) and metallic (B), glTF's and Godot's ORM layout, sampled as linear data;
    /// multiplies <see cref="AmbientOcclusion"/>, <see cref="Roughness"/> and <see cref="Metallic"/>.
    /// <see cref="ShadingMode.Pbr"/> only.
    /// </summary>
    [Export]
    public Texture2D? OrmTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    [ExportGroup("Emission")]
    [Export]
    public DrawingColor EmissionColor
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = DrawingColor.Black;

    /// <summary>Multiplies <see cref="EmissionColor"/> (linear, can exceed 1: HDR).</summary>
    [Export(Range = "0,64,0.01")]
    public float EmissionEnergy
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    [Export]
    public Texture2D? EmissionTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    [ExportGroup("Transparency")]
    [Export]
    public AlphaMode Transparency
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Alpha below which <see cref="AlphaMode.Cutout"/> discards.</summary>
    [Export(Range = "0,1,0.01")]
    public float AlphaCutoff
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.5f;

    [ExportGroup("Rendering")]
    [Export]
    public ShadingMode ShadingMode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    [Export]
    public CullMode CullMode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Draw both sides, lighting back faces with the flipped normal (leaves, cloth, sprites).</summary>
    [Export]
    public bool DoubleSided
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>
    /// How strongly the surface takes on the terrain below it near the ground (ADR 0175, G8e.7's RVT-lite: boulders and
    /// logs sink into moss and dirt): within <see cref="TerrainBlendHeight"/> above the ground, albedo, normal and
    /// roughness blend towards the world's <see cref="Terrain3D.MacroTexture"/> texel below, with a noisy edge. 0 (default):
    /// off. <see cref="ShadingMode.Pbr"/> surfaces only; nothing happens without a terrain macro texture in the world.
    /// </summary>
    [ExportGroup("Terrain blend")]
    [Export(Range = "0,1,0.01")]
    public float TerrainBlend
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Height (m) above the ground over which <see cref="TerrainBlend"/> fades out.</summary>
    [Export(Range = "0.01,5,0.01")]
    public float TerrainBlendHeight
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.3f;

    [ExportGroup("UV")]
    [Export]
    public Vector2 UvScale
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = Vector2.One;

    [Export]
    public Vector2 UvOffset
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    public override MaterialRenderState RenderState => new(Transparency, CullMode, DoubleSided);
}

/// <summary>
/// An inverted-hull outline (Godot's common <c>cull_front, unshaded</c> outline shader as a built-in material): each
/// vertex is pushed along its clip-space normal by <see cref="Width"/> pixels and only back faces are drawn, so the
/// surface it is drawn over hides all but a rim of constant screen width. Unshaded, blended like the Godot shader
/// (which writes <c>ALPHA</c>), no shadows. Use it as a <see cref="Material.NextPass"/> or a
/// <see cref="GeometryInstance3D.MaterialOverlay"/>.
/// </summary>
[EditorIcon("palette")]
public sealed class OutlineMaterial3D : Material
{
    /// <summary>The outline colour (sRGB; alpha below 1 blends).</summary>
    [Export]
    public DrawingColor Color
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = DrawingColor.White;

    /// <summary>Outline width in pixels of the render target.</summary>
    [Export(Range = "0,64,0.1")]
    public float Width
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    public override MaterialRenderState RenderState => new(AlphaMode.Blend, CullMode.Front, false);
}

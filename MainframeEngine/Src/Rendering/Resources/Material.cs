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
}

/// <summary>
/// How a surface is shaded (Godot's <c>Material</c>). Materials are resources: share one between many meshes and
/// the renderer keeps one descriptor set (set 2) per material; instances drawn with the same material and mesh
/// are batched into one instanced draw.
/// </summary>
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
/// The engine's standard surface material. Blinn-Phong lighting for now (ADR "Blinn-Phong now, PBR later"):
/// albedo colour × texture, optional tangent-space normal map, specular strength and shininess, emission, alpha
/// modes (opaque, cutout, blend), culling and double-sided lighting. Colours are authored in sRGB, like every
/// colour in the engine, and converted to linear for the shader.
/// </summary>
public sealed class StandardMaterial3D : Material
{
    /// <summary>The material used when a surface has none: white, lit, opaque.</summary>
    public static StandardMaterial3D Default { get; } = new() { ResourceName = "Default" };

    [ExportGroup("Albedo")]
    [Export]
    public Color AlbedoColor
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = Color.White;

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

    [ExportGroup("Emission")]
    [Export]
    public Color EmissionColor
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = Color.Black;

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

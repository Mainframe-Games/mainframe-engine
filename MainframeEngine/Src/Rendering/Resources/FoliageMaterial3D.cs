using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>How a <see cref="FoliageMaterial3D"/> treats back faces.</summary>
public enum FoliageBackFace : byte
{
    /// <summary>Drawn, lit with the flipped normal (leaf cards, grass blades).</summary>
    Flip,

    /// <summary>Drawn, lit with the authored normal (rounded canopies with custom normals: Ez Tree's <c>uCustomNormals</c>).</summary>
    Keep,

    /// <summary>Culled (bark, closed meshes).</summary>
    Cull,
}

/// <summary>
/// The built-in foliage material (ADR 0151): leaves, grass and bark that sway in the world's wind
/// (<see cref="WorldEnvironment.WindStrength"/> and friends). Its vertex shader moves each vertex by the mesh's
/// <see cref="MeshSurface.Custom0"/> stream: Ez Tree's leaf flutter (three sines with a simplex-noise phase over the
/// world position, on leaves: <c>Custom0.y</c> = 1, more towards the top of the leaf: UV v = 0) plus a slow branch bend
/// weighted by <c>Custom0.x²</c> and phased by <c>Custom0.z</c>. Shadow casters sway the same way. The fragment shader
/// cuts out (<see cref="AlphaCutout"/>), lights back faces (<see cref="BackFace"/>), adds light shining through the
/// leaf from the first directional light (<see cref="Translucency"/>) and darkens the ambient term by
/// <c>Custom0.w</c> (AO; 0 or a missing stream means none). Vertex colours multiply the albedo.
/// </summary>
/// <remarks>
/// Meshes without the second stream still draw (it reads white and zero: no wind). Lighting is Blinn-Phong with
/// <see cref="Roughness"/> drives the PBR highlight, or a Blinn-Phong one (<see cref="ShadingMode"/>).
/// </remarks>
[EditorIcon("palette")]
public sealed class FoliageMaterial3D : Material
{
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

    /// <summary>Tangent-space normal map (OpenGL/glTF convention: +Y up).</summary>
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

    /// <summary>Alpha-test against <see cref="AlphaCutoff"/> (leaves, grass); off for bark.</summary>
    [ExportGroup("Alpha")]
    [Export]
    public bool AlphaCutout
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = true;

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

    [ExportGroup("Lighting")]
    [Export]
    public FoliageBackFace BackFace
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Light from the first directional light shining through the leaf when it is back-lit (0..1).</summary>
    [Export(Range = "0,1,0.01")]
    public float Translucency
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.5f;

    /// <summary>
    /// <see cref="ShadingMode.BlinnPhong"/> (with <see cref="Roughness"/> mapped to the highlight), <see cref="ShadingMode.Pbr"/>
    /// (dielectric, <see cref="Roughness"/>, vertex AO; ADR 0150) or unshaded.
    /// </summary>
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
    } = 0.8f;

    /// <summary>Scales the world's wind for this material (0 = still).</summary>
    [ExportGroup("Wind")]
    [Export(Range = "0,4,0.01")]
    public float WindStrength
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    /// <summary>Scales the slow bend of whole branches (weighted by <c>Custom0.x²</c>); 0 = leaves only flutter.</summary>
    [Export(Range = "0,4,0.01")]
    public float WindBranchBend
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 1f;

    public override MaterialRenderState RenderState =>
        new(AlphaCutout ? AlphaMode.Cutout : AlphaMode.Opaque, CullMode.Back, BackFace != FoliageBackFace.Cull);

    /// <summary>The Blinn-Phong highlight (strength, exponent) standing in for a roughness until PBR shading.</summary>
    internal static (float Specular, float Shininess) BlinnFromRoughness(float roughness)
    {
        var r = Math.Clamp(roughness, 0.05f, 1f);
        var a = r * r;
        return (0.25f * (1f - r), Math.Clamp(2f / (a * a) - 2f, 1f, 1024f));
    }
}

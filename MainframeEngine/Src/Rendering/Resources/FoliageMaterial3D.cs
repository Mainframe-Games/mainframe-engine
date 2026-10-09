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
/// Godot's <c>BaseMaterial3D.AlphaAntialiasing</c> for <see cref="FoliageMaterial3D"/> (ADR 0172). The engine renders
/// single-sample targets, so <see cref="AlphaToCoverage"/> maps onto TAA's dithered cutout (<see cref="FoliageMaterial3D.AlphaDither"/>):
/// the alpha edge sharpened by its screen-space derivative, dithered over <see cref="FoliageMaterial3D.AlphaAntialiasingEdge"/>,
/// and resolved by TAA into fractional coverage. Without TAA it is the plain alpha test.
/// </summary>
public enum AlphaAntialiasing : byte
{
    Off,
    AlphaToCoverage,
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

    /// <summary>
    /// With <see cref="AntiAliasing.Taa"/>, dithers the alpha test (ADR 0166): each frame keeps a fragment when its coverage
    /// — the alpha edge sharpened by its screen-space derivative, about a pixel wide — beats a per-frame noise threshold,
    /// and TAA averages that into smooth partial coverage, so leaf and needle edges stop crawling. No effect without TAA
    /// (the plain test against <see cref="AlphaCutoff"/>) or without <see cref="AlphaCutout"/>; shadows never dither.
    /// </summary>
    [Export]
    public bool AlphaDither
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
    /// Godot's <c>alpha_antialiasing_mode</c>: <see cref="AlphaAntialiasing.AlphaToCoverage"/> dithers the cutout like
    /// <see cref="AlphaDither"/> with an edge of <see cref="AlphaAntialiasingEdge"/> (ADR 0172; single-sample targets have no
    /// hardware alpha to coverage).
    /// </summary>
    [Export]
    public AlphaAntialiasing AlphaAntialiasingMode
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
    /// Godot's <c>alpha_antialiasing_edge</c>: the width of the dithered alpha edge (0.5 = one pixel, the width of
    /// <see cref="AlphaDither"/>; smaller is sharper). Used with <see cref="AlphaAntialiasing.AlphaToCoverage"/>.
    /// </summary>
    [Export(Range = "0.05,1,0.01")]
    public float AlphaAntialiasingEdge
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.3f;

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

    /// <summary>Tints the light shining through (sRGB; white keeps the albedo's colour).</summary>
    [Export]
    public DrawingColor TranslucencyColor
    {
        get;
        set
        {
            if (field.ToArgb() == value.ToArgb()) return;
            field = value;
            Touch();
        }
    } = DrawingColor.White;

    /// <summary>How tightly the back-light gathers around the direction to the light (the exponent of <c>V·−L</c>).</summary>
    [Export(Range = "0.5,32,0.1")]
    public float TranslucencyScatter
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 4f;

    /// <summary>
    /// Linear thickness map (R = thickness 0..1, G = ambient occlusion; a <see cref="TreeClusterAtlas"/>'s): the back-light
    /// becomes thickness-driven wrap transmission (ADR 0172), so the dense centre of a twig stays dark and its edges glow,
    /// and G darkens the ambient term.
    /// </summary>
    [Export]
    public Texture2D? ThicknessTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

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

    /// <summary>
    /// Linear ORM map (R occlusion, G roughness, B metallic; <see cref="OrmPacker"/> builds one from separate maps):
    /// multiplies <see cref="Roughness"/> and the vertex AO in <see cref="ShadingMode.Pbr"/> (bark; ADR 0158).
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

    /// <summary>
    /// Scales the trunk's sway in the hierarchical wind (meshes with the <see cref="MeshSurface.Custom1"/> and
    /// <see cref="MeshSurface.Custom2"/> pivot streams, ADR 0172); 0 keeps trunks still.
    /// </summary>
    [Export(Range = "0,4,0.01")]
    public float WindTrunkSway
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
    /// The share of the cutout that casts into directional and spot shadow maps (ADR 0172): 1 (default) casts every kept
    /// texel; less thins it with a stable dither the shadow filter turns into a partial shadow, for leaf cards that are
    /// denser than the canopy they stand for (cluster cards, far levels), so light still reaches the floor between them.
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float ShadowDensity
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
    /// Moss on top (ADR 0172): how much of the bark <see cref="MossColor"/> covers — up-facing surfaces first (world normal
    /// · up), broken into patches by a world-space noise, and climbing every side of the trunk over its first 1.6 m. 0 = none.
    /// </summary>
    [ExportGroup("Bark detail")]
    [Export(Range = "0,1,0.01")]
    public float MossCoverage
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>The moss albedo (sRGB).</summary>
    [Export]
    public DrawingColor MossColor
    {
        get;
        set
        {
            if (field.ToArgb() == value.ToArgb()) return;
            field = value;
            Touch();
        }
    } = DrawingColor.FromArgb(255, 0x3E, 0x54, 0x26);

    /// <summary>Size of the moss patches (m): the world-space noise's feature size.</summary>
    [Export(Range = "0.05,10,0.01")]
    public float MossPatchSize
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.6f;

    /// <summary>
    /// Detail normals (ADR 0172): the normal map sampled again at this many times the UV tiling and blended with UDN, so
    /// bark stays crisp up close. 0 = off.
    /// </summary>
    [Export(Range = "0,32,0.1")]
    public float DetailScale
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    [Export(Range = "0,2,0.01")]
    public float DetailStrength
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.6f;

    /// <summary>
    /// Per-instance visibility range (ADR 0172), for a level of detail of instanced trees: each instance is drawn while
    /// its origin's distance to the camera lies in [<see cref="InstanceVisibilityBegin"/>, <see cref="InstanceVisibilityEnd"/>)
    /// (0 = unbounded), dithering across ±<see cref="InstanceVisibilityMargin"/> at each end so neighbouring levels
    /// cross-fade (TAA smooths the dither). Shadows switch at the ends. Off: every instance draws.
    /// </summary>
    [ExportGroup("Instance visibility")]
    [Export]
    public bool InstanceVisibility
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    [Export(Range = "0,10000,0.1")]
    public float InstanceVisibilityBegin
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    [Export(Range = "0,10000,0.1")]
    public float InstanceVisibilityEnd
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    [Export(Range = "0,100,0.1")]
    public float InstanceVisibilityMargin
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
    /// Where an instance casts into the fine shadow passes when that differs from where it is drawn (ADR 0179): camera
    /// distances in [<see cref="InstanceShadowBegin"/>, <see cref="InstanceShadowEnd"/>) (m; an end of 0 is unbounded).
    /// −1 (default) follows <see cref="InstanceVisibilityBegin"/> and <see cref="InstanceVisibilityEnd"/>. A
    /// <see cref="TreeScatter"/> sets it on its coarse shadow level, which casts from where its finer casting levels end:
    /// every tree casts one level at every distance, in every cascade. The coarse passes (the far shadow) ignore it.
    /// </summary>
    [Export(Range = "-1,10000,0.1")]
    public float InstanceShadowBegin
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = -1f;

    /// <summary>The end of the fine shadow passes' per-instance range (see <see cref="InstanceShadowBegin"/>; 0: unbounded, −1: <see cref="InstanceVisibilityEnd"/>).</summary>
    [Export(Range = "-1,10000,0.1")]
    public float InstanceShadowEnd
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = -1f;

    /// <summary>
    /// Per-instance brightness variation (ADR 0175): each instance's albedo is scaled by 1 ± this, from a hash of its
    /// origin (<see cref="InstanceVariation"/>; no instance data), so a stand of one species does not read as copies.
    /// 0 (default): off. Instanced draws only.
    /// </summary>
    [ExportGroup("Instance variation")]
    [Export(Range = "0,0.5,0.01")]
    public float InstanceValueJitter
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
    /// Per-instance hue variation (ADR 0175): each instance's albedo shifts warmer (towards olive and yellow) or cooler
    /// (towards blue-green) by up to this, from the same hash as <see cref="InstanceValueJitter"/>. 0 (default): off.
    /// </summary>
    [Export(Range = "0,0.5,0.01")]
    public float InstanceHueJitter
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
    /// Cut out with <see cref="AlphaCutout"/>, and also with <see cref="InstanceVisibility"/> (its dithered bands discard; an
    /// opaque surface's cutoff is 0, so only the bands cut).
    /// </summary>
    public override MaterialRenderState RenderState =>
        new(AlphaCutout || InstanceVisibility ? AlphaMode.Cutout : AlphaMode.Opaque, CullMode.Back, BackFace != FoliageBackFace.Cull);

    /// <summary>The Blinn-Phong highlight (strength, exponent) standing in for a roughness until PBR shading.</summary>
    internal static (float Specular, float Shininess) BlinnFromRoughness(float roughness)
    {
        var r = Math.Clamp(roughness, 0.05f, 1f);
        var a = r * r;
        return (0.25f * (1f - r), Math.Clamp(2f / (a * a) - 2f, 1f, 1024f));
    }
}

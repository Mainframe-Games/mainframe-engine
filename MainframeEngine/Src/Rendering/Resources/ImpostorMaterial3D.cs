using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// Draws an octahedral impostor (ADR 0172; <see cref="TreeImpostor"/>, which creates these): its mesh is a quad whose
/// vertex shader (<c>Impostor/Impostor.vk.vert</c>) turns it to the camera around <see cref="Center"/> and picks the three
/// nearest of the <see cref="Frames"/>² baked views; the fragment shader blends them, alpha-tests, reconstructs the surface
/// from the baked depth and lights it live with the foliage model (PBR, translucency on leaves, the vertex AO, SSAO, fog).
/// Its shadow caster faces the light and takes the view nearest it. Supports the per-instance visibility range of
/// <see cref="FoliageMaterial3D"/>, so it cross-fades with the mesh levels of a <see cref="TreeScatter"/>.
/// </summary>
[EditorIcon("palette")]
public sealed class ImpostorMaterial3D : Material
{
    /// <summary>Albedo with coverage alpha (sRGB): <see cref="TreeImpostor.Albedo"/>.</summary>
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

    /// <summary>Object-space normal (× 0.5 + 0.5) and depth (alpha): <see cref="TreeImpostor.Normal"/>.</summary>
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

    /// <summary>Thickness (R), ambient occlusion (G) and the leaf mask (B): <see cref="TreeImpostor.Detail"/>.</summary>
    [Export]
    public Texture2D? DetailTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Views per side of the atlas grid.</summary>
    [Export(Range = "2,32,1")]
    public int Frames
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 8;

    /// <summary>Hemi-octahedral views (the upper hemisphere) or full octahedral.</summary>
    [Export]
    public bool Hemi
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = true;

    /// <summary>
    /// Each pixel takes one of the three nearest views, chosen by a dither with the probability of its weight: a third of
    /// the fetches and no ghosting, but where the views' silhouettes differ a pixel is covered only some of the frames, so
    /// under TAA a canopy thins into haze. Off (default): the views are blended with sharpened weights (the nearest
    /// dominates) and the blended alpha is tested.
    /// </summary>
    [Export]
    public bool DitherViews
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>The views' centre (object space).</summary>
    [Export]
    public Vector3 Center
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>The views' half-size (object units): the baked mesh's bounding sphere.</summary>
    [Export(Range = "0.01,1000,0.01")]
    public float Radius
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
    public DrawingColor AlbedoColor
    {
        get;
        set
        {
            if (field.ToArgb() == value.ToArgb()) return;
            field = value;
            Touch();
        }
    } = DrawingColor.White;

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
    /// The share of the silhouette that casts into directional and spot shadow maps (ADR 0172): 1 (default) casts the baked
    /// silhouette; less thins it with a stable dither the shadow filter turns into a partial shadow, since one view of a
    /// whole tree is far denser than the sun's view through its canopy (sunflecks under a low sun).
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

    /// <summary>The cutout dithered as alpha to coverage under TAA (<see cref="FoliageMaterial3D.AlphaAntialiasingMode"/>).</summary>
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
    } = AlphaAntialiasing.AlphaToCoverage;

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
    } = 0.5f;

    /// <summary>Light through the leaves (the detail map's leaf mask), as <see cref="FoliageMaterial3D.Translucency"/>.</summary>
    [ExportGroup("Lighting")]
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

    /// <summary>Roughness of the leaves (the leaf mask) and of the bark.</summary>
    [Export(Range = "0,1,0.01")]
    public float LeafRoughness
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.65f;

    [Export(Range = "0,1,0.01")]
    public float BarkRoughness
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    } = 0.9f;

    /// <summary>Per-instance visibility range, as <see cref="FoliageMaterial3D.InstanceVisibility"/>.</summary>
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

    /// <summary>Cut out, both sides (the quad always faces the camera).</summary>
    public override MaterialRenderState RenderState => new(AlphaMode.Cutout, CullMode.Back, DoubleSided: true);
}

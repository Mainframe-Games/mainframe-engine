using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// The built-in water material (G8c, ADR 0159): flowing ripples, sky reflection, sun glints, depth colour and foam for
/// <see cref="River3D"/> ribbons and terrain ponds. By default it is the low-tier look of docs/design/future/water.md: the
/// surface is alpha-blended over what lies behind it and drawn after the opaques. With <see cref="RefractionEnabled"/>
/// (ADR 0173) it reads the view's scene copy instead: refraction, the true column, caustics and screen-space reflections.
/// </summary>
/// <remarks>
/// <para>Per vertex it reads <see cref="MeshSurface.Custom0"/>: x = water column depth in metres (bed to surface),
/// yz = surface flow in m/s along the mesh's local X and Z, w = foam (0..1); a missing stream is still, deep-less water.</para>
/// <para>The fragment shader (<c>Water/Water.vk.frag</c>): two layers (near and far scale) of the normal map in world
/// XZ, each advected along the flow in two phases (Vlachos, "Water Flow in Portal 2"); Schlick Fresnel with F0 = 0.02;
/// the sky's prefiltered radiance along the reflected ray × the split-sum BRDF; GGX glints from every light, shadowed;
/// Beer–Lambert absorption over the column along the view ray (column depth ÷ cos of the view angle) with
/// <see cref="ScatterColor"/> in-scattering; alpha from the absorption, faded out over <see cref="SoftEdgeDistance"/> of
/// column so shallow edges vanish; shore, rapids and flow foam; fog last.</para>
/// <para>Null <see cref="NormalMap"/> and <see cref="FoamTexture"/> use built-in tileable textures generated on first use
/// (<see cref="WaterTextures"/>). Every texture is sampled with the first texture's sampler (the foam texture's), so
/// custom maps should tile (repeat wrap).</para>
/// </remarks>
[EditorIcon("ripple")]
public sealed class WaterMaterial3D : Material
{
    /// <summary>Tangent-space ripple normal map, sampled in world XZ (OpenGL convention, +Y up); null: the built-in one.</summary>
    [ExportGroup("Surface")]
    [Export]
    public Texture2D? NormalMap
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Metres per repeat of the near normal layer.</summary>
    [Export(Range = "0.1,64,0.1")]
    public float NormalScaleNear { get; set { if (field == value) return; field = value; Touch(); } } = 3f;

    /// <summary>Metres per repeat of the far normal layer (rotated, half as fast).</summary>
    [Export(Range = "0.1,256,0.1")]
    public float NormalScaleFar { get; set { if (field == value) return; field = value; Touch(); } } = 11f;

    /// <summary>Ripple strength (0 = a flat mirror).</summary>
    [Export(Range = "0,4,0.01")]
    public float NormalStrength { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>Perceptual roughness of the reflection and the glints.</summary>
    [Export(Range = "0,1,0.01")]
    public float Roughness { get; set { if (field == value) return; field = value; Touch(); } } = 0.06f;

    /// <summary>Seconds per flow phase: shorter cycles stretch the ripples less but pulse more.</summary>
    [ExportGroup("Flow")]
    [Export(Range = "0.1,10,0.05")]
    public float FlowCycle { get; set { if (field == value) return; field = value; Touch(); } } = 1.6f;

    /// <summary>Scales the vertex flow (<c>Custom0.yz</c>).</summary>
    [Export(Range = "0,4,0.01")]
    public float FlowScale { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>How much the world's wind (<see cref="WorldEnvironment.WindStrength"/>) pushes the ripples, m/s per unit of strength.</summary>
    [Export(Range = "0,1,0.01")]
    public float WindDrift { get; set { if (field == value) return; field = value; Touch(); } } = 0.05f;

    /// <summary>Absorption per metre of water, linear RGB (Beer–Lambert): red goes first, so deep water turns blue-green.</summary>
    [ExportGroup("Colour")]
    [Export]
    public Vector3 Absorption { get; set { if (field == value) return; field = value; Touch(); } } = new(0.45f, 0.09f, 0.06f);

    /// <summary>The colour light scattered inside the water takes on (sRGB).</summary>
    [Export]
    public DrawingColor ScatterColor { get; set { if (field == value) return; field = value; Touch(); } } = DrawingColor.FromArgb(255, 13, 56, 61);

    /// <summary>Strength of the in-scattered light (0 = clear water that only absorbs).</summary>
    [Export(Range = "0,4,0.01")]
    public float ScatterStrength { get; set { if (field == value) return; field = value; Touch(); } } = 0.6f;

    /// <summary>
    /// Reads the view's scene copy (ADR 0173): the bed refracted through the ripples, the true water column from the
    /// scene depth, caustics on the bed and screen-space reflections. Off: the alpha-blended look of ADR 0159.
    /// </summary>
    [ExportGroup("Refraction")]
    [Export]
    public bool RefractionEnabled { get; set { if (field == value) return; field = value; Touch(); } }

    /// <summary>How much the ripples and the surface bend the view of the bed: 1 is water's (IOR 1.33), 0 none.</summary>
    [Export(Range = "0,4,0.01")]
    public float RefractionStrength { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>Blur of the bed through deep water: the fraction of the copy's levels used at 2 m of path.</summary>
    [Export(Range = "0,1,0.01")]
    public float RefractionRoughness { get; set { if (field == value) return; field = value; Touch(); } } = 0.15f;

    /// <summary>Scales the reflection (the sky's, and the scene's where screen-space reflections find it).</summary>
    [ExportGroup("Reflection")]
    [Export(Range = "0,2,0.01")]
    public float ReflectionStrength { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>
    /// Reflects the scene where a screen-space ray finds it (with <see cref="RefractionEnabled"/>; quality from
    /// <see cref="RenderServer.WaterSsr"/>), falling back to the sky elsewhere.
    /// </summary>
    [Export]
    public bool ScreenSpaceReflections { get; set { if (field == value) return; field = value; Touch(); } } = true;

    /// <summary>The caustic pattern (R, tileable), projected along the sun onto the bed; null: the built-in one.</summary>
    [ExportGroup("Caustics")]
    [Export]
    public Texture2D? CausticsTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Metres per repeat of the caustic pattern.</summary>
    [Export(Range = "0.1,32,0.1")]
    public float CausticsScale { get; set { if (field == value) return; field = value; Touch(); } } = 3f;

    /// <summary>Brightness of the caustics on the sunlit bed (0 = none; with <see cref="RefractionEnabled"/>).</summary>
    [Export(Range = "0,4,0.01")]
    public float CausticsStrength { get; set { if (field == value) return; field = value; Touch(); } } = 0.5f;

    /// <summary>Depth below the surface (metres) by which the caustics have faded out.</summary>
    [Export(Range = "0.1,20,0.1")]
    public float CausticsMaxDepth { get; set { if (field == value) return; field = value; Touch(); } } = 3f;

    /// <summary>Foam mask texture (R), sampled in world XZ; null: the built-in cells.</summary>
    [ExportGroup("Foam")]
    [Export]
    public Texture2D? FoamTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Metres per repeat of the foam texture.</summary>
    [Export(Range = "0.1,64,0.1")]
    public float FoamScale { get; set { if (field == value) return; field = value; Touch(); } } = 2f;

    /// <summary>Water column (metres) under which shore foam appears; at banks the ribbon's UV.u edge adds a band too.</summary>
    [Export(Range = "0,4,0.01")]
    public float ShoreFoamDistance { get; set { if (field == value) return; field = value; Touch(); } } = 0.15f;

    /// <summary>Scales all foam (shore, vertex foam, fast flow).</summary>
    [Export(Range = "0,4,0.01")]
    public float FoamStrength { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>Water column (metres) over which the surface fades in from the shoreline (soft edges).</summary>
    [ExportGroup("Edges")]
    [Export(Range = "0,2,0.01")]
    public float SoftEdgeDistance { get; set { if (field == value) return; field = value; Touch(); } } = 0.1f;

    /// <summary>Blended (back to front after the opaques), back faces culled, no depth writes, no shadows cast.</summary>
    public override MaterialRenderState RenderState => new(AlphaMode.Blend, CullMode.Back, false);
}

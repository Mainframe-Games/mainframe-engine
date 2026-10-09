using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// The look of a fall's sheet (G8e.6, ADR 0173): the jet <see cref="River3D"/> generates where its profile drops
/// (<see cref="River3D.FallSlope"/>). Streaks of the foam texture stretched along the ribbon run down the sheet at its
/// fall speed, the sheet turns from clear water at the lip to aerated white at the base, and thins out at its edges.
/// Blended (drawn back to front after the opaques), double-sided, no depth writes, no shadows.
/// </summary>
/// <remarks>
/// <para>Per vertex (<see cref="MeshSurface.Custom0"/>, as River3D writes it): x = how far down the fall (0 at the lip, 1
/// at the base), yz = the sheet's speed in ribbon-UV units per second along any horizontal direction (its length is
/// what counts), w = aeration (0 clear … 1 white). UV.u is across the sheet, UV.v along it.</para>
/// <para>Shaders <c>Water/Water.vk.vert</c> + <c>Water/Waterfall.vk.frag</c>. Sun and sky light, a translucent glow where
/// the sun shines through the sheet, GGX glints on its clear parts, fog. In the main view it marks itself reactive for
/// TAA like water.</para>
/// </remarks>
[EditorIcon("ripple")]
public sealed class WaterfallMaterial3D : Material
{
    /// <summary>The streak texture (R), stretched along the sheet; null: the built-in foam cells.</summary>
    [ExportGroup("Sheet")]
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

    /// <summary>Streak repeats across the sheet.</summary>
    [Export(Range = "0.5,32,0.1")]
    public float StreakScale { get; set { if (field == value) return; field = value; Touch(); } } = 4f;

    /// <summary>How many times longer than wide the streaks are.</summary>
    [Export(Range = "1,16,0.1")]
    public float StreakLength { get; set { if (field == value) return; field = value; Touch(); } } = 5f;

    /// <summary>Scales the speed the streaks run down the sheet.</summary>
    [Export(Range = "0,4,0.01")]
    public float FlowScale { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>Perceptual roughness of the clear water's glints.</summary>
    [Export(Range = "0,1,0.01")]
    public float Roughness { get; set { if (field == value) return; field = value; Touch(); } } = 0.2f;

    /// <summary>The clear water at the lip (sRGB).</summary>
    [ExportGroup("Colour")]
    [Export]
    public DrawingColor WaterColor { get; set { if (field == value) return; field = value; Touch(); } } = DrawingColor.FromArgb(255, 46, 74, 70);

    /// <summary>The aerated water (sRGB).</summary>
    [Export]
    public DrawingColor FoamColor { get; set { if (field == value) return; field = value; Touch(); } } = DrawingColor.FromArgb(255, 232, 238, 236);

    /// <summary>Opacity of the sheet where it is clear (it is opaque where white).</summary>
    [Export(Range = "0,1,0.01")]
    public float Opacity { get; set { if (field == value) return; field = value; Touch(); } } = 0.7f;

    /// <summary>Scales the vertex aeration: more turns the sheet white sooner.</summary>
    [Export(Range = "0,4,0.01")]
    public float Aeration { get; set { if (field == value) return; field = value; Touch(); } } = 1f;

    /// <summary>Blended, double-sided, no depth writes, no shadows.</summary>
    public override MaterialRenderState RenderState => new(AlphaMode.Blend, CullMode.Back, true);
}

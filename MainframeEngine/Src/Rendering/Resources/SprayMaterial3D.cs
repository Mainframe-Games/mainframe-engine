using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// The look of <see cref="SprayCards3D"/> (G8e.6, ADR 0173): camera-facing soft cards of mist and spray. Each card rises,
/// grows and fades over its own cycle, its alpha is scrolling noise in a round falloff, it fades where it meets the
/// scene behind it (the view's scene copy, when the view has one) and near the camera, and it is lit by the sky and the
/// sun (brighter looking towards the sun: forward scattering). Blended, no depth writes, no shadows.
/// </summary>
/// <remarks>Shaders <c>Water/Spray.vk.vert</c> (billboarding) + <c>Water/Spray.vk.frag</c>; set 3 of the water-scene layout.</remarks>
[EditorIcon("ripple")]
public sealed class SprayMaterial3D : Material
{
    /// <summary>The noise the card alpha is cut from (R, tileable); null: the built-in mist (<see cref="WaterTextures.Mist"/>).</summary>
    [Export]
    public Texture2D? NoiseTexture
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Touch();
        }
    }

    /// <summary>The mist's colour (sRGB) before lighting.</summary>
    [Export]
    public DrawingColor Color { get; set { if (field == value) return; field = value; Touch(); } } = DrawingColor.FromArgb(255, 236, 240, 240);

    /// <summary>Peak opacity of a card.</summary>
    [Export(Range = "0,1,0.01")]
    public float Opacity { get; set { if (field == value) return; field = value; Touch(); } } = 0.28f;

    /// <summary>Seconds per card cycle (rise, grow, fade).</summary>
    [Export(Range = "0.2,20,0.1")]
    public float Cycle { get; set { if (field == value) return; field = value; Touch(); } } = 3.5f;

    /// <summary>Metres a card rises over its cycle.</summary>
    [Export(Range = "0,10,0.05")]
    public float Rise { get; set { if (field == value) return; field = value; Touch(); } } = 1.2f;

    /// <summary>How much a card grows over its cycle (1 = none).</summary>
    [Export(Range = "1,4,0.01")]
    public float Growth { get; set { if (field == value) return; field = value; Touch(); } } = 1.8f;

    /// <summary>Distance (metres) over which a card fades out where it meets the scene behind it.</summary>
    [Export(Range = "0,4,0.01")]
    public float SoftDistance { get; set { if (field == value) return; field = value; Touch(); } } = 0.6f;

    /// <summary>Blended, both faces (the cards face the camera), no depth writes, no shadows.</summary>
    public override MaterialRenderState RenderState => new(AlphaMode.Blend, CullMode.Disabled, true);
}

namespace MainframeEngine;

/// <summary>How a canvas item blends into what is below it (Godot's <c>CanvasItemMaterial.BlendMode</c>).</summary>
public enum CanvasBlendMode : byte
{
    /// <summary>Alpha blending: colour <c>SRC_ALPHA, 1 − SRC_ALPHA</c>; alpha <c>ONE, 1 − SRC_ALPHA</c>.</summary>
    Mix,

    /// <summary>Additive: <c>SRC_ALPHA, ONE</c>.</summary>
    Add,

    /// <summary>Subtractive: reverse-subtract <c>SRC_ALPHA, ONE</c>.</summary>
    Sub,

    /// <summary>Multiplicative: <c>DST_COLOR, ZERO</c>.</summary>
    Mul,

    /// <summary>Premultiplied alpha: <c>ONE, 1 − SRC_ALPHA</c>.</summary>
    PremultAlpha,

    /// <summary>No blending (a shader's <c>render_mode blend_disabled</c>).</summary>
    Disabled,

    /// <summary>
    /// Inside a clip-children group (internal): premultiplied colour <c>DST_ALPHA, 1 − SRC_ALPHA</c>, alpha kept
    /// (<c>ZERO, ONE</c>), so a child lands only where its owner drew.
    /// </summary>
    Atop,
}

/// <summary>How 2D lights affect an item (Godot's <c>CanvasItemMaterial.LightMode</c>).</summary>
public enum CanvasLightMode : byte
{
    Normal,
    Unshaded,
    LightOnly,
}

/// <summary>
/// Fixed-function canvas item material (Godot's <c>CanvasItemMaterial</c>): blend mode and light mode, no shader.
/// </summary>
[EditorIcon("palette")]
public sealed class CanvasItemMaterial : Material
{
    [Export]
    public CanvasBlendMode BlendMode
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
    public CanvasLightMode LightMode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>Canvas materials carry no 3D render state.</summary>
    public override MaterialRenderState RenderState => default;
}

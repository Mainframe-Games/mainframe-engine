using System.Numerics;

namespace MainframeEngine;

/// <summary>How a 2D light combines with what it lights (Godot's <c>Light2D.BlendMode</c>).</summary>
public enum Light2DBlendMode : byte
{
    Add,
    Sub,
    Mix,
}

/// <summary>
/// A textured 2D light (Godot's <c>PointLight2D</c>, ADR 0117): its <see cref="Texture"/>, centred on the node (plus
/// <see cref="Offset"/>) and scaled by <see cref="TextureScale"/>, adds <c>texture × Color × Energy × the item's own colour</c>
/// to every item on the same canvas whose light mask matches <see cref="RangeItemCullMask"/> and whose z index is in
/// range — after the <see cref="CanvasModulate"/>, so it shows through a night tint. No shadows or normal maps yet; up to
/// eight lights per frame.
/// </summary>
[EditorIcon("bulb", Family = EditorIconFamily.Space2D)]
public class PointLight2D : Node2D
{
    private Canvas? _canvas;

    [Export] public bool Enabled { get; set; } = true;

    [Export] public Texture2D? Texture { get; set; }

    [Export(Range = "0.01,50,0.01")] public float TextureScale { get; set; } = 1f;

    /// <summary>Moves the texture relative to the node (Godot's <c>offset</c>).</summary>
    [Export] public Vector2 Offset { get; set; }

    /// <summary>Straight colour; alpha scales the light with <see cref="Energy"/>.</summary>
    [Export] public Vector4 Color { get; set; } = Vector4.One;

    [Export(Range = "0,16,0.01")] public float Energy { get; set; } = 1f;

    [Export] public Light2DBlendMode BlendMode { get; set; }

    /// <summary>Items lit: those whose <see cref="CanvasItem.LightMask"/> shares a bit with this.</summary>
    [Export] public uint RangeItemCullMask { get; set; } = 1;

    [Export] public int RangeZMin { get; set; } = -1024;

    [Export] public int RangeZMax { get; set; } = 1024;

    /// <summary>Godot's <c>shadow_enabled</c> (kept for ported scenes; shadows are not drawn yet).</summary>
    [Export] public bool ShadowEnabled { get; set; }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _canvas = GetCanvas();
        _canvas?.AddLight(this);
    }

    protected override void OnExitTree()
    {
        _canvas?.RemoveLight(this);
        _canvas = null;
        base.OnExitTree();
    }

    /// <summary>Lights the item with <paramref name="lightMask"/> and absolute z <paramref name="z"/>.</summary>
    public bool Affects(uint lightMask, int z) => (lightMask & RangeItemCullMask) != 0 && z >= RangeZMin && z <= RangeZMax;
}

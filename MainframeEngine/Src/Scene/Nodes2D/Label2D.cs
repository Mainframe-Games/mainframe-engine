using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// One line of text in the world: drawn on the canvas with <see cref="Font"/> at <see cref="FontSize"/>, its top-left at
/// the node, aligned inside <see cref="Width"/>. Layers in Godot's label order: the shadow's outline, the shadow
/// (<see cref="ShadowColor"/>, <see cref="ShadowOffset"/>), the outline, then the text. Nothing is drawn without a font.
/// For menus and HUDs use the UI (<see cref="UiDocument"/>); this is for prompts and tags that move with the world.
/// </summary>
[EditorIcon("letter-t", Family = EditorIconFamily.Space2D)]
public class Label2D : Node2D
{
    private string _text = "";
    private Font? _font;
    private int _fontSize = 16;
    private Color _fontColor = Colors.White;
    private int _outlineSize;
    private Color _outlineColor = Colors.Black;
    private Color _shadowColor = Colors.Transparent;
    private Vector2 _shadowOffset = new(1, 1);
    private int _shadowOutlineSize;
    private HorizontalAlignment _alignment;
    private float _width;

    [Export(Translatable = true)]
    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? "");
    }

    [Export]
    public Font? Font
    {
        get => _font;
        set
        {
            if (ReferenceEquals(_font, value))
                return;
            _font = value;
            QueueRedraw();
        }
    }

    [Export(Range = "1,256,1")]
    public int FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, Math.Max(1, value));
    }

    [Export]
    public Color FontColor
    {
        get => _fontColor;
        set => Set(ref _fontColor, value);
    }

    /// <summary>Outline thickness in pixels (0 = none).</summary>
    [Export(Range = "0,64,1")]
    public int OutlineSize
    {
        get => _outlineSize;
        set => Set(ref _outlineSize, Math.Max(0, value));
    }

    [Export]
    public Color OutlineColor
    {
        get => _outlineColor;
        set => Set(ref _outlineColor, value);
    }

    /// <summary>The shadow's colour; fully transparent (the default) draws no shadow.</summary>
    [Export]
    public Color ShadowColor
    {
        get => _shadowColor;
        set => Set(ref _shadowColor, value);
    }

    [Export]
    public Vector2 ShadowOffset
    {
        get => _shadowOffset;
        set => Set(ref _shadowOffset, value);
    }

    /// <summary>The shadow's own outline in pixels (0 = none).</summary>
    [Export(Range = "0,64,1")]
    public int ShadowOutlineSize
    {
        get => _shadowOutlineSize;
        set => Set(ref _shadowOutlineSize, Math.Max(0, value));
    }

    [Export]
    public HorizontalAlignment HorizontalAlignment
    {
        get => _alignment;
        set => Set(ref _alignment, value);
    }

    /// <summary>The box the text aligns in, in pixels; never narrower than the text (<see cref="GetMinimumSize"/>).</summary>
    [Export]
    public float Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>The line's width (rounded up) and the font's height; zero without a font.</summary>
    public Vector2 GetMinimumSize()
    {
        if (_font is null)
            return Vector2.Zero;
        var size = _font.GetStringSize(_text, fontSize: _fontSize);
        return new Vector2(MathF.Ceiling(size.X), size.Y);
    }

    /// <summary>The label's box: <see cref="Width"/> (at least the text's) by the font's height.</summary>
    public Vector2 Size => new(MathF.Max(_width, GetMinimumSize().X), GetMinimumSize().Y);

    protected override void OnDraw()
    {
        if (_text.Length == 0 || _font is not { } font)
            return;
        var width = MathF.Ceiling(font.GetStringSize(_text, fontSize: _fontSize).X);
        var box = MathF.Max(_width, width);
        var x = _alignment switch
        {
            HorizontalAlignment.Center => (int)(box - width) / 2,
            HorizontalAlignment.Right => box - width,
            _ => 0f,
        };
        var baseline = new Vector2(x, font.GetAscent(_fontSize));

        if (_shadowColor.A > 0)
        {
            if (_shadowOutlineSize > 0)
                DrawStringOutline(font, baseline + _shadowOffset, _text, fontSize: _fontSize, size: _shadowOutlineSize, modulate: _shadowColor);
            DrawString(font, baseline + _shadowOffset, _text, fontSize: _fontSize, modulate: _shadowColor);
        }

        if (_outlineSize > 0 && _outlineColor.A != 0)
            DrawStringOutline(font, baseline, _text, fontSize: _fontSize, size: _outlineSize, modulate: _outlineColor);
        DrawString(font, baseline, _text, fontSize: _fontSize, modulate: _fontColor);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        QueueRedraw();
    }
}

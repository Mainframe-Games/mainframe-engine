using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Text in the world: drawn on the canvas with <see cref="Font"/> at <see cref="FontSize"/>, its top-left at the node,
/// aligned inside <see cref="Width"/> by <see cref="Height"/>. Newlines break lines, and <see cref="Autowrap"/> wraps lines
/// wider than <see cref="Width"/> (Godot's <c>Label</c> with <c>autowrap_mode</c>); lines are
/// <see cref="LineSpacing"/> apart. Layers in Godot's label order: the shadow's outline, the shadow
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
    private VerticalAlignment _verticalAlignment;
    private AutowrapMode _autowrap;
    private float _width;
    private float _height;
    private float _lineSpacing = 3f;
    private readonly List<string> _lines = [];

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

    /// <summary>Where the lines sit in <see cref="Height"/> (top by default).</summary>
    [Export]
    public VerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        set => Set(ref _verticalAlignment, value);
    }

    /// <summary>
    /// How lines wider than <see cref="Width"/> break (Godot's <c>autowrap_mode</c>; off by default). Needs a
    /// <see cref="Width"/>: with 0 only newlines break lines.
    /// </summary>
    [Export]
    public AutowrapMode Autowrap
    {
        get => _autowrap;
        set => Set(ref _autowrap, value);
    }

    /// <summary>
    /// The box the text aligns in, in pixels, and the wrap width for <see cref="Autowrap"/> (0 = no wrap); never narrower
    /// than the widest line (<see cref="GetMinimumSize"/>).
    /// </summary>
    [Export]
    public float Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>The box's height for <see cref="VerticalAlignment"/>, in pixels; never shorter than the lines (0 = theirs).</summary>
    [Export]
    public float Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    /// <summary>Extra pixels between lines (Godot's <c>line_spacing</c>, 3 by default).</summary>
    [Export(Range = "-64,256,1")]
    public float LineSpacing
    {
        get => _lineSpacing;
        set => Set(ref _lineSpacing, value);
    }

    /// <summary>The lines as laid out (newlines and <see cref="Autowrap"/>); empty without a font.</summary>
    public IReadOnlyList<string> GetLines()
    {
        Layout();
        return _lines;
    }

    /// <summary>The widest line (rounded up) by the lines' height; zero without a font.</summary>
    public Vector2 GetMinimumSize()
    {
        if (_font is not { } font)
            return Vector2.Zero;
        Layout();
        var width = 0f;
        foreach (var line in _lines)
            width = MathF.Max(width, MathF.Ceiling(font.GetStringSize(line, fontSize: _fontSize).X));
        return new Vector2(width, TextHeight(font));
    }

    /// <summary>The label's box: <see cref="Width"/> by <see cref="Height"/>, each at least the text's.</summary>
    public Vector2 Size
    {
        get
        {
            var min = GetMinimumSize();
            return new Vector2(MathF.Max(_width, min.X), MathF.Max(_height, min.Y));
        }
    }

    private float TextHeight(Font font) =>
        _lines.Count == 0 ? 0f : _lines.Count * font.GetHeight(_fontSize) + (_lines.Count - 1) * _lineSpacing;

    protected override void OnDraw()
    {
        if (_text.Length == 0 || _font is not { } font)
            return;
        var size = Size; // lays out the lines
        var lineHeight = font.GetHeight(_fontSize) + _lineSpacing;
        var top = _verticalAlignment switch
        {
            VerticalAlignment.Center => (int)(size.Y - TextHeight(font)) / 2,
            VerticalAlignment.Bottom => size.Y - TextHeight(font),
            _ => 0f,
        };
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Length == 0)
                continue;
            var width = MathF.Ceiling(font.GetStringSize(line, fontSize: _fontSize).X);
            var x = _alignment switch
            {
                HorizontalAlignment.Center => (int)(size.X - width) / 2,
                HorizontalAlignment.Right => size.X - width,
                _ => 0f,
            };
            DrawLine(font, new Vector2(x, top + i * lineHeight + font.GetAscent(_fontSize)), line);
        }
    }

    private void DrawLine(Font font, Vector2 baseline, string line)
    {
        if (_shadowColor.A > 0)
        {
            if (_shadowOutlineSize > 0)
                DrawStringOutline(font, baseline + _shadowOffset, line, fontSize: _fontSize, size: _shadowOutlineSize, modulate: _shadowColor);
            DrawString(font, baseline + _shadowOffset, line, fontSize: _fontSize, modulate: _shadowColor);
        }

        if (_outlineSize > 0 && _outlineColor.A != 0)
            DrawStringOutline(font, baseline, line, fontSize: _fontSize, size: _outlineSize, modulate: _outlineColor);
        DrawString(font, baseline, line, fontSize: _fontSize, modulate: _fontColor);
    }

    // The lines for the current text, font, size, width and wrap mode (rebuilt when one of them changed).
    private (string Text, Font? Font, int Size, float Width, AutowrapMode Mode)? _laidOut;

    private void Layout()
    {
        var key = (_text, _font, _fontSize, _width, _autowrap);
        if (_laidOut == key)
            return;
        _laidOut = key;
        _lines.Clear();
        if (_font is not { } font || _text.Length == 0)
            return;
        var wrap = _autowrap != AutowrapMode.Off && _width > 0f;
        foreach (var paragraph in _text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            if (!wrap)
                _lines.Add(paragraph);
            else
                Wrap(font, paragraph);
    }

    private float Measure(Font font, string text) => font.GetStringSize(text, fontSize: _fontSize).X;

    // Greedy wrapping, as Godot's: words go on the line while it fits; the spaces at a break are dropped.
    private void Wrap(Font font, string paragraph)
    {
        if (_autowrap == AutowrapMode.Arbitrary)
        {
            BreakArbitrary(font, paragraph);
            return;
        }

        var line = "";
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Measure(font, candidate) <= _width)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
                _lines.Add(line);
            if (_autowrap == AutowrapMode.WordSmart && Measure(font, word) > _width)
                line = BreakArbitrary(font, word, keepLast: true);
            else
                line = word; // Word: a word wider than the line overflows it
        }

        _lines.Add(line);
    }

    // Breaks between characters; with keepLast the unfinished last piece is returned instead of added.
    private string BreakArbitrary(Font font, string text, bool keepLast = false)
    {
        var start = 0;
        for (var end = 1; end <= text.Length; end++)
        {
            if (end - start > 1 && Measure(font, text[start..end]) > _width)
            {
                _lines.Add(text[start..(end - 1)].TrimEnd(' '));
                start = end - 1;
                while (start < text.Length - 1 && text[start] == ' ')
                    start++;
            }
        }

        var last = text[start..];
        if (keepLast)
            return last;
        _lines.Add(last);
        return "";
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        QueueRedraw();
    }
}

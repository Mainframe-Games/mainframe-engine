using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A textured rectangle on the canvas (Godot's <c>Sprite2D</c>): the whole texture or a region of it, optionally split
/// into <see cref="Hframes"/> × <see cref="Vframes"/> frames, centred on the node or offset, flipped. Draws exactly as
/// Godot's <c>Sprite2D::_get_rects</c> + <c>draw_rect_region</c>.
/// </summary>
[EditorIcon("photo", Family = EditorIconFamily.Space2D)]
public class Sprite2D : Node2D
{
    private Texture2D? _texture;
    private bool _centered = true;
    private Vector2 _offset;
    private bool _flipH, _flipV;
    private bool _regionEnabled;
    private Rect2 _regionRect;
    private bool _regionFilterClip;
    private int _hframes = 1, _vframes = 1, _frame;

    [Export]
    public Texture2D? Texture
    {
        get => _texture;
        set
        {
            if (ReferenceEquals(_texture, value))
                return;
            if (_texture is not null)
                _texture.Changed -= QueueRedraw;
            _texture = value;
            if (_texture is not null)
                _texture.Changed += QueueRedraw;
            QueueRedraw();
            TextureChanged?.Invoke();
        }
    }

    /// <summary>Centres the texture on the node's origin (otherwise its top-left corner is at <see cref="Offset"/>).</summary>
    [Export]
    public bool Centered
    {
        get => _centered;
        set => Set(ref _centered, value);
    }

    [Export]
    public Vector2 Offset
    {
        get => _offset;
        set => Set(ref _offset, value);
    }

    [Export]
    public bool FlipH
    {
        get => _flipH;
        set => Set(ref _flipH, value);
    }

    [Export]
    public bool FlipV
    {
        get => _flipV;
        set => Set(ref _flipV, value);
    }

    [Export]
    public bool RegionEnabled
    {
        get => _regionEnabled;
        set => Set(ref _regionEnabled, value);
    }

    /// <summary>The part of the texture drawn while <see cref="RegionEnabled"/> (texture pixels).</summary>
    [Export]
    public Rect2 RegionRect
    {
        get => _regionRect;
        set => Set(ref _regionRect, value);
    }

    [Export]
    public bool RegionFilterClipEnabled
    {
        get => _regionFilterClip;
        set => Set(ref _regionFilterClip, value);
    }

    [Export(Range = "1,16384,1")]
    public int Hframes
    {
        get => _hframes;
        set => Set(ref _hframes, Math.Max(1, value));
    }

    [Export(Range = "1,16384,1")]
    public int Vframes
    {
        get => _vframes;
        set => Set(ref _vframes, Math.Max(1, value));
    }

    [Export]
    public int Frame
    {
        get => _frame;
        set => Set(ref _frame, Math.Clamp(value, 0, _hframes * _vframes - 1));
    }

    /// <summary>Raised when <see cref="Texture"/> changes.</summary>
    [Signal]
    public event Action? TextureChanged;

    /// <summary>The drawn rectangle in local space (Godot's <c>get_rect</c>; 1×1 at the origin without a texture).</summary>
    public Rect2 GetRect()
    {
        if (_texture is null)
            return new Rect2(0, 0, 1, 1);
        // Godot works in Size2i here: the size truncates, then divides by the frame counts (integer division).
        int w, h;
        if (_regionEnabled)
            (w, h) = ((int)_regionRect.Size.X, (int)_regionRect.Size.Y);
        else
            (w, h) = (_texture.Width, _texture.Height);
        w /= _hframes;
        h /= _vframes;
        var offset = _offset;
        if (_centered)
            offset -= new Vector2(w, h) / 2f;
        if (w == 0 && h == 0)
            (w, h) = (1, 1);
        return new Rect2(offset, new Vector2(w, h));
    }

    private protected override void DrawBuiltin()
    {
        if (_texture is null)
            return;
        GetRects(out var src, out var dst);
        CanvasPrimitives.TextureRectRegion(DrawList, _texture, dst, src, Vector4.One, false);
    }

    /// <summary>Godot's <c>Sprite2D::_get_rects</c>.</summary>
    private void GetRects(out Rect2 src, out Rect2 dst)
    {
        var baseRect = _regionEnabled ? _regionRect : new Rect2(0, 0, _texture!.Width, _texture.Height);
        var frameSize = baseRect.Size / new Vector2(_hframes, _vframes);
        var frameOffset = new Vector2(_frame % _hframes, _frame / _hframes) * frameSize;
        src = new Rect2(baseRect.Position + frameOffset, frameSize);

        var destOffset = _offset;
        if (_centered)
            destOffset -= frameSize / 2f;
        dst = new Rect2(destOffset, frameSize);
        if (_flipH)
            dst.Size.X = -dst.Size.X;
        if (_flipV)
            dst.Size.Y = -dst.Size.Y;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        QueueRedraw();
    }
}

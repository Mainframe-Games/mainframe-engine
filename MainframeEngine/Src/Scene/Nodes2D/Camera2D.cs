using System.Numerics;

namespace MainframeEngine;

/// <summary>Where a <see cref="Camera2D"/>'s position sits on screen (Godot's <c>Camera2D.AnchorMode</c>).</summary>
public enum Camera2DAnchorMode : byte
{
    FixedTopLeft,
    DragCenter,
}

/// <summary>When a <see cref="Camera2D"/> updates its scroll (Godot's <c>Camera2D.Camera2DProcessCallback</c>).</summary>
public enum Camera2DProcessCallback : byte
{
    Physics,
    Idle,
}

/// <summary>
/// The 2D camera (Godot 4.7's <c>Camera2D</c>, ported): while current it sets its viewport's
/// <see cref="SceneViewport.CanvasTransform"/> — position (centred with <see cref="Camera2DAnchorMode.DragCenter"/>),
/// <see cref="Zoom"/> (2 = twice as close), <see cref="Offset"/>, limits, drag margins and position smoothing behave as in
/// Godot, including when the scroll updates: every process step (subclasses call <c>base.OnProcess</c> first, as Godot's
/// internal process runs before the script's), on transform changes without smoothing, and on every
/// <see cref="Zoom"/>/<see cref="Offset"/> set (which keeps the smoothed position). Also the ortho camera 3D visuals are
/// drawn with when the viewport has no <see cref="Camera3D"/>.
/// </summary>
[EditorIcon("camera")]
public class Camera2D : Node2D, ICurrentCamera
{
    private const float DefaultLimit = 10000000;

    private readonly OrthographicCamera _camera = new() { Forward = Vector3.UnitZ, Up = -Vector3.UnitY };
    private bool _current;
    private Vector2 _zoom = Vector2.One;
    private Vector2 _zoomScale = Vector2.One;
    private Vector2 _offset;
    private bool _first = true;
    private Vector2 _cameraPos, _smoothedCameraPos;
    private float _cameraAngle;
    private Vector2 _cameraScreenCenter;
    private bool _dragHorizontalOffsetChanged, _dragVerticalOffsetChanged;
    private float _dragHorizontalOffset, _dragVerticalOffset;

    public Camera2D()
    {
        SetNotifyTransform(true);
    }

    /// <summary>Renders the viewport (Godot's <c>is_current</c>/<c>make_current</c>).</summary>
    [Export]
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            if (value && GetViewport() is { } viewport)
            {
                viewport.MakeCurrent(this);
                UpdateScroll();
            }
        }
    }

    /// <summary>Godot's <c>enabled</c>: a camera entering a viewport without a current camera becomes current.</summary>
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public Camera2DAnchorMode AnchorMode { get; set; } = Camera2DAnchorMode.DragCenter;

    [Export]
    public bool IgnoreRotation { get; set; } = true;

    [Export]
    public Camera2DProcessCallback ProcessCallback { get; set; } = Camera2DProcessCallback.Idle;

    /// <summary>Godot's zoom: 2 shows the world twice as large. Setting it updates the scroll and keeps the smoothed position.</summary>
    [Export]
    public Vector2 Zoom
    {
        get => _zoom;
        set
        {
            if (MathF.Abs(value.X) < 0.00001f || MathF.Abs(value.Y) < 0.00001f)
                throw new ArgumentException("Zoom level must be different from 0 (can be negative).", nameof(value));
            _zoom = value;
            _zoomScale = Vector2.One / value;
            var old = _smoothedCameraPos;
            UpdateScroll();
            _smoothedCameraPos = old;
        }
    }

    /// <summary>Screen offset in canvas units (shakes). Setting it updates the scroll and keeps the smoothed position.</summary>
    [Export]
    public Vector2 Offset
    {
        get => _offset;
        set
        {
            _offset = value;
            var old = _smoothedCameraPos;
            UpdateScroll();
            _smoothedCameraPos = old;
        }
    }

    [Export]
    public bool LimitEnabled { get; set; } = true;

    [Export]
    public float LimitLeft { get; set; } = -DefaultLimit;

    [Export]
    public float LimitTop { get; set; } = -DefaultLimit;

    [Export]
    public float LimitRight { get; set; } = DefaultLimit;

    [Export]
    public float LimitBottom { get; set; } = DefaultLimit;

    [Export]
    public bool LimitSmoothed { get; set; }

    [Export]
    public bool PositionSmoothingEnabled { get; set; }

    [Export]
    public float PositionSmoothingSpeed { get; set; } = 5f;

    [Export]
    public bool RotationSmoothingEnabled { get; set; }

    [Export]
    public float RotationSmoothingSpeed { get; set; } = 5f;

    [Export]
    public bool DragHorizontalEnabled { get; set; }

    [Export]
    public bool DragVerticalEnabled { get; set; }

    [Export]
    public float DragLeftMargin { get; set; } = 0.2f;

    [Export]
    public float DragTopMargin { get; set; } = 0.2f;

    [Export]
    public float DragRightMargin { get; set; } = 0.2f;

    [Export]
    public float DragBottomMargin { get; set; } = 0.2f;

    public float DragHorizontalOffset
    {
        get => _dragHorizontalOffset;
        set
        {
            _dragHorizontalOffset = value;
            _dragHorizontalOffsetChanged = true;
        }
    }

    public float DragVerticalOffset
    {
        get => _dragVerticalOffset;
        set
        {
            _dragVerticalOffset = value;
            _dragVerticalOffsetChanged = true;
        }
    }

    /// <summary>Distance of the 3D ortho camera behind the z = 0 plane (content must lie within near/far of it).</summary>
    [Export]
    public float Distance { get; set; } = 500f;

    public ICamera RenderCamera => _camera;

    public void MakeCurrent() => Current = true;

    internal void ClearCurrentFlag() => _current = false;

    /// <summary>The centre of the screen in canvas space (Godot's <c>get_screen_center_position</c>).</summary>
    public Vector2 GetScreenCenterPosition() => _cameraScreenCenter;

    public float GetScreenRotation() => _cameraAngle;

    /// <summary>Snaps the smoothed position to the target (Godot's <c>reset_smoothing</c>).</summary>
    public void ResetSmoothing()
    {
        _smoothedCameraPos = _cameraPos;
        UpdateScroll();
    }

    public void ForceUpdateScroll() => UpdateScroll();

    /// <summary>Copies the screen centre, the visible size and the zoom into the wrapped 3D ortho camera.</summary>
    public ICamera SyncRenderCamera(Vector2 viewportSize)
    {
        var visible = GetViewport()?.GetVisibleRect().Size is { X: > 0 } v ? v : viewportSize;
        _camera.Position = new Vector3(_cameraScreenCenter, -Distance);
        _camera.Size = visible;
        _camera.Zoom = _zoomScale.X;
        return _camera;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        GetViewport()?.AddCamera(this);
        if (Enabled && GetViewport() is { ActiveCamera2D: null })
            MakeCurrent();
        _first = true;
        UpdateScroll();
    }

    protected override void OnExitTree()
    {
        GetViewport()?.RemoveCamera(this);
        base.OnExitTree();
    }

    /// <summary>Godot's <c>NOTIFICATION_INTERNAL_PROCESS</c>: subclasses overriding this call the base first.</summary>
    protected override void OnProcess(in GameTime gameTime)
    {
        _processDelta = gameTime.DeltaTime;
        if (ProcessCallback == Camera2DProcessCallback.Idle)
            UpdateScroll();
    }

    protected override void OnPhysicsProcess(float delta)
    {
        _physicsDelta = delta;
        if (ProcessCallback == Camera2DProcessCallback.Physics)
            UpdateScroll();
    }

    protected override void OnTransformChanged()
    {
        if (!PositionSmoothingEnabled)
            UpdateScroll();
    }

    private float _processDelta, _physicsDelta;

    /// <summary>Godot's <c>_update_scroll</c>: the current camera writes the viewport's canvas transform.</summary>
    private void UpdateScroll()
    {
        if (!IsInsideTree || !_current || GetViewport() is not { } viewport)
            return;
        if (Tree is { EditMode: true })
            return; // the editor's view drives an edited scene's canvas, as in Godot's editor
        viewport.CanvasTransform = GetCameraTransform(viewport);
    }

    /// <summary>Godot's <c>get_camera_transform</c> (drag margins, limits, smoothing), canvas space → viewport.</summary>
    private Transform2D GetCameraTransform(SceneViewport viewport)
    {
        var screenSize = viewport.GetVisibleRect().Size;
        var newCameraPos = GlobalPosition;
        Vector2 retCameraPos;

        if (!_first)
        {
            if (AnchorMode == Camera2DAnchorMode.DragCenter)
            {
                if (DragHorizontalEnabled && !_dragHorizontalOffsetChanged)
                {
                    _cameraPos.X = MathF.Min(_cameraPos.X, newCameraPos.X + screenSize.X * 0.5f * _zoomScale.X * DragLeftMargin);
                    _cameraPos.X = MathF.Max(_cameraPos.X, newCameraPos.X - screenSize.X * 0.5f * _zoomScale.X * DragRightMargin);
                }
                else
                {
                    _cameraPos.X = _dragHorizontalOffset < 0
                        ? newCameraPos.X + screenSize.X * 0.5f * DragRightMargin * _dragHorizontalOffset
                        : newCameraPos.X + screenSize.X * 0.5f * DragLeftMargin * _dragHorizontalOffset;
                    _dragHorizontalOffsetChanged = false;
                }

                if (DragVerticalEnabled && !_dragVerticalOffsetChanged)
                {
                    _cameraPos.Y = MathF.Min(_cameraPos.Y, newCameraPos.Y + screenSize.Y * 0.5f * _zoomScale.Y * DragTopMargin);
                    _cameraPos.Y = MathF.Max(_cameraPos.Y, newCameraPos.Y - screenSize.Y * 0.5f * _zoomScale.Y * DragBottomMargin);
                }
                else
                {
                    _cameraPos.Y = _dragVerticalOffset < 0
                        ? newCameraPos.Y + screenSize.Y * 0.5f * DragBottomMargin * _dragVerticalOffset
                        : newCameraPos.Y + screenSize.Y * 0.5f * DragTopMargin * _dragVerticalOffset;
                    _dragVerticalOffsetChanged = false;
                }
            }
            else
            {
                _cameraPos = newCameraPos;
            }

            var offset0 = AnchorMode == Camera2DAnchorMode.DragCenter ? screenSize * 0.5f * _zoomScale : Vector2.Zero;
            var rect0 = new Rect2(-offset0 + _cameraPos, screenSize * _zoomScale);
            if (LimitEnabled && LimitSmoothed)
            {
                if (LimitLeft > LimitRight - rect0.Size.X)
                    _cameraPos.X -= rect0.Position.X + (rect0.Size.X - LimitRight - LimitLeft) / 2;
                else if (rect0.Position.X < LimitLeft)
                    _cameraPos.X -= rect0.Position.X - LimitLeft;
                else if (rect0.Position.X + rect0.Size.X > LimitRight)
                    _cameraPos.X -= rect0.Position.X + rect0.Size.X - LimitRight;

                if (LimitTop > LimitBottom - rect0.Size.Y)
                    _cameraPos.Y -= rect0.Position.Y + (rect0.Size.Y - LimitBottom - LimitTop) / 2;
                else if (rect0.Position.Y < LimitTop)
                    _cameraPos.Y -= rect0.Position.Y - LimitTop;
                else if (rect0.Position.Y + rect0.Size.Y > LimitBottom)
                    _cameraPos.Y -= rect0.Position.Y + rect0.Size.Y - LimitBottom;
            }

            if (PositionSmoothingEnabled)
            {
                var delta = ProcessCallback == Camera2DProcessCallback.Physics ? _physicsDelta : _processDelta;
                var c = PositionSmoothingSpeed * delta;
                _smoothedCameraPos = (_cameraPos - _smoothedCameraPos) * c + _smoothedCameraPos;
                retCameraPos = _smoothedCameraPos;
            }
            else
            {
                retCameraPos = _smoothedCameraPos = _cameraPos;
            }
        }
        else
        {
            retCameraPos = _smoothedCameraPos = _cameraPos = newCameraPos;
            _first = false;
        }

        var screenOffset = AnchorMode == Camera2DAnchorMode.DragCenter ? screenSize * 0.5f * _zoomScale : Vector2.Zero;
        if (!IgnoreRotation)
        {
            if (RotationSmoothingEnabled)
            {
                var step = RotationSmoothingSpeed * (ProcessCallback == Camera2DProcessCallback.Physics ? _physicsDelta : _processDelta);
                _cameraAngle = LerpAngle(_cameraAngle, GlobalRotation, step);
            }
            else
            {
                _cameraAngle = GlobalRotation;
            }

            screenOffset = Rotated(screenOffset, _cameraAngle);
        }

        var screenRect = new Rect2(-screenOffset + retCameraPos, screenSize * _zoomScale);
        if (LimitEnabled && (!PositionSmoothingEnabled || !LimitSmoothed))
        {
            var bottomRight = screenRect.Position + 2f * (retCameraPos - screenRect.Position);
            if (LimitLeft > LimitRight - (bottomRight.X - screenRect.Position.X))
                screenRect.Position.X = (LimitLeft + LimitRight - (bottomRight.X - screenRect.Position.X)) / 2;
            else if (screenRect.Position.X < LimitLeft)
                screenRect.Position.X = LimitLeft;
            else if (bottomRight.X > LimitRight)
                screenRect.Position.X = LimitRight - (bottomRight.X - screenRect.Position.X);

            if (LimitTop > LimitBottom - (bottomRight.Y - screenRect.Position.Y))
                screenRect.Position.Y = (LimitTop + LimitBottom - (bottomRight.Y - screenRect.Position.Y)) / 2;
            else if (screenRect.Position.Y < LimitTop)
                screenRect.Position.Y = LimitTop;
            else if (bottomRight.Y > LimitBottom)
                screenRect.Position.Y = LimitBottom - (bottomRight.Y - screenRect.Position.Y);
        }

        if (_offset != Vector2.Zero)
            screenRect.Position += _offset;

        // xform: scale_basis(zoom_scale), set_rotation(angle) unless ignored, origin = screen rect position.
        var xform = new Transform2D(new Vector2(_zoomScale.X, 0), new Vector2(0, _zoomScale.Y), screenRect.Position);
        if (!IgnoreRotation)
            xform = Transform2D.FromTrs(screenRect.Position, _cameraAngle, _zoomScale);
        _cameraScreenCenter = xform.TransformPoint(0.5f * screenSize);
        return xform.AffineInverse();
    }

    private static Vector2 Rotated(Vector2 v, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    private static float LerpAngle(float from, float to, float weight)
    {
        var difference = (to - from) % MathF.Tau;
        var distance = (2f * difference) % MathF.Tau - difference;
        return from + distance * weight;
    }
}

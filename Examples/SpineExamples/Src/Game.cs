using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;
using SilkSpine.UI;
using Spine;

namespace SilkSpine;

internal sealed class Game : Engine
{
    private static readonly SpineFolder[] Folders =
    [
        new("Content/SpineBoy"),
        new("Content/Raptor"),
        new("Content/Windmill"),
        new("Content/CelestialCircus"),
    ];

    private SpineNode? _spineNode;

    private readonly InspectorUI _inspectorUI = new(Folders);

    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;
    private float _cameraSpeed = 20f;
    private bool CanMoveCamera => _mouse.Cursor.CursorMode is CursorMode.Raw;

    private readonly Camera2D _cameraOrth = new()
    {
        Position = new Vector3(0f, 1f, 0f),
        Zoom = 0.01f,
    };
    private readonly Camera3D _cameraPer = new()
    {
        Position = new Vector3(0f, 1f, 5f),
    };
    private ICamera CurrentCamera => _inspectorUI.UseOrthographicCamera ? _cameraOrth : _cameraPer;

    private static Vector2 _lastMousePos;
    private SceneGrid3d _sceneGrid3d = null!;
    private SceneGrid2d _sceneGrid2d = null!;

    public Game(in EngineOptions engineOptions) : base(in engineOptions)
    {
        _inspectorUI.OnModelChanged += OnModelChanged;
        _inspectorUI.OnAnimationChanged += SetAnimation;
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        _keyboard = InputContext.Keyboards[0];
        _mouse = InputContext.Mice[0];
        _keyboard.KeyDown += OnKeyDown;
        _mouse.Scroll += OnMouseScroll;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

        Renderer.SetClearColor(0.18f, 0.20f, 0.22f);

        _sceneGrid3d = new SceneGrid3d(Renderer);
        _sceneGrid2d = new SceneGrid2d(Renderer);

        OnModelChanged(Folders[0]);
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        if (_spineNode is null)
            return;

        var position = _spineNode.Position;
        var rotation = _spineNode.Rotation;
        var scale = _spineNode.Scale.X;

        _inspectorUI.OnImGui(
            _spineNode.Skeleton, CurrentCamera,
            ref _cameraSpeed,
            ref position, ref rotation, ref scale);

        _spineNode.Position = position;
        _spineNode.Rotation = rotation;
        _spineNode.Scale = new Vector3(scale, scale, scale);
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        UpdateCamera(gameTime.DeltaTime);

        SetSpineScale();

        _spineNode?.UpdateType = _inspectorUI.UpdatePhysics
            ? Skeleton.Physics.Update
            : Skeleton.Physics.None;

        _spineNode?.OnUpdate(gameTime);
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        Renderer.Clear();

        var fbSize = new Vector2(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        if (_inspectorUI.UseOrthographicCamera)
        {
            _cameraOrth.Size = fbSize;
            _sceneGrid2d.Draw(CurrentCamera);
        }
        else
        {
            _cameraPer.AspectRatio = fbSize.X / fbSize.Y;
            _sceneGrid3d.Draw(CurrentCamera);
        }

        _spineNode?.Draw(CurrentCamera, new LightEnvironment());
    }

    protected override void OnClose()
    {
        _spineNode?.Dispose();
        _sceneGrid3d?.Dispose();
        _sceneGrid2d?.Dispose();
        base.OnClose();
    }

    private void OnModelChanged(SpineFolder folder)
    {
        _spineNode?.Dispose();
        _spineNode = new SpineNode(Renderer, folder);

        _inspectorUI.SpineScale = _spineNode.SpineScale;
        _inspectorUI.ZSpacing = _spineNode.ZSpacing;
    }

    private void SetAnimation(string animationName)
    {
        _spineNode?.SetAnimation(animationName);
    }

    private void SetSpineScale()
    {
        _spineNode?.FlipX(_inspectorUI.IsFlipped);
    }

    private void UpdateCamera(double delta)
    {
        if (!CanMoveCamera) return;

        var camera = CurrentCamera;
        var baseSpeed = _keyboard.IsKeyPressed(Key.ShiftLeft) ? _cameraSpeed * 2 : _cameraSpeed;
        var speed = baseSpeed * (float)delta;

        if (camera is Camera3D)
        {
            if (_keyboard.IsKeyPressed(Key.W)) camera.Position += speed * camera.Forward;
            if (_keyboard.IsKeyPressed(Key.S)) camera.Position -= speed * camera.Forward;
            if (_keyboard.IsKeyPressed(Key.A)) camera.Position -= Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * speed;
            if (_keyboard.IsKeyPressed(Key.D)) camera.Position += Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * speed;
            if (_keyboard.IsKeyPressed(Key.Q)) camera.Position -= camera.Up * speed;
            if (_keyboard.IsKeyPressed(Key.E)) camera.Position += camera.Up * speed;
        }
        else
        {
            if (_keyboard.IsKeyPressed(Key.W)) camera.Position += camera.Up * speed;
            if (_keyboard.IsKeyPressed(Key.S)) camera.Position -= camera.Up * speed;
            if (_keyboard.IsKeyPressed(Key.A)) camera.Position -= Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * speed;
            if (_keyboard.IsKeyPressed(Key.D)) camera.Position += Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * speed;
        }
    }

    private void OnKeyDown(IKeyboard kb, Key key, int sc)
    {
        if (key == Key.Escape)
            Quit(ExitCode.Ok);
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (!CanMoveCamera)
        {
            _lastMousePos = default;
            return;
        }

        if (_lastMousePos == default)
        {
            _lastMousePos = pos;
            return;
        }

        var dx = (pos.X - _lastMousePos.X) * 0.1f;
        var dy = (pos.Y - _lastMousePos.Y) * 0.1f;
        _lastMousePos = pos;

        if (CurrentCamera is Camera3D cam3d)
            cam3d.ModifyDirection(dx, dy);
        else
            CurrentCamera.Position += new Vector3(-dx * 0.1f, dy * 0.1f, 0);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
    {
        if (CurrentCamera is Camera2D cam2d)
            cam2d.ModifyZoom(-scroll.Y * 0.05f);
    }

    private void OnMouseDown(IMouse mouse, MouseButton btn)
    {
        if (btn == MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Raw;
    }

    private void OnMouseUp(IMouse mouse, MouseButton btn)
    {
        if (btn == MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Normal;
    }
}

using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;
using SilkSpine.UI;
using Spine;
using Math = System.Math;

namespace SilkSpine;

internal class Game : IGame
{
    private static readonly SpineFolder[] Folders =
    [
        new("Content/SpineBoy"),
        new("Content/Raptor"),
        new("Content/Windmill"),
        new("Content/CelestialCircus"),
    ];

    private Engine _engine = null!;
    private IRenderer _renderer = null!;

    private Atlas _atlas = null!;
    private Skeleton _spineSkeleton = null!;
    private AnimationState _animationState = null!;
    private SpineRenderer? _spineRenderer;

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
    private Vector3 _modelPosition;
    private Vector3 _modelRotation;
    private float _modelScale = 0.2f;

    private SceneGrid3d _sceneGrid3d = null!;
    private SceneGrid2d _sceneGrid2d = null!;

    public Game()
    {
        _inspectorUI.OnModelChanged += OnModelChanged;
        _inspectorUI.OnAnimationChanged += SetAnimation;
    }

    public void OnLoad(in Engine engine)
    {
        _engine = engine;
        _renderer = engine.Renderer;

        _keyboard = engine.InputContext.Keyboards[0];
        _mouse = engine.InputContext.Mice[0];
        _keyboard.KeyDown += OnKeyDown;
        _mouse.Scroll += OnMouseScroll;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

        _renderer.SetClearColor(0.18f, 0.20f, 0.22f);

        _sceneGrid3d = new SceneGrid3d(_renderer);
        _sceneGrid2d = new SceneGrid2d(_renderer);

        OnModelChanged(Folders[0]);
    }

    public void OnResize(in Vector2 newSize) { }

    public void OnImGui(in GameTime gameTime)
    {
        if (_spineRenderer is null)
            return;
        
        _inspectorUI.OnImGui(
            _spineSkeleton, CurrentCamera,
            ref _cameraSpeed,
            ref _modelPosition, ref _modelRotation, ref _modelScale);
    }

    public void OnUpdate(in GameTime gameTime)
    {
        UpdateCamera(gameTime.DeltaTime);

        SetSpineScale();
        _spineSkeleton.UpdateWorldTransform(_inspectorUI.UpdatePhysics
            ? Skeleton.Physics.Update
            : Skeleton.Physics.None);
        _animationState.Update((float)gameTime.DeltaTime);
        _animationState.Apply(_spineSkeleton);
    }

    public void OnRender(in GameTime gameTime)
    {
        _renderer.Clear();

        var fbSize = new Vector2(_engine.Window.FramebufferSize.X, _engine.Window.FramebufferSize.Y);

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

        if (_spineRenderer is null) return;

        var model =
            Matrix4x4.CreateScale(_modelScale)
            * Matrix4x4.CreateRotationX(float.DegreesToRadians(_modelRotation.X))
            * Matrix4x4.CreateRotationY(float.DegreesToRadians(_modelRotation.Y))
            * Matrix4x4.CreateRotationZ(float.DegreesToRadians(_modelRotation.Z))
            * Matrix4x4.CreateTranslation(_modelPosition);

        _spineRenderer.Draw(
            _inspectorUI.ZSpacing,
            model,
            CurrentCamera.ViewMatrix,
            CurrentCamera.ProjectionMatrix);
    }

    public void OnClose()
    {
        _spineRenderer?.Dispose();
        _sceneGrid3d?.Dispose();
        _sceneGrid2d?.Dispose();
    }

    private void OnModelChanged(SpineFolder folder)
    {
        _spineRenderer?.Dispose();

        var textureLoader = new SpineTextureLoader();
        _atlas = new Atlas(folder.AtlasPath, textureLoader);

        var json = new SkeletonJson(_atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);
        _spineSkeleton = new Skeleton(skeletonData);
        _spineSkeleton.SetSkin(skeletonData.DefaultSkin);

        _inspectorUI.SpineScale = 0.02f;
        _inspectorUI.ZSpacing = 0.01f;

        _spineRenderer = new SpineRenderer(_renderer, _spineSkeleton, _atlas.Pages[0].pma, textureLoader);

        var animStateData = new AnimationStateData(skeletonData);
        _animationState = new AnimationState(animStateData);
        SetAnimation(_spineSkeleton.Data.Animations.Items[0].Name);
    }

    private void SetAnimation(string animationName)
    {
        var animation = _spineSkeleton.Data.FindAnimation(animationName);
        _animationState.AddAnimation(0, animation, true, 0);
    }

    private void SetSpineScale()
    {
        var scaleXAbs = Math.Abs(_inspectorUI.SpineScale);
        _spineSkeleton.ScaleX = _inspectorUI.IsFlipped ? -scaleXAbs : scaleXAbs;
        _spineSkeleton.ScaleY = _inspectorUI.SpineScale;
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
        if (key == Key.Escape) _engine.Quit(0);
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (!CanMoveCamera) { _lastMousePos = default; return; }
        if (_lastMousePos == default) { _lastMousePos = pos; return; }

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
        if (btn == MouseButton.Right) _mouse.Cursor.CursorMode = CursorMode.Raw;
    }

    private void OnMouseUp(IMouse mouse, MouseButton btn)
    {
        if (btn == MouseButton.Right) _mouse.Cursor.CursorMode = CursorMode.Normal;
    }
}

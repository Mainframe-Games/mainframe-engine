using System.Drawing;
using System.Numerics;
using ImGuiNET;
using MainframeEngine;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using SilkSpine.UI;
using Spine;
using Math = System.Math;

namespace SilkSpine;

internal class Game
{
    private static readonly SpineFolder[] _folders =
    [
        new("Content/SpineBoy"),
        new("Content/Raptor"),
        new("Content/Windmill"),
        new("Content/CelestialCircus"),
    ];
    
    private Atlas _atlas;
    private Skeleton _spineSkeleton;
    private AnimationState _animationState;
    private SpineRenderer _spineRenderer;
    
    private readonly InspectorUI _inspectorUI = new(_folders);

    private IWindow Window { get; set; } = null!;
    private IInputContext InputContext { get; set; } = null!;
    private GL Gl { get; set; } = null!;
    private ImGuiController ImGuiController { get; set; } = null!;
    private IKeyboard _keyboard;
    private IMouse _mouse;
    private float _cameraSpeed = 20;
    private bool CanMoveCamera => _mouse.Cursor.CursorMode is CursorMode.Raw;

    private readonly CameraOrthographic _cameraOrth = new()
    {
        Position = new Vector3(0.0f, 1.0f, 0.0f),
        Zoom = 0.01f
    };
    private readonly CameraPerspective _cameraPer = new()
    {
        Position = new Vector3(0.0f, 1.0f, 5.0f)
    };
    
    private ICamera CurrentCamera => _inspectorUI.UseOrthographicCamera ? _cameraOrth : _cameraPer;

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;
    private Vector3 _spineModelPosition;
    private Vector3 _spineModelRotation;
    private float _spineModelScale = 0.2f;

    private SceneGrid3d _sceneGrid3d;
    private SceneGrid2d _sceneGrid2d;
    private Quad _quad;
    private Box3d _box3d;
    
    public Game()
    {
        _inspectorUI.OnModelChanged += OnModelChanged;
        _inspectorUI.OnAnimationChanged += SetAnimation;
    }

    private void OnModelChanged(SpineFolder folder)
    {
        // load atlas
        var textureLoader = new SpineTextureLoader(Gl);
        _atlas = new Atlas(folder.AtlasPath, textureLoader);
        var json = new SkeletonJson(_atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);

        _spineSkeleton = new Skeleton(skeletonData);
        _spineSkeleton.SetSkin(skeletonData.DefaultSkin);
        _inspectorUI.SpineScale = 0.02f;
        _inspectorUI.ZSpacing = 0.01f;

        _spineRenderer = new SpineRenderer(Gl,
            _spineSkeleton, 
            _atlas.Pages[0].pma,
            textureLoader.Textures);

        // animations
        var animationStateData = new AnimationStateData(skeletonData);
        _animationState = new AnimationState(animationStateData);
        SetAnimation(_spineSkeleton.Data.Animations.Items[0].Name);
    }

    private void SetAnimation(string animationName)
    {
        var idleAnimation = _spineSkeleton.Data.FindAnimation(animationName);
        _animationState.AddAnimation(0, idleAnimation, true, 0);
    }

    public void OnLoad(IWindow window)
    {
        Window = window;

        InputContext = window.CreateInput();
        _keyboard = InputContext.Keyboards[0];
        _mouse = InputContext.Mice[0];
        
        _keyboard.KeyDown += OnKeyDown;
        _mouse.Scroll += OnMouseScroll;
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

        //Getting the opengl api for drawing to the screen.
        Gl = GL.GetApi(window);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(GLEnum.Version)}");
        Gl.ClearColor(Color.DarkSlateGray);
        
        ImGuiController = new ImGuiController(Gl, window, InputContext);

        _sceneGrid3d = new SceneGrid3d(Gl);
        _sceneGrid2d = new SceneGrid2d(Gl);
        _quad = new Quad(Gl);
        _box3d = new Box3d(Gl);
        OnModelChanged(_folders[0]);
    }

    private void SetSpineScale()
    {
        var scaleXAbs = Math.Abs(_inspectorUI.SpineScale);
        _spineSkeleton.ScaleX = _inspectorUI.IsFlipped ? -scaleXAbs : scaleXAbs;
        _spineSkeleton.ScaleY = _inspectorUI.SpineScale;
    }

    public void OnUpdate(double deltaTime)
    {
        UpdateCameraPosition(deltaTime);
        ImGuiController.Update((float)deltaTime);
        
        _inspectorUI.OnImGui(
            _spineSkeleton,
            CurrentCamera,
            ref _cameraSpeed,
            ref _spineModelPosition,
            ref _spineModelRotation,
            ref _spineModelScale,
            ref _spineRenderer.SrcFactor,
            ref _spineRenderer.DestFactor,
            deltaTime);

        
        SetSpineScale();
        
        _spineSkeleton.UpdateWorldTransform(_inspectorUI.UpdatePhysics ? Skeleton.Physics.Update : Skeleton.Physics.None);
        _animationState.Update((float)deltaTime);
        _animationState.Apply(_spineSkeleton);
    }

    public void OnRender(double deltaTime)
    {
        Gl.Enable(EnableCap.DepthTest);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_inspectorUI.UseOrthographicCamera)
            _sceneGrid2d.Draw(CurrentCamera);
        else
            _sceneGrid3d.Draw(CurrentCamera);
        
        var frameBufferSize = new Vector2(Window.FramebufferSize.X, Window.FramebufferSize.Y);
        
        if (_inspectorUI.UseOrthographicCamera)
        {
            _cameraOrth.Size = frameBufferSize;
            // _quad.Draw(CurrentCamera.ViewMatrix, CurrentCamera.ProjectionMatrix);
        }
        else
        {
            _cameraPer.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
            // _box3d.Draw(CurrentCamera.ViewMatrix, CurrentCamera.ProjectionMatrix);
        }
        
        var model =
            // scale
            Matrix4x4.CreateScale(_spineModelScale)
            // rotation
            * Matrix4x4.CreateRotationX(MainframeEngine.Math.DegreesToRadiansF(_spineModelRotation.X))
            * Matrix4x4.CreateRotationY(MainframeEngine.Math.DegreesToRadiansF(_spineModelRotation.Y))
            * Matrix4x4.CreateRotationZ(MainframeEngine.Math.DegreesToRadiansF(_spineModelRotation.Z))
            // translation
            * Matrix4x4.CreateTranslation(_spineModelPosition);
        
        // bind and render
        _spineRenderer.Draw(
            _inspectorUI.ZSpacing,
            model,
            CurrentCamera.ViewMatrix,
            CurrentCamera.ProjectionMatrix);
        
        ImGuiController.Render();
    }

    public void OnFramebufferResize(Vector2D<int> newSize)
    {
        Gl.Viewport(newSize);
    }

    public void OnClose()
    {
        InputContext.Dispose();
        Gl.Dispose();
    }
    
    private void UpdateCameraPosition(double deltaTime)
    {
        if (!CanMoveCamera)
            return;
        
        var camera = CurrentCamera;

        var baseSpeed = _keyboard.IsKeyPressed(Key.ShiftLeft) ? _cameraSpeed * 2 : _cameraSpeed;
        var moveSpeed = baseSpeed * (float)deltaTime;

        var isPerspectiveCamera = camera is CameraPerspective;

        if (isPerspectiveCamera)
        {
            // forward
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += moveSpeed * camera.Forward;
            
            // back
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= moveSpeed * camera.Forward;
            
            // left
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        
            // right
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position +=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
            
            // up
            if (_keyboard.IsKeyPressed(Key.Q))
                camera.Position -= camera.Up * moveSpeed;

            // down
            if (_keyboard.IsKeyPressed(Key.E))
                camera.Position += camera.Up * moveSpeed;
        }
        else
        {
            // up
            if (_keyboard.IsKeyPressed(Key.W))
                camera.Position += camera.Up * moveSpeed;

            // down
            if (_keyboard.IsKeyPressed(Key.S))
                camera.Position -= camera.Up * moveSpeed;
            
            // left
            if (_keyboard.IsKeyPressed(Key.A))
                camera.Position -=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        
            // right
            if (_keyboard.IsKeyPressed(Key.D))
                camera.Position +=
                    Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up)) * moveSpeed;
        }
    }

    #region Inputs
    
    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key == Key.Escape)
            Window.Close();
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!CanMoveCamera)
        {
            // reset last position so camera doesn't make massive jump when move mouse again
            LastMousePosition = default;
            return;
        }
        if (LastMousePosition == default)
        {
            LastMousePosition = position;
        }
        else
        {
            if (CurrentCamera is CameraPerspective camera)
            {
                const float lookSensitivity = 0.1f;
                var xOffset = (position.X - LastMousePosition.X) * lookSensitivity;
                var yOffset = (position.Y - LastMousePosition.Y) * lookSensitivity;
                LastMousePosition = position;

                camera.ModifyDirection(xOffset, yOffset);
            }
            else
            {
                const float lookSensitivity = 0.01f;
                var xOffset = (position.X - LastMousePosition.X) * lookSensitivity;
                var yOffset = (position.Y - LastMousePosition.Y) * lookSensitivity;
                LastMousePosition = position;
                CurrentCamera.Position += new Vector3(-xOffset, yOffset, 0);
            }
        }
    }
    
    private void OnMouseScroll(IMouse mouse, ScrollWheel delta)
    {
        if (ImGui.GetIO().WantCaptureMouse)
            return;
        
        if (CurrentCamera is not CameraOrthographic camera)
            return;
        
        camera.ModifyZoom(-delta.Y * 0.05f);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Raw;
    }
    
    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Normal;
    }

    #endregion
}

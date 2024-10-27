using System.Drawing;
using System.Numerics;
using Mainframe.Silk;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using SilkSpine.UI;
using Spine;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

internal class Game
{
    private Atlas _atlas;
    private Skeleton _spineSkeleton;
    private AnimationState _animationState;

    private SpineRenderer spineRenderer;
    private Shader _shader;

    private static readonly SpineFolder[] _folders =
    [
        new()
        {
            Name  = "Spine Boy",
            AtlasPath = "Content/SpineBoy/spineboy-pro.atlas",
            JsonPath = "Content/SpineBoy/spineboy-pro.json",
            TexturePath = "Content/SpineBoy/spineboy-pro.png",
        },
        new()
        {
            Name  = "Raptor",
            AtlasPath = "Content/Raptor/raptor-pro.atlas",
            JsonPath = "Content/Raptor/raptor-pro.json",
            TexturePath = "Content/Raptor/raptor-pro.png",
        },
        new()
        {
            Name = "Windmill",
            AtlasPath = "Content/Windmill/windmill-ess.atlas",
            JsonPath = "Content/Windmill/windmill-ess.json",
            TexturePath = "Content/Windmill/windmill-ess.png",
        },
        new()
        {
            Name = "CelestialCircus",
            AtlasPath = "Content/CelestialCircus/celestial-circus-pro.atlas",
            JsonPath = "Content/CelestialCircus/celestial-circus-pro.json",
            TexturePath = "Content/CelestialCircus/celestial-circus-pro.png",
        }
    ];
    private readonly InspectorUI _inspectorUI = new(_folders);

    private IWindow Window { get; set; } = null!;
    private IInputContext InputContext { get; set; } = null!;
    private GL Gl { get; set; } = null!;
    private ImGuiController ImGuiController { get; set; } = null!;
    private IKeyboard _keyboard;

    private readonly CameraOrthographic _cameraOrth = new()
    {
        Position = new Vector3(0.0f, 35.0f, 0.0f),
        Zoom = 0.1f
    };
    private readonly CameraPerspective _cameraPer = new()
    {
        Position = new Vector3(0.0f, 50.0f, 200.0f)
    };
    
    private ICamera CurrentCamera => _inspectorUI.UseOrthographicCamera ? _cameraOrth : _cameraPer;

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;
    private Vector3 _spineModelPosition;
    private Vector3 _spineModelRotation;
    private float _spineModelScale = 1f;

    private Grid _grid;
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
        var textureLoader = new SpineSilkTextureLoader(Gl);
        _atlas = new Atlas(folder.AtlasPath, textureLoader);
        var json = new SkeletonJson(_atlas);
        var skeletonData = json.ReadSkeletonData(folder.JsonPath);

        _spineSkeleton = new Skeleton(skeletonData);
        _spineSkeleton.SetSkin(skeletonData.DefaultSkin);
        _inspectorUI.SpineScale = 0.1f;

        spineRenderer = new SpineRenderer(Gl, _spineSkeleton, _atlas.Pages[0].pma, textureLoader.Textures[0]);

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
        for (int i = 0; i < InputContext.Keyboards.Count; i++)
        {
            InputContext.Keyboards[i].KeyDown += OnKeyDown;
            InputContext.Mice[i].MouseMove += OnMouseMove;
        }

        //Getting the opengl api for drawing to the screen.
        Gl = GL.GetApi(window);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(GLEnum.Version)}");
        Gl.ClearColor(Color.DarkSlateGray);
        
        ImGuiController = new ImGuiController(Gl, window, InputContext);
        _shader = new Shader(Gl, 
            "Content/Shaders/shader.vert",
            "Content/Shaders/shader.frag");

        _grid = new Grid(Gl);
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
            ref _spineModelPosition,
            ref _spineModelRotation,
            ref _spineModelScale,
            ref spineRenderer.sFactor,
            ref spineRenderer.dFactor,
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
        
        var frameBufferSize = new Vector2(Window.FramebufferSize.X, Window.FramebufferSize.Y);
        
        var model =
            // scale
            Matrix4x4.CreateScale(_spineModelScale)
            // rotation
            * Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.X))
            * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.Y))
            * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.Z))
            // translation
            * Matrix4x4.CreateTranslation(_spineModelPosition);

        _grid.Draw(CurrentCamera.ViewMatrix, CurrentCamera.ProjectionMatrix);
        // _quad.Draw(CurrentCamera.ViewMatrix, CurrentCamera.ProjectionMatrix);
        // _box3d.Draw(CurrentCamera.ViewMatrix, CurrentCamera.ProjectionMatrix);
        
        // bind and render
        _shader.Use();
        _shader.SetUniform("uTexture0", 0);
        _shader.SetUniform("uModel", model);
        
        if (_inspectorUI.UseOrthographicCamera)
        {
            _cameraOrth.Size = frameBufferSize;
        }
        else
        {
            _cameraPer.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
        }
        
        _shader.SetUniform("uView", CurrentCamera.ViewMatrix);
        _shader.SetUniform("uProjection", CurrentCamera.ProjectionMatrix);
            
        var drawCalls = spineRenderer.Draw(_inspectorUI.SingleDrawCall, _inspectorUI.ZSpacing);
        _inspectorUI.DrawCallCount = drawCalls;

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
        if (CurrentCamera is not CameraPerspective camera)
            return;

        var baseSpeed = _keyboard.IsKeyPressed(Key.ShiftLeft) ? 100 : 50;
        var moveSpeed = baseSpeed * (float)deltaTime;

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

    #region Inputs
    
    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key is Key.AltLeft)
        {
            var cursor = InputContext.Mice[0].Cursor;
            cursor.CursorMode = cursor.CursorMode is CursorMode.Raw ? CursorMode.Normal : CursorMode.Raw;
        }
        
        if (key == Key.Escape)
            Window.Close();
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (InputContext.Mice[0].Cursor.CursorMode is not CursorMode.Raw)
            return;
        
        if (CurrentCamera is not CameraPerspective camera)
            return;
        
        const float lookSensitivity = 0.1f;
        if (LastMousePosition == default)
        {
            LastMousePosition = position;
        }
        else
        {
            var xOffset = (position.X - LastMousePosition.X) * lookSensitivity;
            var yOffset = (position.Y - LastMousePosition.Y) * lookSensitivity;
            LastMousePosition = position;

            camera.ModifyDirection(xOffset, yOffset);
        }
    }

    #endregion


}

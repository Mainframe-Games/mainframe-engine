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

    private readonly CameraOrthographic _cameraOrth = new();
    private readonly CameraPerspective _cameraPer = new();

    //Used to track change in mouse movement to allow for moving of the Camera
    private static Vector2 LastMousePosition;
    private Vector3 _spineModelPosition;
    private Vector3 _spineModelRotation;
    private float _spineModelScale = 1f;

    private Grid _grid;
    private Quad _quad;
    
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
        _inspectorUI.SpineScale = 0.5f;

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
        for (int i = 0; i < InputContext.Keyboards.Count; i++)
        {
            InputContext.Keyboards[i].KeyDown += OnKeyDown;
            InputContext.Mice[i].Scroll += OnScroll;
        }

        //Getting the opengl api for drawing to the screen.
        Gl = GL.GetApi(window);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(GLEnum.Version)}");
        Gl.ClearColor(Color.DarkSlateGray);
        
        ImGuiController = new ImGuiController(Gl, window, InputContext);
        _shader = new Shader(Gl, "Content/Shaders/shader.vert", "Content/Shaders/shader.frag");

        _grid = new Grid(Gl, 10);
        _quad = new Quad(Gl);
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
        ImGuiController.Update((float)deltaTime);
        OnImGui(deltaTime);
        
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

        _grid.Draw();
        _quad.Draw(_cameraOrth.ViewMatrix, _cameraOrth.ProjectionMatrix);
        
        // bind and render
        _shader.Use();
        _shader.SetUniform("uTexture0", 0);
        _shader.SetUniform("uModel", model);
        
        if (_inspectorUI.UseOrthographicCamera)
        {
            _cameraOrth.Size = frameBufferSize;
            _cameraOrth.Position = new Vector3(0.0f, 150.0f, 10.0f);
            
            _shader.SetUniform("uView", _cameraOrth.ViewMatrix);
            _shader.SetUniform("uProjection", _cameraOrth.ProjectionMatrix);
        }
        else
        {
            _cameraPer.Position = new Vector3(0.0f, 50.0f, 200.0f);
            _cameraPer.AspectRatio = frameBufferSize.X / frameBufferSize.Y;
            _shader.SetUniform("uView", _cameraPer.ViewMatrix);
            _shader.SetUniform("uProjection", _cameraPer.ProjectionMatrix);
        }
            
        var drawCalls = spineRenderer.Draw(_inspectorUI.SingleDrawCall, _inspectorUI.ZSpacing);
        _inspectorUI.DrawCallCount = drawCalls;

        ImGuiController.Render();
    }

    private void OnImGui(double deltaTime)
    {
        _inspectorUI.OnImGui(_spineSkeleton, 
            ref _spineModelPosition,
            ref _spineModelRotation,
            ref _spineModelScale,
            ref spineRenderer.sFactor,
            ref spineRenderer.dFactor,
            deltaTime);
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

    #region Inputs
    
    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key == Key.Escape)
            Window.Close();
    }

    private void OnScroll(IMouse mouse, ScrollWheel delta)
    {
        // _spineModelScale = Math.Clamp(_spineModelScale + delta.Y * 0.1f, 0.1f, 10);
        // _cameraOrth.ModifyZoom(delta.Y);
        // _cameraPer.ModifyZoom(-delta.Y * 2f);
    }

    #endregion


}

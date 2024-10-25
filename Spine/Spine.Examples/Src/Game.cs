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
using Rectangle = System.Drawing.Rectangle;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine;

internal class Game
{
    private Atlas _atlas;
    private Skeleton _spineSkeleton;
    private AnimationState _animationState;

    private SpineModel _spineModel;
    private SpineMesh _spineMesh;
    private Shader _shader;

    private static readonly SpineFolder[] _folders =
    [
        new SpineFolder
        {
            Name  = "Spine Boy",
            AtlasPath = "Content/SpineBoy/spineboy-pro.atlas",
            JsonPath = "Content/SpineBoy/spineboy-pro.json",
            TexturePath = "Content/SpineBoy/spineboy-pro.png",
        },
        // new SpineFolder
        // {
        //     Name = "Percy",
        //     AtlasPath = "Content/Percy/Percy.atlas",
        //     JsonPath = "Content/Percy/Percy.json",
        //     TexturePath = "Content/Percy/Percy.png",
        // }
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
    private Vector3 _spineModelScale = Vector3.One;
    
    private Box3d _box3d = null!;

    public Game()
    {
        _inspectorUI.OnModelChanged += OnModelChanged;
        _inspectorUI.OnAnimationChanged += SetAnimation;
        _inspectorUI.OnFlipped += SetFlip;
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
        _spineSkeleton.ScaleX = 0.5f;
        _spineSkeleton.ScaleY = 0.5f;

        _spineModel = new SpineModel(Gl, _spineSkeleton, true);

        // animations
        var animationStateData = new AnimationStateData(skeletonData);
        _animationState = new AnimationState(animationStateData);
        SetAnimation("idle");
        _animationState.Update(0);
        _animationState.Apply(_spineSkeleton);
        _spineSkeleton.UpdateWorldTransform(Skeleton.Physics.None);
        _spineMesh = new SpineMesh(Gl, textureLoader.Textures);
    }

    private void SetFlip(bool isFlipped)
    {
        var scaleXAbs = Math.Abs(_spineSkeleton.ScaleX);
        _spineSkeleton.ScaleX = isFlipped ? -scaleXAbs : scaleXAbs;
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
            InputContext.Keyboards[i].KeyDown += OnKeyDown;

        //Getting the opengl api for drawing to the screen.
        Gl = GL.GetApi(window);
        Console.WriteLine($"OpenGL: {Gl.GetStringS(GLEnum.Version)}");
        Gl.ClearColor(Color.DarkSlateGray);
        // Gl.DebugMessageCallback(MessageCallback, );
        
        ImGuiController = new ImGuiController(Gl, window, InputContext);
        _shader = new Shader(Gl, "Content/Shaders/shader.vert", "Content/Shaders/shader.frag");
        
        _box3d = new Box3d(Gl);
        OnModelChanged(_folders[0]);
    }

    private void MessageCallback(GLEnum source, GLEnum type, int id, GLEnum severity, int length, IntPtr message, IntPtr userparam)
    {
        throw new NotImplementedException();
    }

    public void OnUpdate(double deltaTime)
    {
        ImGuiController.Update((float)deltaTime);
        OnImGui(deltaTime);
        
        _spineSkeleton.UpdateWorldTransform(Skeleton.Physics.None);
        _animationState.Update((float)deltaTime);
        _animationState.Apply(_spineSkeleton);
        
        _spineMesh.Update(_spineModel.BuildVertices(), _spineModel.BuildIndices());
    }

    public void OnRender(double deltaTime)
    {
        Gl.Enable(EnableCap.DepthTest);
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var viewPort = new Rectangle(_inspectorUI.Width, 0, Window.Size.X - _inspectorUI.Width, Window.Size.Y);
        if (_inspectorUI.ProjectSettingsUI.UseViewport)
            Gl.Viewport(viewPort);
        
        if (_inspectorUI.ProjectSettingsUI.Scissor)
            Gl.Scissor(viewPort.X, viewPort.Y, (uint)viewPort.Width, (uint)viewPort.Height);
        
        var frameBufferSize = new Vector2(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        _cameraOrth.Size = frameBufferSize;
        _cameraOrth.Zoom = 0.6f;
        _cameraOrth.Position = new Vector3(0.0f, 150.0f, 10.0f);
        
        _cameraPer.Position = new Vector3(0.0f, 150.0f, 500.0f);
        _cameraPer.AspectRatio = frameBufferSize.X / frameBufferSize.Y;

        var rot = Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.X))
            * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.Y))
            * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(_spineModelRotation.Z));
        var model =
            Matrix4x4.CreateScale(_spineModelScale)
            * rot
            * Matrix4x4.CreateTranslation(_spineModelPosition);

        DrawBox();

        // bind and render
        _spineMesh.Bind();
        _shader.Use();
        _spineMesh.Textures[0].Bind();
        _shader.SetUniform("uTexture0", 0);
        _shader.SetUniform("uModel", model);
        
        _spineModel.Draw();
        
        if (_inspectorUI.UseOrthographicCamera)
        {
            _shader.SetUniform("uView", _cameraOrth.ViewMatrix);
            _shader.SetUniform("uProjection", _cameraOrth.ProjectionMatrix);
        }
        else
        {
            _shader.SetUniform("uView", _cameraPer.ViewMatrix);
            _shader.SetUniform("uProjection", _cameraPer.ProjectionMatrix);
        }

        Gl.Enable(EnableCap.Blend);
        {
            Gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            Gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_spineMesh.Vertices.Length);
        }
        Gl.Disable(EnableCap.Blend);
        
        // reset viewport
        Gl.Scissor(0, 0, 0, 0);
        Gl.Viewport(new Rectangle(0, 0, Window.Size.X, Window.Size.Y));
        
        ImGuiController.Render();
    }

    private void DrawBox()
    {
        // draw box
        if (_inspectorUI.UseOrthographicCamera)
            _box3d.Render(_cameraOrth.ViewMatrix, _cameraOrth.ProjectionMatrix);
        else
            _box3d.Render(_cameraPer.ViewMatrix, _cameraPer.ProjectionMatrix);
    }

    private void OnImGui(double deltaTime)
    {
        _inspectorUI.OnImGui(_spineSkeleton, 
            ref _spineModelPosition,
            ref _spineModelRotation,
            ref _spineModelScale,
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

    private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key == Key.Escape)
            Window.Close();
    }
}

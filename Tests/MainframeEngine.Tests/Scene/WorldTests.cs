using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Scene;

/// <summary>Node front-ends registering with their world and the render server (no GPU: a non-Vulkan renderer).</summary>
public sealed class WorldTests : IDisposable
{
    private readonly SceneTree _tree = new();

    public void Dispose() => _tree.Shutdown();

    [Fact]
    public void VisualsRegisterByPriorityThenEntryOrder()
    {
        var world = _tree.Root.World3D;
        var floor = new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh() };
        var box = new MeshInstance3D { Name = "Box", Mesh = new BoxMesh() };
        var grid = new Grid3D();
        _tree.Root.AddChild(floor);
        _tree.Root.AddChild(box);
        _tree.Root.AddChild(grid);

        Assert.Equal<VisualInstance3D>([grid, floor, box], world.Visuals);
        Assert.False(grid.CastShadows);

        box.RenderPriority = -200;
        Assert.Equal<VisualInstance3D>([box, grid, floor], world.Visuals);

        _tree.Root.RemoveChild(grid);
        Assert.Equal<VisualInstance3D>([box, floor], world.Visuals);
        grid.Free();
    }

    [Fact]
    public void LightNodesDriveTheirLightsFromTheTransform()
    {
        var lights = _tree.Root.World3D.Lights;
        var rig = new Node3D { Position = new Vector3(0, 10, 0) };
        var sun = new DirectionalLight3D { Color = new Vector3(1, 0, 0), Energy = 2 };
        var lamp = new OmniLight3D { Position = new Vector3(1, 0, 0), Range = 3 };
        var spot = new SpotLight3D { InnerConeAngle = 5, OuterConeAngle = 9 };
        rig.AddChild(sun);
        rig.AddChild(lamp);
        rig.AddChild(spot);
        _tree.Root.AddChild(rig);

        Assert.Equal(3, lights.Count);
        var dir = (DirectionalLight)sun.Light;
        Assert.Equal(new Vector3(1, 0, 0), dir.Color);
        Assert.Equal(2, dir.Intensity);
        Assert.Equal(new Vector3(0, 0, -1), dir.Direction);
        Assert.Equal(new Vector3(1, 10, 0), ((PointLight)lamp.Light).Position);
        Assert.Equal(3, ((PointLight)lamp.Light).Range);

        // Moving the parent is synced to the light after process, once.
        rig.RotationDegrees = new Vector3(-90, 0, 0); // -Z now points down
        _tree.Tick(default);
        Assert.True(Vector3.Distance(new Vector3(0, -1, 0), dir.Direction) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(1, 10, 0), ((PointLight)lamp.Light).Position) < 1e-5f);

        _tree.Root.RemoveChild(rig);
        Assert.Equal(0, lights.Count);
        rig.Free();
    }

    [Fact]
    public void TheFirstOrCurrentCameraIsActive()
    {
        var viewport = _tree.Root;
        var first = new Camera3D { Name = "First" };
        var second = new Camera3D { Name = "Second" };
        _tree.Root.AddChild(first);
        _tree.Root.AddChild(second);
        Assert.Same(first, viewport.ActiveCamera3D);
        Assert.True(first.IsActive);

        second.MakeCurrent();
        Assert.Same(second, viewport.ActiveCamera3D);

        second.Current = false;
        Assert.Same(first, viewport.ActiveCamera3D);

        _tree.Root.RemoveChild(first);
        Assert.Same(second, viewport.ActiveCamera3D);
        first.Free();

        var flat = new Camera2D { Zoom = new Vector2(0.5f) };
        _tree.Root.AddChild(flat);
        Assert.Same(flat, viewport.ActiveCamera2D);
    }

    [Fact]
    public void CameraSyncCopiesTheGlobalTransform()
    {
        var camera = new Camera3D { Position = new Vector3(0, 5, 10), Fov = 60, Near = 0.5f, Far = 50 };
        camera.LookAt(Vector3.Zero);
        _tree.Root.AddChild(camera);

        var math = (PerspectiveCamera)camera.SyncRenderCamera(2f);
        Assert.Equal(new Vector3(0, 5, 10), math.Position);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(0, -5, -10)), math.Forward) < 1e-5f);
        Assert.Equal(2f, math.AspectRatio);
        Assert.Equal(60, math.FieldOfView);
        Assert.Equal(0.5f, math.Near);
        Assert.Equal(50, math.Far);

        // Same view as the legacy camera looking at the same point.
        var legacy = new PerspectiveCamera { Position = new Vector3(0, 5, 10), AspectRatio = 2, FieldOfView = 60, Near = 0.5f, Far = 50 };
        legacy.LookAt(Vector3.Zero);
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                Assert.Equal(legacy.ViewMatrix[r, c], math.ViewMatrix[r, c], 4);

        var extent = new Extent2D(1920, 1080);
        Assert.Same(math, RenderServer.GetRenderCamera(_tree.Root, extent));
        Assert.Equal(1920f / 1080f, math.AspectRatio, 5);
    }

    [Fact]
    public void HiddenLightsAreNotInTheWorldsLights()
    {
        var lights = _tree.Root.World3D.Lights;
        var sun = new DirectionalLight3D { Visible = false };
        var lamp = new OmniLight3D();
        _tree.Root.AddChild(sun);
        _tree.Root.AddChild(lamp);
        Assert.Empty(lights.DirectionalLights);
        Assert.Single(lights.PointLights);

        sun.Visible = true;
        Assert.Single(lights.DirectionalLights);
        sun.Visible = true;
        Assert.Single(lights.DirectionalLights);

        lamp.Visible = false;
        Assert.Empty(lights.PointLights);
        lamp.Visible = true;
        Assert.Single(lights.PointLights);

        _tree.Root.RemoveChild(lamp);
        Assert.Empty(lights.PointLights);
        lamp.Free();
    }

    [Fact]
    public void HidingAnAncestorSwitchesItsLightsOff()
    {
        var lights = _tree.Root.World3D.Lights;
        var group = new Node3D();
        var middle = new Node3D();
        var spot = new SpotLight3D();
        group.AddChild(middle);
        middle.AddChild(spot);
        _tree.Root.AddChild(group);
        Assert.Single(lights.SpotLights);

        group.Visible = false;
        Assert.Empty(lights.SpotLights);

        spot.Visible = false;
        group.Visible = true;
        Assert.Empty(lights.SpotLights); // still hidden itself

        spot.Visible = true;
        Assert.Single(lights.SpotLights);
    }

    [Fact]
    public void WorldEnvironmentSetsTheAmbientLight()
    {
        var lights = _tree.Root.World3D.Lights;
        var env = new WorldEnvironment { AmbientColor = new Vector3(0.5f, 0.4f, 0.3f), Sky = new Sky() };
        _tree.Root.AddChild(env);
        Assert.Same(env, _tree.Root.World3D.Environment);
        Assert.Equal(new Vector3(0.5f, 0.4f, 0.3f), lights.AmbientColor);

        env.AmbientColor = Vector3.One;
        Assert.Equal(Vector3.One, lights.AmbientColor);
    }

    [Fact]
    public void RenderServerTracksVisualsAndReleasesThemAtShutdown()
    {
        using var servers = new ServerRegistry();
        var render = new RenderServer(new HeadlessRenderer());
        servers.Register(render);
        Assert.Same(render, servers.Render);
        Assert.Same(render, servers.Get<RenderServer>());
        Assert.Throws<InvalidOperationException>(() => servers.Register(new RenderServer(new HeadlessRenderer())));

        var tree = new SceneTree(servers);
        var box = new MeshInstance3D { Mesh = new BoxMesh() };
        var orphan = new Sprite3D();
        tree.Root.AddChild(box);
        tree.Root.AddChild(orphan);
        Assert.True(box.HasRenderResources);
        Assert.Equal(2, render.ResourceOwnerCount);
        Assert.Null(render.Shadows); // no Vulkan: no shadow system

        // Removed but never freed: the server still releases it at shutdown.
        tree.Root.RemoveChild(orphan);
        tree.Shutdown();
        Assert.Equal(1, render.ResourceOwnerCount);
        servers.Dispose();
        Assert.Equal(0, render.ResourceOwnerCount);
        Assert.False(orphan.HasRenderResources);
        Assert.Null(servers.Render);
    }

    [Fact]
    public void ServerRegistryTicksFrameAndFixedStepServers()
    {
        using var servers = new ServerRegistry();
        var probe = new ProbeServer();
        servers.Register(probe);
        var tree = new SceneTree(servers);

        tree.Tick(new GameTime { DeltaTime = 2f / 60f });
        Assert.Equal(1, probe.Frames);
        Assert.Equal(2, probe.Steps);

        tree.Paused = true; // physics servers freeze with the tree
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(2, probe.Frames);
        Assert.Equal(2, probe.Steps);

        Assert.True(servers.Unregister(probe));
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal(2, probe.Frames);
        tree.Shutdown();
        Assert.False(probe.Disposed); // unregistered servers are not disposed by the registry
    }

    private sealed class ProbeServer : IFrameServer, IFixedStepServer
    {
        public int Frames { get; private set; }
        public int Steps { get; private set; }
        public bool Disposed { get; private set; }

        public void Process(in GameTime gameTime) => Frames++;
        public void FixedStep(float delta) => Steps++;
        public void Dispose() => Disposed = true;
    }

    /// <summary>An <see cref="IRenderer"/> that is not an <see cref="IVulkanContext"/>, so nodes create no GPU resources.</summary>
    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}

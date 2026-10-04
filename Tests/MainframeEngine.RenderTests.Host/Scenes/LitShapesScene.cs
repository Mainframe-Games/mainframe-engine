using System.Drawing;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Procedural sky, grid, a floor quad and two boxes lit by one shadow-casting directional light.
/// The left box spins at a fixed rate, so a given frame always shows the same pose.
/// </summary>
public class LitShapesScene(HostOptions host) : RenderTestGame(host)
{
    private readonly Camera3D _camera = new();
    private readonly LightEnvironment _lights = new();
    private readonly List<Node> _nodes = [];
    private SkyEnvironment _sky = null!;
    private SceneGrid3d _grid = null!;
    private ShadowSystem? _shadows;
    private Box3d _spinningBox = null!;

    protected IReadOnlyList<Node> Nodes => _nodes;
    protected Camera3D Camera => _camera;
    protected LightEnvironment Lights => _lights;
    protected ShadowSystem? Shadows => _shadows;

    /// <summary>False runs the scene without a <see cref="ShadowSystem"/> (lit pipelines bind the fallback set 2).</summary>
    protected virtual bool UseShadowSystem => true;

    protected override void LoadScene()
    {
        _camera.Position = new Vector3(0, 3.5f, 7f);
        _camera.LookAt(new Vector3(0, 0.5f, 0));

        _sky = new SkyProcedural(Renderer);
        _grid = new SceneGrid3d(Renderer);
        _shadows = UseShadowSystem ? new ShadowSystem(Vulkan) : null;
        Node.Initialize(Renderer, _shadows);

        _nodes.Add(new Quad { Rotation = new Vector3(90, 0, 0), Scale = new Vector3(10, 10, 1), Color = Color.White });
        _spinningBox = new Box3d { Position = new Vector3(-1.5f, 1f, 0), Color = Color.FromArgb(255, 230, 120, 80) };
        _nodes.Add(_spinningBox);
        _nodes.Add(new Box3d { Position = new Vector3(1.5f, 0.5f, 1f), Color = Color.FromArgb(255, 90, 160, 230) });

        AddLights(_lights);
        AddNodes(_nodes);
    }

    /// <summary>One shadow-casting directional light; derived scenes may add or replace lights.</summary>
    protected virtual void AddLights(LightEnvironment lights)
    {
        lights.AddLight(new DirectionalLight
        {
            Position = new Vector3(0, 5, 0),
            Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)),
            Color = new Vector3(1f, 0.95f, 0.8f),
            Intensity = 0.9f,
        });
    }

    /// <summary>Lets derived scenes add nodes that are updated, shadowed and drawn with the rest.</summary>
    protected virtual void AddNodes(List<Node> nodes)
    {
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _spinningBox.Rotation += new Vector3(30f, 45f, 0) * gameTime.DeltaTime;
        foreach (var node in _nodes)
            node.OnUpdate(gameTime);
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
        if (_shadows is null)
            return;

        _shadows.RenderShadows(_lights, _nodes,
            static (nodes, cb, _, _, _) =>
            {
                foreach (var node in nodes)
                    node.DrawShadow2D(cb);
            },
            static (nodes, cb, _, _, _, lightPos, lightRange) =>
            {
                foreach (var node in nodes)
                    node.DrawShadowPoint(cb, lightPos, lightRange);
            });
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        _camera.AspectRatio = AspectRatio;
        _sky.Draw(_camera);
        _grid.Draw(_camera);
        foreach (var node in _nodes)
            node.Draw(_camera, _lights);
    }

    protected override void OnImGui(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
        foreach (var node in _nodes)
            node.Dispose();
        _shadows?.Dispose();
        _grid.Dispose();
        _sky.Dispose();
    }
}

using System.Drawing;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Procedural sky, grid, a floor quad and two boxes lit by one shadow-casting directional light, built as a
/// node tree that the engine's scene tree and render server process and draw. The left box spins at a fixed
/// rate, so a given frame always shows the same pose.
/// </summary>
public class LitShapesScene(HostOptions host) : RenderTestGame(host)
{
    private MeshInstance3D _spinningBox = null!;

    protected Camera3D Camera { get; private set; } = null!;

    protected WorldEnvironment Environment { get; private set; } = null!;

    protected LightEnvironment Lights => Root.World3D.Lights;

    /// <summary>The render server's shadow system (null when <see cref="UseShadowSystem"/> is false).</summary>
    protected ShadowSystem? Shadows => Servers.Render?.ExistingShadows;

    /// <summary>False runs the scene without a <see cref="ShadowSystem"/> (lit pipelines bind the fallback set 2).</summary>
    protected virtual bool UseShadowSystem => true;

    protected override void LoadScene()
    {
        Servers.Render!.ShadowsEnabled = UseShadowSystem;

        var scene = new Node3D { Name = "LitShapes" };

        Camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 3.5f, 7f) };
        Camera.LookAt(new Vector3(0, 0.5f, 0));
        scene.AddChild(Camera);

        Environment = new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } };
        scene.AddChild(Environment);
        scene.AddChild(new Grid3D { Name = "Grid" });

        // One box mesh shared by both boxes (one GPU mesh); each box has its own colour material.
        var box = new BoxMesh();
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(10, 10) } });
        _spinningBox = new MeshInstance3D
        {
            Name = "SpinningBox",
            Position = new Vector3(-1.5f, 1f, 0),
            Mesh = box,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 230, 120, 80) },
        };
        scene.AddChild(_spinningBox);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(1.5f, 0.5f, 1f),
            Mesh = box,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 160, 230) },
        });

        AddLights(scene);
        AddNodes(scene);
        Tree.ChangeScene(scene);
    }

    /// <summary>One shadow-casting directional light; derived scenes may add or replace lights.</summary>
    protected virtual void AddLights(Node scene)
    {
        scene.AddChild(Aim(new DirectionalLight3D
        {
            Name = "Sun",
            Position = new Vector3(0, 5, 0),
            Color = new Vector3(1f, 0.95f, 0.8f),
            Energy = 0.9f,
        }, new Vector3(-0.4f, -1f, -0.6f)));
    }

    /// <summary>Lets derived scenes add nodes that are processed, shadowed and drawn with the rest.</summary>
    protected virtual void AddNodes(Node scene)
    {
    }

    /// <summary>Points a light's <c>-Z</c> axis along <paramref name="direction"/>.</summary>
    protected static T Aim<T>(T light, Vector3 direction) where T : Node3D
    {
        light.LookAt(light.Position + Vector3.Normalize(direction));
        return light;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _spinningBox.RotationDegrees += new Vector3(30f, 45f, 0) * gameTime.DeltaTime;
    }

    protected override void DisposeScene()
    {
        // The engine frees the scene tree (and the nodes' GPU objects) on close.
    }
}

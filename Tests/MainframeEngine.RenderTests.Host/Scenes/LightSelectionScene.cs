using System.Globalization;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// ADR 0217: 24 point lights, more than the lights UBO holds (16), in three groups of eight along a floor — red at
/// x = −24, green at 0, blue at +24, the first light of each casting shadows. The camera looks at the red group until
/// frame <see cref="TeleportFrame"/>, then at the blue one (added last: before nearest-light selection it never lit).
/// Self-checks on the capture frames that the two groups nearest the camera fill the UBO and that the shadow cubes
/// belong to them.
/// </summary>
public sealed class LightSelectionScene(HostOptions host) : ShadowSceneBase(host)
{
    public const uint RedFrame = 10;
    public const uint BlueFrame = 24;
    private const uint TeleportFrame = 14;

    private static readonly float[] GroupX = [-24f, 0f, 24f];
    private static readonly Vector3[] GroupColor = [new(1f, 0.35f, 0.25f), new(0.35f, 1f, 0.4f), new(0.3f, 0.5f, 1f)];

    private readonly List<PointLight>[] _groups = [[], [], []];
    private int _checks;

    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        LookAtGroup(0);
        Environment.AmbientColor = new Vector3(0.06f, 0.06f, 0.07f);
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(80, 16) }, MaterialOverride = Plain(200, 200, 200) });

        var box = new BoxMesh();
        for (var g = 0; g < GroupX.Length; g++)
        {
            scene.AddChild(new MeshInstance3D { Name = $"Box{g}", Position = new Vector3(GroupX[g], 0.5f, 0), Mesh = box, MaterialOverride = Plain(220, 220, 220) });
            for (var n = 0; n < 8; n++)
            {
                var light = new OmniLight3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Lamp{g}_{n}"),
                    Position = new Vector3(GroupX[g] - 4.5f + n % 4 * 3f, 0.8f, n < 4 ? -2f : 2f),
                    Color = GroupColor[g],
                    Energy = 2f,
                    Range = 3.5f,
                    CastsShadows = n == 0,
                };
                scene.AddChild(light);
                _groups[g].Add((PointLight)light.Light);
            }
        }
    }

    private void LookAtGroup(int group)
    {
        Camera.Position = new Vector3(GroupX[group], 6f, 9f);
        Camera.LookAt(new Vector3(GroupX[group], 0f, 0f));
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount == TeleportFrame)
            LookAtGroup(2);
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        base.OnRenderMainPass(gameTime);
        if (gameTime.FrameCount == RedFrame)
            Check(near: 0, dropped: 2);
        else if (gameTime.FrameCount == BlueFrame)
            Check(near: 2, dropped: 0);
    }

    // The near group and the middle one are the 16 lights in the UBO; the far group's are not, nor are its shadows.
    private void Check(int near, int dropped)
    {
        _checks++;
        var lights = Root.World3D.Lights.PointLights;
        var selected = lights.Take(LightEnvironment.MaxPoint).ToHashSet();
        foreach (var group in new[] { near, 1 })
            foreach (var light in _groups[group])
                if (!selected.Contains(light))
                    Fail(string.Create(CultureInfo.InvariantCulture, $"group {group} light at {light.Position} is not in the lights UBO"));

        if (Shadows is not { } shadows)
        {
            Fail("no shadow system");
            return;
        }

        var faces = 0;
        foreach (var pass in shadows.Passes)
        {
            if (pass.Kind != ShadowPassKind.CubeFace)
                continue;
            faces++;
            if (_groups[dropped].Contains(lights[pass.LightIndex]) || pass.LightIndex >= LightEnvironment.MaxPoint)
                Fail(string.Create(CultureInfo.InvariantCulture, $"cube face pass {pass.Index} shadows light {pass.LightIndex}, which is not selected"));
        }

        if (faces != 12)
            Fail(string.Create(CultureInfo.InvariantCulture, $"expected 12 cube faces (the two selected casters), got {faces}"));
    }

    protected override void DisposeScene()
    {
        if (_checks != 2)
            Fail($"the selection self-check ran {_checks} times, expected 2");
        base.DisposeScene();
    }
}

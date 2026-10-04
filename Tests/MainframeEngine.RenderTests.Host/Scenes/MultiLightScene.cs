using System.Globalization;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The lit-shapes geometry under three shadow-casting lights — directional, spot and point — so the
/// frame shows three shadows from three different light matrices (GitHub issue #2: before M1 every
/// shadow sub-pass executed with the last matrix written). Also self-checks the light-VP ring after
/// recording: each sub-pass's slot must still hold its own matrix.
/// </summary>
public sealed class MultiLightScene(HostOptions host) : LitShapesScene(host)
{
    private const uint CheckFrame = 10;

    private DirectionalLight _dir = null!;
    private SpotLight _spot = null!;
    private PointLight _point = null!;
    private bool _checked;

    protected override void AddLights(Node scene)
    {
        Environment.AmbientColor = new Vector3(0.12f, 0.12f, 0.14f);

        var dir = Aim(new DirectionalLight3D
        {
            Name = "Sun",
            Position = new Vector3(0, 5, 0),
            Color = new Vector3(1f, 0.95f, 0.85f),
            Energy = 0.45f,
        }, new Vector3(0.7f, -1f, -0.2f)); // shadows fall to +X
        var spot = Aim(new SpotLight3D
        {
            Name = "Spot",
            Position = new Vector3(-3.5f, 4.5f, 2.5f),
            Color = new Vector3(1f, 0.55f, 0.35f),
            Energy = 1.1f,
            Range = 14f,
            InnerConeAngle = 22f,
            OuterConeAngle = 34f,
        }, new Vector3(0.6f, -1f, -0.45f));
        var point = new OmniLight3D
        {
            Name = "Point",
            Position = new Vector3(0f, 1.3f, 2.6f),
            Color = new Vector3(0.35f, 0.6f, 1f),
            Energy = 1.2f,
            Range = 8f,
        };

        scene.AddChild(dir);
        scene.AddChild(spot);
        scene.AddChild(point);
        _dir = (DirectionalLight)dir.Light;
        _spot = (SpotLight)spot.Light;
        _point = (PointLight)point.Light;
    }

    // The render server records the shadow passes after the OnShadowPass hook; the ring still holds this
    // frame's matrices during the main pass.
    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        base.OnRenderMainPass(gameTime);
        if (gameTime.FrameCount == CheckFrame && Shadows is { } shadows)
        {
            CheckPassMatrices(shadows);
            _checked = true;
        }
    }

    protected override void DisposeScene()
    {
        // A self-check that never ran must not read as a pass (e.g. the frame was skipped).
        if (!_checked)
            Fail($"the light-VP ring self-check did not run (no shadow pass on frame {CheckFrame})");
        base.DisposeScene();
    }

    // After recording, every pass's ring slot must hold the matrix for that pass (not the last one written).
    private void CheckPassMatrices(ShadowSystem shadows)
    {
        var slot = Vulkan.FrameSlot;

        var dirExpected = ShadowSystem.CalcDirLightMatrix(_dir);
        var spotExpected = ShadowSystem.CalcSpotLightMatrix(_spot);
        Expect("dir pass 0", shadows.ReadPassMatrix(slot, ShadowSystem.PassIndexDir(0)), dirExpected);
        Expect("spot pass 0", shadows.ReadPassMatrix(slot, ShadowSystem.PassIndexSpot(0)), spotExpected);

        var faces = new Matrix4x4[6];
        for (var face = 0; face < 6; face++)
            faces[face] = shadows.ReadPassMatrix(slot, ShadowSystem.PassIndexPoint(0, face));

        for (var a = 0; a < 6; a++)
        {
            if (faces[a] == dirExpected || faces[a] == spotExpected)
                Fail($"point face {a} holds a dir/spot matrix");
            for (var b = a + 1; b < 6; b++)
                if (faces[a] == faces[b])
                    Fail(string.Create(CultureInfo.InvariantCulture, $"point faces {a} and {b} share one matrix"));
        }

        if (shadows.PassOffset(slot, ShadowSystem.PassIndexSpot(0)) == shadows.PassOffset(slot, ShadowSystem.PassIndexDir(0)))
            Fail("dir and spot passes share a ring offset");
    }

    private void Expect(string what, Matrix4x4 actual, Matrix4x4 expected)
    {
        if (actual != expected)
            Fail($"{what}: ring holds {actual}, expected {expected}");
    }
}

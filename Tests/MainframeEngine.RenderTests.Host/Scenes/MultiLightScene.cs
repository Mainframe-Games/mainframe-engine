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

    // After recording, every pass's ring slot must hold the matrix planned for that pass (not the last one written):
    // the sun's cascades, the spot's atlas tile and the point light's six cube faces.
    private void CheckPassMatrices(ShadowSystem shadows)
    {
        var slot = Vulkan.FrameSlot;
        var passes = shadows.Passes;
        var cascades = 0;
        var tiles = 0;
        var faces = 0;
        var offsets = new HashSet<uint>();
        var matrices = new List<Matrix4x4>();
        foreach (var pass in passes)
        {
            if (!shadows.PassRendered(pass.Index))
                continue;
            switch (pass.Kind)
            {
                case ShadowPassKind.Cascade: cascades++; break;
                case ShadowPassKind.AtlasTile: tiles++; break;
                case ShadowPassKind.CubeFace: faces++; break;
            }

            Expect(string.Create(CultureInfo.InvariantCulture, $"{pass.Kind} pass {pass.Index}"), shadows.ReadPassMatrix(slot, pass.Index), pass.ViewProjection);
            if (!offsets.Add(shadows.PassOffset(slot, pass.Index)))
                Fail(string.Create(CultureInfo.InvariantCulture, $"pass {pass.Index} shares a ring offset"));
            if (matrices.Contains(pass.ViewProjection))
                Fail(string.Create(CultureInfo.InvariantCulture, $"pass {pass.Index} has the same matrix as an earlier pass"));
            matrices.Add(pass.ViewProjection);
        }

        if (cascades == 0 || tiles != 1 || faces != 6)
            Fail(string.Create(CultureInfo.InvariantCulture, $"expected cascades, 1 atlas tile and 6 cube faces; rendered {cascades}, {tiles}, {faces}"));
    }

    private void Expect(string what, Matrix4x4 actual, Matrix4x4 expected)
    {
        if (actual != expected)
            Fail($"{what}: ring holds {actual}, expected {expected}");
    }
}

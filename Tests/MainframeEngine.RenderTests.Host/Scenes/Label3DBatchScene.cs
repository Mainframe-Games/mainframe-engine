using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// The batched <see cref="Label3D"/> renderer (ADR 0258): 300 labels over two fonts and three sizes on a grid of rows —
/// outlined, billboarded, a row of no-depth-test tags and a row of cutouts that cast no shadow — drawn in a handful of
/// instanced draws (at most one per pipeline and page group), with the labels swaying and the camera orbiting so every
/// frame re-poses and re-sorts them. At <see cref="CheckFrame"/> the scene checks the renderer's counters and that a
/// label picks through the object-ID pass.
/// </summary>
public sealed class Label3DBatchScene(HostOptions host) : ShadowSceneBase(host)
{
    public const uint CheckFrame = 10;
    private const uint PickFrame = 6;
    private const int Rows = 15, Columns = 20;

    private readonly List<(Label3D Label, Vector3 Home)> _labels = [];
    private Label3D _target = null!;
    private Task<PickResult>? _pick;
    private bool _checked;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 4.5f, 12f);
        Camera.LookAt(new Vector3(0, 1.5f, -4f));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(60, 60) }, MaterialOverride = Plain(70, 76, 84) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f)));

        var lato = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve(Label3D.DefaultFontPath)));
        var mono = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/RobotoMono-Regular.ttf")));
        int[] sizes = [24, 32, 48];
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var i = row * Columns + column;
                var home = new Vector3((column - (Columns - 1) / 2f) * 1.1f, 0.5f + row % 5 * 0.7f, -row / 5 * 5f - column % 2 * 0.4f);
                var label = new Label3D
                {
                    Name = $"Label{i}",
                    Position = home,
                    Text = row % 3 == 0 ? $"#{i}" : row % 3 == 1 ? $"npc {i}" : $"{i:000}",
                    Font = i % 2 == 0 ? lato : mono,
                    FontSize = sizes[i % 3],
                    PixelSize = 0.006f,
                    OutlineSize = row % 2 == 0 ? 8 : 0,
                    Billboard = column % 4 == 0 ? BillboardMode.Enabled : BillboardMode.Disabled,
                    NoDepthTest = row == 4,
                    AlphaCut = row == 9 ? SpriteAlphaCut.Discard : SpriteAlphaCut.Disabled,
                    CastShadows = row != 9, // a cutout that casts no shadow stays batched
                    Modulate = Color.FromArgb(255, 120 + i * 37 % 136, 120 + i * 59 % 136, 120 + i * 83 % 136),
                };
                scene.AddChild(label);
                _labels.Add((label, home));
            }
        }

        _target = _labels[Columns * 2 + Columns / 2].Label; // a front-row label near the middle
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var t = gameTime.FrameCount / 60f; // fixed steps: the same pose every run
        for (var i = 0; i < _labels.Count; i += 7)
            _labels[i].Label.Position = _labels[i].Home + new Vector3(0, MathF.Sin(t * 2f + i) * 0.1f, 0);
        if (gameTime.FrameCount > CheckFrame)
        {
            // Allocation runs: orbit, so every frame poses the billboards and re-sorts the blended labels.
            Camera.Position = new Vector3(MathF.Sin(t * 0.5f) * 6f, 4.5f, 12f);
            Camera.LookAt(new Vector3(0, 1.5f, -4f));
        }

        var render = Servers.Render!;
        if (gameTime.FrameCount == PickFrame)
        {
            // The middle of the target's text (its outline included), projected with the camera that draws it.
            var centre = Vector3.Transform(Vector3.Zero, _target.ModelMatrix);
            var pixel = Camera.UnprojectPosition(centre);
            _pick = render.PickAsync((int)pixel.X, (int)pixel.Y);
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        _checked = true;
        var stats = render.Text3DStats;
        var groups = (stats.Pages + Text3DRenderer.PagesPerGroup - 1) / Text3DRenderer.PagesPerGroup;
        if (stats.Labels + stats.Culled != _labels.Count)
            Fail($"expected {_labels.Count} labels considered, got {stats.Labels} drawn + {stats.Culled} culled");
        if (stats.Labels < _labels.Count / 2)
            Fail($"only {stats.Labels} of {_labels.Count} labels drawn");
        // Cutouts, blended and no-depth-test labels: three pipelines (all double-sided), times the page groups.
        if (stats.DrawCalls < 1 || stats.DrawCalls > 3 * groups)
            Fail($"{stats.Labels} labels took {stats.DrawCalls} draws over {stats.Pages} pages ({groups} groups); expected ≤ {3 * groups}");
        if (render.MeshStats.SurfaceInstances > 1)
            Fail($"labels reached the mesh batcher: {render.MeshStats.SurfaceInstances} surface draws (only the floor expected)");
        if (_pick is null || !_pick.IsCompletedSuccessfully)
            Fail($"the label pick did not complete ({_pick?.Status})");
        else if (_pick.Result.Node is not Label3D)
            Fail($"picking a label returned {_pick.Result.Node?.Name ?? "nothing"} (id {_pick.Result.ObjectId})");
        Console.WriteLine($"[label3d-batch] labels {stats.Labels}, culled {stats.Culled}, glyphs {stats.Glyphs}, draws {stats.DrawCalls}, pages {stats.Pages}, pick {_pick?.Result.Node?.Name}");
    }

    protected override void DisposeScene()
    {
        if (!_checked && Host.Count == 0 && Host.AllocationMeasuredFrames == 0)
            Fail($"the label batch self-check did not run (frame {CheckFrame})");
    }
}

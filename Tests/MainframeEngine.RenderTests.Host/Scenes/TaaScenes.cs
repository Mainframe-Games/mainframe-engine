using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// TAA's edges (ADR 0166): thin unshaded white bars at shallow angles (long staircases without anti-aliasing) and a fan
/// of sub-pixel-wide spokes (they break up into dashes without it) over the dark clear colour, a still camera. Run with
/// <c>--aa none</c>, <c>--aa taa</c>, or at a higher <c>--scale</c> without anti-aliasing as the supersampled reference
/// the test box-filters down: TAA's converged frame must be much closer to it than the aliased one.
/// </summary>
public sealed class TaaEdgesScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TaaEdgesScene) };
        scene.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, 3f) });
        var white = new StandardMaterial3D { AlbedoColor = Color.White, ShadingMode = ShadingMode.Unshaded };

        // Bars: 3 cm thick, tilted 1.5° to 14° (left half of the view).
        float[] angles = [1.5f, 3.5f, 6f, 9f, 14f];
        for (var i = 0; i < angles.Length; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = $"Bar{i}",
                Position = new Vector3(-0.9f, -1.0f + i * 0.5f, 0f),
                RotationDegrees = new Vector3(0f, 0f, angles[i]),
                Mesh = new BoxMesh { Size = new Vector3(2.2f, 0.03f, 0.05f) },
                MaterialOverride = white,
            });
        }

        // Spokes: 7 mm wide (under a pixel), fanning out from a hub on the right.
        for (var i = 0; i < 14; i++)
        {
            var angle = 8f + i * 12f;
            var direction = new Vector2(MathF.Cos(float.DegreesToRadians(angle)), MathF.Sin(float.DegreesToRadians(angle)));
            scene.AddChild(new MeshInstance3D
            {
                Name = $"Spoke{i}",
                Position = new Vector3(1.35f + direction.X * 0.65f, -0.2f + direction.Y * 0.65f, 0f),
                RotationDegrees = new Vector3(0f, 0f, angle),
                Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.007f, 0.01f) },
                MaterialOverride = white,
            });
        }

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// TAA ghosting (ADR 0166), self-checked: a red unshaded box slides right across a flat grey unshaded wall at
/// <see cref="Speed"/> (about 3 pixels a frame at 320 × 240) under a still camera. At every captured frame the box's trail
/// — the band it covered since it started, behind its current trailing edge — must show the wall, not a smear of red, and
/// the box itself must still be red. <c>--count 1</c> adds camera cuts: <see cref="RenderServer.ResetTemporalHistory"/>
/// at <see cref="ResetFrame"/> and a switch to a second camera at the same pose at <see cref="SwitchFrame"/>; with a
/// resize (the test passes <c>--resize</c>), the TAA effect must have started a new history exactly
/// <see cref="ExpectedResets"/> times by the capture.
/// </summary>
public sealed class TaaGhostScene(HostOptions host) : RenderTestGame(host)
{
    public const float Speed = 0.04f; // m per frame
    public const float StartX = -2.4f;
    public const float BoxSize = 0.6f;
    public const uint ResetFrame = 20;
    public const uint SwitchFrame = 30;

    /// <summary>First frame, <see cref="ResetFrame"/>, <see cref="SwitchFrame"/> and the resize.</summary>
    public const long ExpectedResets = 4;

    private static readonly Color Wall = Color.FromArgb(255, 120, 124, 128);
    private static readonly Color Red = Color.FromArgb(255, 230, 40, 30);

    private Camera3D _camera = null!;
    private Camera3D _second = null!;
    private MeshInstance3D _box = null!;
    private float _x = StartX;
    private uint _frame;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TaaGhostScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, 4f) };
        _second = new Camera3D { Name = "Second", Position = new Vector3(0f, 0f, 4f), Current = false };
        scene.AddChild(_camera);
        scene.AddChild(_second);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(0f, 0f, -1f),
            Mesh = new BoxMesh { Size = new Vector3(30f, 30f, 0.1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Wall, ShadingMode = ShadingMode.Unshaded },
        });
        _box = new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(StartX, 0f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(BoxSize) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Red, ShadingMode = ShadingMode.Unshaded },
        };
        scene.AddChild(_box);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _frame = gameTime.FrameCount;
        _x = StartX + Speed * _frame;
        _box.Position = new Vector3(_x, 0f, 0f);
        if (Host.Count != 1)
            return;
        if (_frame == ResetFrame)
            Servers.Render!.ResetTemporalHistory();
        if (_frame == SwitchFrame)
            _second.MakeCurrent();
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        CheckTrail(capture);
        if (Host.Count == 1 && Servers.Render?.PostEffects?.Find<TaaEffect>() is { } taa && taa.ResetFrames != ExpectedResets)
            Fail($"TAA started {taa.ResetFrames} histories by frame {_frame}, expected {ExpectedResets} (first frame, reset, camera switch, resize).");
    }

    // The trail band behind the box's trailing edge must be the wall's colour (read far from the box's path).
    private void CheckTrail(FrameCapture capture)
    {
        var (min, max) = ScreenRect(capture, _x);
        var (start, _) = ScreenRect(capture, StartX);
        var margin = 3f * Host.Scale;
        int x0 = Math.Max(0, (int)start.X), x1 = (int)(min.X - margin);
        int y0 = (int)(min.Y + margin), y1 = (int)(max.Y - margin);
        if (x1 - x0 < 10 || y1 <= y0)
        {
            Fail($"Frame {_frame}: the trail is too short to check ({x0}..{x1}).");
            return;
        }

        var reference = Pixel(capture, capture.Width / 2, 4); // wall, above the box's path
        int pixels = 0, off = 0, worst = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var p = Pixel(capture, x, y);
                var delta = Math.Max(Math.Abs(p.R - reference.R), Math.Max(Math.Abs(p.G - reference.G), Math.Abs(p.B - reference.B)));
                worst = Math.Max(worst, delta);
                pixels++;
                if (delta > 3)
                    off++;
            }
        }

        if (worst > 8 || off > pixels / 200)
            Fail($"Frame {_frame}: the box's trail is not the wall: {off} of {pixels} pixels differ by more than 3, worst {worst}.");

        var centre = Pixel(capture, (int)((min.X + max.X) / 2), (int)((min.Y + max.Y) / 2));
        if (centre.R < centre.G + 120)
            Fail($"Frame {_frame}: the box's centre is not red: {centre}.");
    }

    // The box's pixel rectangle with its centre at x (its 8 corners projected by the main camera).
    private (Vector2 Min, Vector2 Max) ScreenRect(FrameCapture capture, float x)
    {
        var camera = _camera.SyncRenderCamera((float)capture.Width / capture.Height);
        var viewProjection = camera.ViewMatrix * camera.ProjectionMatrix;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1) * (BoxSize / 2f);
            var clip = Vector4.Transform(new Vector4(new Vector3(x, 0f, 0f) + offset, 1f), viewProjection);
            var pixel = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * capture.Width, (0.5f - clip.Y / clip.W * 0.5f) * capture.Height);
            min = Vector2.Min(min, pixel);
            max = Vector2.Max(max, pixel);
        }

        return (min, max);
    }

    private static (int R, int G, int B) Pixel(FrameCapture capture, int x, int y)
    {
        var i = (y * capture.Width + x) * 4;
        return (capture.Pixels[i], capture.Pixels[i + 1], capture.Pixels[i + 2]);
    }

    protected override void DisposeScene()
    {
    }
}

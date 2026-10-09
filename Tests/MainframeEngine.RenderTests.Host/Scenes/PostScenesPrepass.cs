using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Motion vectors (ADR 0163) under the velocity debug view, self-checked against the camera's own matrices. A checker
/// floor, posts and the procedural sky; a body (a <see cref="RigidBody3D"/> without gravity) flies across.
/// <c>--count</c>: 0 the camera yaws <see cref="YawDegreesPerFrame"/> every frame while the body moves; 1 a still camera
/// and body with the projection jitter on (every pixel must stay still); 2 both moving with the jitter on (the
/// allocation run).
/// </summary>
/// <remarks>
/// Pure rotation moves every static point, near or at infinity, by the same screen offset at a pixel, so the expected
/// velocity of a pixel is its far-plane point reprojected by last frame's view-projection. The body's centre pixel must
/// show that plus its own motion.
/// </remarks>
public sealed class VelocityScene(HostOptions host) : MeshSceneBase(host)
{
    public const float YawDegreesPerFrame = 0.5f;
    public const float BodySpeed = 3f; // m/s along +X
    public const float BodySize = 0.8f;

    /// <summary>Velocity quantisation of the view (one 8-bit step).</summary>
    public const float Step = 1f / (255f * VelocityDebugView.Scale);

    private RigidBody3D _body = null!;
    private Quaternion _baseRotation;
    private Matrix4x4 _viewProjection;
    private Matrix4x4 _previousViewProjection;
    private Vector3 _previousBody;
    private bool _checking;

    protected override Vector3 CameraPosition => new(0f, 2f, 7f);
    protected override Vector3 CameraTarget => new(0f, 0.8f, 0f);

    private bool Moving => Host.Count != 1;

    protected override void Build(Node3D scene)
    {
        var stone = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 150, 140, 125) };
        var post = new BoxMesh { Size = new Vector3(0.4f, 2.4f, 0.4f) };
        for (var i = 0; i < 5; i++)
            scene.AddChild(new MeshInstance3D { Name = $"Post{i}", Position = new Vector3(-3f + i * 1.5f, 1.2f, -2.5f), Mesh = post, MaterialOverride = stone });

        _body = new RigidBody3D
        {
            Name = "Body",
            Position = new Vector3(-2.5f, 1.1f, 1.2f),
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            CanSleep = false,
        };
        _body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(BodySize) } });
        _body.AddChild(new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new BoxMesh { Size = new Vector3(BodySize) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 230, 90, 60) },
        });
        scene.AddChild(_body);
        _baseRotation = Camera.Rotation;
    }

    protected override void OnFrame(in GameTime gameTime)
    {
        if (gameTime.FrameCount == 1 && Moving)
            _body.LinearVelocity = new Vector3(BodySpeed, 0f, 0f);
        if (Moving)
            Camera.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(YawDegreesPerFrame * gameTime.FrameCount)) * _baseRotation;

        // The matrices this frame renders with, and last frame's; the body's pose last frame (the tree has not ticked yet).
        var extent = Vulkan.SwapchainExtent;
        var camera = Camera.SyncRenderCamera((float)extent.Width / extent.Height);
        _previousViewProjection = _viewProjection;
        _viewProjection = camera.ViewMatrix * camera.ProjectionMatrix;
        _previousBody = _body.GlobalPosition;
        _checking = gameTime.FrameCount > 2 && Array.BinarySearch(Host.CaptureFrames, gameTime.FrameCount) >= 0;
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        if (!_checking)
            return;
        _checking = false;
        var body = _body.GlobalPosition; // this frame's render pose
        CheckStaticPixels(capture, body);
        if (Moving)
            CheckBody(capture, body);
    }

    // Every pixel away from the body moves like the camera's rotation (zero when still); 99 % within two steps.
    private void CheckStaticPixels(FrameCapture capture, Vector3 body)
    {
        Matrix4x4.Invert(_viewProjection, out var inverse);
        var (min, max) = ScreenBounds(capture, body, _viewProjection, _previousBody, _previousViewProjection);
        int checkedPixels = 0, wrong = 0;
        var worst = 0f;
        for (var y = 2; y < capture.Height; y += 6)
        {
            for (var x = 2; x < capture.Width; x += 6)
            {
                if (x >= min.X && x <= max.X && y >= min.Y && y <= max.Y)
                    continue;
                var ndc = PixelNdc(x, y, capture.Width, capture.Height);
                var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
                var expected = Moving ? ScreenUv(ndc) - ScreenUv(Project(new Vector3(far.X, far.Y, far.Z) / far.W, _previousViewProjection)) : Vector2.Zero;
                var error = Vector2.Abs(Decode(capture, x, y) - expected);
                var e = MathF.Max(error.X, error.Y);
                worst = MathF.Max(worst, e);
                checkedPixels++;
                if (e > 2f * Step)
                    wrong++;
            }
        }

        if (wrong > checkedPixels / 100)
            Fail($"{wrong} of {checkedPixels} static pixels are off the camera's motion by more than two steps (worst {worst / Step:F1} steps).");
    }

    // The body's centre pixel: the rotation's velocity plus its own motion. The expectation follows the body's centre,
    // the pixel shows its front face (nearer, so it moves a little more): within 15 % of its own motion and two steps.
    private void CheckBody(FrameCapture capture, Vector3 body)
    {
        var centre = Project(body, _viewProjection);
        var x = (int)((centre.X * 0.5f + 0.5f) * capture.Width);
        var y = (int)((0.5f - centre.Y * 0.5f) * capture.Height);
        var expected = ScreenUv(centre) - ScreenUv(Project(_previousBody, _previousViewProjection));
        var rotation = ScreenUv(centre) - ScreenUv(Project(body, _previousViewProjection)); // a static point there
        var own = expected - rotation;
        var actual = Decode(capture, x, y);
        if (own.Length() < 8f * Step)
            Fail($"The body moves too little on screen to check ({own.Length() / Step:F1} steps).");
        if (Vector2.Distance(actual, expected) > 0.15f * own.Length() + 2f * Step)
            Fail($"The body's velocity at ({x}, {y}) is {actual / Step} steps, expected {expected / Step} (its own motion {own / Step}).");
    }

    // The pixel rectangle the body covers this frame and last (its bounding sphere, with a margin).
    private static (Vector2 Min, Vector2 Max) ScreenBounds(FrameCapture capture, Vector3 body, in Matrix4x4 viewProjection,
        Vector3 previousBody, in Matrix4x4 previousViewProjection)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (centre, matrix) in new[] { (body, viewProjection), (previousBody, previousViewProjection) })
        {
            for (var corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1) * BodySize;
                var ndc = Project(centre + offset, matrix);
                var pixel = new Vector2((ndc.X * 0.5f + 0.5f) * capture.Width, (0.5f - ndc.Y * 0.5f) * capture.Height);
                min = Vector2.Min(min, pixel);
                max = Vector2.Max(max, pixel);
            }
        }

        return (min - new Vector2(6f), max + new Vector2(6f));
    }

    private static Vector2 PixelNdc(int x, int y, int width, int height) =>
        new((x + 0.5f) / width * 2f - 1f, 1f - (y + 0.5f) / height * 2f);

    private static Vector2 Project(Vector3 world, in Matrix4x4 viewProjection)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        return new Vector2(clip.X, clip.Y) / clip.W;
    }

    private static Vector2 ScreenUv(Vector2 ndc) => new(ndc.X * 0.5f + 0.5f, 0.5f - ndc.Y * 0.5f);

    /// <summary>The velocity a pixel of the view encodes (screen UV per frame).</summary>
    public static Vector2 Decode(FrameCapture capture, int x, int y)
    {
        var i = (y * capture.Width + x) * 4;
        return new Vector2(VelocityDebugView.Decode(capture.Pixels[i]), VelocityDebugView.Decode(capture.Pixels[i + 1]));
    }
}

/// <summary>
/// The post-processing framework's write-back and target pool (ADR 0163), self-checked: a test effect in the
/// <c>BeforeTonemap</c> stage clears a pooled ping-pong history to a known linear colour each frame and copies it into the
/// scene colour (<see cref="PostEffectContext.CopyToSceneColor"/>), so the tonemapped frame must be that colour
/// everywhere. The history must be valid from its second frame on.
/// </summary>
public sealed class PostCopyScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The linear HDR colour the effect writes (tonemapped with the default exposure).</summary>
    public static readonly Vector3 Color = new(0.5f, 0.25f, 0.125f);

    private CopyEffect? _effect;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(PostCopyScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 1f, 4f) };
        scene.AddChild(camera);
        scene.AddChild(new MeshInstance3D { Name = "Box", Mesh = new BoxMesh(), MaterialOverride = new StandardMaterial3D() });
        Tree.ChangeScene(scene);
        _effect = new CopyEffect();
        Servers.Render!.PostEffects!.Add(_effect);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (_effect is null || gameTime.FrameCount < 4)
            return;
        if (_effect.Recorded == 0)
            Fail("The BeforeTonemap test effect never ran.");
        if (_effect.InvalidHistories > 0)
        {
            Fail($"The ping-pong history was invalid on {_effect.InvalidHistories} frame(s) after its first.");
            _effect.InvalidHistories = 0;
        }
    }

    protected override void DisposeScene()
    {
    }

    private sealed class CopyEffect() : PostEffect("test copy", PostStage.BeforeTonemap, PostEffectOrder.Taa)
    {
        public int Recorded;
        public int InvalidHistories;

        public override bool IsEnabled(in PostEffectSettings settings) => true;

        protected override void OnRecord(PostEffectContext context)
        {
            var history = context.Targets.GetHistory(new PostTargetDesc("test history", SceneTextures.ColorFormat));
            history.Advance(context.FrameNumber);
            if (Recorded > 0 && !history.IsValid)
                InvalidHistories++;
            else if (Recorded == 0 && history.IsValid)
                InvalidHistories++; // the first frame has no history
            var target = history.Current;
            Span<ClearValue> clear = [new ClearValue { Color = new ClearColorValue(Color.X, Color.Y, Color.Z, 1f) }];
            target.Begin(context.CommandBuffer, clear);
            target.End(context.CommandBuffer);
            history.MarkWritten();
            context.CopyToSceneColor(target.GetColor(0).View);
            Recorded++;
        }
    }
}

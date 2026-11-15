using System.Numerics;
using Silk.NET.Maths;

namespace MainframeEngine.Cli.ContactSheet;

/// <summary>The renderer could not start (no display, no Vulkan device): <c>mfe anim render</c> exits 1.</summary>
internal sealed class RenderUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Renders the cells of a contact sheet (G1.17): a hidden window the size of a cell, the model on a lit ground under a
/// procedural sky, posed with <see cref="ClipSampler"/> and seen from each view's camera in turn, one frame capture per cell.
/// </summary>
internal sealed class ContactSheetRenderer : Engine
{
    private const float FieldOfView = 12f;
    private const float Elevation = 12f; // the three-quarter camera's, degrees

    private readonly Node3D _model;
    private readonly Animation _clip;
    private readonly NodePath _skeletonPath;
    private readonly HumanoidRig _rig;
    private readonly ContactSheetLayout _layout;
    private readonly bool _flat;
    private readonly List<(int View, int Frame)> _cells;
    private readonly Dictionary<(int View, int Frame), FrameCapture> _captures = [];
    private Skeleton3D _skeleton = null!;
    private Camera3D _camera = null!;
    private int _index;
    private int _settled;
    private bool _waiting;
    private float _groundY;
    private float _height;
    private Vector3 _forward;
    private Vector3 _left;

    private ContactSheetRenderer(Node3D model, Animation clip, NodePath skeletonPath, HumanoidRig rig, ContactSheetLayout layout, bool flat)
        : base(Options(layout))
    {
        _model = model;
        _clip = clip;
        _skeletonPath = skeletonPath;
        _rig = rig;
        _layout = layout;
        _flat = flat;
        _cells = [.. layout.Cells()];
    }

    /// <summary>
    /// Renders every cell of <paramref name="layout"/>. <paramref name="model"/> is a fresh import of the model (not in a
    /// tree); <paramref name="rig"/> is bound to its skeleton.
    /// </summary>
    public static IReadOnlyDictionary<(int View, int Frame), FrameCapture> Render(Node3D model, Animation clip, NodePath skeletonPath,
        HumanoidRig rig, ContactSheetLayout layout, bool flat, TextWriter error)
    {
        // The engine logs to stdout, which carries mfe's own output (and --json): its warnings and errors go to stderr instead.
        var sink = new ErrorSink(error);
        var consoleWasOn = Log.RemoveSink(Log.ConsoleSink);
        Log.AddSink(sink);
        try
        {
            return RenderCells(model, clip, skeletonPath, rig, layout, flat);
        }
        finally
        {
            Log.RemoveSink(sink);
            if (consoleWasOn)
                Log.AddSink(Log.ConsoleSink);
        }
    }

    private sealed class ErrorSink(TextWriter error) : ILogSink
    {
        public void Write(in LogEntry entry)
        {
            if (entry.Level is Log.Level.Warning or Log.Level.Error or Log.Level.Fatal)
                lock (error)
                    error.WriteLine($"mfe: render {LogEntry.LevelTag(entry.Level)} {entry.Category}: {entry.Message}");
        }
    }

    private static Dictionary<(int View, int Frame), FrameCapture> RenderCells(Node3D model, Animation clip, NodePath skeletonPath,
        HumanoidRig rig, ContactSheetLayout layout, bool flat)
    {
        ContactSheetRenderer renderer;
        try
        {
            renderer = new ContactSheetRenderer(model, clip, skeletonPath, rig, layout, flat);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new RenderUnavailableException(
                $"mfe anim render needs a GPU and a display (macOS: keep it awake, `caffeinate -u -t 600 &`): {e.Message}. " +
                "`mfe anim describe` and `check` give the numbers without one.", e);
        }

        using (renderer)
        {
            try
            {
                renderer.Run();
            }
            catch (Exception e) when (e is not OutOfMemoryException && renderer._captures.Count == 0)
            {
                throw new RenderUnavailableException($"mfe anim render could not render: {e.Message}", e);
            }

            if (renderer._captures.Count < renderer._cells.Count)
                throw new RenderUnavailableException(
                    $"mfe anim render captured {renderer._captures.Count} of {renderer._cells.Count} cells before the window closed.",
                    new InvalidOperationException("incomplete capture"));
            return renderer._captures;
        }
    }

    private static EngineOptions Options(ContactSheetLayout layout)
    {
        var (width, height) = layout.RenderSize;
        return new EngineOptions
        {
            GameName = "mfe anim render",
            WindowSize = new Vector2D<int>(width, height),
            ContentScale = 1f, // captures are exactly the window size in pixels on any display
            WindowVisible = false,
            EnableFrameCapture = true,
            VSync = false,
            FixedDeltaTime = 1f / 60f,
            MaxFrames = layout.Views.Count * layout.Frames.Count * 12 + 240, // a safety net: never hang
            Audio = new AudioOptions { Device = AudioDeviceMode.Null, BusLayoutPath = null },
            DevOverlayVisible = false,
            Locale = "en",
        };
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        var scene = new Node3D { Name = "ContactSheet" };
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 10, 0), Color = new Vector3(1f, 0.97f, 0.9f), Energy = 1f, CastsShadows = true };
        scene.AddChild(sun);
        _camera = new Camera3D { Name = "Camera", Fov = FieldOfView, Near = 0.5f, Far = 200f };
        scene.AddChild(_camera);

        foreach (var player in Find<AnimationPlayer>(_model).ToList())
            player.Parent?.RemoveChild(player);
        if (_flat)
        {
            var grey = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = System.Drawing.Color.FromArgb(255, 176, 180, 188) };
            foreach (var mesh in Find<MeshInstance3D>(_model))
                mesh.MaterialOverride = grey;
        }

        scene.AddChild(_model);
        _skeleton = _model.GetNode<Skeleton3D>(_skeletonPath);

        // The rest pose gives the ground, the character's height and its facing in world space.
        _skeleton.ResetBonePoses();
        var skeletonWorld = _skeleton.GlobalTransform;
        _groundY = skeletonWorld.TransformPoint(_rig.ToSkeletonSpace(new Vector3(0, ClipDescriber.Ground(_rig), 0))).Y;
        var top = float.NegativeInfinity;
        for (var i = 0; i < _skeleton.GetBoneCount(); i++)
            top = MathF.Max(top, skeletonWorld.TransformPoint(_skeleton.GetBoneGlobalPose(i).Origin).Y);
        _height = MathF.Max(0.1f, (top - _groundY) * 1.08f); // the head's top is above its last bone
        var basis = skeletonWorld.Basis.GetRotation();
        _forward = Flat(Vector3.Transform(Vector3.Transform(Vector3.UnitZ, _rig.CharacterRotation), basis), Vector3.UnitZ);
        _left = Flat(Vector3.Transform(Vector3.Transform(Vector3.UnitX, _rig.CharacterRotation), basis), Vector3.UnitX);

        sun.LookAt(sun.Position + Vector3.Normalize(-0.45f * _left - 0.75f * Vector3.UnitY - 0.5f * _forward));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Position = new Vector3(0, _groundY, 0),
            Mesh = new PlaneMesh { Size = new Vector2(_height * 40f, _height * 40f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.BlinnPhong,
                AlbedoColor = System.Drawing.Color.FromArgb(255, 112, 118, 126),
                Specular = 0.05f,
            },
        });
        Tree.ChangeScene(scene);
    }

    private static Vector3 Flat(Vector3 direction, Vector3 fallback)
    {
        direction.Y = 0;
        return direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : fallback;
    }

    private static IEnumerable<T> Find<T>(Node node) where T : Node
    {
        if (node is T match)
            yield return match;
        foreach (var child in node.Children)
            foreach (var found in Find<T>(child))
                yield return found;
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        if (_waiting || _index >= _cells.Count)
            return;
        if (_settled == 0)
            PoseCell(_cells[_index]);
        _settled++;
        // A few frames for the pose and the camera to reach the GPU (more for the first: pipelines and shadows warm up).
        if (_settled >= (_index == 0 ? 8 : 3))
        {
            _waiting = true;
            CaptureFrame();
        }
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        _captures[_cells[_index]] = capture;
        _index++;
        _settled = 0;
        _waiting = false;
        if (_index >= _cells.Count)
            Quit(ExitCode.Ok);
    }

    private void PoseCell((int View, int Frame) cell)
    {
        var time = _layout.Frames[cell.Frame].Time;
        _skeleton.ResetBonePoses();
        ClipSampler.Apply(_clip, _skeleton, _skeletonPath, time);

        var hips = _skeleton.GlobalTransform.TransformPoint(_skeleton.GetBoneGlobalPose(_rig.GetRigBone(HumanoidBone.Hips)).Origin);
        var target = new Vector3(hips.X, _groundY + _height * 0.5f, hips.Z);
        var frameHeight = _height * 1.45f;
        var distance = frameHeight * 0.5f / MathF.Tan(FieldOfView * 0.5f * MathF.PI / 180f);
        var direction = _layout.Views[cell.View] switch
        {
            SheetView.Front => _forward,
            SheetView.Side => _left,
            _ => Vector3.Normalize(Vector3.Normalize(_forward + _left) * MathF.Cos(Elevation * MathF.PI / 180f) +
                                   Vector3.UnitY * MathF.Sin(Elevation * MathF.PI / 180f)),
        };
        _camera.Position = target + direction * distance;
        _camera.LookAt(target);
    }
}

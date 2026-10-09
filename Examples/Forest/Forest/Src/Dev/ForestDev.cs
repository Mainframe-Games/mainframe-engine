using System.Globalization;
using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The <c>Dev</c> autoload: developer switches passed as game arguments after <c>++</c>. Idle without them.
/// <list type="bullet">
/// <item><c>--autowalk</c>: synthetic input walks the path loop from the trailhead (<see cref="PathWalker"/>: turning,
/// sprinting, a jump), then measures what a 600-frame window allocates on the main thread after a 180-frame warm-up and
/// quits (exit code 0 when it allocated nothing, 1 otherwise). <c>--autowalk-lap</c> walks the whole loop instead and
/// quits when it is back (exit 1 if it got stuck). With <c>--fixed-fps</c> the walk is the same on every run.</item>
/// <item><c>--shot &lt;1-5|name&gt;</c>: a fixed camera at a reference shot (<see cref="ValleyLayout.Shots"/>);
/// <c>--view x,y,z,tx,ty,tz[,fov]</c>: a camera at a world position looking at a point (a y of 0: eye height above the ground; −h: h above it).</item>
/// <item><c>--benchmark [--frames n] [--out file.json] [--baseline file.json [--write-baseline]]</c>: <see cref="ForestBenchmark"/>
/// (exit code 1 when it allocated or is more than 10 % slower than the baseline).</item>
/// <item><c>--set Node.Property=value</c>: tunes the look from the command line (<see cref="ApplyOverrides"/>);
/// <c>--warmup n</c>: the autowalk's warm-up frames.</item>
/// <item><c>--resolution WxH</c>: resizes the window so the frame is W × H pixels (screenshots, benchmarks).</item>
/// <item><c>--no-capture</c>: the controller does not capture the mouse (scripted runs, screenshots).</item>
/// <item>Audio: when the current scene has no <see cref="ForestAudio"/>, one is attached to it (its first
/// <see cref="River3D"/> and <see cref="FirstPersonController"/>) unless <c>--no-audio</c> is passed.</item>
/// </list>
/// </summary>
public sealed class ForestDev : Node
{
    /// <summary>
    /// The autowalk's warm-up: 25 s, past the first jump, landing and sprint (the physics' contact lists grow to their
    /// working size the first time the character meets that many terrain triangles), so the window measures steady state.
    /// </summary>
    public const int DefaultWarmUpFrames = 1500;
    public const int WindowFrames = 600;

    /// <summary>Frames an <c>--autowalk-lap</c> may take before it counts as stuck (12 minutes at 60 fps).</summary>
    public const int LapFrameLimit = 60 * 60 * 12;

    private FirstPersonController? _controller;
    private PathWalker? _walker;
    private ForestBenchmark? _benchmark;
    private bool _autoWalk;
    private bool _lap;
    private int _frame;
    private long _allocatedBefore;
    private int _collectionsBefore;
    private int _footstepsBefore;
    private bool _audioChecked;
    private bool _cameraPlaced;
    private readonly long[] _frameAllocations = new long[WindowFrames];
    private long _lastAllocated;
    private int _warmUp = DefaultWarmUpFrames;
    private string? _shot;
    private string? _view;
    private Vector2I _resolution;

    /// <summary>True when the game was started with <c>++ --autowalk</c> or <c>--autowalk-lap</c>.</summary>
    public bool AutoWalk => _autoWalk;

    protected override void OnReady()
    {
        var args = GameHost.UserArgs;
        _lap = args.Contains("--autowalk-lap");
        _autoWalk = _lap || args.Contains("--autowalk");
        _shot = Argument(args, "--shot");
        if (int.TryParse(Argument(args, "--warmup"), CultureInfo.InvariantCulture, out var warmUp) && warmUp > 0)
            _warmUp = warmUp;
        _view = Argument(args, "--view");
        if (Argument(args, "--resolution") is { } resolution && ParseResolution(resolution) is { } size)
            _resolution = size;
        if (args.Contains("--benchmark"))
        {
            var frames = int.TryParse(Argument(args, "--frames"), CultureInfo.InvariantCulture, out var f) ? f : 1800;
            _benchmark = new ForestBenchmark(frames, Argument(args, "--out"))
            {
                BaselinePath = Argument(args, "--baseline"),
                WriteBaseline = args.Contains("--write-baseline"),
            };
        }

        if (_autoWalk)
            Log.Info(_lap
                ? "[Forest] --autowalk-lap: walking the whole loop."
                : $"[Forest] --autowalk: {_warmUp} warm-up frames, then a {WindowFrames}-frame allocation window.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Tree is not { } tree)
            return;
        if (!_audioChecked && tree.CurrentScene is { } scene)
        {
            _audioChecked = true;
            if (!GameHost.UserArgs.Contains("--no-audio") && scene.FindChildren<ForestAudio>(owned: false).Count == 0)
                ForestAudio.Attach(scene, null, null); // finds the scene's river and player when ready
            if (_resolution.X > 0)
                Resize(tree, _resolution);
            ApplyOverrides(scene, GameHost.UserArgs);
        }

        if (!_cameraPlaced && (_shot ?? _view) is not null && tree.CurrentScene is { } shotScene)
            _cameraPlaced = PlaceCamera(shotScene);

        if (_benchmark is { Done: false } benchmark && tree.CurrentScene is { } benchScene && benchmark.Update(tree, benchScene))
            tree.Quit(benchmark.ExitCode);

        if (_autoWalk)
            Walk(tree, gameTime.DeltaTime);
    }

    private void Walk(SceneTree tree, float delta)
    {
        _controller ??= Find(tree.Root);
        if (_controller is null)
            return;
        _walker ??= new PathWalker(ValleyGenerator.Generate().PathPolyline, new Vector2(_controller.GlobalPosition.X, _controller.GlobalPosition.Z));
        _walker.Drive(tree.Input, _controller, delta);
        _frame++;
        if (_lap)
        {
            if (_walker.LapComplete || _frame >= LapFrameLimit)
            {
                var ok = _walker.LapComplete;
                var message = $"[Forest] autowalk-lap: {(ok ? "back at the trailhead" : "stuck")} after {_frame / 60f:0.0} s, {_walker.Distance:0} m walked ({100f * _walker.FloorFrames / Math.Max(_walker.Frames, 1):0} % of frames on the floor), " +
                              $"{_controller.FootstepCount} footsteps, at {_controller.GlobalPosition}.";
                if (ok)
                    Log.Info(message);
                else
                    Log.Error(message);
                PathWalker.Release(tree.Input);
                tree.Quit(ok ? 0 : 1);
            }

            return;
        }

        if (_frame > _warmUp && _frame <= _warmUp + WindowFrames)
        {
            var now = GC.GetAllocatedBytesForCurrentThread();
            _frameAllocations[_frame - _warmUp - 1] = now - _lastAllocated;
            _lastAllocated = now;
        }

        if (_frame == _warmUp)
        {
            _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            _lastAllocated = _allocatedBefore;
            _collectionsBefore = GC.CollectionCount(0);
            _footstepsBefore = _controller.FootstepCount;
        }
        else if (_frame == _warmUp + WindowFrames)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore;
            var collections = GC.CollectionCount(0) - _collectionsBefore;
            var steps = _controller.FootstepCount - _footstepsBefore;
            if (allocated == 0)
                Log.Info($"[Forest] autowalk: {WindowFrames} frames allocated 0 B ({steps} footsteps, last on {_controller.LastFootstepSurface}, {collections} gen-0 GCs).");
            else
            {
                Log.Error($"[Forest] autowalk: {WindowFrames} frames allocated {allocated} B ({steps} footsteps, {collections} gen-0 GCs).");
                var frames = string.Join(", ", _frameAllocations.Select((b, i) => (b, i)).Where(p => p.b != 0).Take(40).Select(p => $"{p.i + _warmUp + 1}:{p.b}"));
                Log.Error($"[Forest] autowalk: allocating frames (frame:bytes) {frames}");
            }
            PathWalker.Release(tree.Input);
            tree.Quit(allocated == 0 ? 0 : 1);
        }
    }

    /// <summary>Puts a camera at the <c>--shot</c> or <c>--view</c> pose; false until the valley exists.</summary>
    private bool PlaceCamera(Node scene)
    {
        if (scene.FindChildren<ForestValley>(owned: false) is not [{ Terrain: { } terrain }, ..])
            return false;
        Vector3 eye, target;
        float fov = 55f;
        if (_shot is not null)
        {
            if (FindShot(_shot) is not { } shot)
            {
                Log.Error($"[Forest] Unknown shot '{_shot}' (1–{ValleyLayout.Shots.Length} or a name).");
                return true;
            }

            eye = new Vector3(shot.Eye.X, terrain.HeightAt(shot.Eye.X, shot.Eye.Y) + shot.EyeHeight, shot.Eye.Y);
            var targetY = shot.AbsoluteTarget ? shot.TargetHeight : terrain.HeightAt(shot.Target.X, shot.Target.Y) + shot.TargetHeight;
            target = new Vector3(shot.Target.X, targetY, shot.Target.Y);
            fov = shot.Fov;
            Log.Info($"[Forest] Shot {shot.Name}: eye {eye}, target {target}.");
        }
        else
        {
            var v = _view!.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length < 6)
            {
                Log.Error("[Forest] --view needs x,y,z,tx,ty,tz.");
                return true;
            }

            // A y of 0 means eye height above the ground (1.65 m; 1.4 m for the target), a negative y that height above it.
            eye = new Vector3(v[0], v[1] > 0f ? v[1] : terrain.HeightAt(v[0], v[2]) + (v[1] < 0f ? -v[1] : 1.65f), v[2]);
            target = new Vector3(v[3], v[4] > 0f ? v[4] : terrain.HeightAt(v[3], v[5]) + (v[4] < 0f ? -v[4] : 1.4f), v[5]);
            if (v.Length > 6)
                fov = v[6];
        }

        var camera = new Camera3D { Name = "ShotCamera", Near = 0.05f, Far = 1200f, Fov = fov, Position = eye };
        scene.AddChild(camera);
        camera.LookAt(target);
        camera.Current = true;
        return true;
    }

    /// <summary>
    /// <c>--set Node/Path.Property=value</c> (repeatable): sets a property of a scene node (or of a resource property on
    /// it, <c>Environment.Sky.Turbidity=8</c>) before the first frame, for tuning the look without rewriting the scene.
    /// Values: numbers, <c>true</c>/<c>false</c>, enum names and comma-separated vectors. <c>Valley.*</c> settings are applied by
    /// <see cref="ForestValley"/> before it generates (<paramref name="valley"/>).
    /// </summary>
    public static void ApplyOverrides(Node scene, IReadOnlyList<string> args, bool valley = false)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--set")
                continue;
            var assignment = args[i + 1];
            if (assignment.StartsWith("Valley.", StringComparison.Ordinal) != valley)
                continue; // the valley applies its own before it builds
            var eq = assignment.IndexOf('=');
            var dot = eq > 0 ? assignment.IndexOf('.') : -1;
            if (dot <= 0 || dot > eq)
            {
                Log.Error($"[Forest] --set {assignment}: expected Node/Path.Property=value.");
                continue;
            }

            var node = scene.GetNodeOrNull<Node>(assignment[..dot]);
            object? target = node;
            var members = assignment[(dot + 1)..eq].Split('.');
            for (var m = 0; m < members.Length - 1 && target is not null; m++)
                target = target.GetType().GetProperty(members[m])?.GetValue(target);
            var property = target?.GetType().GetProperty(members[^1]);
            if (property is null)
            {
                Log.Error($"[Forest] --set {assignment}: no such node or property.");
                continue;
            }

            var text = assignment[(eq + 1)..];
            object value = property.PropertyType switch
            {
                var t when t == typeof(float) => float.Parse(text, CultureInfo.InvariantCulture),
                var t when t == typeof(int) => int.Parse(text, CultureInfo.InvariantCulture),
                var t when t == typeof(bool) => bool.Parse(text),
                var t when t.IsEnum => Enum.Parse(t, text),
                var t when t == typeof(Vector3) => ParseVector3(text),
                var t when t == typeof(System.Drawing.Color) => ParseColor(text),
                _ => text,
            };
            property.SetValue(target, value);
            Log.Info($"[Forest] --set {assignment}");
        }

        static Vector3 ParseVector3(string text)
        {
            var v = text.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(v[0], v[1], v[2]);
        }

        static System.Drawing.Color ParseColor(string text)
        {
            var v = text.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            return System.Drawing.Color.FromArgb(255, v[0], v[1], v[2]);
        }
    }

    public static ReferenceShot? FindShot(string key)
    {
        if (int.TryParse(key, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= ValleyLayout.Shots.Length)
            return ValleyLayout.Shots[n - 1];
        foreach (var shot in ValleyLayout.Shots)
            if (shot.Name == key || shot.Name.StartsWith(key + "-", StringComparison.Ordinal))
                return shot;
        return null;
    }

    private static void Resize(SceneTree tree, Vector2I pixels)
    {
        if (tree.Window is not { } window || window.Size.X <= 0)
            return;
        var scale = tree.Root.Size.X / window.Size.X;
        if (!(scale > 0f))
            scale = 1f;
        window.Size = new Vector2(pixels.X / scale, pixels.Y / scale);
        Log.Info($"[Forest] Window resized to {pixels.X}x{pixels.Y} px ({window.Size.X}x{window.Size.Y} pt).");
    }

    public static Vector2I? ParseResolution(string text)
    {
        var parts = text.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var w) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? new Vector2I(w, h)
            : null;
    }

    private static string? Argument(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    private static FirstPersonController? Find(Node node)
    {
        if (node is FirstPersonController controller)
            return controller;
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (Find(children[i]) is { } found)
                return found;
        }

        return null;
    }
}

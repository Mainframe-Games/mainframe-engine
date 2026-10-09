using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The stream's sound (docs/design/future/water.md#audio, built in the Forest until <c>River3D</c> plays it itself): a
/// babbling-brook loop (<see cref="ForestSoundBank.Brook"/>) on two <see cref="AudioPlayer3D"/>s that follow the
/// <see cref="River"/>, and a louder rushing loop (<see cref="ForestSoundBank.Falls"/>) at each of
/// <see cref="FallPositions"/>, all on the <see cref="ForestAudio.WaterBus"/>.
/// <list type="bullet">
/// <item>Each frame the near emitter goes to the river point closest to the listener (<c>Curve3D.GetClosestOffset</c>),
/// pushed toward the listener by up to half the river's width, so beside the stream the near bank is heard. The far
/// emitter sits <see cref="SpreadMetres"/> further along (towards the longer side), quieter and started at another point
/// of the loop, so the stream sounds like a line rather than a point.</item>
/// <item>Emitters move at most <see cref="MaxEmitterSpeed"/> (30 m/s): where two bends are equally near, the sound
/// glides instead of jumping.</item>
/// <item>Loudness follows the local flow speed: <c>StreamVolumeDb + clamp(20·log10(speed / 1 m/s), −6, +6)</c> dB.</item>
/// </list>
/// Struct maths and property sets on existing players: 0 B per frame.
/// </summary>
public sealed class StreamAudio : Node3D
{
    /// <summary>Fastest an emitter moves along the river (m/s).</summary>
    public const float MaxEmitterSpeed = 30f;

    private AudioPlayer3D? _near;
    private AudioPlayer3D? _far;
    private AudioPlayer3D[] _falls = [];
    private bool _placed;

    /// <summary>The river to follow (code-set; <see cref="ForestAudio"/> resolves it).</summary>
    public River3D? River { get; set; }

    /// <summary>Where falls are (world space): a rushing loop plays at each. <c>River3D</c> has no falls yet.</summary>
    [Export] public Vector3[] FallPositions { get; set; } = [];

    /// <summary>The brook's level at 1 m/s of flow (dB, on top of the Water bus).</summary>
    [Export(Range = "-40,12,0.5")] public float StreamVolumeDb { get; set; } = 0f;

    /// <summary>The far emitter's level relative to the near one.</summary>
    [Export(Range = "-40,0,0.5")] public float FarVolumeDb { get; set; } = -5f;

    /// <summary>How far along the river the far emitter sits from the near one (m).</summary>
    [Export(Range = "0,50,0.5")] public float SpreadMetres { get; set; } = 12f;

    [Export(Range = "-40,12,0.5")] public float FallsVolumeDb { get; set; } = 0f;

    /// <summary>Distance at which the brook is heard at full level (m); the inverse rolloff starts beyond it.</summary>
    [Export(Range = "0.1,50,0.1")] public float UnitSize { get; set; } = 3f;

    /// <summary>The brook is silent beyond this distance (m).</summary>
    [Export(Range = "1,500,1")] public float MaxDistance { get; set; } = 30f;

    [Export(Range = "1,500,1")] public float FallsMaxDistance { get; set; } = 80f;

    public AudioPlayer3D? Near => _near;

    public AudioPlayer3D? Far => _far;

    public IReadOnlyList<AudioPlayer3D> Falls => _falls;

    /// <summary>The arc offset of the near emitter (m along the river).</summary>
    public float NearOffset { get; private set; }

    /// <summary>The flow speed at the near emitter (m/s).</summary>
    public float FlowSpeed { get; private set; }

    protected override void OnReady()
    {
        base.OnReady();
        var bank = ForestSoundBank.Shared;
        _near = Emitter("BrookNear", bank.Brook, UnitSize, MaxDistance);
        _far = Emitter("BrookFar", bank.Brook, UnitSize, MaxDistance);
        _falls = new AudioPlayer3D[FallPositions.Length];
        for (var i = 0; i < _falls.Length; i++)
        {
            _falls[i] = Emitter($"Falls{i}", bank.Falls, 6f, FallsMaxDistance);
            _falls[i].GlobalPosition = FallPositions[i];
            _falls[i].VolumeDb = FallsVolumeDb;
            _falls[i].Play(fromSeconds: i * 2.7f % ForestSynth.FallsSeconds);
        }

        if (River is null)
            return;
        UpdateEmitters(ListenerPosition(), 0f);
        _near.Play();
        _far.Play(fromSeconds: ForestSynth.BrookSeconds * 0.5f); // decorrelated from the near one
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (River is not null && _near is not null)
            UpdateEmitters(ListenerPosition(), gameTime.DeltaTime);
    }

    /// <summary>The audio server's listener, resolved now (its own copy updates after the tree's process step).</summary>
    internal Vector3 ListenerPosition() => ForestAudio.ListenerPosition(this);

    /// <summary>
    /// Moves the emitters for a listener at <paramref name="listener"/> (world space) after <paramref name="deltaSeconds"/>
    /// (0 snaps them) and sets the brook's level from the flow speed. Called every frame; public for tests.
    /// </summary>
    public void UpdateEmitters(Vector3 listener, float deltaSeconds)
    {
        if (River is not { Curve: { } curve } river || _near is null || _far is null)
            return;
        var length = river.Length;
        if (length <= 0f)
            return;

        var global = river.GlobalTransform;
        var local = global.AffineInverse().TransformPoint(listener);
        var offset = curve.GetClosestOffset(local);
        var farOffset = offset + (offset < length * 0.5f ? SpreadMetres : -SpreadMetres);
        farOffset = Math.Clamp(farOffset, 0f, length);

        var snap = deltaSeconds <= 0f || !_placed;
        Place(_near, Target(river, curve, global, offset, listener), snap, deltaSeconds);
        Place(_far, Target(river, curve, global, farOffset, listener), snap, deltaSeconds);
        _placed = true;

        NearOffset = offset;
        FlowSpeed = river.FlowSpeedAt(offset);
        var level = StreamVolumeDb + Math.Clamp(20f * MathF.Log10(MathF.Max(FlowSpeed, 1e-3f)), -6f, 6f);
        _near.VolumeDb = level;
        _far.VolumeDb = level + FarVolumeDb;
    }

    /// <summary>The centreline point at <paramref name="offset"/>, pushed toward the listener by up to half the width.</summary>
    private static Vector3 Target(River3D river, Curve3D curve, in Transform3D global, float offset, Vector3 listener)
    {
        var centre = global.TransformPoint(curve.SampleBaked(offset) with { Y = river.SurfaceHeightAtOffset(offset) });
        var halfWidth = curve.SampleBakedWidth(offset) * 0.5f;
        var across = new Vector3(listener.X - centre.X, 0f, listener.Z - centre.Z);
        var distance = across.Length();
        return distance > 1e-4f ? centre + across * (MathF.Min(distance, halfWidth) / distance) : centre;
    }

    private static void Place(AudioPlayer3D emitter, Vector3 target, bool snap, float deltaSeconds)
    {
        if (snap)
        {
            emitter.GlobalPosition = target;
            return;
        }

        var current = emitter.GlobalPosition;
        var step = target - current;
        var max = MaxEmitterSpeed * deltaSeconds;
        var distance = step.Length();
        emitter.GlobalPosition = distance <= max ? target : current + step * (max / distance);
    }

    private AudioPlayer3D Emitter(string name, AudioStream stream, float unitSize, float maxDistance)
    {
        var player = new AudioPlayer3D
        {
            Name = name,
            Stream = stream,
            Bus = ForestAudio.WaterBus,
            Loop = true,
            AttenuationModel = AttenuationModel.Inverse,
            UnitSize = unitSize,
            MaxDistance = maxDistance,
            LowPassAtMaxDistance = 2500f, // far water is a muffled hush
        };
        AddChild(player);
        return player;
    }

    /// <summary>
    /// Points along <paramref name="river"/> (world space, on the surface) where it drops more steeply than
    /// <paramref name="minSlope"/> (rise over run), at most one per <paramref name="spacing"/> metres: candidate
    /// <see cref="FallPositions"/> until <c>River3D</c> has falls of its own.
    /// </summary>
    public static Vector3[] FindSteepPoints(River3D river, float minSlope = 0.25f, float spacing = 15f)
    {
        var points = new List<Vector3>();
        if (river.Curve is not { } curve)
            return [];
        var length = river.Length;
        var global = river.GlobalTransform;
        var last = float.NegativeInfinity;
        const float step = 1f;
        for (var s = 0f; s + step <= length; s += step)
        {
            var drop = river.SurfaceHeightAtOffset(s) - river.SurfaceHeightAtOffset(s + step);
            if (drop / step < minSlope || s - last < spacing)
                continue;
            var mid = s + step * 0.5f;
            points.Add(global.TransformPoint(curve.SampleBaked(mid) with { Y = river.SurfaceHeightAtOffset(mid) }));
            last = s;
        }

        return [.. points];
    }
}

using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Footstep sounds for a <see cref="FirstPersonController"/> (the Forest's <c>FootstepAudio</c>): listens to its
/// <see cref="FirstPersonController.Footstep"/> signal and plays one of the surface's
/// <see cref="NeonSoundBank.FootstepVariants"/> synthesised steps (concrete, metal, tile, wood, rug; unknown names map to
/// the nearest) at the feet on <see cref="Bus"/> (<see cref="NeonAudio"/> switches it to the reverberant Bathroom bus in
/// the bathroom). Never the same variation twice in a row; each step varies in pitch and level; crouching is quieter and
/// sprinting louder. Two players alternate so a step's tail is not cut by the next. 0 B per step.
/// </summary>
public sealed class NeonFootstepAudio : Node3D
{
    private readonly AudioPlayer3D[] _players = new AudioPlayer3D[2];
    private FirstPersonController? _subscribed;
    private NeonRandom _rng = new(0x57E95UL);
    private AudioStream[]? _lastSet;
    private int _lastIndex = -1;
    private int _next;

    /// <summary>The controller whose steps play (default: the parent).</summary>
    public FirstPersonController? Player { get; set; }

    /// <summary>The bus the next steps play on.</summary>
    public string Bus { get; set; } = NeonAudio.FoleyBus;

    [Export(Range = "-40,12,0.5")] public float VolumeDb { get; set; } = 0f;

    [Export(Range = "0,12,0.1")] public float VolumeSpreadDb { get; set; } = 1.5f;

    [Export(Range = "-30,0,0.5")] public float CrouchVolumeDb { get; set; } = -6f;

    [Export(Range = "0,12,0.5")] public float SprintVolumeDb { get; set; } = 2f;

    /// <summary>Steps played so far.</summary>
    public int Played { get; private set; }

    /// <summary>The stream of the last step.</summary>
    public AudioStream? LastStream { get; private set; }

    public IReadOnlyList<AudioPlayer3D> Players => _players;

    protected override void OnReady()
    {
        base.OnReady();
        NeonSoundBank.Shared.Footsteps("default"); // synthesise the sets now, not at the first step
        for (var i = 0; i < _players.Length; i++)
        {
            _players[i] = new AudioPlayer3D
            {
                Name = $"Step{i}",
                Bus = Bus,
                UnitSize = 2.5f, // the ears are ≈ 1.6 m above the feet: full level, centred
                PanningStrength = 0.3f,
            };
            AddChild(_players[i]);
        }
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _subscribed = Player ?? Parent as FirstPersonController;
        if (_subscribed is not null)
            _subscribed.Footstep += OnFootstep;
    }

    protected override void OnExitTree()
    {
        if (_subscribed is not null)
            _subscribed.Footstep -= OnFootstep;
        _subscribed = null;
        base.OnExitTree();
    }

    /// <summary>Plays a step on <paramref name="surface"/> (the signal handler; public for tests).</summary>
    public void OnFootstep(string surface)
    {
        var set = NeonSoundBank.Shared.Footsteps(surface);
        var index = _rng.Range(0, set.Length);
        if (ReferenceEquals(set, _lastSet) && index == _lastIndex)
            index = (index + 1 + _rng.Range(0, set.Length - 1)) % set.Length;
        _lastSet = set;
        _lastIndex = index;

        var player = _players[_next];
        _next = (_next + 1) % _players.Length;
        if (player is null)
            return;
        var gait = _subscribed is { IsCrouching: true } ? CrouchVolumeDb : _subscribed is { IsSprinting: true } ? SprintVolumeDb : 0f;
        player.Bus = Bus;
        player.Stream = set[index];
        player.VolumeDb = VolumeDb + gait + _rng.Range(-VolumeSpreadDb, VolumeSpreadDb);
        player.Position = Vector3.Zero;
        player.Play();
        LastStream = set[index];
        Played++;
    }
}

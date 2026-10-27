using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Neon's soundscape, all synthesised (<see cref="NeonSoundBank"/>, <see cref="NeonMusic"/>): a root node of the scene
/// that, when ready, finds the <see cref="FirstPersonController"/> and the <see cref="NeonApartment"/> (or uses
/// <see cref="PlayerPath"/>/<see cref="ApartmentPath"/>) and builds, as generated (unsaved) children:
/// <list type="bullet">
/// <item>the beds: the rain and the city, each an outdoor and an indoor (muffled) loop, crossfaded (equal power) by
/// <see cref="Openness"/>: 0 inside the apartment's box (x −7..7, y 0..6, z −5.5..5.5 in apartment space), 1 on the
/// balcony (z 5.5..8) and beyond, eased over <see cref="Response"/> seconds; inside, the open balcony door lets in up to
/// <see cref="SliderLeak"/> near it. (Bus effects are fixed while a layout plays, so "muffled" is a second loop, not a
/// moving filter.)</item>
/// <item>the city's events: distant horns, sirens and PA chimes (<see cref="CityEventInterval"/>), and flying cars
/// passing the balcony with Doppler (<see cref="FlyByInterval"/>); these and every emitter registered with
/// <see cref="RegisterOutdoor"/> are low-passed while the listener is inside (each voice's distance low-pass, pinned to
/// the emitter's distance);</item>
/// <item>footsteps (<see cref="NeonFootstepAudio"/> on the player; the Bathroom bus in the bathroom), the fridge's hum
/// and the computer room's fans, and the stereo (<see cref="NeonMusicPlayer"/>, muffled by room and doors);</item>
/// <item>the interactables' sounds: doors (<see cref="DoorBase.Toggled"/>, <see cref="DoorBase.Settled"/>), the front
/// door's buzz (<see cref="FrontDoor.Rattled"/>) and the switches (<see cref="LightSwitch.Switched"/>).</item>
/// </list>
/// Interactables call the static <see cref="PlayAt"/> (one-shots from a pool of positional players) and
/// <see cref="CreateLoop"/> (an emitter they own). Property sets on existing players: 0 B per frame.
/// </summary>
public sealed class NeonAudio : Node
{
    /// <summary>The city's drone and events, the train and airships.</summary>
    public const string AmbienceBus = "Ambience";

    /// <summary>The rain beds.</summary>
    public const string RainBus = "Rain";

    /// <summary>Footsteps, doors, switches, the machines (a light room reverb).</summary>
    public const string FoleyBus = "Foley";

    /// <summary>The bathroom's sounds (a tiled room's reverb): the shower, the bath tap, steps in there.</summary>
    public const string BathroomBus = "Bathroom";

    /// <summary>The stereo.</summary>
    public const string MusicBus = "Music";

    /// <summary>The TV.</summary>
    public const string TvBus = "Tv";

    /// <summary>Neon's buses (<c>Assets/Settings/AudioBusLayout.mres</c>), Master first.</summary>
    public static readonly string[] Buses = [AudioBusLayout.MasterBus, AmbienceBus, RainBus, FoleyBus, BathroomBus, MusicBus, TvBus];

    /// <summary>The apartment's interior (apartment space): inside it the world outside is muffled.</summary>
    public static readonly Vector3 InsideMin = new(-7f, 0f, -5.5f), InsideMax = new(7f, 6f, 5.5f);

    /// <summary>The balcony (apartment space): fully open.</summary>
    public static readonly Vector3 BalconyMin = new(-7f, -1f, 5.5f), BalconyMax = new(7f, 6f, 8f);

    /// <summary>One-shot players in the <see cref="PlayAt"/> pool.</summary>
    public const int PoolSize = 10;

    private const int CityPoolSize = 3;
    private const float IndoorCutoff = 800f;
    private const float OutdoorCutoff = 9000f;

    private static NeonAudio? s_current;

    private readonly AudioPlayer3D[] _pool = new AudioPlayer3D[PoolSize];
    private readonly bool[] _poolOutdoor = new bool[PoolSize];
    private readonly AudioStream?[] _lastVariant = new AudioStream?[NeonSoundBank.All.Count];
    private readonly AudioPlayer3D[] _cityPool = new AudioPlayer3D[CityPoolSize];
    private readonly List<AudioPlayer3D> _outdoor = [];
    private readonly List<Vector3> _outdoorLast = [];
    private readonly List<Action> _unhook = [];
    private readonly List<(string Room, Vector3 Min, Vector3 Max)> _zones = [];
    private AudioPlayer? _rainOut, _rainIn, _cityOut, _cityIn;
    private AudioPlayer3D? _flyBy;
    private NeonRandom _rng;
    private int _nextPool;
    private float _cityTimer, _flyByTimer, _flyBySpeed;
    private bool _hooked;
    private float _openness = -1f;

    /// <summary>The player whose footsteps play; empty: the first <see cref="FirstPersonController"/> in the scene.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>The apartment; empty: the first <see cref="NeonApartment"/> in the scene.</summary>
    [Export] public NodePath ApartmentPath { get; set; } = "";

    /// <summary>The rain's level (dB, on top of the Rain bus; the indoor loop is quieter by its own level).</summary>
    [Export(Range = "-40,12,0.5")] public float RainVolumeDb { get; set; } = 0f;

    /// <summary>The city drone's level (dB, on top of the Ambience bus).</summary>
    [Export(Range = "-40,12,0.5")] public float CityVolumeDb { get; set; } = 0f;

    /// <summary>Seconds the indoor/outdoor mix takes to follow the listener (one-pole time constant).</summary>
    [Export(Range = "0,5,0.05")] public float Response { get; set; } = 0.35f;

    /// <summary>How open it sounds inside next to the fully open balcony door (0–1).</summary>
    [Export(Range = "0,1,0.01")] public float SliderLeak { get; set; } = 0.35f;

    /// <summary>Mean seconds between distant horns, sirens and PA chimes (0: none).</summary>
    [Export(Range = "0,120,0.5")] public float CityEventInterval { get; set; } = 9f;

    /// <summary>Mean seconds between flying cars passing the balcony (0: none).</summary>
    [Export(Range = "0,300,1")] public float FlyByInterval { get; set; } = 30f;

    /// <summary>Render the stereo's stems when ready (else at its first play).</summary>
    [Export] public bool PreloadMusic { get; set; } = true;

    /// <summary>The seed of the city's schedule.</summary>
    [Export] public int Seed { get; set; } = 0x4E656F;

    /// <summary>The running scene's audio (the static API's target), null when none is in a tree.</summary>
    public static NeonAudio? Current => s_current;

    public FirstPersonController? Player { get; set; }

    public NeonApartment? Apartment { get; set; }

    /// <summary>0 (inside, muffled) to 1 (the open air), eased.</summary>
    public float Openness => MathF.Max(_openness, 0f);

    /// <summary>The room the listener is in (<c>Living</c>, <c>Bath</c>, ...; the apartment's <c>Zone_*</c> names), null outside them.</summary>
    public string? Room { get; private set; }

    public NeonFootstepAudio? Footsteps { get; private set; }

    public NeonMusicPlayer? Music { get; private set; }

    public AudioPlayer3D? Fridge { get; private set; }

    public IReadOnlyList<AudioPlayer3D> PcFans { get; private set; } = [];

    public AudioPlayer? RainOutdoor => _rainOut;

    public AudioPlayer? RainIndoor => _rainIn;

    public AudioPlayer? CityOutdoor => _cityOut;

    public AudioPlayer? CityIndoor => _cityIn;

    public AudioPlayer3D? FlyBy => _flyBy;

    public IReadOnlyList<AudioPlayer3D> Pool => _pool;

    /// <summary>Distant city events triggered so far.</summary>
    public int CityEvents { get; private set; }

    /// <summary>Flying cars that have passed so far.</summary>
    public int FlyBys { get; private set; }

    /// <summary>The last <see cref="PlayAt"/> player and its sound (tests, dev tools).</summary>
    public (NeonSound Sound, AudioPlayer3D? Player) LastPlayed { get; private set; }

    /// <summary>Stems for the stereo (null: <see cref="NeonMusic.CacheFolder"/>; tests point it at a scratch folder).</summary>
    public string? MusicFolder { get; set; }

    /// <summary>Load the stereo's songs into memory instead of streaming them (tests: exact, no streaming thread).</summary>
    public bool MusicInMemory { get; set; }

    // ── The API for interactables ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plays a one-shot of <paramref name="sound"/> (a random variation, never the same twice running) at
    /// <paramref name="worldPosition"/> on <paramref name="bus"/> (null: <see cref="NeonSoundBank.DefaultBus"/>) from the
    /// running <see cref="Current"/> audio's pool; does nothing without one. 0 B.
    /// </summary>
    public static void PlayAt(NeonSound sound, Vector3 worldPosition, float volumeDb = 0f, string? bus = null) =>
        s_current?.Play(sound, worldPosition, volumeDb, bus);

    /// <summary>
    /// A positional looping emitter of <paramref name="sound"/> under <paramref name="parent"/> at
    /// <paramref name="localPosition"/>, on <paramref name="bus"/> (null: <see cref="NeonSoundBank.DefaultBus"/>), with the
    /// sound's falloff (<see cref="NeonSoundBank.Emitter"/>). The caller owns it: <c>Play()</c> / <c>Stop()</c> it (it
    /// does not autoplay). Outdoor sounds (the train, airships) are registered for the indoor muffling. Allocates (call
    /// it when building, not per frame).
    /// </summary>
    public static AudioPlayer3D CreateLoop(NeonSound sound, Node3D parent, string? bus = null, Vector3 localPosition = default)
    {
        var player = CreatePlayer(sound, parent, bus, localPosition);
        player.Loop = true;
        return player;
    }

    /// <summary>
    /// As <see cref="CreateLoop"/> but plays <paramref name="sound"/> once per <c>Play()</c> (a flying car's pass on a
    /// traffic node; set <see cref="AudioPlayer3D.DopplerTracking"/> on a moving one).
    /// </summary>
    public static AudioPlayer3D CreatePlayer(NeonSound sound, Node3D parent, string? bus = null, Vector3 localPosition = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var (unit, max) = NeonSoundBank.Emitter(sound);
        var player = new AudioPlayer3D
        {
            Name = sound.ToString(),
            Stream = NeonSoundBank.Shared[sound],
            Bus = bus ?? NeonSoundBank.DefaultBus(sound),
            AttenuationModel = AttenuationModel.Inverse,
            UnitSize = unit,
            MaxDistance = max,
            Position = localPosition,
        };
        parent.AddChild(player);
        if (NeonSoundBank.IsOutdoor(sound))
            s_current?.RegisterOutdoor(player);
        return player;
    }

    /// <summary>Muffles <paramref name="emitter"/> (an outdoor sound) while the listener is inside. Freed emitters drop out by themselves.</summary>
    public void RegisterOutdoor(AudioPlayer3D emitter)
    {
        if (_outdoor.Contains(emitter))
            return;
        _outdoor.Add(emitter);
        _outdoorLast.Add(emitter.IsInsideTree ? emitter.GlobalPosition : emitter.Position);
    }

    public void UnregisterOutdoor(AudioPlayer3D emitter)
    {
        var index = _outdoor.IndexOf(emitter);
        if (index < 0)
            return;
        _outdoor.RemoveAt(index);
        _outdoorLast.RemoveAt(index);
    }

    /// <summary>The instance side of <see cref="PlayAt"/>.</summary>
    public void Play(NeonSound sound, Vector3 worldPosition, float volumeDb = 0f, string? bus = null)
    {
        var slot = FreeSlot();
        var player = _pool[slot];
        if (player is null)
            return;
        var (unit, max) = NeonSoundBank.Emitter(sound);
        player.Stream = PickVariant(sound);
        player.Bus = bus ?? NeonSoundBank.DefaultBus(sound);
        player.VolumeDb = volumeDb;
        player.UnitSize = unit;
        player.MaxDistance = max;
        player.LowPassAtMaxDistance = 0f;
        player.GlobalPosition = worldPosition;
        _poolOutdoor[slot] = NeonSoundBank.IsOutdoor(sound);
        if (_poolOutdoor[slot])
            Muffle(player, ListenerPosition(this));
        player.Play();
        LastPlayed = (sound, player);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (Tree?.EditMode != true)
            s_current = this;
    }

    protected override void OnExitTree()
    {
        if (ReferenceEquals(s_current, this))
            s_current = null;
        foreach (var unhook in _unhook)
            unhook();
        _unhook.Clear();
        _hooked = false;
        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        _rng = new NeonRandom((ulong)Seed);
        var scene = Owner ?? Parent ?? this;
        Player ??= Resolve<FirstPersonController>(scene, PlayerPath);
        Apartment ??= Resolve<NeonApartment>(scene, ApartmentPath);
        var bank = NeonSoundBank.Shared;

        _rainOut = Bed("RainOutdoor", bank[NeonSound.RainOutdoor], RainBus);
        _rainIn = Bed("RainIndoor", bank[NeonSound.RainIndoor], RainBus);
        _cityOut = Bed("CityOutdoor", bank[NeonSound.CityDrone], AmbienceBus);
        _cityIn = Bed("CityIndoor", bank[NeonSound.CityDroneIndoor], AmbienceBus);

        for (var i = 0; i < _pool.Length; i++)
        {
            _pool[i] = new AudioPlayer3D { Name = $"OneShot{i}", AttenuationModel = AttenuationModel.Inverse };
            AddChild(_pool[i]);
        }

        for (var i = 0; i < _cityPool.Length; i++)
        {
            _cityPool[i] = new AudioPlayer3D { Name = $"CityEvent{i}", Bus = AmbienceBus, AttenuationModel = AttenuationModel.Inverse, UnitSize = 30f };
            AddChild(_cityPool[i]);
        }

        _flyBy = new AudioPlayer3D
        {
            Name = "FlyBy",
            Stream = bank[NeonSound.FlyingCar],
            Bus = AmbienceBus,
            AttenuationModel = AttenuationModel.Inverse,
            UnitSize = 6f,
            DopplerTracking = true,
        };
        AddChild(_flyBy);

        if (Player is not null)
        {
            Footsteps = new NeonFootstepAudio { Name = "FootstepAudio", Player = Player };
            Player.AddChild(Footsteps); // at the feet, unowned
        }

        Music = new NeonMusicPlayer { Name = "Stereo", PreloadMusic = PreloadMusic, StemFolder = MusicFolder, InMemory = MusicInMemory };
        AddChild(Music);

        _cityTimer = CityEventInterval > 0f ? _rng.Exponential(CityEventInterval) : 0f;
        _flyByTimer = FlyByInterval > 0f ? MathF.Max(_rng.Exponential(FlyByInterval), 6f) : 0f;
        Hook();
        Update(0f);
        _rainOut.Play();
        _rainIn.Play(fromSeconds: 3.1f);
        _cityOut.Play(fromSeconds: 7.3f);
        _cityIn.Play(fromSeconds: 1.7f);
        Log.Info($"[Neon] Audio: player {Player?.Name ?? "none"}, apartment {Apartment?.Name ?? "none"}, {_unhook.Count} hooks.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_hooked)
            Hook(); // the apartment may build after this node is ready
        Update(gameTime.DeltaTime);
    }

    /// <summary>
    /// One frame of the mix after <paramref name="deltaSeconds"/> (0 snaps): where the listener is, the beds' crossfade,
    /// the muffling of outdoor emitters, the footsteps' bus, the stereo's muffle, the city's schedule and the fly-by.
    /// </summary>
    public void Update(float deltaSeconds)
    {
        var listener = ListenerPosition(this);
        var local = ToApartment(listener);
        Room = RoomAt(local);
        var target = OpennessAt(local);
        _openness = _openness < 0f || deltaSeconds <= 0f || Response <= 0f
            ? target
            : _openness + (target - _openness) * (1f - MathF.Exp(-deltaSeconds / Response));
        var open = _openness;

        // Equal power: uncorrelated beds keep their loudness through the crossfade.
        var outGain = MathF.Sin(open * MathF.PI / 2f);
        var inGain = MathF.Cos(open * MathF.PI / 2f);
        if (_rainOut is not null && _rainIn is not null && _cityOut is not null && _cityIn is not null)
        {
            _rainOut.VolumeDb = RainVolumeDb + Db(outGain);
            _rainIn.VolumeDb = RainVolumeDb + Db(inGain);
            _cityOut.VolumeDb = CityVolumeDb + Db(outGain);
            _cityIn.VolumeDb = CityVolumeDb + Db(inGain);
        }

        for (var i = _outdoor.Count - 1; i >= 0; i--)
        {
            var emitter = _outdoor[i];
            if (!Node.IsInstanceValid(emitter))
            {
                _outdoor.RemoveAt(i);
                _outdoorLast.RemoveAt(i);
            }
            else if (emitter.IsInsideTree)
            {
                // A moving emitter (a train on its path) may move again before the server hears it: allow for a frame's travel.
                var position = emitter.GlobalPosition;
                Muffle(emitter, listener, Vector3.Distance(position, _outdoorLast[i]));
                _outdoorLast[i] = position;
            }
        }

        for (var i = 0; i < _pool.Length; i++)
            if (_poolOutdoor[i] && _pool[i] is { Playing: true } p)
                Muffle(p, listener);
        foreach (var p in _cityPool)
            if (p is { Playing: true })
                Muffle(p, listener);

        if (Footsteps is not null)
            Footsteps.Bus = Room == "Bath" ? BathroomBus : FoleyBus;
        if (Music is not null && _zones.Count > 0) // without an apartment the stereo's muffle is the caller's
            Music.Muffle = MusicMuffle(Room);

        if (deltaSeconds > 0f)
        {
            AdvanceCity(deltaSeconds, listener);
            AdvanceFlyBy(deltaSeconds, listener);
        }
    }

    /// <summary>How open it sounds at <paramref name="local"/> (apartment space), before easing.</summary>
    public float OpennessAt(Vector3 local)
    {
        if (Inside(local, InsideMin, InsideMax))
        {
            if (Apartment?.Doors.GetValueOrDefault("Door_Balcony") is DoorBase { Openness: > 0f } slider)
            {
                var near = Math.Clamp(1f - Vector3.Distance(local, ToApartment(slider.GlobalPosition)) / 8f, 0f, 1f);
                return SliderLeak * slider.Openness * near;
            }

            return 0f;
        }

        return 1f;
    }

    /// <summary>
    /// The stereo's muffle for a listener in <paramref name="room"/>: none in the open-plan living room, kitchen and entry,
    /// a little up on the loft, a lot behind a closed door (half with it open), all of it outside.
    /// </summary>
    public float MusicMuffle(string? room) => room switch
    {
        "Living" or "Kitchen" or "Entry" => 0f,
        "Loft" => 0.25f,
        "Bath" => DoorOpen("Door_Bath") ? 0.5f : 0.9f,
        "Computer" => DoorOpen("Door_Computer") ? 0.5f : 0.9f,
        "Bed2" => DoorOpen("Door_Bed2") ? 0.55f : 0.92f,
        "Balcony" => DoorOpen("Door_Balcony") ? 0.4f : 0.85f,
        _ => 1f,
    };

    private bool DoorOpen(string name) => Apartment?.Doors.GetValueOrDefault(name) is DoorBase { Openness: > 0.2f };

    private string? RoomAt(Vector3 local)
    {
        foreach (var (room, min, max) in _zones)
            if (Inside(local, min, max))
                return room;
        return null;
    }

    private static bool Inside(Vector3 p, Vector3 min, Vector3 max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

    private Vector3 ToApartment(Vector3 world) =>
        Apartment is { IsInsideTree: true } apartment ? apartment.GlobalTransform.AffineInverse().TransformPoint(world) : world;

    private Vector3 FromApartment(Vector3 local) =>
        Apartment is { IsInsideTree: true } apartment ? apartment.GlobalTransform.TransformPoint(local) : local;

    /// <summary>The cutoff outdoor emitters get for the current <see cref="Openness"/>: ≈ 800 Hz inside, 9 kHz in the open.</summary>
    public float OutdoorCutoffNow => IndoorCutoff * MathF.Pow(OutdoorCutoff / IndoorCutoff, Openness);

    /// <summary>
    /// The voice low-pass an <see cref="AudioPlayer3D"/> at <paramref name="distance"/> gets from the engine: 20 kHz at its
    /// unit size, falling linearly to <see cref="AudioPlayer3D.LowPassAtMaxDistance"/> at its max distance (0: none).
    /// </summary>
    public static float EffectiveCutoff(AudioPlayer3D emitter, float distance)
    {
        if (emitter.LowPassAtMaxDistance <= 0f)
            return 20000f;
        var unit = MathF.Max(emitter.UnitSize, 1e-3f);
        var range = emitter.MaxDistance > unit ? emitter.MaxDistance : 100f * unit;
        var t = Math.Clamp((distance - unit) / (range - unit), 0f, 1f);
        return 20000f + (MathF.Min(emitter.LowPassAtMaxDistance, 20000f) - 20000f) * t;
    }

    /// <summary>
    /// Muffles an outdoor emitter with its voice's distance low-pass (the only filter that moves live): its max distance is
    /// set just beyond where it is, and the cutoff at the max aimed so that at the emitter's distance the engine's linear
    /// interpolation lands on <see cref="OutdoorCutoffNow"/>. The margin (4 % of the way past the unit size, plus twice the
    /// emitter's last frame of travel) keeps the emitter inside its max distance, where it is never cut off; it also
    /// limits how low the cutoff can go (≈ 800 Hz), which is why <see cref="IndoorCutoff"/> is that.
    /// </summary>
    private void Muffle(AudioPlayer3D emitter, Vector3 listener, float movement = 0f)
    {
        var unit = MathF.Max(emitter.UnitSize, 1e-3f);
        var d = Vector3.Distance(emitter.GlobalPosition, listener);
        var max = MathF.Max(d, unit) + MathF.Max(0.04f * (d - unit), 0.3f) + 2f * movement;
        emitter.MaxDistance = max;
        var t = Math.Clamp((d - unit) / (max - unit), 0f, 1f);
        var target = OutdoorCutoffNow;
        emitter.LowPassAtMaxDistance = t > 0.01f ? MathF.Max(20000f - (20000f - target) / t, 20f) : target;
    }

    // ── The city's events ─────────────────────────────────────────────────────────────────────────

    private void AdvanceCity(float dt, Vector3 listener)
    {
        if (CityEventInterval <= 0f)
            return;
        _cityTimer -= dt;
        if (_cityTimer > 0f)
            return;
        _cityTimer = MathF.Max(_rng.Exponential(CityEventInterval), 1.5f);
        TriggerCityEvent(listener);
    }

    /// <summary>Plays a horn (most often), a siren or a PA chime somewhere down in the streets (public for tests).</summary>
    public void TriggerCityEvent(Vector3 listener)
    {
        AudioPlayer3D? player = null;
        foreach (var p in _cityPool)
            if (p is { Playing: false })
            {
                player = p;
                break;
            }

        if (player is null)
            return;
        var roll = _rng.NextFloat();
        var sound = roll < 0.6f ? NeonSound.Horn : roll < 0.88f ? NeonSound.Siren : NeonSound.PaChime;
        // In front of the window wall (+Z), twenty storeys down: the streets and the towers across.
        var local = new Vector3(_rng.Range(-160f, 160f), _rng.Range(-70f, -15f), _rng.Range(30f, 220f));
        player.Stream = PickVariant(sound);
        player.VolumeDb = sound == NeonSound.Siren ? 2f : 4f;
        player.GlobalPosition = FromApartment(local);
        Muffle(player, listener);
        player.Play();
        CityEvents++;
    }

    private void AdvanceFlyBy(float dt, Vector3 listener)
    {
        if (_flyBy is null)
            return;
        if (_flyBy.Playing)
        {
            var local = ToApartment(_flyBy.GlobalPosition);
            local.X += _flyBySpeed * dt;
            _flyBy.GlobalPosition = FromApartment(local);
            Muffle(_flyBy, listener);
            return;
        }

        if (FlyByInterval <= 0f)
            return;
        _flyByTimer -= dt;
        if (_flyByTimer > 0f)
            return;
        _flyByTimer = MathF.Max(_rng.Exponential(FlyByInterval), 8f);
        StartFlyBy(listener);
    }

    /// <summary>
    /// Sends a flying car past the balcony: 270 m across in front of the window wall at 45 m/s (the sound's length), 15–30
    /// m out and level with the apartment or a little above, either way (public for tests).
    /// </summary>
    public void StartFlyBy(Vector3 listener)
    {
        if (_flyBy is null)
            return;
        var direction = _rng.NextFloat() < 0.5f ? 1f : -1f;
        _flyBySpeed = 270f / NeonSynth.FlyingCarSeconds * direction;
        var local = new Vector3(-135f * direction, _rng.Range(1f, 10f), _rng.Range(23f, 38f));
        _flyBy.GlobalPosition = FromApartment(local);
        _flyBy.VolumeDb = _rng.Range(-3f, 0f);
        Muffle(_flyBy, listener);
        _flyBy.Play();
        FlyBys++;
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Subscribes to the apartment's doors and switches and starts its machines (once the apartment is built).</summary>
    private void Hook()
    {
        if (_hooked || Apartment is not { } apartment || apartment.Doors.Count == 0)
            return;
        _hooked = true;
        foreach (var (name, socket) in apartment.Sockets)
        {
            if (!name.StartsWith("Zone_", StringComparison.Ordinal))
                continue;
            var centre = socket.GlobalPosition;
            var local = ToApartment(centre);
            var half = Vector3.Abs(socket.Scale);
            _zones.Add((name["Zone_".Length..], new Vector3(local.X - half.X, local.Y, local.Z - half.Z), new Vector3(local.X + half.X, local.Y + half.Y, local.Z + half.Z)));
        }

        // Small rooms first, the big living room (which overlaps the kitchen and entry) last.
        _zones.Sort((a, b) => Volume(a.Min, a.Max).CompareTo(Volume(b.Min, b.Max)));

        foreach (var door in apartment.Doors.Values)
        {
            switch (door)
            {
                case SlidingDoor slider:
                    {
                        void OnSlide(bool open) => PlayAt(open ? NeonSound.SliderOpen : NeonSound.SliderClose, slider.GlobalPosition + new Vector3(0, 1.1f, 0));
                        slider.Toggled += OnSlide;
                        _unhook.Add(() => slider.Toggled -= OnSlide);
                        break;
                    }
                case DoorBase hinged:
                    {
                        void OnToggle(bool open)
                        {
                            if (open)
                                PlayAt(NeonSound.DoorOpen, hinged.GlobalPosition + new Vector3(0, 1.1f, 0));
                        }

                        void OnSettle(bool open)
                        {
                            if (!open)
                                PlayAt(NeonSound.DoorClose, hinged.GlobalPosition + new Vector3(0, 1.1f, 0));
                        }

                        hinged.Toggled += OnToggle;
                        hinged.Settled += OnSettle;
                        _unhook.Add(() =>
                        {
                            hinged.Toggled -= OnToggle;
                            hinged.Settled -= OnSettle;
                        });
                        break;
                    }
                case FrontDoor front:
                    {
                        void OnRattle() => PlayAt(NeonSound.LockedBuzz, front.GlobalPosition + new Vector3(0, 1.3f, 0));
                        front.Rattled += OnRattle;
                        _unhook.Add(() => front.Rattled -= OnRattle);
                        break;
                    }
            }
        }

        foreach (var lightSwitch in apartment.Switches.Values)
        {
            void OnSwitch(bool on) => PlayAt(NeonSound.SwitchClick, lightSwitch.GlobalPosition);
            lightSwitch.Switched += OnSwitch;
            _unhook.Add(() => lightSwitch.Switched -= OnSwitch);
        }

        if (Fridge is null && MeshCentre(apartment, "Furn_Fridge") is { } fridge)
        {
            Fridge = CreateLoop(NeonSound.FridgeHum, apartment, FoleyBus, ToApartment(fridge));
            Fridge.Play(fromSeconds: 2.3f);
        }

        if (PcFans.Count == 0)
        {
            var fans = new List<AudioPlayer3D>();
            foreach (var rack in new[] { "Furn_Rack1", "Furn_Rack2" })
            {
                if (MeshCentre(apartment, rack) is not { } centre)
                    continue;
                var fan = CreateLoop(NeonSound.PcFans, apartment, FoleyBus, ToApartment(centre));
                fan.VolumeDb = -2f;
                fan.Play(fromSeconds: fans.Count * 4.1f);
                fans.Add(fan);
            }

            PcFans = fans;
        }

        if (Music is not null && apartment.Sockets.TryGetValue("Stereo", out var stereo))
            Music.StereoPosition = stereo.GlobalPosition;
        HookAppliances(apartment);
    }

    /// <summary>The shower, taps, TV, stereo, blinds and the computer's keyboard (phase N2/N4 interactables).</summary>
    private void HookAppliances(NeonApartment apartment)
    {
        void Water(Appliance appliance, NeonSound sound, string bus, Vector3 offset)
        {
            var loop = CreateLoop(sound, appliance, bus, offset);
            void OnSwitched(bool on)
            {
                if (on)
                    loop.Play();
                else
                    loop.Stop();
            }

            appliance.Switched += OnSwitched;
            _unhook.Add(() => appliance.Switched -= OnSwitched);
            if (appliance.IsOn)
                loop.Play();
        }

        if (apartment.Shower is { } shower)
            Water(shower, NeonSound.Shower, BathroomBus, new Vector3(0f, -1.2f, 0f));
        foreach (var (room, tap) in apartment.Taps)
            Water(tap, NeonSound.Tap, room == "Bath" ? BathroomBus : FoleyBus, new Vector3(0f, -0.2f, 0f));

        if (apartment.Tv is { } tv)
        {
            var news = CreateLoop(NeonSound.TvStatic, tv, TvBus);
            news.VolumeDb = -14f;
            var jingle = CreateLoop(NeonSound.TvJingle, tv, TvBus);
            void Channel()
            {
                news.Stop();
                jingle.Stop();
                if (!tv.IsOn)
                    return;
                if (tv.Channel == 0)
                    news.Play();
                else
                    jingle.Play();
                jingle.PitchScale = tv.Channel == 2 ? 0.84f : 1f; // the music video: the jingle, slowed into a groove
            }

            void OnSwitched(bool on)
            {
                PlayAt(NeonSound.TvBlip, tv.GlobalPosition, bus: TvBus);
                Channel();
            }

            void OnChannel(int channel)
            {
                PlayAt(NeonSound.TvBlip, tv.GlobalPosition, bus: TvBus);
                Channel();
            }

            tv.Switched += OnSwitched;
            tv.ChannelChanged += OnChannel;
            _unhook.Add(() =>
            {
                tv.Switched -= OnSwitched;
                tv.ChannelChanged -= OnChannel;
            });
            Channel();
        }

        if (apartment.Stereo is { } deck && Music is { } music)
        {
            void OnTrack(int track)
            {
                if (track < 0)
                    music.Stop();
                else
                    music.Play(track % music.SongCount);
            }

            deck.TrackChanged += OnTrack;
            _unhook.Add(() => deck.TrackChanged -= OnTrack);
            deck.TrackCount = music.SongCount;
        }

        foreach (var panel in apartment.BlindsPanels.Values)
        {
            void OnBlinds(bool open) => PlayAt(open ? NeonSound.SliderOpen : NeonSound.SliderClose, panel.GlobalPosition, -8f);
            panel.Switched += OnBlinds;
            _unhook.Add(() => panel.Switched -= OnBlinds);
        }

        if (apartment.Computer is { } computer)
        {
            void OnKey() => PlayAt(NeonSound.KeyClick, computer.KeyboardPosition, -4f);
            computer.KeyPressed += OnKey;
            _unhook.Add(() => computer.KeyPressed -= OnKey);
        }
    }

    private static float Volume(Vector3 min, Vector3 max) => (max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z);

    /// <summary>The world-space centre of the model's mesh named <paramref name="name"/>.</summary>
    private static Vector3? MeshCentre(NeonApartment apartment, string name)
    {
        if (apartment.Model?.FindChild(name, recursive: true, owned: false) is not MeshInstance3D { Mesh: { } mesh } instance)
            return null;
        return instance.GlobalTransform.TransformPoint(mesh.Bounds.Center);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private AudioPlayer Bed(string name, AudioStream stream, string bus)
    {
        var player = new AudioPlayer { Name = name, Stream = stream, Bus = bus, Loop = true };
        AddChild(player);
        return player;
    }

    private int FreeSlot()
    {
        for (var k = 0; k < _pool.Length; k++)
        {
            var i = (_nextPool + k) % _pool.Length;
            if (_pool[i] is { Playing: false })
            {
                _nextPool = (i + 1) % _pool.Length;
                return i;
            }
        }

        var oldest = _nextPool; // all busy: reuse the one started longest ago
        _nextPool = (_nextPool + 1) % _pool.Length;
        return oldest;
    }

    private AudioStream PickVariant(NeonSound sound)
    {
        var set = NeonSoundBank.Shared.Variants(sound);
        var pick = set[_rng.Range(0, set.Length)];
        if (set.Length > 1 && ReferenceEquals(pick, _lastVariant[(int)sound]))
            pick = set[(Array.IndexOf(set, pick) + 1 + _rng.Range(0, set.Length - 1)) % set.Length];
        _lastVariant[(int)sound] = pick;
        return pick;
    }

    private static float Db(float gain) => gain <= 1e-4f ? -80f : 20f * MathF.Log10(gain);

    /// <summary>
    /// Where the audio server will hear from this frame: the current <see cref="AudioListener3D"/>, else the active
    /// camera, else the origin (the server's own copy only updates after the tree's process step).
    /// </summary>
    public static Vector3 ListenerPosition(Node node)
    {
        if (node.Tree?.Servers.Get<AudioServer>()?.CurrentListener is { } listener)
            return listener.GlobalPosition;
        return node.GetViewport()?.ActiveCamera3D is { } camera ? camera.GlobalPosition : Vector3.Zero;
    }

    private static T? Resolve<T>(Node scene, NodePath path) where T : Node
    {
        if (!path.IsEmpty)
            return scene.GetNodeOrNull<T>(path);
        return Find<T>(scene);
    }

    private static T? Find<T>(Node node) where T : Node
    {
        if (node is T match)
            return match;
        foreach (var child in node.Children)
            if (Find<T>(child) is { } found)
                return found;
        return null;
    }
}

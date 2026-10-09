using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's soundscape, all procedural (no recorded audio): <see cref="AmbienceAudio"/> (wind and leaves),
/// <see cref="StreamAudio"/> (the brook along a <see cref="River3D"/>, falls), <see cref="BirdSongs"/> and
/// <see cref="FootstepAudio"/> on the player. Add it to a scene with <see cref="Attach"/>, or put a
/// <c>ForestAudio</c> node in the scene: when ready it finds the first <see cref="River3D"/> and
/// <see cref="FirstPersonController"/> under <see cref="Node.Owner"/> (or its parent) unless <see cref="RiverPath"/> and
/// <see cref="PlayerPath"/> name them. Its children are generated (unowned, never saved). Buses: see
/// <see cref="AmbienceBus"/> (<c>Content/Settings/AudioBusLayout.mres</c>); <see cref="ApplySettings"/> sets the
/// player's volumes on top of the layout's levels.
/// </summary>
public sealed class ForestAudio : Node
{
    /// <summary>Wind, leaves and birds.</summary>
    public const string AmbienceBus = "Ambience";

    /// <summary>The brook and the falls.</summary>
    public const string WaterBus = "Water";

    /// <summary>Footsteps (a light reverb).</summary>
    public const string FoleyBus = "Foley";

    /// <summary>The buses <see cref="ForestAudioSettings"/> controls, with Master.</summary>
    public static readonly string[] Buses = [AudioBusLayout.MasterBus, AmbienceBus, WaterBus, FoleyBus];

    private float[]? _layoutDb;

    /// <summary>The river the stream sound follows; empty: the first <see cref="River3D"/> in the scene.</summary>
    [Export] public NodePath RiverPath { get; set; } = "";

    /// <summary>The player whose footsteps play; empty: the first <see cref="FirstPersonController"/> in the scene.</summary>
    [Export] public NodePath PlayerPath { get; set; } = "";

    /// <summary>Where falls are (world space), passed to <see cref="StreamAudio.FallPositions"/>.</summary>
    [Export] public Vector3[] FallPositions { get; set; } = [];

    /// <summary>The river, set by <see cref="Attach"/> or resolved when ready.</summary>
    public River3D? River { get; set; }

    /// <summary>The player, set by <see cref="Attach"/> or resolved when ready.</summary>
    public FirstPersonController? Player { get; set; }

    /// <summary>Pine density (0–1) at a world position for <see cref="BirdSongs.PineDensity"/>; null: none.</summary>
    public Func<Vector3, float>? PineDensity { get; set; }

    /// <summary>The volumes applied when ready (null: <see cref="ForestSettings.Audio"/> of the running game, else the defaults).</summary>
    public ForestAudioSettings? Settings { get; set; }

    public AmbienceAudio? Ambience { get; private set; }

    public StreamAudio? Stream { get; private set; }

    public BirdSongs? Birds { get; private set; }

    public FootstepAudio? Footsteps { get; private set; }

    /// <summary>
    /// Adds the Forest's audio to <paramref name="root"/> (a scene root): the bed, the stream along
    /// <paramref name="river"/> (none when null), birds and <paramref name="player"/>'s footsteps.
    /// </summary>
    public static ForestAudio Attach(Node root, River3D? river, FirstPersonController? player, ReadOnlySpan<Vector3> fallPositions = default,
        ForestAudioSettings? settings = null)
    {
        var audio = new ForestAudio
        {
            Name = "ForestAudio",
            River = river,
            Player = player,
            FallPositions = fallPositions.ToArray(),
            Settings = settings,
        };
        root.AddChild(audio);
        return audio;
    }

    protected override void OnReady()
    {
        base.OnReady();
        var scene = Owner ?? Parent ?? this;
        River ??= Resolve<River3D>(scene, RiverPath);
        Player ??= Resolve<FirstPersonController>(scene, PlayerPath);

        Ambience = new AmbienceAudio { Name = "Ambience" };
        AddChild(Ambience);
        Stream = new StreamAudio { Name = "Stream", River = River, FallPositions = FallPositions };
        AddChild(Stream);
        Birds = new BirdSongs { Name = "Birds", PineDensity = PineDensity };
        AddChild(Birds);
        if (Player is not null)
        {
            Footsteps = new FootstepAudio { Name = "FootstepAudio", Player = Player };
            Player.AddChild(Footsteps); // at the feet, unowned
        }

        ApplySettings(Settings ?? (GameHost.Project is not null ? ForestSettings.LoadOrDefault(ForestSettings.DefaultPath).Audio : new ForestAudioSettings()));
        Log.Info($"[Forest] Audio: stream {River?.Name ?? "none"}, {FallPositions.Length} falls, footsteps {Player?.Name ?? "none"}.");
    }

    /// <summary>
    /// Sets the bus volumes: each bus's level from the bus layout plus the setting's gain (a linear 0–1 slider, as dB;
    /// 0 mutes). No-op without an audio server.
    /// </summary>
    public void ApplySettings(ForestAudioSettings settings)
    {
        Settings = settings;
        if (Tree?.Servers.Get<AudioServer>() is not { } server)
            return;
        if (_layoutDb is null)
        {
            _layoutDb = new float[Buses.Length];
            for (var i = 0; i < Buses.Length; i++)
                _layoutDb[i] = server.GetBus(Buses[i])?.VolumeDb ?? 0f;
        }

        for (var i = 0; i < Buses.Length; i++)
        {
            if (server.GetBus(Buses[i]) is not { } bus)
                continue;
            var gain = settings.Volume(Buses[i]);
            bus.Mute = gain <= 0f;
            bus.VolumeDb = _layoutDb[i] + (gain > 0f ? 20f * MathF.Log10(gain) : 0f);
        }
    }

    /// <summary>
    /// Where the audio server will hear from this frame: the current <see cref="AudioListener3D"/>, else the active
    /// camera, else the origin (its own <c>Listener3D</c> is only updated after the tree's process step).
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
        {
            if (Find<T>(child) is { } found)
                return found;
        }

        return null;
    }
}

/// <summary>The player's audio volumes (linear 0–1 sliders over the bus layout's levels), saved in <see cref="ForestSettings"/>.</summary>
public sealed class ForestAudioSettings
{
    public float MasterVolume { get; set; } = 1f;

    public float AmbienceVolume { get; set; } = 1f;

    public float WaterVolume { get; set; } = 1f;

    public float FoleyVolume { get; set; } = 1f;

    /// <summary>The volume of <paramref name="bus"/> (1 for buses it does not control).</summary>
    public float Volume(string bus) => Math.Clamp(bus switch
    {
        AudioBusLayout.MasterBus => MasterVolume,
        ForestAudio.AmbienceBus => AmbienceVolume,
        ForestAudio.WaterBus => WaterVolume,
        ForestAudio.FoleyBus => FoleyVolume,
        _ => 1f,
    }, 0f, 1f);
}

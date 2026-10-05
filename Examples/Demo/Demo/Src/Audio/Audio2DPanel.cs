using MainframeEngine;

namespace Demo;

/// <summary>The Audio 2D scene's controls: the bus faders and the emitter's panning and range.</summary>
/// <remarks>The bus faders are server-global, so the panel restores them when the scene leaves the tree.</remarks>
public sealed class Audio2DPanel : UiDocument
{
    private static readonly string[] s_buses = ["Master", "SFX", "Music"];
    private readonly float[] _savedDb = new float[s_buses.Length];
    private AudioServer? _savedFrom;

    public Audio2DPanel()
    {
        Source = "Content/Audio/panel2d.rml";
        AutoFocus = false;
    }

    private AudioServer? Audio => Tree?.Servers.Get<AudioServer>();
    private AudioPlayer2D Emitter => Parent!.Parent!.GetNode<AudioPlayer2D>("Sweeper/Emitter");

    protected override void OnReady()
    {
        if (Audio is { } audio)
        {
            _savedFrom = audio;
            for (var i = 0; i < s_buses.Length; i++)
                _savedDb[i] = audio.GetBus(s_buses[i])?.VolumeDb ?? 0f;
        }

        CreateDataModel("audio2d")
            .Bind("master", this, static d => d.Bus("Master"), static (d, v) => d.SetBus("Master", v))
            .Bind("sfx", this, static d => d.Bus("SFX"), static (d, v) => d.SetBus("SFX", v))
            .Bind("music", this, static d => d.Bus("Music"), static (d, v) => d.SetBus("Music", v))
            .Bind("panning", this, static d => d.Emitter.PanningStrength, static (d, v) => d.Emitter.PanningStrength = v)
            .Bind("distance", this, static d => d.Emitter.MaxDistance, static (d, v) => d.Emitter.MaxDistance = v);
    }

    protected override void OnExitTree()
    {
        if (_savedFrom is { } audio)
            for (var i = 0; i < s_buses.Length; i++)
                if (audio.GetBus(s_buses[i]) is { } bus)
                    bus.VolumeDb = _savedDb[i];
        _savedFrom = null;
        base.OnExitTree();
    }

    private float Bus(string name) => Audio?.GetBus(name)?.VolumeDb ?? 0f;

    private void SetBus(string name, float db)
    {
        if (Audio?.GetBus(name) is { } bus)
            bus.VolumeDb = db;
    }
}

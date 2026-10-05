using MainframeEngine;

namespace Demo;

/// <summary>The Audio 3D scene's controls: attenuation model, low-pass, Doppler and the orbit speed.</summary>
public sealed class Audio3DPanel : UiDocument
{
    private const float LowPassHz = 1200f;

    public Audio3DPanel()
    {
        Source = "Content/Audio/panel3d.rml";
        AutoFocus = false;
    }

    private Node Scene => Parent!.Parent!;
    private AudioPlayer3D Emitter => Scene.GetNode<AudioPlayer3D>("Orbit/Emitter");
    private Orbiter Orbit => Scene.GetNode<Orbiter>("Orbit");

    protected override void OnReady() =>
        CreateDataModel("audio3d")
            .Bind("model", this, static d => (int)d.Emitter.AttenuationModel, static (d, v) => d.Emitter.AttenuationModel = (AttenuationModel)v)
            .Bind("lowpass", this, static d => d.Emitter.LowPassAtMaxDistance > 0f, static (d, v) => d.Emitter.LowPassAtMaxDistance = v ? LowPassHz : 0f)
            .Bind("doppler", this, static d => d.Emitter.DopplerTracking, static (d, v) => d.Emitter.DopplerTracking = v)
            .Bind("speed", this, static d => d.Orbit.DegreesPerSecond, static (d, v) => d.Orbit.DegreesPerSecond = v);
}

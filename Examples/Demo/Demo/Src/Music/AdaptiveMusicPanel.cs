using MainframeEngine;
using MainframeEngine.Audio.Music;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Adaptive music scene's controls: the <c>intensity</c> slider, the <c>area</c> select (Calm / Combat), the Hit
/// stinger and a music toggle, plus what plays (section, bar.beat, the scheduled transition and its countdown). Every
/// control calls the <see cref="AdaptiveMusicPlayer"/> the way game code would.
/// </summary>
public sealed class AdaptiveMusicPanel : UiDocument
{
    private RmlDataModel? _model;
    private string _section = "";
    private int _bar;
    private int _beat;
    private string _pending = "";
    private int _pendingTenths = -1;

    public AdaptiveMusicPanel()
    {
        Source = "Assets/AdaptiveMusic/panel.rml";
        AutoFocus = false;
    }

    private AdaptiveMusicPlayer Player => Parent!.Parent!.GetNode<AdaptiveMusicPlayer>("Music");

    /// <summary>How much is going on, 0…1: the Perc layer comes in from 0.2, the Lead from 0.45 (its filter opening up to 1).</summary>
    public float Intensity
    {
        get => Player.Instance?.Director is { } d && d.Parameters.IndexOf("intensity") is var i and >= 0 ? d.Parameters.GetTarget(i) : 0f;
        set
        {
            Player.SetParameter("intensity", Math.Clamp(MathF.Round(value, 2), 0f, 1f));
            _model?.Dirty("intensity");
        }
    }

    /// <summary>0 Calm, 1 Combat: Explore moves to Combat at the next bar, and back with the Hit stinger.</summary>
    public int Area
    {
        get => Player.Instance?.Director is { } d && d.Parameters.IndexOf("area") is var i and >= 0 ? (int)d.Parameters.GetTarget(i) : 0;
        set
        {
            Player.SetParameter("area", value == 1 ? "Combat" : "Calm");
            _model?.Dirty("area");
        }
    }

    public bool MusicOn
    {
        get => Player.Playing;
        set
        {
            if (value == Player.Playing)
                return;
            if (value)
                Player.Play();
            else
                Player.Stop(MusicQuantize.Bar, fadeBeats: 2f);
            _model?.Dirty("music");
        }
    }

    /// <summary>Fires the Hit stinger on the next beat.</summary>
    public void Hit() => Player.TriggerStinger("hit");

    protected override void OnReady()
    {
        _model = CreateDataModel("adaptive")
            .Bind("intensity", this, static d => d.Intensity, static (d, v) => d.Intensity = v)
            .Bind("area", this, static d => d.Area, static (d, v) => d.Area = v)
            .Bind("music", this, static d => d.MusicOn, static (d, v) => d.MusicOn = v)
            .Bind("section", this, static d => d._section)
            .Bind("bar", this, static d => d._bar)
            .Bind("beat", this, static d => d._beat)
            .Bind("pending", this, static d => d._pending)
            .Bind("seconds", this, static d => d._pendingTenths / 10f)
            .Event("hit", Hit);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        if (_model is not { } model || Player.Instance?.Director is not { } director)
            return;
        var position = Player.GetPosition();
        var section = position.Section is { } id && director.Plan.FindSection(id) is var s and >= 0 ? director.Plan.Sections[s].Name : "";
        if (!ReferenceEquals(section, _section) || position.Bar != _bar || position.Beat != _beat)
        {
            (_section, _bar, _beat) = (section, position.Bar, position.Beat);
            model.Dirty("section");
            model.Dirty("bar");
            model.Dirty("beat");
            model.Dirty("music");
        }

        var pending = Player.PendingTransition is { } to && director.Plan.FindSection(to) is var p and >= 0 ? director.Plan.Sections[p].Name : "";
        var tenths = pending.Length > 0 && Player.PendingSeconds is var left and >= 0 ? (int)(left * 10) : -1;
        if (!ReferenceEquals(pending, _pending) || tenths != _pendingTenths)
        {
            (_pending, _pendingTenths) = (pending, tenths);
            model.Dirty("pending");
            model.Dirty("seconds");
        }
    }

    protected override void OnLocaleChanged()
    {
        base.OnLocaleChanged();
        _model?.DirtyAll();
    }
}

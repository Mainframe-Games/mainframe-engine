using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The wall TV: <c>interact</c> turns it on, flips through <see cref="ChannelCount"/> channels and turns it off after
/// the last. The picture is <see cref="Screen"/>'s emission (this TV's own material): a channel's
/// <see cref="ChannelTexture"/> when one is set (the TV's sub-viewport feeds), else a flat channel colour.
/// </summary>
public sealed class Tv : Appliance
{
    private static readonly Vector3[] Fallback = [new(0.2f, 0.55f, 1f), new(1f, 0.25f, 0.65f), new(0.95f, 0.75f, 0.25f)];

    /// <summary>The channel shown while on (0-based).</summary>
    public int Channel { get; private set; }

    public int ChannelCount { get; set; } = 3;

    /// <summary>The screen's material (emission is the picture).</summary>
    public StandardMaterial3D? Screen { get; set; }

    /// <summary>Per channel, the picture (null: the channel's flat colour).</summary>
    public Func<int, Texture2D?>? ChannelTexture { get; set; }

    /// <summary>How bright the picture is.</summary>
    [Export(Range = "0,20,0.1")] public float Brightness { get; set; } = 3f;

    /// <summary>Raised when the channel changes while on (the channel).</summary>
    [Signal] public event Action<int>? ChannelChanged;

    public override string Verb => !IsOn ? Verbs.TurnOn : Channel == ChannelCount - 1 ? Verbs.TurnOff : Verbs.NextChannel;

    public override void Interact()
    {
        if (!IsOn)
        {
            Channel = 0;
            SetOn(true);
            return;
        }

        if (Channel == ChannelCount - 1)
        {
            SetOn(false);
            return;
        }

        Channel++;
        ShowPicture();
        ChannelChanged?.Invoke(Channel);
    }

    protected override void OnSwitched(bool on) => ShowPicture();

    private void ShowPicture()
    {
        if (Screen is not { } screen)
            return;
        if (!IsOn)
        {
            screen.EmissionEnergy = 0f;
            screen.EmissionTexture = null;
            return;
        }

        var texture = ChannelTexture?.Invoke(Channel);
        screen.EmissionTexture = texture;
        var c = texture is null ? Fallback[Channel % Fallback.Length] : Vector3.One;
        screen.EmissionColor = System.Drawing.Color.FromArgb(255, (int)(c.X * 255), (int)(c.Y * 255), (int)(c.Z * 255));
        screen.EmissionEnergy = Brightness;
    }
}

/// <summary>
/// The stereo: <c>interact</c> plays the first track, skips to the next, and stops after the last. The music itself is
/// the audio's (it listens to <see cref="TrackChanged"/>); this keeps the state and lights the stereo's display.
/// </summary>
public sealed class Stereo : Appliance
{
    /// <summary>The track playing while on (0-based).</summary>
    public int Track { get; private set; }

    public int TrackCount { get; set; } = 2;

    /// <summary>The display's glow (this stereo's own material).</summary>
    public StandardMaterial3D? Display { get; set; }

    /// <summary>Raised when a track starts (the track), and with -1 when the music stops.</summary>
    [Signal] public event Action<int>? TrackChanged;

    public override string Verb => !IsOn ? Verbs.PlayMusic : Track == TrackCount - 1 ? Verbs.StopMusic : Verbs.NextTrack;

    public override void Interact()
    {
        if (!IsOn)
        {
            Track = 0;
            SetOn(true);
        }
        else if (Track == TrackCount - 1)
        {
            SetOn(false);
        }
        else
        {
            Track++;
            TrackChanged?.Invoke(Track);
        }
    }

    /// <summary>Plays <paramref name="track"/> (or stops with -1), as the terminal's <c>music</c> command does.</summary>
    public void Play(int track)
    {
        if (track < 0)
        {
            SetOn(false);
            return;
        }

        Track = Math.Clamp(track, 0, TrackCount - 1);
        if (!IsOn)
            SetOn(true);
        else
            TrackChanged?.Invoke(Track);
    }

    protected override void OnSwitched(bool on)
    {
        if (Display is not null)
            Display.EmissionEnergy = on ? 4f : 0.3f;
        if (IsInsideTree)
            TrackChanged?.Invoke(on ? Track : -1);
    }
}

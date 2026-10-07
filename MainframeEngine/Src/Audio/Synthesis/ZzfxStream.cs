using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// A sound synthesised by ZzFX (<see cref="Zzfx"/>) from its parameters instead of read from a file: coins, lasers,
/// jumps, UI blips. The samples are generated on first use (players preload when they enter the tree) and cached; the
/// <c>.mres</c> stores only the parameters that differ from ZzFX's defaults, so a new stream is ZzFX's default sound.
/// </summary>
/// <remarks>
/// Setting a parameter re-synthesises (and allocates) on the next play, so do not change parameters every frame; voices
/// that are playing keep the samples they started with. ZzFX's <c>randomness</c> is the inherited
/// <see cref="AudioStream.PitchRandomness"/> (default 0.05, ZzFX's), applied on every play. <see cref="AudioStream.File"/>
/// and <see cref="AudioStream.LoadMode"/> are unused.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "An AudioStream (Godot's name, ADR 0010); not a System.IO.Stream.")]
[EditorIcon("music")]
public sealed class ZzfxStream : AudioStream
{
    private float _volume = 1f;
    private float _frequency = 220f;
    private ZzfxShape _shape;
    private float _shapeCurve = 1f;
    private float _attack;
    private float _decay;
    private float _sustain;
    private float _releaseTime = 0.1f;
    private float _sustainVolume = 1f;
    private float _slide;
    private float _deltaSlide;
    private float _pitchJump;
    private float _pitchJumpTime;
    private float _repeatTime;
    private float _noise;
    private float _modulation;
    private float _bitCrush;
    private float _delay;
    private float _tremolo;
    private float _filter;

    public ZzfxStream()
    {
        PitchRandomness = 0.05f;
    }

    /// <summary>Volume scale (ZzFX's master volume 0.3 is applied on top).</summary>
    [ExportGroup("Sound")]
    [Export(Range = "0,5,0.01")]
    public float Volume
    {
        get => _volume;
        set
        {
            if (_volume.Equals(value))
                return;
            _volume = value;
            Invalidate();
        }
    }

    /// <summary>Start frequency in Hz.</summary>
    [Export(Range = "0,20000,1")]
    public float Frequency
    {
        get => _frequency;
        set
        {
            if (_frequency.Equals(value))
                return;
            _frequency = value;
            Invalidate();
        }
    }

    /// <summary>Wave shape.</summary>
    [Export]
    public ZzfxShape Shape
    {
        get => _shape;
        set
        {
            if (_shape.Equals(value))
                return;
            _shape = value;
            Invalidate();
        }
    }

    /// <summary>Shape curve: 1 = the plain wave, below 1 squarer, above 1 thinner; the duty for Square.</summary>
    [Export(Range = "0,5,0.01")]
    public float ShapeCurve
    {
        get => _shapeCurve;
        set
        {
            if (_shapeCurve.Equals(value))
                return;
            _shapeCurve = value;
            Invalidate();
        }
    }

    /// <summary>Attack time in seconds.</summary>
    [ExportGroup("Envelope")]
    [Export(Range = "0,3,0.001")]
    public float Attack
    {
        get => _attack;
        set
        {
            if (_attack.Equals(value))
                return;
            _attack = value;
            Invalidate();
        }
    }

    /// <summary>Decay time in seconds (to SustainVolume).</summary>
    [Export(Range = "0,3,0.001")]
    public float Decay
    {
        get => _decay;
        set
        {
            if (_decay.Equals(value))
                return;
            _decay = value;
            Invalidate();
        }
    }

    /// <summary>Sustain time in seconds.</summary>
    [Export(Range = "0,3,0.001")]
    public float Sustain
    {
        get => _sustain;
        set
        {
            if (_sustain.Equals(value))
                return;
            _sustain = value;
            Invalidate();
        }
    }

    /// <summary>Release time in seconds (ZzFX's <c>release</c>; <c>Release</c> is taken by <see cref="Resource.Release"/>).</summary>
    [Export(Range = "0,3,0.001")]
    public float ReleaseTime
    {
        get => _releaseTime;
        set
        {
            if (_releaseTime.Equals(value))
                return;
            _releaseTime = value;
            Invalidate();
        }
    }

    /// <summary>Volume during sustain.</summary>
    [Export(Range = "0,1,0.01")]
    public float SustainVolume
    {
        get => _sustainVolume;
        set
        {
            if (_sustainVolume.Equals(value))
                return;
            _sustainVolume = value;
            Invalidate();
        }
    }

    /// <summary>Frequency slide.</summary>
    [ExportGroup("Pitch")]
    [Export(Range = "-10,10,0.01")]
    public float Slide
    {
        get => _slide;
        set
        {
            if (_slide.Equals(value))
                return;
            _slide = value;
            Invalidate();
        }
    }

    /// <summary>Change of the slide over time.</summary>
    [Export(Range = "-10,10,0.01")]
    public float DeltaSlide
    {
        get => _deltaSlide;
        set
        {
            if (_deltaSlide.Equals(value))
                return;
            _deltaSlide = value;
            Invalidate();
        }
    }

    /// <summary>Pitch jump in Hz, applied after PitchJumpTime.</summary>
    [Export(Range = "-1000,1000,1")]
    public float PitchJump
    {
        get => _pitchJump;
        set
        {
            if (_pitchJump.Equals(value))
                return;
            _pitchJump = value;
            Invalidate();
        }
    }

    /// <summary>When the pitch jump happens, in seconds.</summary>
    [Export(Range = "0,1,0.001")]
    public float PitchJumpTime
    {
        get => _pitchJumpTime;
        set
        {
            if (_pitchJumpTime.Equals(value))
                return;
            _pitchJumpTime = value;
            Invalidate();
        }
    }

    /// <summary>Resets frequency, slide and pitch jump every RepeatTime seconds (0 = off).</summary>
    [Export(Range = "0,1,0.001")]
    public float RepeatTime
    {
        get => _repeatTime;
        set
        {
            if (_repeatTime.Equals(value))
                return;
            _repeatTime = value;
            Invalidate();
        }
    }

    /// <summary>Random frequency noise.</summary>
    [ExportGroup("Effects")]
    [Export(Range = "0,5,0.01")]
    public float Noise
    {
        get => _noise;
        set
        {
            if (_noise.Equals(value))
                return;
            _noise = value;
            Invalidate();
        }
    }

    /// <summary>Frequency modulation in Hz.</summary>
    [Export(Range = "-500,500,0.1")]
    public float Modulation
    {
        get => _modulation;
        set
        {
            if (_modulation.Equals(value))
                return;
            _modulation = value;
            Invalidate();
        }
    }

    /// <summary>Sample-and-hold bit crush.</summary>
    [Export(Range = "0,1,0.01")]
    public float BitCrush
    {
        get => _bitCrush;
        set
        {
            if (_bitCrush.Equals(value))
                return;
            _bitCrush = value;
            Invalidate();
        }
    }

    /// <summary>Echo delay in seconds (lengthens the sound).</summary>
    [Export(Range = "0,0.5,0.001")]
    public float Delay
    {
        get => _delay;
        set
        {
            if (_delay.Equals(value))
                return;
            _delay = value;
            Invalidate();
        }
    }

    /// <summary>Volume tremolo strength (needs RepeatTime).</summary>
    [Export(Range = "0,1,0.01")]
    public float Tremolo
    {
        get => _tremolo;
        set
        {
            if (_tremolo.Equals(value))
                return;
            _tremolo = value;
            Invalidate();
        }
    }

    /// <summary>Biquad filter cutoff in Hz: positive = low-pass, negative = high-pass, 0 = off.</summary>
    [Export(Range = "-10000,10000,1")]
    public float Filter
    {
        get => _filter;
        set
        {
            if (_filter.Equals(value))
                return;
            _filter = value;
            Invalidate();
        }
    }

    /// <summary>All parameters as one value (ZzFX order; <see cref="ZzfxParameters.Randomness"/> is <see cref="AudioStream.PitchRandomness"/>).</summary>
    public ZzfxParameters Parameters
    {
        get => new()
        {
            Volume = _volume,
            Frequency = _frequency,
            Shape = _shape,
            ShapeCurve = _shapeCurve,
            Attack = _attack,
            Decay = _decay,
            Sustain = _sustain,
            Release = _releaseTime,
            SustainVolume = _sustainVolume,
            Slide = _slide,
            DeltaSlide = _deltaSlide,
            PitchJump = _pitchJump,
            PitchJumpTime = _pitchJumpTime,
            RepeatTime = _repeatTime,
            Noise = _noise,
            Modulation = _modulation,
            BitCrush = _bitCrush,
            Delay = _delay,
            Tremolo = _tremolo,
            Filter = _filter,
            Randomness = PitchRandomness,
        };
        set
        {
            Volume = value.Volume;
            Frequency = value.Frequency;
            Shape = value.Shape;
            ShapeCurve = value.ShapeCurve;
            Attack = value.Attack;
            Decay = value.Decay;
            Sustain = value.Sustain;
            ReleaseTime = value.Release;
            SustainVolume = value.SustainVolume;
            Slide = value.Slide;
            DeltaSlide = value.DeltaSlide;
            PitchJump = value.PitchJump;
            PitchJumpTime = value.PitchJumpTime;
            RepeatTime = value.RepeatTime;
            Noise = value.Noise;
            Modulation = value.Modulation;
            BitCrush = value.BitCrush;
            Delay = value.Delay;
            Tremolo = value.Tremolo;
            Filter = value.Filter;
            PitchRandomness = value.Randomness;
        }
    }

    /// <summary>A stream for a ZzFX line (<c>zzfx(...[…])</c>); throws <see cref="FormatException"/> when it does not parse.</summary>
    public static ZzfxStream FromLine(string line, string name = "ZzFX")
    {
        if (!ZzfxParameters.TryParse(line, out var parameters, out var error))
            throw new FormatException(error);
        return new ZzfxStream { ResourceName = name, Parameters = parameters };
    }

    /// <summary>The parameters as a ZzFX line (<see cref="ZzfxParameters.ToLine"/>).</summary>
    public string ToLine() => Parameters.ToLine();

    private protected override AudioSource? CreateSource()
    {
        var samples = Zzfx.Generate(Parameters); // throws past Zzfx.MaxSeconds: a LoadError, logged once
        return AudioClipData.FromSamples(string.IsNullOrEmpty(ResourceName) ? "ZzFX" : ResourceName, samples, 1, Zzfx.SampleRate);
    }
}

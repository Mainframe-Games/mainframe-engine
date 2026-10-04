namespace MainframeEngine.Audio;

internal enum AudioCommandType : byte
{
    None,
    PlayVoice,
    SetVoiceParams,
    PauseVoice,
    ResumeVoice,
    StopVoice,
    SeekVoice,
    SetBusGain,
    SwapGraph,
    /// <summary>A streamed voice seeks: read stream generation <see cref="AudioCommand.StreamGeneration"/> from <see cref="AudioCommand.Frame"/>.</summary>
    RestartStream,
}

/// <summary>
/// A game-thread → audio-thread command. Plain data copied into the <see cref="SpscRing{T}"/> by value; the only
/// reference it carries is the sound being played (or a graph), which the audio thread reads but never frees.
/// </summary>
internal struct AudioCommand
{
    public AudioCommandType Type;
    /// <summary>Voice or bus index.</summary>
    public int Index;
    /// <summary>The voice play this targets; commands for an older generation are ignored.</summary>
    public int Generation;
    /// <summary>Stream-channel request generation (PlayVoice of a streamed sound).</summary>
    public int StreamGeneration;
    public bool Positional;
    public bool Loop;
    /// <summary>PlayVoice: start paused (the owner may not run now), in the same command so a batch can never split them.</summary>
    public bool StartPaused;
    public object? Ref;
    public double Frame;
    public long LoopStart;
    public long LoopEnd;
    public VoiceParams Params;
}

internal enum AudioEventType : byte
{
    None,
    /// <summary>A voice played its sound to the end (not looping, not stopped).</summary>
    VoiceFinished,
    /// <summary>A streamed voice ended because its file failed to open or decode (no <c>Finished</c> signal).</summary>
    VoiceFailed,
    /// <summary>The audio thread switched to a new graph; <see cref="AudioEvent.Ref"/> is the old one to dispose.</summary>
    GraphRetired,
}

/// <summary>An audio-thread → game-thread notification.</summary>
internal struct AudioEvent
{
    public AudioEventType Type;
    public int Index;
    public int Generation;
    public object? Ref;
}

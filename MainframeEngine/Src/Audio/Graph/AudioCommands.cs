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

    // Adaptive music (Index: the music voice; Generation: its play). See MusicVoice and docs/design/future/adaptive-music.md.

    /// <summary>Starts a music voice: <see cref="AudioCommand.Ref"/> is the <c>MusicRuntime</c>; it waits for its first section.</summary>
    MusicPlay,
    /// <summary>Start <see cref="MusicCommand.Section"/> in lane group <see cref="MusicCommand.Group"/> at music frame <see cref="AudioCommand.Frame"/>.</summary>
    MusicSchedule,
    /// <summary>Hold lane group <see cref="MusicCommand.Group"/> ready with the continuation section that starts at the frame.</summary>
    MusicPrefetch,
    /// <summary>Play stinger <see cref="MusicCommand.Section"/> from the frame.</summary>
    MusicStinger,
    /// <summary>Stop at the frame, fading over <see cref="MusicCommand.FadeOutFrames"/>.</summary>
    MusicStop,
    /// <summary>Drop scheduled action <see cref="MusicCommand.Action"/> (ignored once it has happened).</summary>
    MusicCancel,
    /// <summary>Layer <see cref="MusicCommand.Layer"/>'s gain and filter cutoff targets.</summary>
    MusicSetLayer,
    /// <summary>The voice's volume (<see cref="VoiceParams.Gain"/>).</summary>
    MusicSetVolume,
    MusicPause,
    MusicResume,
    /// <summary>Stops at once (a few milliseconds' fade), without <c>Finished</c>.</summary>
    MusicHalt,
}

/// <summary>The music fields of an <see cref="AudioCommand"/> (plain values).</summary>
internal struct MusicCommand
{
    public int Action;
    public int Section;
    public int Then;
    public int Group;
    public int Layer;
    public long EntryOffset;
    public long FadeOutFrames;
    public long FadeInFrames;
    public float Gain;
    public float CutoffHz;
    /// <summary>Schedule/stop: the outgoing section ends at its end (play its tails). Set layer: jump to the values (no ramp).</summary>
    public bool Flag;
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
    public MusicCommand Music;
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

    // Adaptive music (Index: the music voice; Generation: its play).

    /// <summary>A section started at <see cref="AudioEvent.Frame"/> (its frame 0 is <see cref="AudioEvent.Origin"/>).</summary>
    MusicSectionStarted,
    /// <summary>Scheduled action <see cref="AudioEvent.Action"/> could not happen at its frame (a lane was not ready, or it came late).</summary>
    MusicMissed,
    /// <summary>Lane group <see cref="AudioEvent.Group"/> is free again (its files can close).</summary>
    MusicGroupFreed,
    /// <summary>The music ended by its authored flow and every tail has rung out.</summary>
    MusicFinished,
    /// <summary>The voice went idle after a stop or a halt.</summary>
    MusicStopped,
}

/// <summary>An audio-thread → game-thread notification.</summary>
internal struct AudioEvent
{
    public AudioEventType Type;
    public int Index;
    public int Generation;
    public object? Ref;
    public long Frame;
    public long Origin;
    public int Section;
    public int Action;
    public int Group;
}

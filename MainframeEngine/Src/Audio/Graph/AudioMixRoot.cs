using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>
/// The engine's entry point on the audio thread: a SoundFlow component placed in the output device's master mixer
/// (or pumped directly by the null device). Each block it (1) drains the command ring — every command the game
/// thread published since the last block, in order — (2) mixes the current <see cref="AudioGraph"/>, and
/// (3) reports voices that finished through the event ring.
/// </summary>
/// <remarks>
/// <para>No locks and no allocations on this path: commands and events are value types in
/// <see cref="SpscRing{T}"/>s, and every SoundFlow object it touches was created on the game thread.</para>
/// <para>An exception here would otherwise unwind into miniaudio's native callback and take the process down, so the
/// block is guarded: the first fault is kept for the game thread to log, and the root outputs silence until a new
/// graph is swapped in.</para>
/// </remarks>
internal sealed class AudioMixRoot : SoundComponent
{
    private readonly SpscRing<AudioCommand> _commands;
    private readonly SpscRing<AudioEvent> _events;
    private AudioGraph? _graph;
    private AudioGraph? _pendingRetire;
    private long _renderedFrames;
    private long _blocks;
    private int _underruns;
    private Exception? _fault;

    public AudioMixRoot(AudioEngine engine, AudioFormat format, SpscRing<AudioCommand> commands, SpscRing<AudioEvent> events)
        : base(engine, format)
    {
        _commands = commands;
        _events = events;
        Name = "Mainframe Audio";
        Volume = AudioGraph.UnityVolume;
    }

    /// <summary>Frames rendered so far (any thread).</summary>
    public long RenderedFrames => Volatile.Read(ref _renderedFrames);

    /// <summary>Blocks (device callbacks) rendered so far.</summary>
    public long Blocks => Volatile.Read(ref _blocks);

    /// <summary>Streamed voices that ran out of decoded samples (total).</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>The exception that stopped mixing, if any (read by the game thread).</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        try
        {
            Drain();
            var graph = _graph;
            if (graph is not null && _fault is null)
            {
                graph.Mix(buffer, channels);
                AfterMix(graph);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _fault, e);
            buffer.Clear();
        }

        Volatile.Write(ref _renderedFrames, _renderedFrames + buffer.Length / Math.Max(channels, 1));
        Volatile.Write(ref _blocks, _blocks + 1);
    }

    private void Drain()
    {
        // A retired graph is reported once the event ring has room (normally immediately).
        if (_pendingRetire is not null && Post(AudioEventType.GraphRetired, -1, 0, _pendingRetire))
            _pendingRetire = null;

        while (_commands.TryDequeue(out var command))
            Apply(command);
    }

    private void Apply(in AudioCommand command)
    {
        if (command.Type == AudioCommandType.SwapGraph)
        {
            var old = _graph;
            old?.HaltAll();
            _graph = (AudioGraph?)command.Ref;
            Volatile.Write(ref _fault, null);
            if (old is not null && !Post(AudioEventType.GraphRetired, -1, 0, old))
                _pendingRetire = old;
            return;
        }

        var graph = _graph;
        if (graph is null)
            return;

        if (command.Type == AudioCommandType.SetBusGain)
        {
            if ((uint)command.Index < (uint)graph.Buses.Length)
                graph.Buses[command.Index].Fader.TargetGain = command.Params.Gain;
            return;
        }

        if ((uint)command.Index >= (uint)graph.Voices.Length)
            return;
        var voice = graph.Voices[command.Index];
        if (command.Type == AudioCommandType.PlayVoice)
        {
            if (command.Ref is AudioSource source)
            {
                voice.Play(command.Generation, source, voice.StreamChannel, command.StreamGeneration, command.Frame,
                    command.Params, command.Positional, command.Loop, command.LoopStart, command.LoopEnd);
            }

            return;
        }

        if (voice.Generation != command.Generation)
            return; // a command for a sound this voice no longer plays

        switch (command.Type)
        {
            case AudioCommandType.SetVoiceParams:
                voice.SetParams(command.Params);
                break;
            case AudioCommandType.PauseVoice:
                voice.Pause();
                break;
            case AudioCommandType.ResumeVoice:
                voice.Resume();
                break;
            case AudioCommandType.StopVoice:
                voice.Stop();
                break;
            case AudioCommandType.SeekVoice:
                voice.Seek(command.Frame);
                break;
        }
    }

    private void AfterMix(AudioGraph graph)
    {
        var voices = graph.Voices;
        for (var i = 0; i < voices.Length; i++)
        {
            var voice = voices[i];
            var underruns = voice.Source.Underruns;
            if (underruns != voice.ReportedUnderruns)
            {
                Interlocked.Add(ref _underruns, underruns - voice.ReportedUnderruns);
                voice.ReportedUnderruns = underruns;
            }

            if (voice.AfterMix() == VoiceEnd.Finished)
                voice.FinishPending = true;

            // Retried every block until the game thread has room for it, so a finish is never lost.
            if (voice.FinishPending && Post(AudioEventType.VoiceFinished, voice.Index, voice.Generation, null))
                voice.FinishPending = false;
        }
    }

    private bool Post(AudioEventType type, int index, int generation, object? reference) =>
        _events.TryEnqueue(new AudioEvent { Type = type, Index = index, Generation = generation, Ref = reference });

    /// <summary>The graph the audio thread is mixing (tests; read only from the audio thread or after it stopped).</summary>
    internal AudioGraph? CurrentGraph => _graph;
}

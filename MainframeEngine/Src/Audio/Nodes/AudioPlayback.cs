using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// The voice bookkeeping shared by <see cref="AudioPlayer"/>, <see cref="AudioPlayer2D"/> and
/// <see cref="AudioPlayer3D"/>: the node's playing voices (oldest first), polyphony, stop/seek/position. Allocation
/// free once the handle list has grown to the node's polyphony.
/// </summary>
internal sealed class AudioPlayback
{
    private readonly List<AudioVoiceHandle> _voices = new(2);

    public AudioServer? Server { get; private set; }

    public bool IsPlaying => _voices.Count > 0;

    public int VoiceCount => _voices.Count;

    /// <summary>The handles of the playing voices, oldest first.</summary>
    public IReadOnlyList<AudioVoiceHandle> Voices => _voices;

    /// <summary>
    /// Called from the node's <c>OnEnterTree</c>. In <see cref="SceneTree.EditMode"/> (the editor) audio nodes are inert:
    /// no server, so <c>Play()</c> and <c>Autoplay</c> do nothing and nothing is preloaded (the editor previews sounds
    /// itself).
    /// </summary>
    public void Attach(Node node, AudioStream? stream)
    {
        Server = node.Tree is { EditMode: false } tree ? tree.Servers.Get<AudioServer>() : null;
        if (Server is not null)
            stream?.Preload();
    }

    /// <summary>Called from the node's <c>OnExitTree</c>: stops everything (Godot stops players leaving the tree).</summary>
    public void Detach()
    {
        StopAll();
        Server = null;
    }

    public void Play(IAudioVoiceOwner owner, AudioStream? stream, string bus, int priority, float fromSeconds, bool loop,
        bool positional, int maxPolyphony, in VoiceParams initial)
    {
        if (Server is not { } server || stream is null)
            return;

        // Polyphony: restart the oldest voice(s) to make room.
        var limit = Math.Max(1, maxPolyphony);
        while (_voices.Count >= limit)
            StopVoice(_voices[0]);

        var handle = server.PlayForOwner(owner, stream, bus, priority, fromSeconds, loop, positional, initial);
        if (handle.IsValid)
            _voices.Add(handle);
    }

    public void StopAll()
    {
        while (_voices.Count > 0)
            StopVoice(_voices[^1]);
    }

    public void Seek(double seconds)
    {
        if (Server is not { } server)
            return;
        foreach (var voice in _voices)
            server.Seek(voice, seconds);
    }

    /// <summary>Position of the newest voice in seconds (0 when silent).</summary>
    public double GetPlaybackPosition() =>
        Server is { } server && _voices.Count > 0 ? server.GetPlaybackPosition(_voices[^1]) : 0;

    /// <summary>The server ended <paramref name="handle"/>; returns true when it was one of ours.</summary>
    public bool OnVoiceEnded(AudioVoiceHandle handle) => _voices.Remove(handle);

    private void StopVoice(AudioVoiceHandle handle)
    {
        if (Server is { } server && server.IsCurrent(handle))
            server.Stop(handle); // calls back OnVoiceEnded, which removes it
        _voices.Remove(handle);
    }
}

using System.Diagnostics;

namespace MainframeEngine.Sandbox;

/// <summary>
/// <c>--qa-audio</c>: plays a short test melody through the real output device on the UI bus (alongside the scene's
/// streamed ambience), then after ~2.5 s checks the audio server's counters — a real device, the expected number
/// of frames rendered, no mixer fault, no stream errors, the melody finished — logs the result and exits with
/// <see cref="ExitCode.Ok"/> or <see cref="ExitCode.Error"/>. Used for local QA on a machine with speakers.
/// </summary>
public sealed class QaAudio
{
    private const double MelodyAt = 0.25;
    private const double CheckAt = 2.75;

    private readonly Stopwatch _clock = new();
    private AudioVoiceHandle _melody;
    private bool _melodyFinished;
    private bool _done;
    private long _framesAtStart;
    private float _sfxPeak;
    private float _uiPeak;

    public static QaAudio? FromArgs(IReadOnlyList<string> args) => args.Contains("--qa-audio") ? new QaAudio() : null;

    /// <summary>Call once per frame. Returns the exit code once the check has run, else null.</summary>
    public ExitCode? Update(AudioServer? audio)
    {
        if (_done)
            return null;
        if (audio is null)
        {
            _done = true;
            Log.Error("[QA] Audio: no AudioServer registered.");
            return ExitCode.Error;
        }

        if (!_clock.IsRunning)
        {
            _clock.Start();
            _framesAtStart = audio.Stats.RenderedFrames;
            Log.Info($"[QA] Audio device: {audio.DeviceName}, {audio.SampleRate} Hz, {audio.TotalVoices} voices");
        }

        var t = _clock.Elapsed.TotalSeconds;
        if (!_melody.IsValid && t >= MelodyAt)
        {
            _melody = audio.PlayOneShot(Melody(audio.SampleRate), bus: "UI", volumeDb: -14f, processMode: ProcessMode.Always);
            Log.Info($"[QA] Audio: playing the test melody ({(_melody.IsValid ? "ok" : "REJECTED")})");
        }

        if (_melody.IsValid && !audio.IsPlaying(_melody))
            _melodyFinished = true;
        _sfxPeak = Math.Max(_sfxPeak, audio.GetBus("SFX")?.Peak ?? 0f); // the scene's streamed ambience
        _uiPeak = Math.Max(_uiPeak, audio.GetBus("UI")?.Peak ?? 0f); // the melody

        if (t < CheckAt)
            return null;

        _done = true;
        var stats = audio.Stats;
        var rendered = stats.RenderedFrames - _framesAtStart;
        var expected = (long)(t * audio.SampleRate);
        var problems = new List<string>();
        if (audio.IsNullDevice)
            problems.Add("no real output device (null device)");
        if (rendered < expected * 0.8)
            problems.Add($"rendered {rendered} frames, expected about {expected}");
        if (stats.Faulted)
            problems.Add("the mixer faulted");
        if (stats.StreamErrors > 0)
            problems.Add($"{stats.StreamErrors} stream errors");
        if (!_melodyFinished)
            problems.Add("the test melody did not finish");
        if (_uiPeak < 0.01f)
            problems.Add($"the UI bus never carried the melody (peak {_uiPeak:0.000})");
        if (_sfxPeak <= 0f)
            problems.Add("the SFX bus never carried the ambience");

        Log.Info($"[QA] Audio: {rendered} frames in {t:0.00} s ({rendered / t:0} Hz), {stats.Blocks} blocks, " +
                 $"{stats.ActiveVoices} active voices, {stats.Underruns} underruns, {stats.Steals} steals, " +
                 $"peaks UI {AudioMath.LinearToDb(_uiPeak):0.0} dB / SFX {AudioMath.LinearToDb(_sfxPeak):0.0} dB");
        if (problems.Count == 0)
        {
            Log.Info("[QA] Audio OK");
            return ExitCode.Ok;
        }

        Log.Error("[QA] Audio FAILED: " + string.Join("; ", problems));
        return ExitCode.Error;
    }

    // C5 E5 G5 C6, 0.2 s each with a short attack/release: obviously "the engine works" when heard.
    private static AudioStream Melody(int sampleRate)
    {
        float[] notes = [523.25f, 659.25f, 783.99f, 1046.5f];
        var noteFrames = sampleRate / 5;
        var samples = new float[notes.Length * noteFrames];
        for (var n = 0; n < notes.Length; n++)
        {
            for (var i = 0; i < noteFrames; i++)
            {
                var envelope = Math.Min(1f, Math.Min(i / (0.01f * sampleRate), (noteFrames - i) / (0.05f * sampleRate)));
                samples[n * noteFrames + i] = 0.5f * envelope * MathF.Sin(2 * MathF.PI * notes[n] * i / sampleRate);
            }
        }

        return AudioStream.FromSamples(samples, 1, sampleRate, "QA melody");
    }
}

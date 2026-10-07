using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// An in-process <see cref="IPluginHost"/> for tests (music editor proposal, "Testing"): scripted encode results,
/// simulated crashes and timeouts, with the real client's stop/restart semantics (a failed request stops the host and
/// raises <see cref="Stopped"/>; the next request restarts it and raises <see cref="Restarted"/>).
/// </summary>
internal sealed class FakePluginHost : IPluginHost
{
    private bool _crashed;
    private int _pid = 1000;

    /// <summary>Performs an encode (default: copies the WAV bytes to the output, which is not a real Ogg file).</summary>
    public Func<string, string, int, PluginHostEncodeResult> EncodeHandler { get; set; } = static (wav, ogg, _) =>
    {
        File.Copy(wav, ogg, overwrite: true);
        return new PluginHostEncodeResult(0, 0, 2);
    };

    /// <summary>The next request behaves as if the helper crashed during it.</summary>
    public bool CrashOnNextRequest { get; set; }

    /// <summary>The next request behaves as if the helper missed its deadline.</summary>
    public bool TimeoutOnNextRequest { get; set; }

    public List<string> Requests { get; } = [];

    public int Starts { get; private set; }

    public bool IsRunning => Info is not null;

    public PluginHostInfo? Info { get; private set; }

    public event Action<PluginHostStop>? Stopped;

    public event Action<PluginHostInfo>? Restarted;

    public PluginHostInfo Start()
    {
        if (Info is not null)
            return Info;
        Starts++;
        Info = new PluginHostInfo(PluginHostProtocol.Version, "fake", PluginHostCapabilities.Encode, ++_pid);
        if (_crashed)
        {
            _crashed = false;
            Restarted?.Invoke(Info);
        }

        return Info;
    }

    public void Ping(TimeSpan? timeout = null) => Request("ping");

    public PluginHostEncodeResult Encode(string wavPath, string oggPath, int quality, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Request("encode");
        return EncodeHandler(wavPath, oggPath, quality);
    }

    public void Dispose() => Info = null;

    private void Request(string what)
    {
        Start();
        Requests.Add(what);
        if (CrashOnNextRequest)
        {
            CrashOnNextRequest = false;
            Stop("Plugin host stopped (exit code 139)", what);
            throw new PluginHostException($"Plugin host stopped (exit code 139) during {what}.");
        }

        if (TimeoutOnNextRequest)
        {
            TimeoutOnNextRequest = false;
            Stop($"Plugin host did not answer {what} within 10 s", what);
            throw new PluginHostTimeoutException($"The plugin host did not answer {what} within 10 s.");
        }
    }

    private void Stop(string reason, string what)
    {
        Info = null;
        _crashed = true;
        Stopped?.Invoke(new PluginHostStop(reason, what));
    }
}

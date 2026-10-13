namespace MainframeEngine;

/// <summary>A span of the recording's timeline in seconds. <see cref="Label"/> is set for Keep and Skip.</summary>
internal sealed record CaptureSpan(double Start, double End, string? Label = null);

/// <summary>A point the game marked, in seconds.</summary>
internal sealed record CaptureMarker(double Time, string Label);

/// <summary>Everything the sidecar lists, sorted by time.</summary>
internal sealed record CaptureSpans(
    IReadOnlyList<CaptureSpan> Input,
    IReadOnlyList<CaptureSpan> Loading,
    IReadOnlyList<CaptureSpan> Paused,
    IReadOnlyList<CaptureSpan> Keep,
    IReadOnlyList<CaptureSpan> Skip,
    IReadOnlyList<CaptureMarker> Markers);

/// <summary>
/// Builds the sidecar's spans from per-frame state and events, all on the recording clock. Input: an
/// event holds the span open 250 ms, a held control keeps it open, and a gap under 100 ms rejoins the
/// previous span. Loading and pause follow flags polled once per frame. Keep/Skip are explicit scopes.
/// </summary>
internal sealed class CaptureTimeline
{
    private const double InputTail = 0.25;
    private const double MergeGap = 0.1;

    private readonly List<CaptureSpan> _input = [];
    private readonly List<CaptureSpan> _loading = [];
    private readonly List<CaptureSpan> _paused = [];
    private readonly List<CaptureSpan> _keep = [];
    private readonly List<CaptureSpan> _skip = [];
    private readonly List<CaptureMarker> _markers = [];
    private readonly Dictionary<int, (bool Keep, double Start, string Label)> _scopes = [];

    private double? _inputStart;
    private double _inputUntil;
    private double? _loadingStart;
    private double? _pausedStart;
    private int _nextHandle;

    public void Input(double t)
    {
        if (_inputStart is null)
        {
            if (_input.Count > 0 && t - _input[^1].End < MergeGap)
            {
                _inputStart = _input[^1].Start;
                _input.RemoveAt(_input.Count - 1);
            }
            else
            {
                _inputStart = t;
            }
        }

        _inputUntil = Math.Max(_inputUntil, t + InputTail);
    }

    public void Update(double t, bool held, bool loading, bool paused)
    {
        if (held && _inputStart is not null)
            _inputUntil = Math.Max(_inputUntil, t + InputTail);
        else if (held)
            Input(t);
        if (_inputStart is { } start && !held && t >= _inputUntil)
        {
            _input.Add(new CaptureSpan(start, _inputUntil));
            _inputStart = null;
        }

        Flag(ref _loadingStart, loading, t, _loading);
        Flag(ref _pausedStart, paused, t, _paused);
    }

    public void Mark(double t, string label) => _markers.Add(new CaptureMarker(t, label));

    public int OpenKeep(double t, string label) => Open(keep: true, t, label);

    public int OpenSkip(double t, string label) => Open(keep: false, t, label);

    public void Close(int handle, double t)
    {
        if (!_scopes.Remove(handle, out var scope))
            return;
        (scope.Keep ? _keep : _skip).Add(new CaptureSpan(scope.Start, Math.Max(scope.Start, t), scope.Label));
    }

    /// <summary>Closes everything still open at <paramref name="t"/> (the end of the video) and returns the spans.</summary>
    public CaptureSpans Finish(double t)
    {
        if (_inputStart is { } start)
            _input.Add(new CaptureSpan(start, Math.Min(_inputUntil, t)));
        _inputStart = null;
        Flag(ref _loadingStart, false, t, _loading);
        Flag(ref _pausedStart, false, t, _paused);
        foreach (var handle in _scopes.Keys.ToArray())
            Close(handle, t);

        static List<CaptureSpan> Clip(IEnumerable<CaptureSpan> spans, double end) =>
            [.. spans.Select(s => s with { End = Math.Min(s.End, end) }).Where(s => s.End > s.Start).OrderBy(s => s.Start)];

        return new CaptureSpans(Clip(_input, t), Clip(_loading, t), Clip(_paused, t), Clip(_keep, t), Clip(_skip, t),
            [.. _markers.Where(m => m.Time <= t).OrderBy(m => m.Time)]);
    }

    private int Open(bool keep, double t, string label)
    {
        var handle = ++_nextHandle;
        _scopes[handle] = (keep, t, label);
        return handle;
    }

    private static void Flag(ref double? start, bool on, double t, List<CaptureSpan> into)
    {
        if (on && start is null)
            start = t;
        else if (!on && start is { } s)
        {
            if (t > s)
                into.Add(new CaptureSpan(s, t));
            start = null;
        }
    }
}

namespace MainframeEngine;

/// <summary>
/// One loader's progress in a <see cref="SceneLoad"/> (an <see cref="ISceneLoadable"/>, or a stage of the load itself):
/// a fraction from 0 to 1 and a stage label for the loading screen ("Growing trees"). Thread-safe: a worker reports, the
/// main thread reads.
/// </summary>
public sealed class SceneLoadProgress
{
    private readonly Lock _gate = new();
    private float _fraction;
    private string? _stage;

    internal SceneLoadProgress(CancellationToken cancellation, float weight = 1f)
    {
        CancellationToken = cancellation;
        Weight = weight;
    }

    /// <summary>A reporter nobody reads (tests, and loadables called outside a <see cref="SceneLoad"/>).</summary>
    public static SceneLoadProgress None => new(CancellationToken.None);

    /// <summary>Cancelled when the load is (<see cref="SceneLoad.Cancel"/>, or another scene change replaced it).</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The reporter's share of its stage (<see cref="ISceneLoadable.LoadWeight"/>).</summary>
    public float Weight { get; }

    /// <summary>The last reported fraction, 0–1 (never decreases).</summary>
    public float Fraction
    {
        get
        {
            lock (_gate)
                return _fraction;
        }
    }

    /// <summary>The last reported stage label, or null.</summary>
    public string? Stage
    {
        get
        {
            lock (_gate)
                return _stage;
        }
    }

    /// <summary>The time of the last report (<see cref="Environment.TickCount64"/>), so the newest label wins.</summary>
    internal long ReportedAt { get; private set; }

    /// <summary>Reports <paramref name="fraction"/> (clamped to 0–1, kept monotonic) and, when not null, a new stage label.</summary>
    public void Report(float fraction, string? stage = null)
    {
        lock (_gate)
        {
            if (float.IsFinite(fraction))
                _fraction = Math.Max(_fraction, Math.Clamp(fraction, 0f, 1f));
            if (stage is not null)
            {
                _stage = stage;
                ReportedAt = Environment.TickCount64;
            }
        }
    }

    /// <summary>Reports a new stage label without changing the fraction.</summary>
    public void Report(string stage) => Report(float.NaN, stage);

    /// <summary>Throws <see cref="OperationCanceledException"/> when the load was cancelled.</summary>
    public void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();
}

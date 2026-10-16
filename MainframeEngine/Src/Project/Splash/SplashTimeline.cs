namespace MainframeEngine;

/// <summary>Where a <see cref="SplashTimeline"/> is.</summary>
public enum SplashPhase
{
    FadeIn,
    Hold,
    FadeOut,

    /// <summary>The last card, held at full opacity until the start scene's load completes.</summary>
    WaitingForLoad,
    Done,
}

/// <summary>
/// The splash sequence's clock (BRUTAL B1, ADR 0192): per card a <see cref="FadeSeconds"/> fade in, the hold, a
/// <see cref="FadeSeconds"/> fade out; after the last card's hold it waits for the start scene's load
/// (<see cref="SplashPhase.WaitingForLoad"/>) before fading out. A press ends a hold early once it has run
/// <see cref="SkipFloorSeconds"/>, so every card, BRUTAL's included, is legible. Pure state: no engine objects, no
/// allocation; <see cref="SplashSequence"/> drives it with the frame's delta.
/// </summary>
public sealed class SplashTimeline
{
    public const float FadeSeconds = 0.3f;

    /// <summary>No card's hold can be skipped before it has run this long.</summary>
    public const float SkipFloorSeconds = 1.5f;

    /// <summary>The most one <see cref="Advance"/> counts: a frame hitch never jumps over a card.</summary>
    public const float MaxStepSeconds = 0.1f;

    public SplashTimeline(int cardCount, float holdSeconds, bool skippable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cardCount);
        if (float.IsNaN(holdSeconds))
            throw new ArgumentOutOfRangeException(nameof(holdSeconds), holdSeconds, "The hold must be a number of seconds.");
        CardCount = cardCount;
        HoldSeconds = Math.Clamp(holdSeconds, SplashProjectSettings.MinHoldSeconds, SplashProjectSettings.MaxHoldSeconds);
        Skippable = skippable;
        Phase = cardCount == 0 ? SplashPhase.Done : SplashPhase.FadeIn;
    }

    public int CardCount { get; }

    /// <summary>Seconds each card is held, clamped like <see cref="SplashProjectSettings.HoldSeconds"/>.</summary>
    public float HoldSeconds { get; }

    public bool Skippable { get; }

    public int CardIndex { get; private set; }

    public SplashPhase Phase { get; private set; }

    /// <summary>Seconds into <see cref="Phase"/>.</summary>
    public float PhaseTime { get; private set; }

    public bool IsDone => Phase == SplashPhase.Done;

    public bool IsLastCard => CardIndex == CardCount - 1;

    /// <summary>The current card's nominal opacity (the document fades with RCSS transitions of the same length).</summary>
    public float Opacity => Phase switch
    {
        SplashPhase.FadeIn => Math.Clamp(PhaseTime / FadeSeconds, 0f, 1f),
        SplashPhase.Hold or SplashPhase.WaitingForLoad => 1f,
        SplashPhase.FadeOut => 1f - Math.Clamp(PhaseTime / FadeSeconds, 0f, 1f),
        _ => 0f,
    };

    /// <summary>A press would end the current hold now.</summary>
    public bool CanSkip => Skippable && Phase == SplashPhase.Hold && PhaseTime >= SkipFloorSeconds;

    /// <summary>Ends the current hold (straight to its fade out) when <see cref="CanSkip"/>; true when it did.</summary>
    public bool TrySkip()
    {
        if (!CanSkip)
            return false;
        Enter(SplashPhase.FadeOut, 0f);
        return true;
    }

    /// <summary>Ends the sequence now (a failed start load: the host takes the error path).</summary>
    public void Abort() => Enter(SplashPhase.Done, 0f);

    /// <summary>Moves the clock by <paramref name="deltaSeconds"/> (clamped to 0–<see cref="MaxStepSeconds"/>).</summary>
    /// <param name="loadCompleted">The start scene's load has completed (or there is none): the last card may fade out.</param>
    public void Advance(float deltaSeconds, bool loadCompleted)
    {
        if (IsDone)
            return;
        PhaseTime += float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0f, MaxStepSeconds) : 0f;
        while (true)
        {
            switch (Phase)
            {
                case SplashPhase.FadeIn when PhaseTime >= FadeSeconds:
                    Enter(SplashPhase.Hold, PhaseTime - FadeSeconds);
                    continue;
                case SplashPhase.Hold when PhaseTime >= HoldSeconds:
                    if (IsLastCard && !loadCompleted)
                        Enter(SplashPhase.WaitingForLoad, 0f);
                    else
                        Enter(SplashPhase.FadeOut, PhaseTime - HoldSeconds);
                    continue;
                case SplashPhase.WaitingForLoad when loadCompleted:
                    Enter(SplashPhase.FadeOut, 0f);
                    continue;
                case SplashPhase.FadeOut when PhaseTime >= FadeSeconds:
                    if (IsLastCard)
                    {
                        Abort();
                        return;
                    }

                    var carry = PhaseTime - FadeSeconds;
                    CardIndex++;
                    Enter(SplashPhase.FadeIn, carry);
                    continue;
                default:
                    return;
            }
        }
    }

    private void Enter(SplashPhase phase, float time)
    {
        Phase = phase;
        PhaseTime = time;
    }
}

namespace MainframeEngine;

/// <summary>What one <see cref="PathMotion.Step"/> of a follower's speed movement crossed.</summary>
internal enum PathStepEvent : byte
{
    None,
    Looped,
    EndReached,
}

/// <summary>
/// The progress rules <see cref="PathFollow3D"/> and <see cref="PathFollow2D"/> share (G13): wrap or clamp a progress,
/// and advance it by a speed with loop, stop and ping-pong.
/// </summary>
internal static class PathMotion
{
    /// <summary><paramref name="value"/> on a curve of <paramref name="length"/>: wrapped when <paramref name="wrap"/>, else clamped (0 when not finite; unchanged without length).</summary>
    public static float Constrain(float value, float length, bool wrap)
    {
        if (!float.IsFinite(value))
            return 0f;
        if (length <= 0)
            return value;
        if (wrap)
        {
            var wrapped = value % length;
            return wrapped < 0 ? wrapped + length : wrapped;
        }

        return Math.Clamp(value, 0f, length);
    }

    /// <summary>
    /// Advances <paramref name="progress"/> by <paramref name="speed"/> × <paramref name="direction"/> ×
    /// <paramref name="delta"/> into <paramref name="next"/> (still to be constrained): past an end it loops when
    /// <paramref name="wrap"/>, turns back when <paramref name="pingPong"/> (flipping <paramref name="direction"/>), else
    /// stops; <paramref name="atEnd"/> keeps a stop from reporting twice.
    /// </summary>
    public static PathStepEvent Step(float progress, float speed, float delta, float length, bool wrap, bool pingPong,
        ref float direction, ref bool atEnd, out float next)
    {
        next = progress + speed * direction * delta;
        if (wrap)
            return next >= length || next < 0 ? PathStepEvent.Looped : PathStepEvent.None;

        if (next > length || next < 0)
        {
            if (pingPong)
            {
                next = next > length ? 2 * length - next : -next;
                direction = -direction;
                return PathStepEvent.EndReached;
            }

            if (!atEnd)
            {
                atEnd = true;
                return PathStepEvent.EndReached;
            }

            return PathStepEvent.None;
        }

        atEnd = false;
        return PathStepEvent.None;
    }
}

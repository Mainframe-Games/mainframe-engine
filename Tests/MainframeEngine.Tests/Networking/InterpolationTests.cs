using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>The interpolation buffer, the blends, and client-side interpolation end to end.</summary>
public sealed class InterpolationTests
{
    private static InterpolationTime At(double renderTick, uint latest, double maxExtrapolation = 0) => new(renderTick, latest, maxExtrapolation);

    private static float Sample(InterpolationBuffer<float> buffer, in InterpolationTime time)
    {
        Assert.True(buffer.Sample(in time, out var from, out var to, out var t));
        return NetLerp.Lerp(from, to, t);
    }

    [Fact]
    public void BlendsBetweenTheBracketingSamples()
    {
        var buffer = new InterpolationBuffer<float>();
        Assert.False(buffer.Sample(At(1, 1), out _, out _, out _));

        buffer.Push(10, 0f, 10);
        Assert.Equal(0f, Sample(buffer, At(12, 10)));
        buffer.Push(11, 10f, 10);
        buffer.Push(12, 30f, 11);

        Assert.Equal(0f, Sample(buffer, At(5, 12)));     // before the oldest: the oldest
        Assert.Equal(5f, Sample(buffer, At(10.5, 12)));
        Assert.Equal(10f, Sample(buffer, At(11, 12)));
        Assert.Equal(20f, Sample(buffer, At(11.5, 12)));
        Assert.Equal(3, buffer.Count);
        Assert.Equal(12u, buffer.NewestTick);
    }

    [Fact]
    public void AMemberThatWasStillHoldsUntilThePreviousSnapshot()
    {
        // Still at 0 from tick 10; the next change arrives with the snapshot of tick 20 (previous snapshot: 19).
        var buffer = new InterpolationBuffer<float>();
        buffer.Push(10, 0f, 10);
        buffer.Push(20, 10f, 19);
        Assert.Equal(3, buffer.Count);                    // a hold sample at 19
        Assert.Equal(0f, Sample(buffer, At(15, 20)));     // not 5: it had not started moving
        Assert.Equal(5f, Sample(buffer, At(19.5, 20)));
    }

    [Fact]
    public void ExtrapolatesLateDataButOnlyAsFarAsTheClamp()
    {
        var buffer = new InterpolationBuffer<float>();
        buffer.Push(10, 0f, 10);
        buffer.Push(11, 1f, 10);

        Assert.Equal(2f, Sample(buffer, At(12, 11, maxExtrapolation: 3)));
        Assert.Equal(3f, Sample(buffer, At(15, 11, maxExtrapolation: 2))); // clamped at 2 ticks past the newest
        Assert.Equal(1f, Sample(buffer, At(15, 11, maxExtrapolation: 0)));

        // A newer snapshot came without this member: it is known to be still, so it holds.
        Assert.Equal(1f, Sample(buffer, At(15, 14, maxExtrapolation: 3)));
    }

    [Fact]
    public void KeepsTheNewestSamplesAndIgnoresOldOnes()
    {
        var buffer = new InterpolationBuffer<float>();
        for (uint tick = 1; tick <= 40; tick++)
            buffer.Push(tick, tick, tick - 1);
        Assert.Equal(InterpolationBuffer<float>.Capacity, buffer.Count);
        Assert.Equal(9f, Sample(buffer, At(0, 40))); // the oldest kept

        buffer.Push(30, -1f, 29);                     // older than the newest: ignored
        buffer.Push(40, 400f, 39);                    // same tick: replaced
        Assert.Equal(400f, Sample(buffer, At(40, 40)));
        Assert.Equal(219.5f, Sample(buffer, At(39.5, 40)));

        buffer.Clear();
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void BlendsEveryInterpolatableType()
    {
        Assert.Equal(5.0, NetLerp.Lerp(0.0, 10.0, 0.5f));
        Assert.Equal(new Vector2(1, 2), NetLerp.Lerp(Vector2.Zero, new Vector2(2, 4), 0.5f));
        Assert.Equal(new Vector3(3, 0, 0), NetLerp.Lerp(Vector3.Zero, Vector3.UnitX, 3f)); // unclamped
        Assert.Equal(new Vector4(1), NetLerp.Lerp(Vector4.Zero, new Vector4(2), 0.5f));

        var a = Quaternion.Identity;
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 18); // 10°
        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 36), NetLerp.Lerp(a, b, 0.5f));
        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 9), NetLerp.Lerp(a, b, 2f)); // extrapolated 20°
        AssertRotation(b, NetLerp.Lerp(b, b, 3f));
        AssertRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 9), NetLerp.Lerp(a, -b, 2f)); // shortest path
    }

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 0.9999f, $"expected {expected}, got {actual} (|dot| {dot})");
    }

    [Fact]
    public void ClientsShowInterpolatedMotionDelayedByTheInterpolationDelay()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var copy = client.Find<NetBox>(box.NetworkId)!;

        // Server: constant velocity along X, recorded at every network tick.
        var samples = new SortedDictionary<uint, float>();
        var checkedFrames = 0;
        for (var frame = 0; frame < 240; frame++)
        {
            box.NetPosition += new Vector3(0.05f, 0, 0);
            net.Step();
            samples.TryAdd(net.Server.Tick, box.Position.X); // captured when the tick advanced

            var render = client.Api.RenderTick;
            if (frame < 30 || !TryInterpolate(samples, render, out var expected))
                continue;
            Assert.Equal(expected, copy.Position.X, 3);
            Assert.True(copy.Position.X < box.Position.X, "the client shows the past");
            checkedFrames++;
        }

        Assert.True(checkedFrames > 150, $"only {checkedFrames} frames had a bracketing pair");
        // About InterpolationDelay (0.1 s = 3 ticks at 30 Hz = 6 frames of 0.05) behind.
        Assert.InRange(box.Position.X - copy.Position.X, 0.2f, 0.45f);
    }

    [Fact]
    public void ExtrapolationStopsAtTheClampWhenSnapshotsStop()
    {
        using var net = new NetHarness(conditions: NetworkConditions.None);
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var copy = client.Find<NetBox>(box.NetworkId)!;
        for (var frame = 0; frame < 60; frame++)
        {
            box.NetPosition += new Vector3(0.05f, 0, 0);
            net.Step();
        }

        // Every snapshot is lost from now on, while the box keeps moving on the server.
        ((SimulatedTransport)net.Server.Bus!.Transport).Conditions = new NetworkConditions { Loss = 1 };
        var lastReceived = 0f;
        for (var frame = 0; frame < 60; frame++)
        {
            box.NetPosition += new Vector3(0.05f, 0, 0);
            net.Step();
            if (frame == 0)
                lastReceived = box.Position.X - 0.05f; // the last snapshot sent before the loss
        }

        // 0.25 s of extrapolation at 3 units/s (0.05 per frame) = 0.75 past the newest sample, then it stops.
        var stoppedAt = copy.Position.X;
        net.Step(30);
        Assert.Equal(stoppedAt, copy.Position.X);
        Assert.InRange(stoppedAt, lastReceived + 0.6f, lastReceived + 0.85f);
    }

    [Fact]
    public void StillMembersDoNotDriftAndInterpolationCanBeDisabled()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        box.NetPosition = new Vector3(5, 0, 0);
        net.Step(3);
        var copy = client.Find<NetBox>(box.NetworkId)!;
        Assert.Equal(new Vector3(5, 0, 0), copy.Position); // spawn state applies at once
        net.Step(60);
        Assert.Equal(new Vector3(5, 0, 0), copy.Position);

        // Moving after standing still: blends from the hold sample, never moves before the server did.
        box.NetPosition = new Vector3(6, 0, 0);
        net.Step(2);
        Assert.Equal(new Vector3(5, 0, 0), copy.Position); // still in the delayed past
        net.Step(30);
        Assert.Equal(new Vector3(6, 0, 0), copy.Position);

        client.Api.Interpolation = false;
        box.NetPosition = new Vector3(7, 0, 0);
        net.Step(3);
        Assert.Equal(new Vector3(7, 0, 0), copy.Position); // snaps on arrival
    }

    private static bool TryInterpolate(SortedDictionary<uint, float> samples, double tick, out float value)
    {
        uint? before = null, after = null;
        foreach (var key in samples.Keys)
        {
            if (key <= tick)
                before = key;
            if (key >= tick)
            {
                after = key;
                break;
            }
        }

        value = 0;
        if (before is null || after is null)
            return false;
        if (before == after)
        {
            value = samples[before.Value];
            return true;
        }

        var t = (float)((tick - before.Value) / (after.Value - before.Value));
        value = samples[before.Value] + (samples[after.Value] - samples[before.Value]) * t;
        return true;
    }
}

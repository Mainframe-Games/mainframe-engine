using MainframeEngine;

namespace Demo.Tests;

/// <summary>Ticks a <see cref="SceneTree"/> with a fixed 60 Hz <see cref="GameTime"/> (what the game loop would pass).</summary>
internal static class TestTime
{
    public static void Tick(SceneTree tree, ref uint frame)
    {
        // GameTime's setters are internal to the engine; a boxed copy lets a test game loop fill one in.
        object time = default(GameTime);
        typeof(GameTime).GetProperty(nameof(GameTime.DeltaTime))!.SetValue(time, 1f / 60f);
        typeof(GameTime).GetProperty(nameof(GameTime.FrameCount))!.SetValue(time, ++frame);
        tree.Tick((GameTime)time);
    }
}

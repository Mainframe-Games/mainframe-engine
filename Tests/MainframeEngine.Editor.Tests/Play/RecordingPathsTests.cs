namespace MainframeEngine.Editor.Tests.Play;

public sealed class RecordingPathsTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 10, 15, 0);

    [Fact]
    public void NamesTheFileAfterTheSceneAndTime()
    {
        Assert.Equal(Path.Combine("/u", "recordings", "forest-20261013-101500.mp4"),
            RecordingPaths.NewPath("/u", "Assets/Scenes/forest.mscene", Now, _ => false));
    }

    [Fact]
    public void FallsBackToGameWithoutAScene()
    {
        Assert.EndsWith("game-20261013-101500.mp4", RecordingPaths.NewPath("/u", null, Now, _ => false));
    }

    [Fact]
    public void AddsASuffixWhenTheNameIsTaken()
    {
        var taken = new HashSet<string> { Path.Combine("/u", "recordings", "game-20261013-101500.mp4") };
        Assert.EndsWith("game-20261013-101500-2.mp4", RecordingPaths.NewPath("/u", null, Now, taken.Contains));
    }

    [Fact]
    public void UnsafeSceneNameCharactersAreReplaced()
    {
        Assert.EndsWith("my_scene-20261013-101500.mp4", RecordingPaths.NewPath("/u", "Assets/my scene.mscene", Now, _ => false));
    }
}

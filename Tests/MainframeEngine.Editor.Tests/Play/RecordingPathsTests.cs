namespace MainframeEngine.Editor.Tests.Play;

public sealed class RecordingPathsTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 10, 15, 0);

    [Fact]
    public void ProjectRecordingsGoUnderAssetsVideo()
    {
        var directory = RecordingPaths.Directory("/u");
        Assert.Equal(Path.Combine("/u", "Assets", "Video", "Recordings"), directory);
        Assert.True(ContentPaths.IsEditorOnly(Path.GetRelativePath("/u", directory)), directory);
    }

    [Fact]
    public void NamesTheFileAfterTheSceneAndTime()
    {
        Assert.Equal(Path.Combine("/u", "Assets", "Video", "Recordings", "forest-20261013-101500.mp4"),
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
        var taken = new HashSet<string> { Path.Combine("/u", "Assets", "Video", "Recordings", "game-20261013-101500.mp4") };
        Assert.EndsWith("game-20261013-101500-2.mp4", RecordingPaths.NewPath("/u", null, Now, taken.Contains));
    }

    [Fact]
    public void UnsafeSceneNameCharactersAreReplaced()
    {
        Assert.EndsWith("my_scene-20261013-101500.mp4", RecordingPaths.NewPath("/u", "Assets/my scene.mscene", Now, _ => false));
    }
}

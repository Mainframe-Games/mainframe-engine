namespace MainframeEngine.Editor.Tests.Updates;

public sealed class EditorSettingsUpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-settings").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void CheckForUpdatesDefaultsToOn() => Assert.True(new EditorSettings().CheckForUpdates);

    [Fact]
    public void CheckForUpdatesIsSavedLoadedAndCloned()
    {
        var path = Path.Combine(_directory, "editor_settings.json");
        new EditorSettings { CheckForUpdates = false }.Save(path);

        Assert.False(EditorSettings.Load(path).CheckForUpdates);
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(new EditorSettings { CheckForUpdates = false }.Clone().CheckForUpdates);
    }

    [Fact]
    public void SettingsFilesWithoutTheFieldKeepChecksOn()
    {
        var path = Path.Combine(_directory, "editor_settings.json");
        File.WriteAllText(path, """{ "format": 1, "accent": "#3b82f6" }""");
        Assert.True(EditorSettings.Load(path).CheckForUpdates);
    }
}

[Collection(nameof(SerialEditor))]
public sealed class EditorSettingsDialogUpdateTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    [Fact]
    public void TheDialogAppliesTheUpdateCheckSetting()
    {
        var w = _editor.Workspace;
        w.EditorSettingsDialog.Open();
        _editor.Tick();
        w.EditorSettingsDialog.Working.CheckForUpdates = false;
        w.EditorSettingsDialog.Apply();
        Assert.False(w.Settings.CheckForUpdates);
        Assert.Empty(_editor.RmlMessages);
    }
}

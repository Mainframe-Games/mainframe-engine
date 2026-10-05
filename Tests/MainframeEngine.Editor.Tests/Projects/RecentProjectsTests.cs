namespace MainframeEngine.Editor.Tests.Projects;

public sealed class RecentProjectsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-recent").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "recent_projects.json");

    private string Project(string name, bool withProjectFile = false)
    {
        var path = Path.Combine(_directory, name);
        if (withProjectFile)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ProjectSettings.FileName), "{ \"format\": 1, \"name\": \"" + name + "\" }");
        }

        return path;
    }

    [Fact]
    public void TouchPutsTheProjectFirstAndMovesExistingEntriesUp()
    {
        var recent = new RecentProjects(null);
        recent.Touch(Project("A"), "A");
        recent.Touch(Project("B"), "B");
        recent.Touch(Project("C"), "C");
        Assert.Equal(["C", "B", "A"], recent.Items.Select(p => p.Name));

        recent.Touch(Project("A") + Path.DirectorySeparatorChar, "A renamed"); // same folder, trailing separator
        Assert.Equal(["A renamed", "C", "B"], recent.Items.Select(p => p.Name));
        Assert.Equal(3, recent.Items.Count);
        Assert.Equal(Project("A"), recent.Items[0].Path);
        Assert.True(recent.Items[0].LastOpenedUtc >= recent.Items[1].LastOpenedUtc);
        Assert.Equal(DateTimeKind.Utc, recent.Items[0].LastOpenedUtc.Kind);
    }

    [Fact]
    public void AddKeepsTheGivenTimeInDateOrder()
    {
        var now = DateTime.UtcNow;
        var recent = new RecentProjects(FilePath);
        recent.Add(new RecentProject(Project("Old"), "Old", now.AddDays(-5)));
        recent.Add(new RecentProject(Project("New"), "New", now));
        recent.Add(new RecentProject(Project("Mid"), "Mid", now.AddHours(-26)));
        Assert.Equal(["New", "Mid", "Old"], recent.Items.Select(p => p.Name));

        recent.Add(new RecentProject(Project("Old"), "Old again", now.AddMinutes(1))); // same folder: replaced, moved up
        Assert.Equal(["Old again", "New", "Mid"], recent.Items.Select(p => p.Name));
        Assert.Equal(["Old again", "New", "Mid"], RecentProjects.Load(FilePath).Items.Select(p => p.Name)); // saved
    }

    [Fact]
    public void KeepsAtMostMaxItems()
    {
        var recent = new RecentProjects(null);
        for (var i = 0; i < RecentProjects.MaxItems + 5; i++)
            recent.Touch(Project($"P{i}"), $"P{i}");

        Assert.Equal(RecentProjects.MaxItems, recent.Items.Count);
        Assert.Equal($"P{RecentProjects.MaxItems + 4}", recent.Items[0].Name);
        Assert.DoesNotContain(recent.Items, p => p.Name == "P0");
    }

    [Fact]
    public void RemoveDropsTheEntryAndSaves()
    {
        var recent = new RecentProjects(FilePath);
        recent.Touch(Project("A"), "A");
        recent.Touch(Project("B"), "B");

        Assert.True(recent.Remove(Project("A")));
        Assert.False(recent.Remove(Project("A")));
        Assert.Equal(["B"], RecentProjects.Load(FilePath).Items.Select(p => p.Name));
    }

    [Fact]
    public void PathsCompareCaseInsensitivelyOnWindowsOnly()
    {
        var recent = new RecentProjects(null);
        recent.Touch(Project("Game"), "Game");
        recent.Touch(Project("GAME"), "GAME");
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, recent.Items.Count);
    }

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var recent = new RecentProjects(FilePath);
        recent.Touch(Project("A"), "Alpha");
        recent.Touch(Project("B"), "Beta");

        var loaded = RecentProjects.Load(FilePath);
        Assert.Equal(recent.Items, loaded.Items);
        Assert.Equal(FilePath, loaded.FilePath);
        Assert.Contains("\"format\": 1", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp")); // written atomically, no temp file left
    }

    [Fact]
    public void LoadSortsNewestFirstAndDropsDuplicatesAndBadEntries()
    {
        var a = Project("A").Replace("\\", "\\\\", StringComparison.Ordinal);
        var b = Project("B").Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(FilePath, $$"""
            {
              "format": 1,
              // hand-edited
              "projects": [
                { "path": "{{a}}", "name": "A", "lastOpenedUtc": "2026-01-01T00:00:00Z" },
                { "path": "{{b}}", "name": "B", "lastOpenedUtc": "2026-03-01T00:00:00Z" },
                { "path": "{{a}}", "name": "A old", "lastOpenedUtc": "2025-01-01T00:00:00Z" },
                { "path": "relative/folder", "name": "R", "lastOpenedUtc": "2026-02-01T00:00:00Z" },
                { "name": "no path", "lastOpenedUtc": "2026-02-01T00:00:00Z" },
                null,
              ],
            }
            """);

        var loaded = RecentProjects.Load(FilePath);
        Assert.Equal(["B", "A"], loaded.Items.Select(p => p.Name));
        Assert.Equal(DateTimeKind.Utc, loaded.Items[0].LastOpenedUtc.Kind);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": 1, \"projects\": 42 }")]
    [InlineData("[]")]
    [InlineData("")]
    public void CorruptFileGivesAnEmptyListThatCanBeSavedOver(string content)
    {
        File.WriteAllText(FilePath, content);
        var recent = RecentProjects.Load(FilePath);
        Assert.Empty(recent.Items);

        recent.Touch(Project("A"), "A");
        Assert.Equal(["A"], RecentProjects.Load(FilePath).Items.Select(p => p.Name));
    }

    [Fact]
    public void NewerFormatIsIgnoredAndNeverOverwritten()
    {
        const string newer = "{ \"format\": 99, \"projects\": [], \"somethingNew\": true }";
        File.WriteAllText(FilePath, newer);

        var recent = RecentProjects.Load(FilePath);
        Assert.Empty(recent.Items);
        recent.Touch(Project("A"), "A");
        Assert.Single(recent.Items); // usable in memory
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Fact]
    public void MissingFileAndInMemoryListsAreEmpty()
    {
        Assert.Empty(RecentProjects.Load(FilePath).Items);
        Assert.False(File.Exists(FilePath));
        var memory = RecentProjects.Load(null);
        Assert.Null(memory.FilePath);
        memory.Touch(Project("A"), "A");
        memory.Save();
        Assert.Single(memory.Items);
    }

    [Fact]
    public void IsValidChecksTheProjectFileStillExists()
    {
        var valid = new RecentProject(Project("Good", withProjectFile: true), "Good", DateTime.UtcNow);
        var gone = new RecentProject(Project("Gone"), "Gone", DateTime.UtcNow);
        var noFile = new RecentProject(Directory.CreateDirectory(Project("Empty")).FullName, "Empty", DateTime.UtcNow);

        Assert.True(RecentProjects.IsValid(valid));
        Assert.False(RecentProjects.IsValid(gone));
        Assert.False(RecentProjects.IsValid(noFile));
    }

    [Fact]
    public void GuardsArguments()
    {
        var recent = new RecentProjects(null);
        Assert.Throws<ArgumentException>(() => recent.Touch(" ", "A"));
        Assert.Throws<ArgumentException>(() => recent.Touch(Project("A"), ""));
        Assert.Throws<ArgumentNullException>(() => recent.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => RecentProjects.IsValid(null!));
        Assert.EndsWith(Path.Combine(".mainframe", "recent_projects.json"), RecentProjects.DefaultPath, StringComparison.Ordinal);
    }
}

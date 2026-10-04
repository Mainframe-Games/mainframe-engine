namespace MainframeEngine.Editor.Tests.Projects;

public sealed class NewProjectValidationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-newproject").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("MyGame")]
    [InlineData("Game2")]
    [InlineData("my_game")]
    [InlineData("A")]
    [InlineData("Class")] // keywords are case-sensitive
    [InlineData("var")] // contextual keywords are identifiers
    [InlineData("Spinner")] // a namespace may share the name of a type inside it
    public void AcceptsIdentifierNames(string name) => Assert.Null(NewProjectValidation.ValidateName(name));

    [Theory]
    [InlineData("", "Enter a name")]
    [InlineData("   ", "Enter a name")]
    [InlineData("2Fast", "must start with a letter")]
    [InlineData("_Game", "must start with a letter")]
    [InlineData("My Game", "cannot contain spaces")]
    [InlineData("My-Game", "cannot contain '-'")]
    [InlineData("My.Game", "cannot contain '.'")]
    [InlineData("Jeué", "cannot contain 'é'")]
    [InlineData("class", "is a C# keyword")]
    [InlineData("namespace", "is a C# keyword")]
    [InlineData("MainframeEngine", "is a reserved name")]
    [InlineData("mainframeengine", "is a reserved name")]
    [InlineData("SYSTEM", "is a reserved name")]
    [InlineData("Microsoft", "is a reserved name")]
    [InlineData("con", "is a reserved name")]
    [InlineData("Node", "is the name of an engine type")]
    [InlineData("GameHost", "is the name of an engine type")]
    [InlineData("Vector3", "is the name of an engine type")]
    public void RejectsBadNamesWithAReason(string name, string reason)
    {
        var error = NewProjectValidation.ValidateName(name);
        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.Ordinal);
        Assert.EndsWith(".", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOverlongNames()
    {
        Assert.Null(NewProjectValidation.ValidateName(new string('a', NewProjectValidation.MaxNameLength - 1) + "Z"));
        Assert.Contains("too long", NewProjectValidation.ValidateName(new string('a', NewProjectValidation.MaxNameLength + 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void LocationMustExistAndTheTargetMustBeNewOrEmpty()
    {
        Assert.Null(NewProjectValidation.ValidateLocation(_directory, "Fresh"));

        Directory.CreateDirectory(Path.Combine(_directory, "Empty"));
        Assert.Null(NewProjectValidation.ValidateLocation(_directory, "Empty"));

        var full = Directory.CreateDirectory(Path.Combine(_directory, "Full")).FullName;
        File.WriteAllText(Path.Combine(full, "readme.txt"), "x");
        Assert.Contains("already exists and is not empty", NewProjectValidation.ValidateLocation(_directory, "Full"), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_directory, "AFile"), "x");
        Assert.Contains("already a file", NewProjectValidation.ValidateLocation(_directory, "AFile"), StringComparison.Ordinal);

        Assert.Contains("does not exist", NewProjectValidation.ValidateLocation(Path.Combine(_directory, "missing"), "Game"), StringComparison.Ordinal);
        Assert.Contains("is a file", NewProjectValidation.ValidateLocation(Path.Combine(_directory, "AFile"), "Game"), StringComparison.Ordinal);
        Assert.Contains("not a full folder path", NewProjectValidation.ValidateLocation("relative/folder", "Game"), StringComparison.Ordinal);
        Assert.Contains("Choose the folder", NewProjectValidation.ValidateLocation("", "Game"), StringComparison.Ordinal);
    }

    [Fact]
    public void EnginePathMustBeACheckout()
    {
        Assert.Contains("Choose the Mainframe Engine folder", NewProjectValidation.ValidateEnginePath(""), StringComparison.Ordinal);
        Assert.Contains("does not exist", NewProjectValidation.ValidateEnginePath(Path.Combine(_directory, "missing")), StringComparison.Ordinal);
        Assert.Contains("not a full folder path", NewProjectValidation.ValidateEnginePath("engine"), StringComparison.Ordinal);
        Assert.Contains("is not a Mainframe Engine checkout", NewProjectValidation.ValidateEnginePath(_directory), StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(_directory, "MainframeEngine"));
        File.WriteAllText(Path.Combine(_directory, "MainframeEngine", "MainframeEngine.csproj"), "<Project />");
        Assert.Null(NewProjectValidation.ValidateEnginePath(_directory));
    }

    [Fact]
    public void ValidateReportsTheFirstProblemAndTheRequestComposesTheFolder()
    {
        var request = new NewProjectRequest("bad name", Path.Combine(_directory, "missing"), "");
        Assert.Contains("cannot contain spaces", NewProjectValidation.Validate(request), StringComparison.Ordinal);
        Assert.Contains("does not exist", NewProjectValidation.Validate(request with { Name = "Good" }), StringComparison.Ordinal);
        Assert.Contains("Choose the Mainframe Engine folder", NewProjectValidation.Validate(request with { Name = "Good", ParentDirectory = _directory }), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_directory, "Good"), (request with { Name = "Good", ParentDirectory = _directory }).ProjectDirectory);
    }
}

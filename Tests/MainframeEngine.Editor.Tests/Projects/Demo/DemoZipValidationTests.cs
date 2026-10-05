namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoZipValidationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-demo-cli").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = DemoZipValidation.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void GoodZipIsOk()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "ok.zip"), DemoZips.ValidProject());
        var (exit, output, _) = Run("--validate-demo-zip", zip);
        Assert.Equal(0, exit);
        Assert.StartsWith("ok ", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output["ok ".Length..].Trim())); // the temp folder is gone
    }

    [Fact]
    public void BadZipExitsOneWithTheReason()
    {
        var files = DemoZips.ValidProject();
        files.Remove("MainframeEngine.Demo/project.mfproj");
        var zip = DemoZips.Write(Path.Combine(_directory, "bad.zip"), files);
        var (exit, _, error) = Run("--validate-demo-zip", zip);
        Assert.Equal(1, exit);
        Assert.Contains("project.mfproj", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileExitsOne() =>
        Assert.Equal(1, Run("--validate-demo-zip", Path.Combine(_directory, "nope.zip")).Exit);

    [Fact]
    public void MissingArgumentIsAUsageError() => Assert.Equal(2, Run("--validate-demo-zip").Exit);
}

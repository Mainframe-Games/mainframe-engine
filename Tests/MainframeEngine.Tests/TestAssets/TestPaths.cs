namespace MainframeEngine.Tests.TestAssets;

/// <summary>Locations of the repository's committed test content.</summary>
public static class TestPaths
{
    /// <summary>The repository root (the folder holding <c>MainframeEngine.slnx</c>), found above the test output.</summary>
    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("MainframeEngine.slnx not found above the test output.");
    }
}

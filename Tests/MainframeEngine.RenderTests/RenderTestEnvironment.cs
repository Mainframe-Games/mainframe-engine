using System.Reflection;

[assembly: AssemblyFixture(typeof(MainframeEngine.RenderTests.NewGoldensCleanup))]

namespace MainframeEngine.RenderTests;

/// <summary>Paths and switches shared by the render tests.</summary>
public static class RenderTestEnvironment
{
    private static string Metadata(string key) =>
        typeof(RenderTestEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"Missing assembly metadata '{key}'.");

    /// <summary><c>Tests/MainframeEngine.RenderTests/Goldens</c> in the source tree.</summary>
    public static string GoldensDirectory { get; } = Metadata("GoldensDirectory");

    /// <summary>Where actual/diff images and host output go: <c>$RENDER_TEST_ARTIFACTS</c> or <c>artifacts/render-tests</c>.</summary>
    public static string ArtifactsDirectory { get; } =
        Environment.GetEnvironmentVariable("RENDER_TEST_ARTIFACTS") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Metadata("RepositoryRoot"), "artifacts", "render-tests");

    /// <summary>Frames without a golden for their device's tag: <c>new-goldens/&lt;platform-tag&gt;/&lt;scene&gt;_frameNNNN.png</c>.</summary>
    public static string NewGoldensDirectory => Path.Combine(ArtifactsDirectory, "new-goldens");

    /// <summary><c>UPDATE_GOLDENS=1</c> rewrites the goldens for the current device instead of comparing.</summary>
    public static bool UpdateGoldens { get; } = Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";

    /// <summary>On CI every gate is mandatory: missing validation layers fail instead of skipping.</summary>
    public static bool IsCi { get; } = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Empties <see cref="RenderTestEnvironment.NewGoldensDirectory"/> before the run, so it lists only this run's frames.</summary>
public sealed class NewGoldensCleanup
{
    public NewGoldensCleanup()
    {
        if (Directory.Exists(RenderTestEnvironment.NewGoldensDirectory))
            Directory.Delete(RenderTestEnvironment.NewGoldensDirectory, recursive: true);
    }
}

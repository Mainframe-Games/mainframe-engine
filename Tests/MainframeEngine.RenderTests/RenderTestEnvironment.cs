using System.Reflection;

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

    /// <summary><c>UPDATE_GOLDENS=1</c> rewrites the goldens for the current device instead of comparing.</summary>
    public static bool UpdateGoldens { get; } = Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";

    /// <summary>On CI every gate is mandatory: missing validation layers fail instead of skipping.</summary>
    public static bool IsCi { get; } = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}

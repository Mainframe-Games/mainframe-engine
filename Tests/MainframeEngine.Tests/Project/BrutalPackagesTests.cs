using System.Xml.Linq;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL B1 (ADR 0192): every Ahwoo.BRUTAL package and its third-party dependencies are pinned exactly (upstream ranges are
/// minimum-only and the API is experimental), the engine references each BRUTAL package without its analyzers, and the
/// pinned versions are what the build resolved.
/// </summary>
public sealed class BrutalPackagesTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Ahwoo.BRUTAL.Collections"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Core"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Interop"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Logging"] = "[2026.10.2]",
        ["Ahwoo.BRUTAL.Memory"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Numerics"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Strings"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Jolt"] = "[2026.10.2]",
        ["CommunityToolkit.HighPerformance"] = "[8.4.2]",
        ["Microsoft.Extensions.Logging"] = "[10.0.0]",
        ["Microsoft.Extensions.Logging.Console"] = "[10.0.0]",
        ["Microsoft.Extensions.ObjectPool"] = "[11.0.0-preview.5.26302.115]",
    };

    [Fact]
    public void EveryBrutalPackageAndItsThirdPartyDependenciesArePinnedExactly()
    {
        var props = XDocument.Load(Path.Combine(TestPaths.RepositoryRoot(), "Directory.Packages.props"));
        var versions = props.Descendants("PackageVersion").ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);
        foreach (var (id, version) in Expected)
            Assert.True(versions.TryGetValue(id, out var pinned) && pinned == version, $"{id} must be pinned to {version} (found {pinned ?? "nothing"}).");
        Assert.All(versions.Where(v => v.Key.StartsWith("Ahwoo.BRUTAL.", StringComparison.Ordinal)),
            v => Assert.Matches(@"^\[\d{4}\.\d{1,2}\.\d+\]$", v.Value));
    }

    [Fact]
    public void TheEngineReferencesEachBrutalPackageWithoutItsAnalyzers()
    {
        var project = XDocument.Load(Path.Combine(TestPaths.RepositoryRoot(), "MainframeEngine", "MainframeEngine.csproj"));
        var references = project.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string?)e.Attribute("ExcludeAssets"));
        foreach (var id in Expected.Keys)
            Assert.True(references.ContainsKey(id), $"MainframeEngine.csproj does not reference {id}.");
        foreach (var id in Expected.Keys.Where(k => k.StartsWith("Ahwoo.BRUTAL.", StringComparison.Ordinal)))
            Assert.Equal("analyzers", references[id]);
    }

    [Fact]
    public void TheBuildResolvedThePinnedVersions()
    {
        var deps = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainframeEngine.Tests.deps.json"));
        foreach (var (id, version) in Expected)
            Assert.Contains($"\"{id}/{version.Trim('[', ']')}\"", deps, StringComparison.Ordinal);
    }
}

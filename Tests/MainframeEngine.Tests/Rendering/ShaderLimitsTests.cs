using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// limits.json is the single source of truth; the build generates ShaderLimits.g.cs and include/limits.slang
/// from it. These tests fail if either generated file is stale or edited by hand, or if a C# limit stops
/// being derived from the generated constants.
/// </summary>
public sealed partial class ShaderLimitsTests
{
    private static readonly string Repo = FindRepoRoot();
    private static readonly string ShaderDir = Path.Combine(Repo, "MainframeEngine", "Content", "Shaders");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (MainframeEngine.slnx) not found above the test output.");
    }

    private static List<(string Shader, string CSharp, int Value)> ReadJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ShaderDir, "limits.json")));
        return doc.RootElement.GetProperty("limits").EnumerateArray()
            .Select(e => (e.GetProperty("shader").GetString()!, e.GetProperty("csharp").GetString()!, e.GetProperty("value").GetInt32()))
            .ToList();
    }

    [Fact]
    public void GeneratedCSharpConstantsMatchTheJson()
    {
        var limits = ReadJson();
        var fields = typeof(LightEnvironment).Assembly.GetType("MainframeEngine.ShaderLimits", throwOnError: true)!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);

        Assert.Equal(limits.Count, fields.Count);
        foreach (var (_, name, value) in limits)
            Assert.Equal(value, fields[name]);
    }

    [Fact]
    public void GeneratedSlangHeaderMatchesTheJson()
    {
        var header = File.ReadAllText(Path.Combine(ShaderDir, "include", "limits.slang"));
        var defines = DefineRegex().Matches(header).ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        var limits = ReadJson();
        Assert.Equal(limits.Count, defines.Count);
        foreach (var (shader, _, value) in limits)
            Assert.Equal(value, defines[shader]);
    }

    [Fact]
    public void EngineLimitsComeFromTheGeneratedConstants()
    {
        var limits = ReadJson().ToDictionary(l => l.CSharp, l => l.Value);

        // Read through a dictionary so the analyzer does not mistake the constants for "expected" values.
        var engine = new Dictionary<string, int>
        {
            ["MaxDirectionalLights"] = LightEnvironment.MaxDirectional,
            ["MaxPointLights"] = LightEnvironment.MaxPoint,
            ["MaxSpotLights"] = LightEnvironment.MaxSpot,
            ["MaxShadowDirectional"] = ShadowSystem.MaxShadowDir,
            ["MaxShadowSpot"] = ShadowSystem.MaxShadowSpot,
            ["MaxShadowPoint"] = ShadowSystem.MaxShadowPoint,
            ["MaxShadowCascades"] = ShadowSystem.MaxCascades,
            ["MaxShadowAtlasMaps"] = ShaderLimits.MaxShadowAtlasMaps,
        };

        foreach (var (name, value) in engine)
            Assert.Equal(limits[name], value);
    }

    [Fact]
    public void ShadersUseTheSharedLimitsInsteadOfLocalDefines()
    {
        var limitNames = ReadJson().Select(l => l.Shader).ToHashSet();
        var offenders = Directory.EnumerateFiles(ShaderDir, "*.vk.*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".spv", StringComparison.Ordinal))
            .Where(p => DefineRegex().Matches(File.ReadAllText(p)).Any(m => limitNames.Contains(m.Groups[1].Value)))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"^#define\s+([A-Z_0-9]+)\s+(-?\d+)", RegexOptions.Multiline)]
    private static partial Regex DefineRegex();
}

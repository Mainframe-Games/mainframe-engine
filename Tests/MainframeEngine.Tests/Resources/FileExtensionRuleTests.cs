using System.Reflection;
using System.Text.RegularExpressions;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Resources;

/// <summary>
/// ADR 0249: every file extension the engine, editor, video library or tools define starts with <c>.mf</c>. The scan
/// reads each <c>const string …Extension… = ".x"</c> (and <c>…Suffix…</c>) in their sources; a third-party format or a
/// temporary name is listed below with its reason.
/// </summary>
public sealed partial class FileExtensionRuleTests
{
    // Not engine formats: someone else's, temporary, a folder or part of a file name.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        [".slang"] = "Slang's shader source",
        [".tmp"] = "a file still being written",
        [".baking.tmp"] = "a song bake still being written",
        [".partial"] = "a recording folder still being written",
        [".stems"] = "a folder named after its .mfmusic (Theme.stems)",
        [".Desktop.csproj"] = "an MSBuild project name",
        [".Launcher.csproj"] = "an MSBuild project name (older games)",
    };

    private static readonly string[] Sources = ["MainframeEngine", "MainframeEngine.Editor", "MainframeEngine.Video", "MainframeEngine.Generators", "Tools"];

    [GeneratedRegex(@"const string (?<name>\w*(?:Extension|Suffix)\w*) = ""(?<value>\.[^""]+)""")]
    private static partial Regex ExtensionConstant();

    /// <summary>The <c>name = value</c> of every extension constant on <paramref name="line"/> that breaks the rule.</summary>
    internal static IEnumerable<string> Violations(string line)
    {
        foreach (Match m in ExtensionConstant().Matches(line))
        {
            var value = m.Groups["value"].Value;
            if (!value.StartsWith(FileExtensions.Prefix, StringComparison.Ordinal) && !Exempt.ContainsKey(value))
                yield return $"{m.Groups["name"].Value} = \"{value}\"";
        }
    }

    [Fact]
    public void EveryEngineExtensionConstantStartsWithMf()
    {
        var root = TestPaths.RepositoryRoot();
        var hits = new List<string>();
        foreach (var source in Sources)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, source), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    foreach (var violation in Violations(lines[i]))
                        hits.Add($"{relative}:{i + 1}: {violation}");
            }

        Assert.True(hits.Count == 0,
            $"Engine file extensions start with {FileExtensions.Prefix} (ADR 0249); list a third-party or temporary one in " +
            $"{nameof(FileExtensionRuleTests)}.{nameof(Exempt)} with its reason:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void TheScanCatchesAnExtensionWithoutMf()
    {
        Assert.Equal(["Extension = \".scene\""], Violations("    public const string Extension = \".scene\";"));
        Assert.Equal(["LockSuffix = \".lock\""], Violations("private const string LockSuffix = \".lock\";"));
        Assert.Empty(Violations("    public const string Extension = \".mfscene\";"));
        Assert.Empty(Violations("    public const string Extension = \".slang\";")); // exempt
        Assert.Empty(Violations("    public const string Name = \".scene\";"));       // not an extension constant
    }

    [Fact]
    public void EveryFileExtensionsConstantStartsWithMf()
    {
        var values = typeof(FileExtensions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.True(values.Length >= 14);
        Assert.All(values, v => Assert.StartsWith(FileExtensions.Prefix, v, StringComparison.Ordinal));
    }

    [Fact]
    public void NoCurrentNameEndsWithALegacyOne()
    {
        // So Matches, FindExisting and the editor's migration never mistake a migrated file for an old one.
        foreach (var (legacy, _) in FileExtensions.Legacy)
            foreach (var (_, current) in FileExtensions.Legacy)
                Assert.False(("x" + current).EndsWith(legacy, StringComparison.OrdinalIgnoreCase), $"{current} ends with {legacy}");
    }

    [Fact]
    public void MatchesTakesTheCurrentAndTheLegacyName()
    {
        Assert.True(FileExtensions.Matches("a/Main.mfscene", FileExtensions.Scene));
        Assert.True(FileExtensions.Matches("a/Main.MSCENE", FileExtensions.Scene));
        Assert.False(FileExtensions.Matches("a/Main.mfres", FileExtensions.Scene));
        Assert.True(FileExtensions.Matches("x.png.meta", FileExtensions.Meta));
        Assert.True(FileExtensions.Matches("x.png.mfmeta", FileExtensions.Meta));
        Assert.True(FileExtensions.Matches("Assets.mfpak", FileExtensions.Pack)); // never had another name
    }

    [Fact]
    public void UpgradeMovesReferencedFormatsOnly()
    {
        Assert.Equal("Assets/Scenes/Main.mfscene", FileExtensions.Upgrade("Assets/Scenes/Main.mscene"));
        Assert.Equal("a.mfres", FileExtensions.Upgrade("a.mres"));
        Assert.Equal("a.mfmusic", FileExtensions.Upgrade("a.mmusic"));
        Assert.Equal("a.mfsong", FileExtensions.Upgrade("a.msong"));
        Assert.Equal("a.png.meta", FileExtensions.Upgrade("a.png.meta")); // sidecars are never named in content
        Assert.Equal("a.mresults", FileExtensions.Upgrade("a.mresults"));
        var unchanged = "Assets/Scenes/Main.mfscene";
        Assert.Same(unchanged, FileExtensions.Upgrade(unchanged));
    }

    [Fact]
    public void FindExistingPrefersTheCurrentName()
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { "a.png.meta" };
        Assert.Equal("a.png.meta", FileExtensions.FindExisting("a.png", FileExtensions.Meta, files.Contains));
        files.Add("a.png.mfmeta");
        Assert.Equal("a.png.mfmeta", FileExtensions.FindExisting("a.png", FileExtensions.Meta, files.Contains));
        Assert.Null(FileExtensions.FindExisting("b.png", FileExtensions.Meta, files.Contains));
        Assert.Equal(".meta", FileExtensions.LegacyOf(FileExtensions.Meta));
        Assert.Null(FileExtensions.LegacyOf(FileExtensions.Pack));
    }
}

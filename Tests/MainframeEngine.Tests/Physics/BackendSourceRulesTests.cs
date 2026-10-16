using System.Text.RegularExpressions;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Physics libraries stay in their backend folders (ADR 0192, B2): Jitter2 in <c>Src/Physics/3D/Jitter</c>, Jolt through
/// BRUTAL in <c>Src/Physics/3D/Jolt</c>, Box2D.NET in <c>Src/Physics/2D</c>. Counts imports (<c>using Jitter2…</c>) and
/// qualified names (<c>Jitter2.Logger</c>, <c>global::Jitter2</c>) in engine and editor sources and project-wide
/// <c>&lt;Using&gt;</c> items; comments are ignored, and members named like a library (<c>settings.Jitter2</c>,
/// <c>PhysicsBackendKind.Jitter2</c>) are not library uses.
/// </summary>
public sealed class BackendSourceRulesTests
{
    public static TheoryData<string, string, bool> Rules => new()
    {
        { "Jitter2", "MainframeEngine/Src/Physics/3D/Jitter/", true },
        { "Brutal.JoltApi", "MainframeEngine/Src/Physics/3D/Jolt/", false },
        { "Box2D.NET", "MainframeEngine/Src/Physics/2D/", true },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void LibraryNamespacesStayInTheirBackendFolder(string ns, string folder, bool usedThere)
    {
        var root = PhysicsTraceTests.RepoRoot();
        var name = Regex.Escape(ns);
        var use = new Regex($@"^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?{name}\b|(?<![\w.])(global::)?{name}\.[A-Z]", RegexOptions.Multiline);
        var violations = new List<string>();
        var found = false;
        foreach (var dir in (string[])["MainframeEngine/Src", "MainframeEngine.Editor/Src"])
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var comment = lines[i].IndexOf("//", StringComparison.Ordinal);
                    var code = comment < 0 ? lines[i] : lines[i][..comment];
                    if (!use.IsMatch(code))
                        continue;
                    if (relative.StartsWith(folder, StringComparison.Ordinal))
                        found = true;
                    else
                        violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        foreach (var project in (string[])["MainframeEngine/MainframeEngine.csproj", "MainframeEngine.Editor/MainframeEngine.Editor.csproj"])
            if (File.ReadAllText(Path.Combine(root, project)).Contains($"<Using Include=\"{ns}", StringComparison.Ordinal))
                violations.Add($"{project}: a project-wide <Using> of {ns}");

        Assert.True(violations.Count == 0, $"{ns} is used outside {folder}:\n" + string.Join('\n', violations));
        if (usedThere)
            Assert.True(found, $"No {ns} use found in {folder}: the rule's pattern no longer matches real uses.");
    }
}

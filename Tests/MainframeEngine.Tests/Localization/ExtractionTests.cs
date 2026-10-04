using System.Diagnostics;
using MainframeEngine.L10n.Extraction;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Localization;

/// <summary>Extraction from the fixture project: RML documents, scenes/resources, and C# through GetText.NET's extractor.</summary>
public sealed class ExtractionTests
{
    private static string Project => LocaleFixture.FixtureProject;

    private static Dictionary<string, PoEntry> Extracted(TemplateBuilder builder) => builder.Build("Fixture").ByKey();

    [Fact]
    public void ExtractsRmlRunsWithReferencesAndSkipsOptOuts()
    {
        var builder = new TemplateBuilder();
        var added = RmlExtractor.Extract(Path.Combine(Project, "Content", "UI", "menu.rml"), "Content/UI/menu.rml", builder);
        Assert.Equal(13, added);
        var entries = Extracted(builder);
        Assert.Equal(["Content/UI/menu.rml:13"], entries["Start game"].References);
        Assert.Equal(["RML <h1>"], entries["Start game"].ExtractedComments);
        Assert.Equal(["RML <input placeholder=\"…\">"], entries["Player name"].ExtractedComments);
        Assert.True(entries.ContainsKey("Options & settings"));
        Assert.False(entries.ContainsKey("Debug build"));
        Assert.False(entries.ContainsKey("Nested opt-out"));
        Assert.False(entries.ContainsKey("Typed by the player"));
        Assert.False(entries.ContainsKey("Default notes"));
        Assert.Equal(["RML <title>"], entries["Main menu title"].ExtractedComments);
        Assert.Equal(["RML <textarea placeholder=\"…\">"], entries["Notes"].ExtractedComments);
    }

    [Fact]
    public void ExtractsTranslatableSceneAndResourceStrings()
    {
        TypeRegistry.EnsureRegistered(typeof(LocalizedLabel).Assembly);
        var index = TranslatablePropertyIndex.FromRegistry([], []);
        Assert.True(index.IsTranslatable("LocalizedLabel", "Text"));
        Assert.False(index.IsTranslatable("LocalizedLabel", "Key"));

        var builder = new TemplateBuilder();
        var extractor = new SceneExtractor(index, Project);
        var scenes = Path.Combine(Project, "Content", "Scenes");
        Assert.Equal(6, extractor.Extract(Path.Combine(scenes, "Main.mscene"), "Content/Scenes/Main.mscene", builder));
        Assert.Equal(2, extractor.Extract(Path.Combine(scenes, "Sub.mscene"), "Content/Scenes/Sub.mscene", builder));
        Assert.Equal(1, extractor.Extract(Path.Combine(scenes, "greeting.mres"), "Content/Scenes/greeting.mres", builder));

        var entries = Extracted(builder);
        Assert.Equal(["Content/Scenes/Main.mscene:16"], entries["Settings"].References);
        Assert.Equal(["node Main/Title (LocalizedLabel.Text)"], entries["Settings"].ExtractedComments);
        Assert.Equal(["Content/Scenes/Main.mscene:17"], entries["Second line"].References);
        Assert.Equal(["Content/Scenes/Main.mscene:6"], entries["Hello, traveller!"].References);     // inline resource
        Assert.Equal(["Content/Scenes/Main.mscene:26"], entries["Overridden panel title"].References); // instance root, typed via Sub.mscene
        Assert.Equal(["node Main/Panel/Caption (LocalizedLabel.Text)"], entries["Overridden caption"].ExtractedComments);
        Assert.Equal(["Content/Scenes/greeting.mres:6"], entries["Greetings from a resource file"].References);
        Assert.True(entries.ContainsKey("Panel title"));
        Assert.True(entries.ContainsKey("Caption text"));
        Assert.False(entries.ContainsKey("title"));      // not translatable
        Assert.False(entries.ContainsKey("Guard"));      // DialogueLine.Speaker is not translatable
        Assert.False(entries.ContainsKey("Unknown type text"));
        Assert.Contains("UnknownWidget", extractor.UnknownTypes);
    }

    [Fact]
    public void ExplicitTranslatablePropertiesWorkWithoutAssemblies()
    {
        var index = TranslatablePropertyIndex.From(("UnknownWidget", "Text"));
        var builder = new TemplateBuilder();
        new SceneExtractor(index, Project).Extract(Path.Combine(Project, "Content", "Scenes", "Main.mscene"), "Main.mscene", builder);
        Assert.Equal(["Unknown type text"], Extracted(builder).Keys);
    }

    [Fact]
    public void TemplateBuilderMergesDuplicatesAndOrdersBySource()
    {
        var builder = new TemplateBuilder();
        builder.Add(null, "Settings", null, "b.rml:9", "RML <p>");
        builder.Add(null, "Settings", null, "a.cs:20", null, ["csharp-format"]);
        builder.Add(null, "Settings", null, "a.cs:3");
        builder.Add("menu", "Settings", null, "a.cs:1");
        builder.Add(null, "{0} file", "{0} files", "a.cs:10");
        builder.Add(null, "{0} file", "{0} items", "a.cs:11");
        builder.Add(null, string.Empty, null, "ignored.cs:1");

        var catalog = builder.Build("Demo");
        Assert.Equal("Demo", catalog.GetHeader("Project-Id-Version"));
        var messages = catalog.Messages.ToList();
        Assert.Equal(["menu\u0004Settings", "Settings", "{0} file"], messages.Select(m => m.Key));
        Assert.Equal(["a.cs:3", "a.cs:20", "b.rml:9"], messages[1].References); // numeric line order
        Assert.Equal(["csharp-format"], messages[1].Flags);
        Assert.Equal("{0} files", messages[2].IdPlural);
        Assert.Single(builder.Warnings);
    }

    /// <summary>The C# side runs the real GetText.NET extractor with the aliases the justfile uses.</summary>
    [Fact]
    public void GetTextExtractorFindsTrCalls()
    {
        var output = Path.Combine(Path.GetTempPath(), "mf-l10n-extractor", Guid.NewGuid().ToString("N"), "code.pot");
        try
        {
            if (!RunExtractor(Path.Combine(Project, "Src"), output, out var log))
                Assert.Skip($"GetText.Extractor is not available (run 'dotnet tool restore'): {log}");

            var entries = PoParser.ParseFile(output).ByKey();
            Assert.Equal(["csharp-format"], entries["Score: {0}"].Flags);
            Assert.True(entries.ContainsKey("Settings"));
            Assert.True(entries.ContainsKey("menu\u0004Open"));
            Assert.Equal("{0} enemies left", entries["{0} enemy left"].IdPlural);
            Assert.Equal("{0} coins", entries["hud\u0004{0} coin"].IdPlural);
            Assert.True(entries.ContainsKey("Hello, {0}!")); // interpolated: what TrInterpolatedStringHandler looks up
            Assert.Equal(6, entries.Count);
        }
        finally
        {
            var directory = Path.GetDirectoryName(output)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Runs <c>dotnet tool run GetText.Extractor</c> from the repository (its tool manifest pins the version).</summary>
    internal static bool RunExtractor(string source, string target, out string log)
    {
        var repository = FindRepositoryRoot();
        if (repository is null)
        {
            log = "repository root not found";
            return false;
        }

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "tool", "run", "GetText.Extractor", "-s", source, "-t", target, "-u", "-o", "-as", "_", "-ad", "P", "-ap", "N", "-adp", "NP" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        log = (stdout.Result + stderr).Trim();
        return process.ExitCode == 0 && File.Exists(target);
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json")))
                return directory.FullName;
        }

        return null;
    }
}

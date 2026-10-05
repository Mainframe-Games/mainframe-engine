using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

/// <summary>Builds Demo-shaped zips in memory for tests.</summary>
internal static class DemoZips
{
    public const string Props = """
        <Project>
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <!-- The engine checkout this game builds against. -->
            <MainframeEnginePath>$([System.IO.Path]::GetFullPath($([System.IO.Path]::Combine('$(MSBuildThisFileDirectory)', '../..'))))</MainframeEnginePath>
          </PropertyGroup>
        </Project>
        """;

    public static Dictionary<string, string> ValidProject(string top = "MainframeEngine.Demo") => new()
    {
        [$"{top}/project.mfproj"] = """{ "format": 1, "name": "Mainframe Demo", "mainScene": "Content/Scenes/basic_3d.mscene" }""",
        [$"{top}/Directory.Build.props"] = Props,
        [$"{top}/Demo.Desktop/Demo.Desktop.csproj"] = "<Project />",
        [$"{top}/Content/Scenes/basic_3d.mscene"] = "{}",
    };

    public static string Write(string path, IReadOnlyDictionary<string, string> files, Action<ZipArchive>? extra = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        extra?.Invoke(zip);
        return path;
    }
}

using System.Text.RegularExpressions;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Core.Content;

/// <summary>
/// Runtime content is read through <see cref="ContentFiles"/>, so an export can serve it from a pack (G19, ADR 0239). A
/// read that must see the disk says why with a <c>// disk-io:</c> comment on its line; files that only read and write
/// user data, caches, natives or build outputs are listed below.
/// </summary>
public sealed partial class ContentIoAuditTests
{
    // Paths under MainframeEngine/Src, '/'-separated: a folder ends with '/'.
    private static readonly string[] DiskOnly =
    [
        "Core/Content/",                             // the content file system itself
        "Saves/",                                    // save games: user data
        "Recording/",                                // gameplay recordings and their sidecars
        "Serialization/DurableFile.cs",              // durable writes with backups
        "Serialization/ConfigFile.cs",               // settings.json: user data
        "Debugging/FileLogSink.cs",                  // logs
        "Debugging/Crash/",                          // crash reports: user data
        "UI/Crash/",                                 // the next-launch crash prompt reads those reports
        "Rendering/Shaders/SurfaceShaderBuild.cs",   // the mf-shaders build and check (its run-time lock read uses ContentFiles)
        "Project/GameAssemblyLoader.cs",             // the editor's collectible game assembly
        "Rendering/Vulkan/PipelineCache.cs",         // the driver's pipeline cache: user data
        "Trees/Baking/TreeBakeCache.cs",             // a bake cache
        "Core/SilkNativeResolver.cs",                // native libraries
        "Rendering/Vulkan/VulkanLoaderBootstrap.cs", // the Vulkan loader
        "Steamworks/Native/SteamworksLoader.cs",     // the Steamworks shim assemblies
    ];

    [GeneratedRegex(@"\bFile\.(Exists|Open|OpenRead|OpenText|ReadAll\w*|ReadLines)\(|new FileStream\(|new FileInfo\(|\bDirectory\.(Exists|Enumerate\w*|Get(Files|Directories|FileSystemEntries))\(|new DirectoryInfo\(")]
    private static partial Regex ReadApi();

    [Fact]
    public void RuntimeContentCodeDoesNotCallFileApis()
    {
        var source = Path.Combine(TestPaths.RepositoryRoot(), "MainframeEngine", "Src");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (DiskOnly.Any(entry => entry.EndsWith('/') ? relative.StartsWith(entry, StringComparison.Ordinal) : relative == entry))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*') || lines[i].Contains("disk-io:", StringComparison.Ordinal))
                    continue;
                if (ReadApi().IsMatch(lines[i]))
                    hits.Add($"{relative}:{i + 1}: {code}");
            }
        }

        Assert.True(hits.Count == 0,
            "Read content through ContentFiles, or mark a read that must see the disk with `// disk-io: <reason>`:\n" + string.Join("\n", hits));
    }
}

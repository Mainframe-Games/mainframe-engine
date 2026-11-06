using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// G19 phase 4 (ADR 0259) pieces a JIT test process can check: the macOS bundle base directory of a NativeAOT executable,
/// and the rd.xml that gives Facepunch.Steamworks' structs their marshalling data under NativeAOT, kept in sync with the
/// vendored assemblies.
/// </summary>
public sealed class NativeAotTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mf-aot").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ANativeExecutableInABundleUsesItsMonoBundleFolder()
    {
        var contents = Path.Combine(_folder, "Game.app", "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        Directory.CreateDirectory(macOs);
        Assert.Null(NativeAppBundle.BaseDirectoryFor(macOs)); // no MonoBundle: not a release bundle

        Directory.CreateDirectory(Path.Combine(contents, NativeAppBundle.AppDirectory));
        Assert.Equal(Path.Combine(contents, "MonoBundle") + Path.DirectorySeparatorChar, NativeAppBundle.BaseDirectoryFor(macOs));
        Assert.Equal(Path.Combine(contents, "MonoBundle") + Path.DirectorySeparatorChar, NativeAppBundle.BaseDirectoryFor(macOs + Path.DirectorySeparatorChar));
        Assert.Null(NativeAppBundle.BaseDirectoryFor(Path.Combine(contents, "MonoBundle")));
        Assert.Null(NativeAppBundle.BaseDirectoryFor(_folder));
        Assert.Null(NativeAppBundle.BaseDirectoryFor(null));
        Assert.Null(NativeAppBundle.Apply()); // a JIT process changes nothing
    }

    public static TheoryData<string> FacepunchBuilds => new() { "Posix", "Win64" };

    [Theory]
    [MemberData(nameof(FacepunchBuilds))]
    public void TheSteamworksRdXmlListsEveryFacepunchStruct(string build)
    {
        var repo = RepoRoot();
        var assembly = Assembly.LoadFile(Path.Combine(repo, "Plugins", "Facepunch.Steamworks", $"Facepunch.Steamworks.{build}.dll"));
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = [.. e.Types.OfType<Type>()];
        }

        var expected = types.Where(t => t.IsValueType && !t.IsEnum && !t.ContainsGenericParameters && !Generated(t))
            .Select(t => t.FullName!).Order(StringComparer.Ordinal).ToArray();
        var file = Path.Combine(repo, "build", "steamworks", $"Facepunch.Steamworks.{build}.rd.xml");
        var document = XDocument.Load(file);
        var listed = document.Descendants().Where(e => e.Name.LocalName == "Type")
            .Select(e => (Name: (string)e.Attribute("Name")!, Marshal: (string?)e.Attribute("MarshalStructure"))).ToArray();
        Assert.Equal($"Facepunch.Steamworks.{build}", (string?)document.Descendants().Single(e => e.Name.LocalName == "Assembly").Attribute("Name"));
        Assert.All(listed, t => Assert.Equal("Required All", t.Marshal));
        // A Facepunch upgrade changes its structs: regenerate the file (the comment at its top names this test).
        Assert.Equal(expected, listed.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    private static bool Generated(Type? type)
    {
        for (; type is not null; type = type.DeclaringType)
            if (type.Name.Contains('<', StringComparison.Ordinal) || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                return true;
        return false;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        for (var directory = Path.GetDirectoryName(here); directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "MainframeEngine.slnx")))
                return directory;
        throw new DirectoryNotFoundException("MainframeEngine.slnx not found above " + here);
    }
}

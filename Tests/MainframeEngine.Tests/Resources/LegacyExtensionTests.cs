using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Resources;

/// <summary>
/// A project made before ADR 0249 (<c>.mscene</c>, <c>.mres</c>, <c>x.png.meta</c>, …) still loads in the runtime: a game
/// can update its engine package before the editor migrates it. Process-wide loader state, so serial.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class LegacyExtensionTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-legacy-ext", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public LegacyExtensionTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    // Writes a scene that references a resource, both with the current names, then gives them (and the scene's
    // reference) the names they had before ADR 0249.
    private (string ResourceUid, string SceneUid) WriteLegacyProject()
    {
        var resourceUid = ResourceSaver.Save(new TestResource { Value = 7, Label = "old" }, ContentPath("Data/Stats.mfres"));
        var root = new AllTypesNode { Name = "Main", Data = ResourceLoader.Load<TestResource>(ContentPath("Data/Stats.mfres")) };
        var sceneUid = SceneSaver.Save(root, ContentPath("Scenes/Main.mfscene"));
        root.Free();
        ResourceLoader.ClearCache();

        File.Move(ContentPath("Data/Stats.mfres"), ContentPath("Data/Stats.mres"));
        File.WriteAllText(ContentPath("Scenes/Main.mscene"), File.ReadAllText(ContentPath("Scenes/Main.mfscene")).Replace(".mfres", ".mres", StringComparison.Ordinal));
        File.Delete(ContentPath("Scenes/Main.mfscene"));
        File.WriteAllBytes(ContentPath("logo.png"), [0x89, (byte)'P', (byte)'N', (byte)'G']);
        File.WriteAllText(ContentPath("logo.png.meta"), """{ "uid": "tex_0123456789ab" }""");
        return (resourceUid, sceneUid);
    }

    [Fact]
    public void TheScanFindsLegacyScenesResourcesAndSidecars()
    {
        var (resourceUid, sceneUid) = WriteLegacyProject();

        var assets = new AssetDatabase(_project);
        assets.Scan();

        Assert.Equal("Assets/Data/Stats.mres", assets.GetPath(resourceUid));
        Assert.Equal("Assets/Scenes/Main.mscene", assets.GetPath(sceneUid));
        Assert.Equal("Assets/logo.png", assets.GetPath("tex_0123456789ab"));
        Assert.Null(assets.GetUid("Assets/logo.png.meta")); // a sidecar, not an asset
        Assert.False(File.Exists(ContentPath("logo.png.mfmeta"))); // the runtime never writes a second sidecar
    }

    [Fact]
    public void ALegacySceneLoadsWithItsLegacyResource()
    {
        WriteLegacyProject();
        AssetDatabase.Current = new AssetDatabase(_project);

        var scene = ResourceLoader.Load<PackedScene>("Assets/Scenes/Main.mscene");
        var main = scene.Instantiate<AllTypesNode>();

        Assert.Equal(7, ((TestResource)main.Data!).Value);
        main.Free();
    }

    [Fact]
    public void AnOldPathFindsTheFileAfterTheEditorRenamedIt()
    {
        var uid = ResourceSaver.Save(new TestResource { Value = 3 }, ContentPath("Data/Renamed.mfres"));
        ResourceLoader.ClearCache();

        // Game code (or project.mfproj) written before the migration still says .mres.
        Assert.Equal(Path.GetFullPath(ContentPath("Data/Renamed.mfres")), AssetDatabase.Current.ToAbsolutePath("Assets/Data/Renamed.mres"));
        Assert.Equal(3, ResourceLoader.Load<TestResource>("Assets/Data/Renamed.mres").Value);
        Assert.Equal(uid, ResourceLoader.Load<TestResource>("Assets/Data/Renamed.mres").Uid);
    }

    [Fact]
    public void AnOldPathWhoseFileStillHasTheOldNameIsNotRedirected()
    {
        WriteLegacyProject();
        File.WriteAllText(ContentPath("Data/Stats.mfres"), "{}"); // both exist: the path names the one it means

        Assert.Equal(Path.GetFullPath(ContentPath("Data/Stats.mres")), AssetDatabase.Current.ToAbsolutePath("Assets/Data/Stats.mres"));
    }

    [Fact]
    public void LegacyShaderOutputsAndProbeDataAreBuildOutputs()
    {
        Assert.True(AssetDatabase.IsBuildOutput("wave.slang.spvlock"));
        Assert.True(AssetDatabase.IsBuildOutput("wave.slang.layout.json"));
        Assert.True(AssetDatabase.IsBuildOutput("lighting.probes"));
        Assert.True(AssetDatabase.IsBuildOutput("wave.slang.mfspvlock"));
        Assert.True(AssetDatabase.IsBuildOutput("wave.slang.mflayout.json"));
        Assert.True(AssetDatabase.IsBuildOutput("lighting.mfprobes"));
        Assert.False(AssetDatabase.IsBuildOutput("editor_layout.json"));
    }
}

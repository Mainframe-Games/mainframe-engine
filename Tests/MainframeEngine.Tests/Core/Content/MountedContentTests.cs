using System.Text;
using MainframeEngine.Audio;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;
using MainframeEngine.Tests.Localization;
using MainframeEngine.Tests.Scene;
using MainframeEngine.Tests.UI;
using MainframeEngine.UI.Rml;
using Spine;

namespace MainframeEngine.Tests.Core.Content;

/// <summary>
/// A game whose files exist only in a mounted source (the shape of a packed export): a temporary root that is not on
/// disk, with copies of the test output's content under its <c>Assets/</c>. <c>packed</c> mounts them as a real
/// <c>.mfpak</c> written by <see cref="PackWriter"/> (G19 phase 2) instead of the in-memory source.
/// </summary>
internal sealed class MountedProject : IDisposable
{
    private readonly bool _packed;
    private IDisposable? _mount;
    private string? _packFolder;

    public MountedProject(bool packed = false)
    {
        _packed = packed;
        Source = new MemoryContentSource(Root, "test.mfpak");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mf-mounted-" + Guid.NewGuid().ToString("N"));

    public MemoryContentSource Source { get; }

    /// <summary>Copies a file from the test output's <c>Assets/</c> to the same path under this project.</summary>
    public MountedProject Copy(string assetsPath)
    {
        Source.Add(assetsPath, File.ReadAllBytes(Path.Combine(ContentPaths.BaseDirectory, assetsPath)));
        return this;
    }

    public string PathOf(string assetsPath) => Path.Combine(Root, assetsPath);

    public MountedProject Mount()
    {
        Assert.False(Directory.Exists(Root));
        if (!_packed)
        {
            _mount = ContentFiles.Mount(Source);
            return this;
        }

        // The memory source's files, written out and packed: the pack is the only copy under Root.
        _packFolder = Directory.CreateTempSubdirectory("mf-pack-").FullName;
        var files = new List<PackFile>();
        foreach (var path in Source.EnumerateFiles("", recursive: true))
        {
            var file = Path.Combine(_packFolder, "files", path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var input = Source.OpenRead(path)!)
            using (var output = File.Create(file))
                input.CopyTo(output);
            files.Add(new PackFile(path, file));
        }

        var pack = Path.Combine(_packFolder, PackFormat.FileName);
        PackWriter.Write(pack, files);
        var source = new PackContentSource(PackReader.Open(pack), Root);
        var mount = ContentFiles.Mount(source);
        _mount = new Unmount(mount, source);
        return this;
    }

    public void Dispose()
    {
        _mount?.Dispose();
        if (_packFolder is not null)
            Directory.Delete(_packFolder, recursive: true);
    }

    private sealed class Unmount(IDisposable mount, IDisposable source) : IDisposable
    {
        public void Dispose()
        {
            mount.Dispose();
            source.Dispose();
        }
    }
}

/// <summary>Loaders that read through <see cref="ContentFiles"/> load a game that exists only in a mount.</summary>
[Collection(nameof(SerialResources))] // process-wide asset database and loader cache
public sealed class MountedResourceTests : IDisposable
{
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private MountedProject _project = new();

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        _project.Dispose();
    }

    private void UseProject()
    {
        _project.Mount();
        AssetDatabase.Current = new AssetDatabase(_project.Root);
        ResourceLoader.ClearCache();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceLoaderReadsScenesFromAMount(bool packed)
    {
        _project = new MountedProject(packed);
        // Author the scene on disk, then ship only its bytes (and the texture's) in the mount.
        var authoring = Directory.CreateTempSubdirectory("mf-mounted-authoring").FullName;
        try
        {
            var assets = Path.Combine(authoring, AssetDatabase.ContentFolder);
            Directory.CreateDirectory(assets);
            File.Copy(Path.Combine(ContentPaths.Root, "Models/TestModel/checker.png"), Path.Combine(assets, "checker.png"));
            AssetDatabase.Current = new AssetDatabase(authoring);
            AssetDatabase.Current.Refresh(createMissingMeta: true);
            ResourceLoader.ClearCache();
            var root = new Sprite2D { Name = "Root", Texture = ResourceLoader.Load<Texture2D>("Assets/checker.png") };
            SceneSaver.Save(root, Path.Combine(assets, "main.mscene"));
            root.Free();
            foreach (var file in Directory.EnumerateFiles(assets))
                _project.Source.Add("Assets/" + Path.GetFileName(file), File.ReadAllBytes(file));
        }
        finally
        {
            AssetDatabase.Current = _previousAssets;
            Directory.Delete(authoring, recursive: true);
        }

        UseProject();
        var instance = (Sprite2D)ResourceLoader.Load<PackedScene>("Assets/main.mscene").Instantiate();

        Assert.True(ResourceLoader.Exists("Assets/main.mscene"));
        Assert.NotNull(instance.Texture);
        Assert.Equal(Texture2D.FromFile(Path.Combine(ContentPaths.Root, "Models/TestModel/checker.png")).Width, instance.Texture.Width);
        instance.Free();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureLoadsFromAMount(bool packed)
    {
        _project = new MountedProject(packed);
        const string png = "Assets/Models/TestModel/checker.png";
        _project.Copy(png).Copy(png + ".meta");
        var onDisk = Texture2D.FromFile(Path.Combine(ContentPaths.BaseDirectory, png));
        UseProject();

        var mounted = ResourceLoader.Load<Texture2D>(png);

        Assert.Equal((onDisk.Width, onDisk.Height), (mounted.Width, mounted.Height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelImporterLoadsAGltfWithExternalBuffersThroughTheContentFileSystem(bool packed)
    {
        _project = new MountedProject(packed);
        const string folder = "Assets/Models/TestModel/";
        _project.Copy(folder + "test_model.gltf").Copy(folder + "test_model.bin").Copy(folder + "checker.png")
            .Copy(folder + "test_model.gltf.meta").Copy(folder + "checker.png.meta");
        var fromDisk = Meshes(ModelImporter.ImportNodes(Path.Combine(ContentPaths.BaseDirectory, folder + "test_model.gltf")));
        UseProject();

        var mounted = ModelImporter.ImportNodes(_project.PathOf(folder + "test_model.gltf"));

        Assert.NotEmpty(fromDisk);
        Assert.Equal(fromDisk, Meshes(mounted));
        mounted.Free();
    }

    // Each mesh instance's name, vertex count and whether its material has a texture.
    private static List<(string, int, bool)> Meshes(Node root)
    {
        var meshes = new List<(string, int, bool)>();
        Walk(root);
        return meshes;

        void Walk(Node node)
        {
            if (node is MeshInstance3D { Mesh: { } mesh } instance)
            {
                for (var i = 0; i < mesh.SurfaceCount; i++)
                {
                    var material = mesh.GetSurfaceMaterial(i) as StandardMaterial3D;
                    meshes.Add((instance.Name, mesh.GetSurface(i).Positions.Length, material?.AlbedoTexture is not null));
                }
            }

            foreach (var child in node.Children)
                Walk(child);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpineFolderLoadsFromAMount(bool packed)
    {
        _project = new MountedProject(packed);
        const string folder = "Assets/Models/Spine/SpineBoy";
        _project.Copy(folder + "/spineboy-pro.atlas").Copy(folder + "/spineboy-pro.json").Copy(folder + "/spineboy-pro.png");
        UseProject();

        var spine = new SpineFolder(_project.PathOf(folder));
        var loader = new RecordingTextureLoader();
        var atlas = SpineContent.ReadAtlas(spine.AtlasPath, loader);
        var data = spine.ReadSkeletonData(atlas);

        Assert.Equal("spineboy-pro", data.Name);
        Assert.NotEmpty(data.Bones);
        Assert.NotEmpty(data.Animations);
        Assert.Equal([Path.GetFullPath(_project.PathOf(folder + "/spineboy-pro.png"))], loader.Pages.Select(Path.GetFullPath));
        Assert.True(loader.PageBytes > 0);
    }

    private sealed class RecordingTextureLoader : TextureLoader
    {
        public List<string> Pages { get; } = [];
        public int PageBytes { get; private set; }

        public void Load(AtlasPage page, string path)
        {
            Pages.Add(path);
            PageBytes += ContentFiles.ReadAllBytes(path).Length;
        }

        public void Unload(object texture)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamedAudioSeeksThroughAMount(bool packed)
    {
        _project = new MountedProject(packed);
        const string ogg = "Assets/Audio/ambient_hum.ogg";
        _project.Copy(ogg).Mount();
        using var disk = AudioDecoder.Open(Path.Combine(ContentPaths.BaseDirectory, ogg));
        using var mounted = AudioDecoder.Open(_project.PathOf(ogg));
        var expected = new float[4096];
        var actual = new float[4096];

        foreach (var frame in new long[] { 0, 12_345, 1_000 })
        {
            Assert.True(disk.Seek(frame));
            Assert.True(mounted.Seek(frame));
            var read = disk.Read(expected);
            Assert.Equal(read, mounted.Read(actual));
            Assert.True(read > 0);
            Assert.Equal(expected.AsSpan(0, read).ToArray(), actual.AsSpan(0, read).ToArray());
        }
    }
}

/// <summary>RmlUi documents, their stylesheets and font faces load from a mount through the UI file interface.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class MountedUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UiDocumentAndFontLoadFromAMount(bool packed)
    {
        using var project = new MountedProject(packed);
        project.Copy("Assets/UI/fonts/LatoLatin-Bold.ttf");
        project.Source.Add("Assets/UI/hud.rcss", "body { font-family: LatoLatin; font-weight: bold; } #hp { width: 50dp; }");
        project.Source.Add("Assets/UI/hud.rml", """
            <rml><head><link type="text/rcss" href="hud.rcss"/></head><body><div id="hp">100</div></body></rml>
            """);
        project.Mount();
        using var host = new RmlTestHost();

        // Throws when RmlUi cannot read the face: the file exists only in the mount, so it was read through the file interface.
        RmlCore.LoadFontFace(project.PathOf("Assets/UI/fonts/LatoLatin-Bold.ttf"));
        var document = host.Context.LoadDocument(project.PathOf("Assets/UI/hud.rml"));
        document.Show();
        host.Frame();

        Assert.Equal("100", document.GetElementById("hp")!.InnerRml);
        host.AssertNoRmlErrors();
        document.Close();
        host.Frame();
    }
}

/// <summary>Locale folders and their catalogs come from a mount.</summary>
[Collection(nameof(LocalizationState))]
public sealed class MountedLocalizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogsAndLocalesComeFromAMount(bool packed)
    {
        using var project = new MountedProject(packed);
        var po = new StringBuilder()
            .Append("msgid \"\"\nmsgstr \"\"\n\"Language: es\\n\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n")
            .Append("\"Plural-Forms: nplurals=2; plural=(n != 1);\\n\"\n\n")
            .Append("msgid \"Start\"\nmsgstr \"Empezar\"\n")
            .ToString();
        project.Source.Add("Assets/locale/es/LC_MESSAGES/messages.mo", MoFormat.Write(PoParser.Parse(po)));
        project.Mount();
        try
        {
            Tr.Configure(new LocalizationOptions { LocaleDirectory = project.PathOf("Assets/locale"), LogMissingTranslations = false }, "es");

            Assert.Contains("es", Tr.GetAvailableLocales());
            Assert.Equal("Empezar", Tr._("Start"));
        }
        finally
        {
            Tr.ResetForTests();
        }
    }
}

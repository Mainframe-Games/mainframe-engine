using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Resources;

/// <summary>
/// G1.18: a resource file changed on disk reloads into the instance already loaded (references stay valid, named inline
/// resources keep their instances); the loader's own reads and saves count as unchanged; eviction forces a fresh load.
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class ResourceReloadTests : IDisposable
{
    private const string LibraryPath = "Assets/anims.mfres";

    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private readonly string _root = Directory.CreateTempSubdirectory("mf-reload").FullName;

    public ResourceReloadTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_root);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        Directory.Delete(_root, recursive: true);
    }

    private static AnimationLibrary Library(params (string Name, float Length)[] clips)
    {
        var library = new AnimationLibrary();
        foreach (var (name, length) in clips)
            library.Add(name, new MainframeEngine.Animation { ResourceName = name, Length = length });
        return library;
    }

    // Writes a library's file the way another process would: saved elsewhere, then its bytes put over the path.
    private void WriteFromOutside(AnimationLibrary library, string path = LibraryPath)
    {
        ResourceSaver.Save(library, "Assets/elsewhere.mfres");
        File.WriteAllBytes(Path.Combine(_root, path), File.ReadAllBytes(Path.Combine(_root, "Assets", "elsewhere.mfres")));
        File.SetLastWriteTimeUtc(Path.Combine(_root, path), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void AChangedFileReloadsIntoTheSameInstances()
    {
        ResourceSaver.Save(Library(("jump", 1f), ("old", 0.5f)), LibraryPath);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        var jump = library.Get("jump")!;
        var version = jump.Version;

        WriteFromOutside(Library(("jump", 2f), ("hop", 0.4f)));
        var result = ResourceLoader.ReloadInPlace(LibraryPath);

        Assert.Equal(ResourceReloadStatus.Reloaded, result.Status);
        Assert.Same(library, result.Resource);
        Assert.Same(jump, library.Get("jump"));
        Assert.Equal(2f, jump.Length);
        Assert.True(jump.Version > version);
        Assert.NotNull(library.Get("hop"));
        Assert.Null(library.Get("old"));
        Assert.Same(library, ResourceLoader.Load<AnimationLibrary>(LibraryPath));
    }

    [Fact]
    public void ItsOwnLoadAndSaveAreUnchanged()
    {
        ResourceSaver.Save(Library(("jump", 1f)), LibraryPath);
        ResourceLoader.ClearCache();
        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);

        Assert.Equal(ResourceReloadStatus.Unchanged, ResourceLoader.ReloadInPlace(LibraryPath).Status);
        library.Get("jump")!.Length = 3f;
        ResourceSaver.Save(library, LibraryPath);
        Assert.Equal(ResourceReloadStatus.Unchanged, ResourceLoader.ReloadInPlace(LibraryPath).Status);
        Assert.Equal(3f, library.Get("jump")!.Length);
    }

    [Fact]
    public void AnUnloadedFileIsNotCachedAndABrokenOneFails()
    {
        ResourceSaver.Save(Library(("jump", 1f)), LibraryPath);
        ResourceLoader.ClearCache();
        Assert.Equal(ResourceReloadStatus.NotCached, ResourceLoader.ReloadInPlace(LibraryPath).Status);

        var library = ResourceLoader.Load<AnimationLibrary>(LibraryPath);
        File.WriteAllText(Path.Combine(_root, LibraryPath), "{ not json");
        File.SetLastWriteTimeUtc(Path.Combine(_root, LibraryPath), DateTime.UtcNow.AddSeconds(5));
        var result = ResourceLoader.ReloadInPlace(LibraryPath);

        Assert.Equal(ResourceReloadStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(1f, library.Get("jump")!.Length);
    }

    [Fact]
    public void EvictingMakesTheNextLoadReadTheFile()
    {
        ResourceSaver.Save(Library(("jump", 1f)), LibraryPath);
        ResourceLoader.ClearCache();
        var first = ResourceLoader.Load<AnimationLibrary>(LibraryPath);

        Assert.True(ResourceLoader.Evict(LibraryPath));
        Assert.False(ResourceLoader.Evict(LibraryPath));
        var second = ResourceLoader.Load<AnimationLibrary>(LibraryPath);

        Assert.NotSame(first, second);
        Assert.Equal(ResourceReloadStatus.NotCached, ResourceLoader.ReloadInPlace("Assets/none.mfres").Status);
    }
}

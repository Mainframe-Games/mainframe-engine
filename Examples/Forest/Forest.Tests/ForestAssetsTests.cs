using System.Numerics;
using System.Text;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>
/// The CC0 art under <c>Content/Art</c> (Git LFS): the manifest matches the files and NOTICE.md, every prop imports
/// through Assimp with the PBR override, and the terrain layers load. Skipped when LFS content is not checked out.
/// </summary>
public sealed class ForestAssetsTests
{
    private static string ForestRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static void RequireLfsContent()
    {
        var probe = ForestAssets.Resolve(ForestAssets.SkyPanorama);
        if (!File.Exists(probe) || IsLfsPointer(probe))
            Assert.Skip("The Forest's LFS art is not checked out (git lfs pull).");
    }

    private static bool IsLfsPointer(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[24];
        var read = stream.Read(head);
        return Encoding.ASCII.GetString(head, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
    }

    [Fact]
    public void LayersCoverTheForestSurfacesInSplatOrder()
    {
        Assert.Equal(["grass", "leaves", "moss", "rock", "dirt", "gravel", "mud", "needles"], ForestAssets.TerrainLayers.Select(l => l.Tag));
        Assert.True(ForestAssets.TerrainLayers.Count <= TerrainSplatMaterial3D.MaxLayers);
        Assert.Equal(3, ForestAssets.LayerIndex("rock"));
        Assert.Equal(-1, ForestAssets.LayerIndex("lava"));
        Assert.All(ForestAssets.TerrainLayers, l => Assert.InRange(l.TilingMeters, 1f, 8f));
    }

    [Fact]
    public void EveryManifestFileResolvesAndEveryArtFileIsInTheManifest()
    {
        var listed = ForestAssets.AllFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var path in listed)
            Assert.True(File.Exists(ForestAssets.Resolve(path)), $"missing: {path}");

        // Sources, not the build output: every file under Content/Art is listed (or is a .meta sidecar with its asset).
        var art = Path.Combine(ForestRoot, ForestAssets.ArtFolder);
        foreach (var file in Directory.EnumerateFiles(art, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ForestRoot, file).Replace('\\', '/');
            if (relative.EndsWith(".meta", StringComparison.Ordinal))
                Assert.True(File.Exists(file[..^5]), $"orphan sidecar: {relative}");
            else
            {
                Assert.Contains(relative, listed);
                Assert.True(File.Exists(file + ".meta"), $"no .meta: {relative}");
            }
        }
    }

    [Fact]
    public void NoticeListsEveryAsset()
    {
        var notice = File.ReadAllText(Path.Combine(ForestRoot, "NOTICE.md"));
        foreach (var id in ForestAssets.TerrainLayers.Select(l => l.AssetId).Concat(ForestAssets.Props.Select(p => p.AssetId))
                     .Concat(ForestAssets.Debris.Select(d => d.AssetId)).Append("lilienstein"))
            Assert.Contains($"`{id}`", notice);
        Assert.DoesNotContain("no third-party assets yet", notice);
    }

    [Fact]
    public void TerrainLayersLoadTheirTextures()
    {
        RequireLfsContent();
        var material = ForestAssets.CreateTerrainMaterial();
        Assert.Equal(ForestAssets.TerrainLayers.Count, material.Layers.Length);
        foreach (var (layer, asset) in material.Layers.Zip(ForestAssets.TerrainLayers))
        {
            Assert.Equal(asset.Tag, layer.Tag);
            Assert.Equal(asset.TilingMeters, layer.TilingMeters);
            foreach (var texture in new[] { layer.Albedo, layer.Normal, layer.Orm, layer.Height })
            {
                Assert.NotNull(texture);
                Assert.Equal((1024, 1024), (texture.Width, texture.Height));
                Assert.StartsWith(asset.Folder, texture.ResourcePath); // saved scenes reference the files
            }
        }
    }

    [Fact]
    public void OrmMapsPackOcclusionRoughnessAndNoMetal()
    {
        RequireLfsContent();
        foreach (var asset in ForestAssets.TerrainLayers)
        {
            var (rgba, width, height) = Texture2D.FromFile(ForestAssets.Resolve(asset.OrmPath)).DecodePixels();
            long metal = 0, roughness = 0;
            for (var i = 0; i < width * height; i++)
            {
                roughness += rgba[i * 4 + 1];
                metal += rgba[i * 4 + 2];
            }

            Assert.Equal(0, metal);
            Assert.InRange(roughness / (width * height), 60, 255); // ground is rough
        }
    }

    public static TheoryData<string> PropIds() => [.. ForestAssets.Props.Select(p => p.AssetId)];

    [Theory]
    [MemberData(nameof(PropIds))]
    public void PropInstantiatesWithThePbrMaterial(string id)
    {
        RequireLfsContent();
        var prop = ForestAssets.Props.Single(p => p.AssetId == id);
        var root = ForestAssets.InstantiateProp(prop);
        try
        {
            Assert.Equal(prop.ModelPath, root.SceneFilePath);
            var meshes = root.FindChildren<MeshInstance3D>(owned: false);
            Assert.NotEmpty(meshes);
            var material = Assert.IsType<StandardMaterial3D>(meshes[0].MaterialOverride);
            Assert.All(meshes, m => Assert.Same(material, m.MaterialOverride));
            Assert.Equal(ShadingMode.Pbr, material.ShadingMode);
            Assert.Equal(0f, material.Metallic);
            Assert.EndsWith("_arm_" + prop.Resolution + ".jpg", material.OrmTexture!.ResourcePath);
            Assert.NotNull(material.AlbedoTexture);
            Assert.NotNull(material.NormalTexture);
            Assert.Equal(prop.Cutout, material.Transparency == AlphaMode.Cutout);
            Assert.Equal(prop.Cutout, material.DoubleSided);
            if (prop.Cutout)
                Assert.True(material.AlbedoTexture!.DecodePixels().Rgba.Where((_, i) => i % 4 == 3).Any(a => a < 128), "the cut-out albedo has alpha");

            // The documented size matches the geometry (glTF y up, metres).
            var size = ForestAssets.LoadPropMeshes(prop).Aggregate(new Aabb(new Vector3(float.MaxValue), new Vector3(float.MinValue)),
                (box, entry) => Corners(entry.Mesh.Bounds).Aggregate(box, (b, c) => b.Encapsulate(entry.Transform * c))).Size;
            Assert.True(Vector3.Distance(size, prop.Size) < 0.05f * prop.Size.Length() + 0.01f, $"{id}: {size} vs {prop.Size}");
            Assert.All(meshes, m => Assert.True(m.Mesh!.SurfaceCount > 0));
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void PanoramaSkyIsAnLdrImageUnderTenMegabytes()
    {
        RequireLfsContent();
        var path = ForestAssets.Resolve(ForestAssets.SkyPanorama);
        Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
        var sky = ForestAssets.CreatePanoramaSky();
        Assert.Equal(SkyEnvironmentType.Panoramic, sky.Mode);
        var texture = Texture2D.FromFile(path);
        Assert.Equal((4096, 2048), (texture.Width, texture.Height));
    }

    [Fact]
    public void CommittedGallerySceneMatchesTheBuilder()
    {
        var root = new ForestAssetGallery { Name = ForestAssetGallery.SceneId };
        try
        {
            var committed = File.ReadAllText(Path.Combine(ForestRoot, ForestAssetGallery.ScenePath)).ReplaceLineEndings();
            var uid = System.Text.Json.JsonDocument.Parse(committed).RootElement.GetProperty("uid").GetString()!;
            Assert.Equal(committed, Encoding.UTF8.GetString(SceneSaver.ToJson(root, uid)).ReplaceLineEndings());
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void GalleryBandsPaintOneLayerEach()
    {
        Span<float> w = stackalloc float[8];
        for (var band = 0; band < ForestAssets.TerrainLayers.Count; band++)
        {
            ForestAssetGallery.Weights(4f + (band + 0.5f) * 7f, 20f, w);
            var strongest = 0;
            for (var i = 1; i < w.Length; i++)
                if (w[i] > w[strongest])
                    strongest = i;
            Assert.Equal(band, strongest);
        }
    }

    private static IEnumerable<Vector3> Corners(Aabb box)
    {
        for (var i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
    }
}

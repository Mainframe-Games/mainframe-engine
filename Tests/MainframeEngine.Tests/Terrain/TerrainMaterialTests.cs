namespace MainframeEngine.Tests.Terrain;

using Color = System.Drawing.Color;

/// <summary>G8a.4: <see cref="TerrainMaterial3D"/> (palette packing, defaults, the terrain link) and the Faceted cell maps.</summary>
public sealed class TerrainMaterialTests
{
    [Fact]
    public void ThePaletteHoldsColoursStripesAndMagentaPastTheSurfaces()
    {
        var material = new TerrainMaterial3D
        {
            Surfaces =
            [
                new TerrainSurface { Color = Color.FromArgb(255, 10, 20, 30) },
                new TerrainSurface { Color = Color.FromArgb(255, 40, 50, 60), Stripes = true, StripeColor = Color.FromArgb(255, 70, 80, 90) },
            ],
        };
        var (rgba, width, height) = material.Palette.DecodePixels();
        Assert.Equal((TerrainMaterial3D.MaxSurfaces, 2), (width, height));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, rgba[0..4]);
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, rgba[4..8]);
        var row1 = width * 4;
        Assert.Equal(0, rgba[row1 + 3]); // surface 0: no bands
        Assert.Equal(new byte[] { 70, 80, 90, 255 }, rgba[(row1 + 4)..(row1 + 8)]);
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, rgba[8..12]); // id 2 is past the palette
        Assert.Equal(TextureImportColorSpace.Srgb, material.Palette.ImportSettings.ColorSpace);
    }

    [Fact]
    public void EditingASurfaceRebuildsThePaletteAndTouchesTheMaterial()
    {
        var surface = new TerrainSurface { Color = Color.Red };
        var material = new TerrainMaterial3D { Surfaces = [surface] };
        var palette = material.Palette;
        var version = material.Version;
        var textureVersion = palette.Version;
        surface.Color = Color.Blue;
        Assert.True(material.Version > version);
        Assert.Same(palette, material.Palette); // updated in place
        Assert.True(palette.Version > textureVersion);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, material.Palette.DecodePixels().Rgba[0..4]);
    }

    [Fact]
    public void AFacetedTerrainWithoutAMaterialDrawsWithItsOwnDefaultPalette()
    {
        var faceted = new Terrain3D { Data = TerrainData.Create(TerrainProfile.Faceted, 32, 2f) };
        var material = Assert.IsType<TerrainMaterial3D>(faceted.RenderMaterial);
        Assert.Same(faceted, material.Terrain);
        Assert.Same(material, faceted.RenderMaterial);
        Assert.Equal(8, material.Surfaces.Length);
        Assert.NotSame(material, new Terrain3D { Data = TerrainData.Create(TerrainProfile.Faceted, 32, 2f) }.RenderMaterial);

        var realistic = new Terrain3D { Data = TerrainData.Create(TerrainProfile.Realistic, 64, 1f) };
        Assert.Same(Terrain3D.DefaultMaterial, realistic.RenderMaterial);
        var own = new TerrainMaterial3D();
        realistic.Material = own;
        Assert.Same(own, realistic.RenderMaterial);
        Assert.Same(realistic, own.Terrain);
        realistic.Material = null;
        Assert.Null(own.Terrain);
    }

    [Fact]
    public void FacetedSurfaceTagsComeFromThePalette()
    {
        var terrain = new Terrain3D { Data = TerrainData.Create(TerrainProfile.Faceted, 32, 2f) };
        terrain.PaintSurface(new System.Numerics.Vector2(10f, 10f), 3f, 3);
        Assert.Equal("sand", terrain.SurfaceTagAt(10f, 10f));
        Assert.Equal("grass", terrain.SurfaceTagAt(25f, 25f));
    }

    [Fact]
    public void TheSurfaceAndUserMapsFollowEditsAndUndo()
    {
        var terrain = new Terrain3D { Data = TerrainData.Create(TerrainProfile.Faceted, 32, 2f) };
        var data = terrain.Data!;
        var surface = data.GetSurfaceTexture();
        var user = data.GetUserTexture();
        Assert.Equal((data.CellsPerSide, data.CellsPerSide), (surface.Width, surface.Height));
        Assert.Equal(TextureFilter.Nearest, surface.ImportSettings.Filter);
        var surfaceVersion = surface.Version;
        var userVersion = user.Version;

        terrain.Edit.Begin(TerrainLayers.Surface | TerrainLayers.User);
        terrain.SetCells(TerrainLayers.Surface, 0, new Rect2I(2, 3, 1, 1), [5u]);
        terrain.SetCells(TerrainLayers.User, 0, new Rect2I(2, 3, 1, 1), [0x0000_00FFu]);
        var record = terrain.Edit.End();
        Assert.True(surface.Version > surfaceVersion);
        Assert.True(user.Version > userVersion);
        Assert.Equal(5, surface.DecodePixels().Rgba[(3 * data.CellsPerSide + 2) * 4]);
        Assert.Equal(255, user.DecodePixels().Rgba[(3 * data.CellsPerSide + 2) * 4]);

        record.Undo();
        Assert.Equal(0, surface.DecodePixels().Rgba[(3 * data.CellsPerSide + 2) * 4]);
        Assert.Equal(0, user.DecodePixels().Rgba[(3 * data.CellsPerSide + 2) * 4]);
        Assert.Throws<InvalidOperationException>(() => TerrainData.Create(TerrainProfile.Realistic, 64, 1f).GetSurfaceTexture());
    }

    [Fact]
    public void TheMaterialRoundTripsThroughAResourceFile()
    {
        var material = TerrainMaterial3D.CreateDefault();
        material.StripeWidth = 2.5f;
        material.TintChannel = 1;
        var path = Path.Combine(Path.GetTempPath(), $"mf-faceted-{Guid.NewGuid():N}.mfres");
        try
        {
            ResourceSaver.Save(material, path);
            var loaded = Assert.IsType<TerrainMaterial3D>(ResourceLoader.Load(path));
            Assert.Equal(2.5f, loaded.StripeWidth);
            Assert.Equal(1, loaded.TintChannel);
            Assert.Equal(8, loaded.Surfaces.Length);
            Assert.Equal("Lawn", loaded.Surfaces[7].Name);
            Assert.True(loaded.Surfaces[7].Stripes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

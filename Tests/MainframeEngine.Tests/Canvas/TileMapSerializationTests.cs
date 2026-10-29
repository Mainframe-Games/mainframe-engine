using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// G2b: a <see cref="TileMapLayer"/>'s cells in a scene — one base64 line per chunk (Godot 4.3's records), sorted and
/// deterministic, the long-string layout rule, the <see cref="ExportAttribute.Storage"/> hint — and a
/// <see cref="TileSet"/> as a <c>.mres</c> with its sources, overrides, polygons and custom data.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class TileMapSerializationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-tiles-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public TileMapSerializationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Tiles"));
        Png.WriteRgba8(Path.Combine(_project, AssetDatabase.ContentFolder, "Tiles", "tiles.png"), 64, 32, new byte[64 * 32 * 4]);
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previous;
        Directory.Delete(_project, recursive: true);
    }

    private static TileMapLayer Layer()
    {
        var layer = new TileMapLayer { Name = "Ground" };
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(1, 0));
        layer.SetCell(new Vector2I(1, 0), 0, new Vector2I(2, 1), TileSetAtlasSource.TransformFlipH | 3);
        layer.SetCell(new Vector2I(-1, -1), 2, new Vector2I(0, 0));
        layer.SetCell(new Vector2I(16, 0), 0, new Vector2I(3, 0));
        for (var x = 2; x < 7; x++) // a chunk line past 64 characters: the array gets a line per chunk
            layer.SetCell(new Vector2I(x, 3), 0, new Vector2I(0, 1));
        return layer;
    }

    [Fact]
    public void CellsSaveAsOneSortedBase64LinePerChunkAndRoundTrip()
    {
        var root = new Node2D { Name = "Root" };
        var layer = Layer();
        root.AddChild(layer);
        layer.Owner = root;
        var json = SceneSaver.ToJson(root);
        var text = Encoding.UTF8.GetString(json);
        Assert.Equal(json, SceneSaver.ToJson(root)); // same cells, same bytes
        root.Free();

        using (var doc = JsonDocument.Parse(json))
        {
            var props = doc.RootElement.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("name").GetString() == "Ground").GetProperty("props");
            var chunks = props.GetProperty("TileMapData").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal(3, chunks.Length); // (-1,-1), (0,0), (1,0): sorted by y then x
            var first = Convert.FromBase64String(chunks[0]);
            Assert.Equal(2 + 12, first.Length);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(first));
            Assert.Equal(-1, BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(2)));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(6)));
            Assert.Equal(2 + 7 * 12, Convert.FromBase64String(chunks[1]).Length);
        }

        // Each chunk on a line of its own (long strings are never inlined).
        Assert.Contains("\"TileMapData\": [\n", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        var copy = PackedScene.Parse(json).Instantiate().GetNode<TileMapLayer>("Ground");
        Assert.Equal(9, copy.UsedCellCount);
        Assert.Equal(new TileCell(0, new Vector2I(2, 1), TileSetAtlasSource.TransformFlipH | 3), copy.GetCell(new Vector2I(1, 0)));
        Assert.Equal(2, copy.GetCellSourceId(new Vector2I(-1, -1)));
        Assert.Equal(new Vector2I(3, 0), copy.GetCellAtlasCoords(new Vector2I(16, 0)));
        copy.Free();
    }

    [Fact]
    public void AnEmptyLayerWritesNoCells()
    {
        var root = new Node2D { Name = "Root" };
        var layer = new TileMapLayer { Name = "Empty" };
        root.AddChild(layer);
        layer.Owner = root;
        Assert.DoesNotContain("TileMapData", Encoding.UTF8.GetString(SceneSaver.ToJson(root)), StringComparison.Ordinal);
        root.Free();
    }

    [Fact]
    public void AGodotTileMapDataArrayDecodes()
    {
        // Godot 4.3's tile_map_data: u16 format 0, then (x, y, source, atlas x, atlas y, alternative) per cell, any chunk.
        var bytes = new byte[2 + 3 * 12];
        TileMapDataCodec.WriteRecord(bytes.AsSpan(2), 0, 0, new TileCell(1, new Vector2I(4, 2)));
        TileMapDataCodec.WriteRecord(bytes.AsSpan(14), -5, 30, new TileCell(0, new Vector2I(0, 7), TileSetAtlasSource.TransformTranspose));
        TileMapDataCodec.WriteRecord(bytes.AsSpan(26), 40, -2, new TileCell(3, new Vector2I(1, 1), 2));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 0, 4, 0, 2, 0, 0, 0], bytes.AsSpan(0, 14).ToArray());

        var layer = new TileMapLayer();
        TileMapDataCodec.DecodeRecords(layer, bytes);
        Assert.Equal(3, layer.UsedCellCount);
        Assert.Equal(new TileCell(1, new Vector2I(4, 2)), layer.GetCell(Vector2I.Zero));
        Assert.True(layer.IsCellTransposed(new Vector2I(-5, 30)));
        Assert.Equal(2, layer.GetCellAlternativeTile(new Vector2I(40, -2)));
        Assert.Equal(3, layer.ChunkCount);
        layer.Free();
    }

    [Fact]
    public void BadOrNewerDataIsSkipped()
    {
        var layer = new TileMapLayer();
        layer.TileMapData = ["not base64!", Convert.ToBase64String([9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])];
        Assert.Equal(0, layer.UsedCellCount);
        layer.Free();
    }

    [Fact]
    public void LongStringsInArraysGetALineEachAndShortOnesStayInline()
    {
        var json = Encoding.UTF8.GetBytes("{\"a\":[\"x\",\"y\"],\"b\":[\"" + new string('Q', 65) + "\",\"z\"]}");
        var text = Encoding.UTF8.GetString(SceneJsonLayout.Format(json)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\"a\": [\"x\", \"y\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"b\": [\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCellDataIsStorageOnly()
    {
        var info = TypeRegistry.Get(typeof(TileMapLayer))!;
        var data = info.FindProperty("TileMapData")!;
        Assert.True(data.Hints.Storage);
        Assert.False(info.FindProperty("TileSet")!.Hints.Storage);
    }

    [Fact]
    public void ATileSetResourceRoundTripsWithItsTilesPolygonsAndCustomData()
    {
        var texture = ResourceLoader.Load<Texture2D>("Assets/Tiles/tiles.png");
        var set = new TileSet { TileSize = new Vector2I(16, 16) };
        set.AddPhysicsLayer(CollisionLayers.Layer(2), CollisionLayers.Layer(1));
        set.AddCustomDataLayer("surface", CustomDataType.String);
        var source = new TileSetAtlasSource { Texture = texture, Separation = new Vector2I(0, 0) };
        source.CreateTile(new Vector2I(0, 0));
        source.CreateTile(new Vector2I(1, 0));
        set.AddSource(source, 4);
        var data = source.GetOrCreateTileData(new Vector2I(1, 0));
        data.Collision.Add(TileCollisionPolygon.FullTile(set.TileSize));
        data.NotifyChanged();
        data.SetCustomData(0, "stone");
        data.YSortOrigin = 5;
        var uid = ResourceSaver.Save(set, "Assets/Tiles/tiles.mres");
        var text = File.ReadAllText(Path.Combine(_project, "Assets", "Tiles", "tiles.mres"));
        Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("resources").EnumerateObject(), r => r.Value.TryGetProperty("type", out var t) && t.GetString() == "TileData");
        texture.Release();
        ResourceLoader.ClearCache();

        var loaded = ResourceLoader.Load<TileSet>(uid);
        var copy = loaded.GetSource(4)!;
        Assert.True(copy.HasTile(new Vector2I(1, 0)));
        Assert.False(copy.HasTile(new Vector2I(2, 0)));
        var tile = copy.GetTileData(new Vector2I(1, 0));
        Assert.Equal("stone", tile.GetCustomString(loaded.GetCustomDataLayerByName("surface")));
        Assert.Equal(5, tile.YSortOrigin);
        Assert.Single(tile.Collision);
        Assert.Equal(4, tile.Collision[0].Points.Length);
        Assert.Equal(CollisionLayers.Layer(2), loaded.PhysicsLayers[0].CollisionLayer);
        Assert.Equal(64, copy.Texture!.Width);

        // Editing a loaded tile raises the set's Changed.
        var changes = 0;
        loaded.Changed += () => changes++;
        tile.Modulate = new System.Numerics.Vector4(1, 0, 0, 1);
        Assert.Equal(1, changes);
        ResourceSaver.Save(loaded, "Assets/Tiles/tiles.mres");
        loaded.Release();
    }
}

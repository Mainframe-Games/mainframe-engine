using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G2b: the paint tools' cell sets (no editor), brush transforms, the shortcut table.</summary>
public sealed class TilePaintTests
{
    private static TileSet Set()
    {
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]) };
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                source.CreateTile(new Vector2I(x, y));
        var set = new TileSet();
        set.AddSource(source);
        return set;
    }

    private static TileCell Cell(int x, int y = 0, int alternative = 0) => new(0, new Vector2I(x, y), alternative);

    [Fact]
    public void PaintStampsThePatternAndLinesFollowBresenham()
    {
        var brush = new TileBrush();
        brush.Set([(new Vector2I(5, 5), Cell(0)), (new Vector2I(6, 5), Cell(1)), (new Vector2I(5, 6), Cell(2))]);
        Assert.Equal(new Vector2I(2, 2), brush.Size);
        var output = new Dictionary<Vector2I, TileCell>();
        TilePaint.Stamp(new Vector2I(10, -3), new TilePaint.Options(brush), output);
        Assert.Equal(Cell(0), output[new Vector2I(10, -3)]);
        Assert.Equal(Cell(1), output[new Vector2I(11, -3)]);
        Assert.Equal(Cell(2), output[new Vector2I(10, -2)]);
        Assert.Equal(3, output.Count);

        var line = new List<Vector2I>();
        TilePaint.Line(new Vector2I(0, 0), new Vector2I(4, 2), line);
        Assert.Equal([new(0, 0), new(1, 1), new(2, 1), new(3, 2), new(4, 2)], line); // ties step diagonally first
        line.Clear();
        TilePaint.Line(new Vector2I(2, 2), new Vector2I(2, -1), line);
        Assert.Equal([new(2, 2), new(2, 1), new(2, 0), new(2, -1)], line);
    }

    [Fact]
    public void RectRepeatsThePatternFromTheFirstCornerAndTheEraserEmpties()
    {
        var brush = new TileBrush();
        brush.Set([(Vector2I.Zero, Cell(0)), (new Vector2I(1, 0), Cell(1))]);
        var output = new Dictionary<Vector2I, TileCell>();
        TilePaint.Rect(new Vector2I(3, 1), new Vector2I(0, 0), new TilePaint.Options(brush), output);
        Assert.Equal(8, output.Count);
        Assert.Equal(Cell(0), output[new Vector2I(0, 0)]);
        Assert.Equal(Cell(1), output[new Vector2I(1, 1)]);
        Assert.Equal(Cell(0), output[new Vector2I(2, 1)]);

        output.Clear();
        TilePaint.Rect(new Vector2I(0, 0), new Vector2I(1, 1), new TilePaint.Options(brush, Erase: true), output);
        Assert.All(output.Values, v => Assert.True(v.IsEmpty));
        Assert.Equal(4, output.Count);
    }

    [Fact]
    public void TheBucketFillsTheConnectedRegionOrEveryMatchingCell()
    {
        var layer = new TileMapLayer { TileSet = Set() };
        // A ring of tile 1 around (1,1)..(2,2), and one more tile-0 cell far away.
        for (var x = 0; x < 4; x++)
        {
            layer.SetCell(new Vector2I(x, 0), 0, new Vector2I(1, 0));
            layer.SetCell(new Vector2I(x, 3), 0, new Vector2I(1, 0));
        }

        for (var y = 1; y < 3; y++)
        {
            layer.SetCell(new Vector2I(0, y), 0, new Vector2I(1, 0));
            layer.SetCell(new Vector2I(3, y), 0, new Vector2I(1, 0));
            layer.SetCell(new Vector2I(1, y), 0, Vector2I.Zero);
            layer.SetCell(new Vector2I(2, y), 0, Vector2I.Zero);
        }

        layer.SetCell(new Vector2I(8, 8), 0, Vector2I.Zero);
        var output = new Dictionary<Vector2I, TileCell>();
        var brush = TileBrush.OfCell(Cell(2));
        TilePaint.Bucket(layer, new Vector2I(1, 1), contiguous: true, new TilePaint.Options(brush), output);
        Assert.Equal(4, output.Count);
        Assert.DoesNotContain(new Vector2I(8, 8), output.Keys);

        output.Clear();
        TilePaint.Bucket(layer, new Vector2I(1, 1), contiguous: false, new TilePaint.Options(brush), output);
        Assert.Equal(5, output.Count);
        Assert.Contains(new Vector2I(8, 8), output.Keys);

        // Filling empty space stays inside the used rect grown by one chunk (16).
        output.Clear();
        TilePaint.Bucket(layer, new Vector2I(5, 5), contiguous: true, new TilePaint.Options(brush), output);
        var bounds = TilePaint.FillBounds(layer, new Vector2I(5, 5), fillingEmpty: true);
        Assert.Equal(new Rect2I(-16, -16, 9 + 32, 9 + 32), bounds);
        Assert.Equal(bounds.Size.X * bounds.Size.Y - layer.UsedCellCount, output.Count);
        layer.Free();
    }

    [Fact]
    public void RandomScatterPicksByProbabilityAndLeavesCellsEmpty()
    {
        var set = Set();
        set.GetSource(0)!.GetOrCreateTileData(new Vector2I(1, 0)).Probability = 3f;
        var brush = new TileBrush { Random = true };
        brush.Set([(Vector2I.Zero, Cell(0)), (new Vector2I(1, 0), Cell(1))]);
        var output = new Dictionary<Vector2I, TileCell>();
        TilePaint.Rect(new Vector2I(0, 0), new Vector2I(99, 99), new TilePaint.Options(brush, Scattering: 0.5f, Rng: new Random(7), TileSet: set), output);
        Assert.InRange(output.Count, 4500, 5500); // half left empty
        var ones = output.Values.Count(c => c.AtlasX == 1);
        Assert.InRange(ones / (double)output.Count, 0.7, 0.8); // weight 3 : 1

        var again = new Dictionary<Vector2I, TileCell>();
        TilePaint.Rect(new Vector2I(0, 0), new Vector2I(99, 99), new TilePaint.Options(brush, Scattering: 0.5f, Rng: new Random(7), TileSet: set), again);
        Assert.Equal(output, again); // seeded: the same cells
    }

    [Fact]
    public void BrushTransformsFollowGodot()
    {
        // Godot's rotation cycle: none → (V, T) → (H, V) → (H, T) → none.
        var rotated = TileBrush.TransformAlternative(0, TileBrushTransform.RotateRight);
        Assert.Equal(TileSetAtlasSource.TransformFlipV | TileSetAtlasSource.TransformTranspose, rotated);
        Assert.Equal(0, TileBrush.TransformAlternative(rotated, TileBrushTransform.RotateLeft));
        var four = 3;
        for (var i = 0; i < 4; i++)
            four = TileBrush.TransformAlternative(four, TileBrushTransform.RotateRight);
        Assert.Equal(3, four); // the alternative id is kept
        Assert.Equal(TileSetAtlasSource.TransformFlipH | 2, TileBrush.TransformAlternative(2, TileBrushTransform.FlipH));

        var brush = new TileBrush();
        brush.Set([(Vector2I.Zero, Cell(0)), (new Vector2I(1, 0), Cell(1))]);
        brush.Transform(TileBrushTransform.FlipH);
        Assert.Contains((new Vector2I(1, 0), Cell(0, alternative: TileSetAtlasSource.TransformFlipH)), brush.Cells);
        brush.Transform(TileBrushTransform.RotateRight);
        Assert.Equal(new Vector2I(1, 2), brush.Size);
    }

    [Fact]
    public void TileKeysWinOnlyWhilePainting()
    {
        Assert.Equal("tilemap.tool.eraser", TileMapEditor.ShortcutFor(Key.E, EditorModifiers.None, TilePaintTool.Paint, false, false));
        Assert.Equal("tilemap.tool.rect", TileMapEditor.ShortcutFor(Key.R, EditorModifiers.None, TilePaintTool.Line, false, false));
        Assert.Null(TileMapEditor.ShortcutFor(Key.E, EditorModifiers.None, TilePaintTool.Select, false, false)); // the gizmo's rotate
        Assert.Null(TileMapEditor.ShortcutFor(Key.R, EditorModifiers.None, TilePaintTool.Select, false, false));
        Assert.Equal("tilemap.tool.select", TileMapEditor.ShortcutFor(Key.Q, EditorModifiers.None, TilePaintTool.Bucket, false, false));
        Assert.Equal("tilemap.tool.bucket", TileMapEditor.ShortcutFor(Key.B, EditorModifiers.None, TilePaintTool.Select, false, false));
        Assert.Equal("tilemap.rotate_left", TileMapEditor.ShortcutFor(Key.Z, EditorModifiers.None, TilePaintTool.Paint, false, false));
        Assert.Null(TileMapEditor.ShortcutFor(Key.Z, EditorModifiers.Command, TilePaintTool.Paint, false, false)); // undo
        Assert.Equal("tilemap.copy", TileMapEditor.ShortcutFor(Key.C, EditorModifiers.Command, TilePaintTool.Select, true, false));
        Assert.Null(TileMapEditor.ShortcutFor(Key.C, EditorModifiers.Command, TilePaintTool.Select, false, false));
        Assert.Equal("tilemap.delete", TileMapEditor.ShortcutFor(Key.Delete, EditorModifiers.None, TilePaintTool.Select, true, false));
        Assert.Null(TileMapEditor.ShortcutFor(Key.Delete, EditorModifiers.None, TilePaintTool.Select, false, false)); // deletes the node
    }
}

/// <summary>G2b in the headless editor: strokes in the 2D view, one undo entry each, the paint bar, palette, TileSet tab.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TileMapEditorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private TileMapEditor Tiles => W.TileMap;

    private static TileSet Set()
    {
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]) };
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                source.CreateTile(new Vector2I(x, y));
        var set = new TileSet();
        set.AddSource(source);
        return set;
    }

    private (EditedScene Scene, TileMapLayer Layer) Scene2D()
    {
        var scene = W.Session.NewScene("Node2D");
        var layer = new TileMapLayer { Name = "Ground", TileSet = Set() };
        scene.AddNode(layer, scene.Root);
        var walls = new TileMapLayer { Name = "Walls", TileSet = layer.TileSet };
        scene.AddNode(walls, scene.Root);
        scene.Selection.Set(layer);
        scene.Camera.Center2D = new Vector2(64, 64);
        scene.Camera.Zoom2D = 2f;
        _editor.Tick(2);
        return (scene, layer);
    }

    private Vector2 Window(EditedScene scene, Vector2 world)
    {
        var pixel = scene.Camera.WorldToScreen2D(world, W.Viewport.ViewPixels);
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    private void Drag(EditedScene scene, Vector2 from, Vector2 to, MouseButton button = MouseButton.Left)
    {
        var a = Window(scene, from);
        var b = Window(scene, to);
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = a });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = button, Pressed = true, Position = a });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = (a + b) / 2 });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = b });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = button, Pressed = false, Position = b });
        _editor.Tick();
    }

    [Fact]
    public void ThePaintBarAndTheTileMapTabFollowTheSelection()
    {
        var (scene, layer) = Scene2D();
        Assert.True(Tiles.IsActive);
        Assert.Equal(BottomDockTab.TileMap, W.BottomTab);
        Assert.True(W.TileMapPanel.Visible);
        Assert.Contains("tilemap.tool.bucket", Tiles.PaintBarRml(), StringComparison.Ordinal);
        Assert.Contains("data-tooltip=\"Bucket (B)", Tiles.PaintBarRml(), StringComparison.Ordinal);
        scene.Selection.Set(scene.Root);
        _editor.Tick(2);
        Assert.False(Tiles.IsActive);
        Assert.Equal(BottomDockTab.Output, W.BottomTab);
        Assert.False(W.TileMapPanel.Visible);
        scene.Selection.Set(layer);
        _editor.Tick(2);
        W.Commands.Execute("tilemap.edit_tileset");
        _editor.Tick(2);
        Assert.Equal(BottomDockTab.TileSet, W.BottomTab);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void APaintStrokeIsOneUndoEntryAndUndoRedoRestoresExactCells()
    {
        var (scene, layer) = Scene2D();
        layer.SetCell(new Vector2I(2, 2), 0, new Vector2I(3, 1)); // painted over, then restored by undo
        Tiles.SelectTiles(0, [new Vector2I(1, 0)]);
        var entries = scene.History.Position;
        Drag(scene, new Vector2(8, 40), new Vector2(72, 40)); // cells (0,2) … (4,2)
        Assert.Equal(entries + 1, scene.History.Position);
        Assert.Equal("Paint Tiles", scene.History.UndoAction?.Name);
        for (var x = 0; x <= 4; x++)
            Assert.Equal(new TileCell(0, new Vector2I(1, 0)), layer.GetCell(new Vector2I(x, 2)));

        scene.History.Undo();
        Assert.Equal(new TileCell(0, new Vector2I(3, 1)), layer.GetCell(new Vector2I(2, 2)));
        Assert.Equal(1, layer.UsedCellCount);
        scene.History.Redo();
        Assert.Equal(5, layer.UsedCellCount);

        // A right drag erases, as one "Erase Tiles" entry.
        Drag(scene, new Vector2(24, 40), new Vector2(40, 40), MouseButton.Right);
        Assert.Equal("Erase Tiles", scene.History.UndoAction?.Name);
        Assert.Equal(3, layer.UsedCellCount);
    }

    [Fact]
    public void LineRectAndBucketToolsCommitOnRelease()
    {
        var (scene, layer) = Scene2D();
        Tiles.SelectTiles(0, [new Vector2I(2, 0)]);
        W.Commands.Execute("tilemap.tool.rect");
        Drag(scene, new Vector2(8, 8), new Vector2(40, 24)); // (0,0) … (2,1)
        Assert.Equal(6, layer.UsedCellCount);
        Assert.Equal("Paint Tiles", scene.History.UndoAction?.Name);

        W.Commands.Execute("tilemap.tool.line");
        Drag(scene, new Vector2(8, 88), new Vector2(88, 88)); // (0,5) … (5,5)
        Assert.Equal(12, layer.UsedCellCount);

        Tiles.SelectTiles(0, [new Vector2I(3, 0)]);
        W.Commands.Execute("tilemap.tool.bucket");
        Drag(scene, new Vector2(8, 8), new Vector2(8, 8)); // the rect's region
        Assert.Equal("Fill Tiles", scene.History.UndoAction?.Name);
        Assert.Equal(6, layer.GetCell(new Vector2I(0, 0)) == new TileCell(0, new Vector2I(3, 0)) ? CountOf(layer, new Vector2I(3, 0)) : -1);
        scene.History.Undo();
        Assert.Equal(0, CountOf(layer, new Vector2I(3, 0)));
    }

    private static int CountOf(TileMapLayer layer, Vector2I atlas)
    {
        var cells = new List<Vector2I>();
        return layer.GetUsedCellsById(cells, 0, atlas);
    }

    [Fact]
    public void ThePickerAndCtrlClickPickTheCellIntoTheBrush()
    {
        var (scene, layer) = Scene2D();
        layer.SetCell(new Vector2I(1, 1), 0, new Vector2I(2, 1), TileSetAtlasSource.TransformFlipV);
        W.Commands.Execute("tilemap.tool.picker");
        Drag(scene, new Vector2(24, 24), new Vector2(24, 24));
        Assert.Equal(new TileCell(0, new Vector2I(2, 1), TileSetAtlasSource.TransformFlipV), Tiles.Brush.Cells[0].Cell);
        Assert.Equal(TilePaintTool.Paint, Tiles.Tool);
        Assert.Equal([new Vector2I(2, 1)], Tiles.SelectedTiles);
    }

    [Fact]
    public void SelectCopyPasteAndDeleteCells()
    {
        var (scene, layer) = Scene2D();
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(0, 0));
        layer.SetCell(new Vector2I(1, 0), 0, new Vector2I(1, 0));
        W.Commands.Execute("tilemap.tool.select");
        Drag(scene, new Vector2(4, 4), new Vector2(28, 4));
        Assert.Equal(new Rect2I(0, 0, 2, 1), Tiles.CellSelection);
        _editor.Key(Key.C, Key.ControlLeft);
        Assert.Equal(new Vector2I(2, 1), Tiles.Clipboard!.Size);
        _editor.Key(Key.V, Key.ControlLeft);
        Assert.Equal(TilePaintTool.Paint, Tiles.Tool);
        Drag(scene, new Vector2(8, 72), new Vector2(8, 72));
        Assert.Equal("Paste Tiles", scene.History.UndoAction?.Name);
        Assert.Equal(new TileCell(0, new Vector2I(1, 0)), layer.GetCell(new Vector2I(1, 4)));

        W.Commands.Execute("tilemap.tool.select");
        Drag(scene, new Vector2(4, 4), new Vector2(28, 4));
        _editor.Key(Key.Delete);
        Assert.True(layer.GetCell(new Vector2I(0, 0)).IsEmpty);
        Assert.Equal(2, layer.UsedCellCount);
        Assert.Same(layer, scene.Selection.Primary); // Delete erased the cells, not the node
    }

    [Fact]
    public void ShortcutsSwitchToolsAndEscCancelsAStroke()
    {
        var (scene, layer) = Scene2D();
        _editor.Key(Key.B);
        Assert.Equal(TilePaintTool.Bucket, Tiles.Tool);
        _editor.Key(Key.E);
        Assert.Equal(TilePaintTool.Eraser, Tiles.Tool);
        Assert.NotEqual(GizmoMode.Rotate, W.Gizmo.Mode); // the tile key won
        _editor.Key(Key.Q);
        Assert.Equal(TilePaintTool.Select, Tiles.Tool);
        _editor.Key(Key.E); // with Select active, E is the gizmo's again
        Assert.Equal(GizmoMode.Rotate, W.Gizmo.Mode);

        Tiles.SelectTiles(0, [Vector2I.Zero]);
        var a = Window(scene, new Vector2(8, 8));
        var b = Window(scene, new Vector2(60, 8));
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = a });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = a });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = b });
        _editor.Tick();
        Assert.True(layer.UsedCellCount > 1);
        _editor.Key(Key.Escape);
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = b });
        _editor.Tick();
        Assert.Equal(0, layer.UsedCellCount);
    }

    [Fact]
    public void TheLayerSwitcherCyclesSiblingLayersAndHighlightDimsTheOthers()
    {
        var (scene, layer) = Scene2D();
        var walls = (TileMapLayer)scene.Root.GetNode("Walls");
        var entries = scene.History.Position;
        W.Commands.Execute("tilemap.highlight");
        _editor.Tick();
        Assert.Equal(0.3f, walls.EditorModulate.W, 3);
        W.Commands.Execute("tilemap.layer.next");
        _editor.Tick();
        Assert.Same(walls, scene.Selection.Primary);
        Assert.Equal(1f, walls.EditorModulate.W);
        Assert.Equal(0.3f, layer.EditorModulate.W, 3);
        W.Commands.Execute("tilemap.highlight");
        Assert.Equal(1f, layer.EditorModulate.W);
        Assert.Equal(entries, scene.History.Position); // dimming is not an edit
    }

    [Fact]
    public void TheGhostIsAnUnsavedLayerBesideTheScene()
    {
        var (scene, layer) = Scene2D();
        Tiles.SelectTiles(0, [new Vector2I(1, 0), new Vector2I(2, 0)]);
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = Window(scene, new Vector2(40, 40)) });
        _editor.Tick();
        var ghost = scene.Viewport.Children.OfType<TileMapLayer>().Single(n => n.Name == "TilePaintPreview2D");
        Assert.Equal(2, ghost.UsedCellCount);
        Assert.Equal(0, layer.UsedCellCount);
        Assert.DoesNotContain("TilePaintPreview2D", System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(scene.Root)), StringComparison.Ordinal);
    }

    [Fact]
    public void TileSetEditsAreUndoable()
    {
        var (scene, layer) = Scene2D();
        var set = layer.TileSet!;
        W.Commands.Execute("tilemap.edit_tileset");
        _editor.Tick(2);
        var panel = W.TileMapPanel;
        panel.SelectSource(0);
        panel.SetMode(TileSetEditMode.Setup);
        panel.ApplyField("size-x", "8");
        Assert.Equal(8, set.GetSource(0)!.TextureRegionSize.X);
        scene.History.Undo();
        Assert.Equal(16, set.GetSource(0)!.TextureRegionSize.X);

        panel.SetMode(TileSetEditMode.Collision);
        panel.SelectTile(0, new Vector2I(1, 0));
        panel.RunOp("full-tile");
        Assert.Single(set.PhysicsLayers);
        Assert.Single(set.GetSource(0)!.GetTileData(new Vector2I(1, 0)).Collision);
        scene.History.Undo(); // the full tile
        Assert.Empty(set.GetSource(0)!.GetTileData(new Vector2I(1, 0)).Collision);
        scene.History.Redo();
        Assert.Single(set.GetSource(0)!.GetTileData(new Vector2I(1, 0)).Collision);

        panel.SetMode(TileSetEditMode.Select);
        panel.ApplyField("probability", "0.25");
        Assert.Equal(0.25f, set.GetSource(0)!.GetTileData(new Vector2I(1, 0)).Probability);
        _editor.Tick(2);
        Assert.Empty(_editor.RmlMessages);
    }
}

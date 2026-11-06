using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>The brush's weights and the stroke's stamp spacing (no editor).</summary>
public sealed class TerrainBrushTests
{
    [Fact]
    public void TheShapeIsFullInsideAndFadesToTheEdge()
    {
        var brush = new TerrainBrush { Size = 10f, Falloff = 0.5f };
        Assert.Equal(1f, brush.Shape01(0f, 0f));
        Assert.Equal(1f, brush.Shape01(2.5f, 0f)); // the fade starts at half the radius
        Assert.InRange(brush.Shape01(3.75f, 0f), 0.49f, 0.51f); // halfway through the fade
        Assert.Equal(0f, brush.Shape01(5.01f, 0f));
        Assert.Equal(0f, brush.Shape01(4f, 4f)); // outside the circle
        brush.Shape = TerrainBrushShape.Square;
        Assert.True(brush.Shape01(2.4f, 2.4f) > 0.99f); // inside the square's inner part
        brush.Falloff = 0f;
        Assert.Equal(1f, brush.Shape01(5f, 0f)); // a hard edge includes the edge
    }

    [Fact]
    public void WeightIsShapeTimesStrengthAndTheSlopeLimitSkipsSamples()
    {
        var brush = new TerrainBrush { Size = 10f, Strength = 0.4f, SlopeLimit = true, SlopeMin = 10f, SlopeMax = 30f };
        Assert.Equal(0.4f, brush.Weight(0f, 0f, 20f), 5);
        Assert.Equal(0f, brush.Weight(0f, 0f, 5f));
        Assert.Equal(0f, brush.Weight(0f, 0f, 45f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    public void StampsDependOnThePathNotTheEventRate(int events)
    {
        var stroke = new TerrainStroke();
        var stamps = new List<Vector2> { stroke.Begin(Vector2.Zero) };
        for (var i = 1; i <= events; i++)
            stroke.MoveTo(new Vector2(10f * i / events, 0f), 1.5f, stamps);
        Assert.Equal(7, stamps.Count); // 0, 1.5, 3, … 9
        for (var i = 0; i < stamps.Count; i++)
            Assert.Equal(1.5f * i, stamps[i].X, 3);
    }

    [Theory]
    [InlineData(TerrainProfile.Faceted, 128, 2f, 32)]
    [InlineData(TerrainProfile.Faceted, 48, 2f, 24)] // 24 quads: 16 does not divide them, 12 does
    [InlineData(TerrainProfile.Realistic, 1024, 1f, 64)]
    [InlineData(TerrainProfile.Realistic, 96, 1f, 48)]
    [InlineData(TerrainProfile.Faceted, 50, 2f, 10)] // 25 quads: no even divisor; 10 m chunks of 5 quads would be odd
    public void NewTerrainsGetAChunkSizeThatDividesTheMap(TerrainProfile profile, int size, float spacing, int expected)
    {
        var chunk = TerrainEditorTool.ChunkMetersFor(profile, size, spacing);
        if (size == 50)
        {
            Assert.Equal(0, chunk); // none: Create reports it instead of throwing into the editor
            return;
        }

        Assert.Equal(expected, chunk);
        TerrainData.Create(profile, size, spacing, chunk).Validate();
    }

    [Fact]
    public void AHugeMoveIsCappedPerEvent()
    {
        var stroke = new TerrainStroke();
        var stamps = new List<Vector2>();
        stroke.Begin(Vector2.Zero);
        stroke.MoveTo(new Vector2(1000f, 0f), 1f, stamps);
        Assert.Equal(TerrainStroke.MaxStampsPerMove, stamps.Count);
    }
}

/// <summary>The Terrain dock (G8a): create, sculpt, paint, water, pick, undo and save through the real editor input path.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TerrainDockTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private TerrainEditorTool Tool => Assert.IsType<TerrainEditorTool>(W.Tools.Active);

    private Terrain3D AddTerrain(TerrainProfile profile = TerrainProfile.Faceted, bool withData = true)
    {
        var scene = _editor.Scene;
        var terrain = new Terrain3D { Name = "Terrain" };
        if (withData)
            terrain.Data = TerrainData.Create(profile, 64, profile == TerrainProfile.Faceted ? 2f : 1f);
        scene.AddNode(terrain, scene.Root);
        scene.Selection.Set(terrain);
        // Look straight down at the terrain's centre (32, 0, 32).
        scene.Camera.Pivot = new Vector3(32f, 0f, 32f);
        scene.Camera.Distance = 60f;
        scene.Camera.Yaw = 0f;
        scene.Camera.Pitch = -89f;
        _editor.Tick(2);
        return terrain;
    }

    // The window point over terrain-local (x, z) at the given height.
    private Vector2 Window(float x, float z, float y = 0f)
    {
        var scene = _editor.Scene;
        var view = W.Viewport.ViewPixels;
        scene.Camera.Apply(view);
        Assert.True(scene.Camera.Project(new Vector3(x, y, z), view.X, view.Y, out var pixel));
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    private void Push(InputEvent e)
    {
        _editor.Tree.PushInput(e);
        _editor.Tick();
    }

    private void Drag(Vector2 from, Vector2 to, int steps = 8)
    {
        Push(new InputEventMouseMotion { Position = from });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        for (var i = 1; i <= steps; i++)
            Push(new InputEventMouseMotion { Position = Vector2.Lerp(from, to, i / (float)steps) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = to });
    }

    private static float[] Heights(Terrain3D terrain)
    {
        var data = terrain.Data!;
        var heights = new float[data.VerticesPerSide * data.VerticesPerSide];
        data.GetHeights(new Rect2I(0, 0, data.VerticesPerSide, data.VerticesPerSide), heights);
        return heights;
    }

    [Fact]
    public void ATerrainWithoutDataShowsTheCreateFormAndCreateIsOneUndoStep()
    {
        var terrain = AddTerrain(withData: false);
        Assert.Contains("data-tool-action=\"create\"", W.Inspector.ToolDockRml);
        Tool.SetCreateOptions(TerrainProfile.Faceted, 32, 2f);
        var button = W.Inspector.Document.GetElementById("tool-body").QuerySelector("[data-tool-action=create]");
        button.Click();
        _editor.Tick(2);
        Assert.NotNull(terrain.Data);
        Assert.Equal(32, terrain.Data!.SizeMeters);
        Assert.Contains("data-tool-action=\"mode\"", W.Inspector.ToolDockRml); // the modes replace the form
        _editor.Scene.History.Undo();
        _editor.Tick(2);
        Assert.Null(terrain.Data);
        Assert.Contains("data-tool-action=\"create\"", W.Inspector.ToolDockRml);
    }

    [Fact]
    public void ARaiseStrokeLiftsTheGroundAndUndoesBitExactInOneStep()
    {
        var terrain = AddTerrain();
        Tool.Mode = TerrainDockMode.Sculpt;
        Tool.SculptTool = TerrainSculptTool.Raise;
        Tool.Brush.Size = 12f;
        Tool.Brush.Strength = 1f;
        var before = Heights(terrain);
        var entries = _editor.Scene.History.Position;
        Drag(Window(26f, 32f), Window(38f, 32f));
        Assert.True(terrain.HeightAt(32f, 32f) > 0.4f, $"height {terrain.HeightAt(32f, 32f)}");
        Assert.Equal(0f, terrain.HeightAt(5f, 5f), 2); // far from the stroke (0 quantised to the 16-bit step)
        Assert.Equal(entries + 1, _editor.Scene.History.Position);
        var after = Heights(terrain);

        _editor.Scene.History.Undo();
        Assert.Equal(before, Heights(terrain));
        _editor.Scene.History.Redo();
        Assert.Equal(after, Heights(terrain));
    }

    [Fact]
    public void ShiftTurnsRaiseIntoSmoothAndFlattenHoldsTheFirstStampsHeight()
    {
        var terrain = AddTerrain();
        terrain.SetHeightsFrom((x, z) => x > 32f ? 4f : 0f); // a 4 m step across the middle
        Tool.Mode = TerrainDockMode.Sculpt;
        Tool.SculptTool = TerrainSculptTool.Raise;
        Tool.Brush.Size = 10f;
        Tool.Brush.Strength = 1f;
        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = true });
        Drag(Window(32f, 28f, 2f), Window(32f, 36f, 2f));
        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = false });
        var h = terrain.HeightAt(33f, 32f);
        Assert.InRange(h, 0.3f, 3.7f); // smoothed, not raised past the top
        Assert.True(terrain.HeightAt(40f, 32f) <= 4.001f);

        Tool.SculptTool = TerrainSculptTool.Flatten;
        Drag(Window(20f, 32f), Window(44f, 32f, 4f));
        Assert.InRange(terrain.HeightAt(36f, 32f), 0f, 0.5f); // flattened toward the first stamp's height (0 m)
    }

    [Fact]
    public void SetHeightPicksWithCommandClickAndRampsGoOneUndoStep()
    {
        var terrain = AddTerrain();
        terrain.SetHeightsFrom((x, z) => z < 20f ? 3f : 0f);
        Tool.Mode = TerrainDockMode.Sculpt;
        Tool.SculptTool = TerrainSculptTool.Raise;
        Push(new InputEventMouseMotion { Position = Window(32f, 10f, 3f) });
        Push(new InputEventKey { Key = Key.SuperLeft, Pressed = true });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Window(32f, 10f, 3f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Window(32f, 10f, 3f) });
        Push(new InputEventKey { Key = Key.SuperLeft, Pressed = false });
        Assert.Equal(TerrainSculptTool.SetHeight, Tool.SculptTool);
        Assert.Equal(3f, Tool.TargetHeight, 1);

        Tool.SculptTool = TerrainSculptTool.Ramp;
        Tool.Brush.Size = 6f;
        Tool.Brush.Strength = 1f;
        var entries = _editor.Scene.History.Position;
        Push(new InputEventMouseMotion { Position = Window(32f, 12f, 3f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Window(32f, 12f, 3f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Window(32f, 12f, 3f) });
        Assert.True(Tool.HasRampStart);
        Push(new InputEventMouseMotion { Position = Window(32f, 40f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Window(32f, 40f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Window(32f, 40f) });
        Assert.False(Tool.HasRampStart);
        Assert.Equal(entries + 1, _editor.Scene.History.Position);
        Assert.InRange(terrain.HeightAt(32f, 26f), 1.2f, 2.4f); // halfway down the ramp
    }

    [Fact]
    public void FacetedPaintIsCrispShiftErasesAndCommandClickPicks()
    {
        var terrain = AddTerrain();
        Tool.Mode = TerrainDockMode.Paint;
        Tool.Surface = 3;
        Tool.EraserSurface = 0;
        Tool.Brush.Size = 8f;
        Drag(Window(28f, 32f), Window(36f, 32f));
        Assert.Equal(3, terrain.SurfaceAt(32f, 32f));
        Assert.Equal(0, terrain.SurfaceAt(10f, 10f));

        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = true });
        Drag(Window(31f, 32f), Window(33f, 32f), 2);
        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = false });
        Assert.Equal(0, terrain.SurfaceAt(32f, 32f));

        Tool.Surface = 0;
        Push(new InputEventMouseMotion { Position = Window(25f, 32f) });
        Push(new InputEventKey { Key = Key.SuperLeft, Pressed = true });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Window(25f, 32f) });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Window(25f, 32f) });
        Push(new InputEventKey { Key = Key.SuperLeft, Pressed = false });
        Assert.Equal(3, Tool.Surface);
    }

    [Fact]
    public void RealisticLayersBlendAndStayNormalised()
    {
        var terrain = AddTerrain(TerrainProfile.Realistic);
        Tool.Mode = TerrainDockMode.Paint;
        Tool.Surface = 2;
        Tool.Brush.Size = 10f;
        Tool.Brush.Strength = 0.5f;
        Drag(Window(30f, 32f), Window(34f, 32f), 4);
        var centre = terrain.LayerWeightAt(2, 32f, 32f);
        Assert.InRange(centre, 0.45f, 1f);
        var sum = 0f;
        for (var l = 0; l < TerrainData.MaxLayers; l++)
            sum += terrain.LayerWeightAt(l, 32f, 32f);
        Assert.Equal(1f, sum, 2);
        Assert.Equal(1f, terrain.LayerWeightAt(0, 5f, 5f));
    }

    [Fact]
    public void UserChannelsAndRectangleFills()
    {
        var terrain = AddTerrain();
        Tool.Mode = TerrainDockMode.Paint;
        Tool.PaintTarget = TerrainPaintTarget.Channel;
        Tool.Channel = 2;
        Tool.ChannelMode = TerrainChannelMode.Id;
        Tool.ChannelValue = 7;
        Tool.Rectangle = true;
        Drag(Window(10f, 10f), Window(20f, 16f));
        Assert.Equal(7, terrain.UserChannelAt(2, 15f, 13f));
        Assert.Equal(0, terrain.UserChannelAt(2, 25f, 13f));
        Assert.Equal(0, terrain.UserChannelAt(1, 15f, 13f)); // other channels untouched
        _editor.Scene.History.Undo();
        Assert.Equal(0, terrain.UserChannelAt(2, 15f, 13f));
    }

    [Fact]
    public void TheWaterBrushLaysDepthAndUndoDriesIt()
    {
        var terrain = AddTerrain();
        Tool.Mode = TerrainDockMode.Water;
        Tool.WaterLevel = 0.8f;
        Tool.Brush.Size = 12f;
        Tool.Brush.Strength = 1f;
        Drag(Window(30f, 32f), Window(34f, 32f), 4);
        Assert.InRange(terrain.WaterDepthAt(32f, 32f), 0.7f, 0.9f);
        _editor.Scene.History.Undo();
        Assert.Equal(0f, terrain.WaterDepthAt(32f, 32f));
    }

    [Fact]
    public void TheFoliageBrushPaintsDensityForTheChosenTypeInOneUndoStep()
    {
        var terrain = AddTerrain(TerrainProfile.Realistic);
        var data = terrain.Data!;
        data.FoliageTypes = [new FoliageType { ResourceName = "Grass" }, new FoliageType { ResourceName = "Ferns", AutoGrowth = false }];
        _editor.Tick(2);
        Tool.Mode = TerrainDockMode.Foliage;
        _editor.Tick();
        Assert.Contains("Ferns", W.Inspector.ToolDockRml);
        Tool.FoliageType = 1;
        Tool.Brush.Size = 8f;
        Tool.Brush.Strength = 1f;
        Drag(Window(30f, 32f), Window(34f, 32f), 4);
        Assert.Equal(1f, data.GetFoliageDensity(1, 32, 32));
        Assert.Equal(0f, data.GetFoliageDensity(0, 32, 32));
        Assert.Equal(0f, data.GetFoliageDensity(1, 5, 5));
        _editor.Scene.History.Undo();
        Assert.Equal(0f, data.GetFoliageDensity(1, 32, 32));
    }

    // Two object types: 0 "Rock" and 1 "Crate", one per 4 m cell, each with a one-node scene.
    private static TerrainData WithObjectTypes(Terrain3D terrain)
    {
        var data = terrain.Data!;
        data.ObjectTypes =
        [
            new ScatterObjectType { Name = "Rock", Scenes = [PackedScene.Pack(new Node3D { Name = "Rock" })] },
            new ScatterObjectType { Name = "Crate", Scenes = [PackedScene.Pack(new Node3D { Name = "Crate" })], SlopeMaxDegrees = 90f },
        ];
        return data;
    }

    private void Click(Vector2 at)
    {
        Push(new InputEventMouseMotion { Position = at });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = at });
        Push(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = at });
    }

    private static int[] Ids(TerrainData data) => data.Objects.Select(o => o.Id).Order().ToArray();

    [Fact]
    public void AScatterStrokeFillsTheGridOnceAndUndoRedoKeepTheIds()
    {
        var terrain = AddTerrain();
        var data = WithObjectTypes(terrain);
        Tool.Mode = TerrainDockMode.Objects;
        Tool.ObjectType = 1;
        Tool.Brush.Size = 12f;
        Tool.Brush.Falloff = 0f;
        _editor.Tick();
        Assert.Contains("Crate", W.Inspector.ToolDockRml);
        Assert.Contains("data-tool-action=\"object-tool\"", W.Inspector.ToolDockRml);
        var entries = _editor.Scene.History.Position;

        Drag(Window(26f, 32f), Window(38f, 32f));
        var added = Ids(data);
        Assert.InRange(added.Length, 8, 30); // about 260 m² under the swath, one per 16 m² cell
        Assert.All(data.Objects, o =>
        {
            Assert.Equal(1, o.Type);
            Assert.True(o.X is > 18f and < 46f && o.Z is > 24f and < 40f, $"({o.X}, {o.Z}) is outside the stroke");
            Assert.NotNull(terrain.GetObjectNode(o.Id));
        });
        Assert.Equal(added.Length, data.Objects.Select(o => ((int)(o.X / 4f), (int)(o.Z / 4f))).Distinct().Count()); // one per cell
        Assert.Equal(entries + 1, _editor.Scene.History.Position);
        _editor.Tick();
        Assert.Contains(added.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " objects", W.Inspector.ToolDockRml);

        Drag(Window(26f, 32f), Window(38f, 32f)); // the same ground again: every cell is taken
        Assert.Equal(added, Ids(data));
        Assert.Equal(entries + 1, _editor.Scene.History.Position);

        var objects = data.Objects.ToArray();
        _editor.Scene.History.Undo();
        Assert.Empty(data.Objects);
        Assert.Equal(0, terrain.ObjectsRoot!.Children.Count(c => !c.IsQueuedForDeletion));
        _editor.Scene.History.Redo();
        Assert.Equal(objects.OrderBy(o => o.Id), data.Objects.OrderBy(o => o.Id)); // the same ids, places and sizes
        Assert.All(data.Objects, o => Assert.NotNull(terrain.GetObjectNode(o.Id)));
        Assert.Equal(added.Max() + 1, data.NextObjectId);
    }

    [Fact]
    public void ShiftRemovesOnlyTheChosenTypeUnderTheBrush()
    {
        var terrain = AddTerrain();
        var data = WithObjectTypes(terrain);
        var rock = data.AddObject(0, 32f, 32f);
        Tool.Mode = TerrainDockMode.Objects;
        Tool.ObjectType = 1;
        Tool.Brush.Size = 12f;
        Drag(Window(26f, 32f), Window(38f, 32f));
        Drag(Window(10f, 10f), Window(12f, 10f), 2); // a few far away
        var far = data.Objects.Where(o => o.Type == 1 && o.X < 20f).Select(o => o.Id).ToArray();
        Assert.NotEmpty(far);
        var before = data.Objects.ToArray();
        var entries = _editor.Scene.History.Position;

        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = true });
        Drag(Window(24f, 32f), Window(40f, 32f));
        Assert.StartsWith("Remove objects", W.Tools.CursorLabel);
        Push(new InputEventKey { Key = Key.ShiftLeft, Pressed = false });
        Assert.Equal([rock, .. far], Ids(data)); // the rock (another type) and the far crates stay
        Assert.Equal(entries + 1, _editor.Scene.History.Position);

        _editor.Scene.History.Undo();
        Assert.Equal(before.OrderBy(o => o.Id), data.Objects.OrderBy(o => o.Id));
        _editor.Scene.History.Redo();
        Assert.Equal([rock, .. far], Ids(data));
    }

    [Fact]
    public void PlaceAddsOneObjectPerClickAndWaterIsSkippedByScatter()
    {
        var terrain = AddTerrain();
        var data = WithObjectTypes(terrain);
        Tool.Mode = TerrainDockMode.Objects;
        Tool.ObjectType = 0;
        Tool.ObjectTool = TerrainObjectTool.Place;
        var entries = _editor.Scene.History.Position;
        Click(Window(20f, 22f));
        var placed = Assert.Single(data.Objects);
        Assert.Equal(0, placed.Type);
        Assert.Equal(20f, placed.X, 0);
        Assert.Equal(22f, placed.Z, 0);
        Assert.InRange(placed.Scale, 0.9f, 1.1f);
        Assert.Equal(entries + 1, _editor.Scene.History.Position);
        _editor.Scene.History.Undo();
        Assert.Empty(data.Objects);
        _editor.Scene.History.Redo();
        Assert.Equal(placed, Assert.Single(data.Objects));

        // Scatter skips water (AvoidWater) on the left half.
        data.SetWaterDepthFrom((x, z) => x < 32f ? 1f : 0f);
        Tool.ObjectTool = TerrainObjectTool.Scatter;
        Tool.Brush.Size = 16f;
        Drag(Window(24f, 44f), Window(40f, 44f));
        var scattered = data.Objects.Where(o => o.Id != placed.Id).ToArray();
        Assert.NotEmpty(scattered);
        Assert.All(scattered, o => Assert.True(o.X > 30f, $"an object at x {o.X} is in the water"));
    }

    [Fact]
    public void HotkeysResizeTheBrushAndTheLabelShowsTheTool()
    {
        AddTerrain();
        Tool.Mode = TerrainDockMode.Sculpt;
        Tool.SculptTool = TerrainSculptTool.Raise;
        Tool.Brush.Size = 16f;
        Push(new InputEventMouseMotion { Position = Window(32f, 32f) });
        Push(new InputEventKey { Key = Key.RightBracket, Pressed = true });
        Assert.Equal(20f, Tool.Brush.Size, 3);
        Push(new InputEventKey { Key = Key.LeftBracket, Pressed = true });
        Assert.Equal(16f, Tool.Brush.Size, 3);
        Assert.True(Tool.Hovering);
        Assert.StartsWith("Raise · 16 m", W.Tools.CursorLabel);
    }

    [Fact]
    public void SavingWritesTheEditedLayersBesideTheScene()
    {
        var terrain = AddTerrain();
        Tool.Mode = TerrainDockMode.Sculpt;
        Tool.SculptTool = TerrainSculptTool.Raise;
        Drag(Window(26f, 32f), Window(38f, 32f));
        var path = Path.Combine(_editor.Directory, "Assets", "Valley.mfscene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        W.Session.Save(_editor.Scene, path);
        var folder = Path.Combine(_editor.Directory, "Assets", "Valley_terrain");
        Assert.True(File.Exists(Path.Combine(folder, TerrainData.HeightmapFile)), string.Join(", ", Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!)));
        Assert.False(_editor.Scene.History.IsDirty);
        Assert.True(terrain.Data!.IsExternal);
    }
}

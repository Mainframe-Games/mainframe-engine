using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>ADR 0224: the Select Frames grid maths, SpriteFrames edits as single undo entries, and the snapshot.</summary>
public sealed class SheetSlicerModelTests
{
    [Fact]
    public void CountsAndSizesFollowEachOtherAsInGodotsDialog()
    {
        var model = new SheetSlicerModel(new Vector2I(128, 64));
        Assert.Equal((SheetSliceMode.Counts, 4, 4, new Vector2I(32, 16)), (model.Mode, model.Columns, model.Rows, model.CellSize));

        model.SetCellSize(new Vector2I(16, 16));
        Assert.Equal((SheetSliceMode.Size, 8, 4), (model.Mode, model.Columns, model.Rows));

        model.SetSeparation(new Vector2I(2, 0));
        Assert.Equal(7, model.Columns); // (128 + 2) / 18
        model.SetOffset(new Vector2I(4, 0));
        Assert.Equal(new Rect2I(4, 0, 16, 16), model.Grid.CellRect(Vector2I.Zero));

        model.SetCounts(2, 1);
        Assert.Equal(new Vector2I(61, 64), model.CellSize); // (128 − 4 − 2) / 2
        Assert.Equal(model.Settings, new SheetSlicerModel(new Vector2I(128, 64), model.Settings).Settings);
    }

    [Fact]
    public void PicksAreNumberedInClickOrderAndDroppedWhenTheGridShrinks()
    {
        var model = new SheetSlicerModel(new Vector2I(64, 32));
        model.SetCounts(4, 2);
        model.Toggle(new Vector2I(3, 0));
        model.Toggle(new Vector2I(0, 1));
        model.Toggle(new Vector2I(1, 0));
        Assert.Equal([new(3, 0), new(0, 1), new(1, 0)], model.Selected);
        Assert.Equal((1, 3, 0), (model.NumberOf(new Vector2I(3, 0)), model.NumberOf(new Vector2I(1, 0)), model.NumberOf(new Vector2I(2, 0))));
        Assert.Equal("Add 3 Frames", model.AddLabel);

        model.Toggle(new Vector2I(0, 1)); // unpick
        model.Toggle(new Vector2I(9, 9)); // outside: ignored
        Assert.Equal([new(3, 0), new(1, 0)], model.Selected);

        model.SetCounts(2, 1); // (3, 0) is gone
        Assert.Equal([new(1, 0)], model.Selected);
        Assert.Equal("Add 1 Frame", model.AddLabel);

        model.SelectAll();
        Assert.Equal([new(1, 0), new(0, 0)], model.Selected); // kept picks first, then the rest row by row
        model.Clear();
        Assert.Empty(model.Selected);
    }

    [Fact]
    public void TheLastGridIsRememberedPerSheetInTheProjectCache()
    {
        var project = Directory.CreateTempSubdirectory("mf-sheet-grids").FullName;
        try
        {
            Assert.Null(SheetGridMemory.Get(project, "Assets/hero.png"));
            var settings = new SheetGridSettings(SheetSliceMode.Size, 4, 2, 24, 32, 1, 2, 3, 4);
            SheetGridMemory.Set(project, "Assets/hero.png", settings);
            SheetGridMemory.Set(project, "Assets/tiles.png", settings with { Mode = SheetSliceMode.Counts });
            Assert.Equal(settings, SheetGridMemory.Get(project, "Assets/hero.png"));
            Assert.True(File.Exists(Path.Combine(project, ".mainframe", "cache", "sheet_grids.json")));

            File.WriteAllText(SheetGridMemory.FileOf(project), "{ not json");
            Assert.Null(SheetGridMemory.Get(project, "Assets/hero.png")); // a damaged cache is only forgotten settings
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}

public sealed class SpriteFramesEditingTests
{
    private sealed class Context : IInspectorContext
    {
        public UndoRedo History { get; } = new();

        public void SetProperty(object target, MainframeEngine.Serialization.ExportPropertyInfo property, object? value, string? mergeKey = null) =>
            History.Commit(SetPropertyAction.Create(target, property, value), mergeKey: mergeKey);
    }

    private static readonly Texture2D Sheet = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]);

    [Fact]
    public void AddingFramesFromASheetIsOneUndoEntryAndUndoRestoresEverything()
    {
        var frames = new SpriteFrames();
        var run = frames.AddAnimation("run");
        var context = new Context();
        var grid = new SpriteSheetGrid(new Vector2I(16, 16));

        Assert.True(SpriteFramesEditing.Edit(context, frames, "Add Frames", _ => run.AddFrames(Sheet, grid, [new(0, 0), new(1, 0), new(2, 1)])));
        Assert.Equal(1, context.History.Position);
        Assert.Equal("Add Frames", context.History.UndoAction!.Name);
        Assert.Equal(new Rect2(32, 16, 16, 16), run.Frames[2].Region);
        Assert.Same(Sheet, run.Textures.Single());

        context.History.Undo();
        Assert.Empty(run.Frames);
        Assert.Empty(run.Textures);
        context.History.Redo();
        Assert.Equal(3, run.FrameCount);

        // An edit that changes nothing is not recorded.
        Assert.False(SpriteFramesEditing.Edit(context, frames, "Nothing", _ => run.Speed = run.Speed));
        Assert.Equal(1, context.History.Position);
    }

    [Fact]
    public void AnimationEditsUndoAsOneStepEach()
    {
        var frames = new SpriteFrames();
        var context = new Context();
        SpriteFramesEditing.Edit(context, frames, "Add Animation", f => f.AddAnimation("idle"));
        var idle = frames.GetAnimation("idle")!;
        idle.AddFrame(Sheet, new Rect2(0, 0, 16, 16));
        SpriteFramesEditing.Edit(context, frames, "Duplicate", f => SpriteFramesEditing.Duplicate(f, idle, SpriteFramesEditing.UniqueName(f, "idle")));
        Assert.Equal(["idle", "idle 2"], frames.Animations.Select(a => a.Name));
        Assert.Equal(1, frames.GetAnimation("idle 2")!.FrameCount);

        SpriteFramesEditing.Edit(context, frames, "Rename", f => f.RenameAnimation("idle 2", "run"));
        SpriteFramesEditing.Edit(context, frames, "Delete", f => f.RemoveAnimation("idle"));
        Assert.Equal(["run"], frames.Animations.Select(a => a.Name));

        context.History.Undo();
        Assert.Equal(["idle", "run"], frames.Animations.Select(a => a.Name));
        Assert.Same(idle, frames.GetAnimation("idle")); // the same object comes back
        context.History.Undo();
        Assert.Equal(["idle", "idle 2"], frames.Animations.Select(a => a.Name));
        context.History.Undo();
        context.History.Undo();
        Assert.Empty(frames.Animations);
    }

    [Fact]
    public void FrameOperationsKeepTexturesConsistent()
    {
        var other = Texture2D.FromPixels(8, 8, new byte[8 * 8 * 4]);
        var run = new SpriteAnimation();
        run.AddFrame(Sheet, new Rect2(0, 0, 16, 16));
        run.AddFrame(other);
        run.AddFrame(Sheet, new Rect2(16, 0, 16, 16), 2f);

        Assert.Equal(1, SpriteFramesEditing.MoveFrame(run, 2, -1));
        Assert.Equal(16, run.Frames[1].Region.Position.X);
        Assert.Equal(0, SpriteFramesEditing.MoveFrame(run, 0, -1)); // already first

        SpriteFramesEditing.RemoveFrames(run, 2); // the only frame of `other`: its texture goes, indices stay valid
        Assert.Equal([Sheet], run.Textures);
        Assert.All(run.Frames, f => Assert.Equal(0, f.Texture));

        var target = new SpriteAnimation();
        target.AddFrame(other);
        SpriteFramesEditing.InsertFrames(target, run, [run.Frames[1]], at: 0);
        Assert.Equal(2, target.FrameCount);
        Assert.True(target.TryGetFrame(0, out var texture, out var region));
        Assert.Same(Sheet, texture);
        Assert.Equal(new Rect2(16, 0, 16, 16), region);
        Assert.Equal(2f, target.Frames[0].Duration);

        SpriteFramesEditing.SetDuration(target, 1, 0f);
        Assert.Equal(0.01f, target.Frames[1].Duration);
    }

    [Fact]
    public void ASheetBecomesOneAnimationPerPickedRow()
    {
        var grid = new SpriteSheetGrid(new Vector2I(16, 16));
        var single = SpriteFramesEditing.FromSheet(Sheet, grid, [new(2, 0), new(0, 0)]);
        Assert.Equal(["default"], single.Animations.Select(a => a.Name));
        Assert.Equal(32, single.Animations[0].Frames[0].Region.Position.X); // click order

        var rows = SpriteFramesEditing.FromSheet(Sheet, grid, [new(0, 1), new(0, 0), new(1, 1)]);
        Assert.Equal(["row 1", "row 2"], rows.Animations.Select(a => a.Name));
        Assert.Equal(2, rows.GetFrameCount("row 2"));
    }
}

/// <summary>The SpriteFrames editor in the inspector of an AnimatedSprite2D, the Select Frames dialog and sprite picking.</summary>
[Collection(nameof(SerialEditor))]
public sealed class SpriteFramesInspectorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private static readonly Texture2D Sheet = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]);

    private (EditedScene Scene, AnimatedSprite2D Sprite) Scene()
    {
        var scene = W.Session.NewScene("Node2D");
        var frames = new SpriteFrames();
        var idle = frames.AddAnimation("idle");
        idle.AddFrames(Sheet, new SpriteSheetGrid(new Vector2I(16, 16)), [new(0, 0), new(1, 0)]);
        frames.AddAnimation("run").AddFrames(Sheet, new SpriteSheetGrid(new Vector2I(16, 16)), [new(0, 1), new(1, 1), new(2, 1)]);
        var sprite = new AnimatedSprite2D { Name = "Hero", SpriteFrames = frames, Position = new Vector2(100, 50) };
        scene.AddNode(sprite, scene.Root);
        scene.Selection.Set(sprite);
        _editor.Tick();
        return (scene, sprite);
    }

    [Fact]
    public void TheHeaderListsAnimationsAndFramesAndPickingOneSetsTheSpritesAnimation()
    {
        var (scene, sprite) = Scene();
        Assert.Equal("idle", sprite.Animation);
        var header = CustomInspectors.Find(typeof(AnimatedSprite2D))!.GetHeaderRml(sprite)!;
        Assert.Contains("sf-anim:1", header, StringComparison.Ordinal);
        Assert.Contains("sf-frame:1", header, StringComparison.Ordinal);
        Assert.Contains("data-header-input=\"sf-speed\"", header, StringComparison.Ordinal);
        Assert.IsType<SpriteFramesInspector>(CustomInspectors.Find(typeof(SpriteFrames)));

        W.Inspector.RunHeaderAction("sf-anim:1");
        Assert.Equal("run", sprite.Animation);
        Assert.Equal("Set Animation", scene.History.UndoAction!.Name);

        W.Inspector.RunHeaderAction("sf-speed=12.5");
        Assert.Equal(12.5f, sprite.SpriteFrames!.GetAnimation("run")!.Speed);
        W.Inspector.RunHeaderAction("sf-autoplay");
        Assert.Equal("run", sprite.Autoplay);

        W.Inspector.RunHeaderAction("sf-frame:2");
        W.Inspector.RunHeaderAction("sf-left");
        W.Inspector.RunHeaderAction("sf-duration=3");
        var run = sprite.SpriteFrames.GetAnimation("run")!;
        Assert.Equal((3f, 32f), (run.Frames[1].Duration, run.Frames[1].Region.Position.X));
        Assert.True(scene.IsDirty);

        scene.History.Undo(); // duration
        scene.History.Undo(); // move
        Assert.Equal((1f, 16f), (run.Frames[1].Duration, run.Frames[1].Region.Position.X));
    }

    [Fact]
    public void AddDuplicateDeleteAndRenameFollowTheSprite()
    {
        var (scene, sprite) = Scene();
        var frames = sprite.SpriteFrames!;
        W.Inspector.RunHeaderAction("sf-add");
        Assert.Equal(["idle", "run", "new"], frames.Animations.Select(a => a.Name));
        Assert.Equal("new", sprite.Animation);

        W.Inspector.RunHeaderAction("sf-anim:0");
        W.Inspector.RunHeaderAction("sf-dup");
        Assert.Equal(["idle", "idle 2", "run", "new"], frames.Animations.Select(a => a.Name));
        Assert.Equal(2, frames.GetFrameCount("idle 2"));

        var header = new SpriteFramesHeader();
        header.Rename(frames, sprite, "idle 2", "blink", scene);
        Assert.Equal("blink", sprite.Animation);
        Assert.True(frames.HasAnimation("blink"));

        W.Inspector.RunHeaderAction("sf-delete");
        Assert.False(frames.HasAnimation("blink"));
        Assert.Equal("idle", sprite.Animation);
    }

    [Fact]
    public void ThePreviewStepsFramesWithoutEditingTheSceneAndRestoresOnStop()
    {
        var (scene, sprite) = Scene();
        sprite.SpriteFrames!.GetAnimation("idle")!.Speed = 10f;
        var entries = scene.History.Position;
        W.Inspector.RunHeaderAction("sf-play");
        Assert.True(W.SpritePreview.IsPlaying(sprite));
        _editor.Tick(8); // > 0.1 s at 60 Hz
        Assert.Equal(1, sprite.Frame);
        W.Inspector.RunHeaderAction("sf-stop");
        Assert.Equal(0, sprite.Frame);
        Assert.Equal(entries, scene.History.Position);
        Assert.False(W.SpritePreview.IsPlaying(sprite));
    }

    [Fact]
    public void ASpriteWithoutFramesOffersNewAndClicksOnSpritesPickThem()
    {
        var scene = W.Session.NewScene("Node2D");
        var empty = new AnimatedSprite2D { Name = "Empty" };
        scene.AddNode(empty, scene.Root);
        scene.Selection.Set(empty);
        _editor.Tick();
        Assert.Contains("sf-new", CustomInspectors.Find(typeof(AnimatedSprite2D))!.GetHeaderRml(empty), StringComparison.Ordinal);
        W.Inspector.RunHeaderAction("sf-new");
        Assert.True(empty.SpriteFrames?.HasAnimation("default"));

        var picture = new Sprite2D { Name = "Picture", Texture = Sheet, Position = new Vector2(300, 0) };
        scene.AddNode(picture, scene.Root);
        _editor.Tick();
        var view = W.Viewport.ViewPixels;
        // Inside the 64×32 texture, 25 px from its origin: farther than an origin pick reaches.
        Assert.Same(picture, W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(325, 12), view)));
        Assert.Null(W.Viewport.Pick2D(scene, scene.Camera.WorldToScreen2D(new Vector2(340, 40), view)));
    }

    [Fact]
    public void TheSelectFramesDialogAddsThePickedCellsAndRemembersTheGrid()
    {
        var project = W.Session.ProjectRoot ?? _editor.Directory;
        var assets = Path.Combine(project, "Assets");
        Directory.CreateDirectory(assets);
        Png.WriteRgba8(Path.Combine(assets, "sheet.png"), 64, 32, new byte[64 * 32 * 4]);
        var previous = AssetDatabase.Current;
        AssetDatabase.Current = new AssetDatabase(project);
        try
        {
            SpriteSheetGrid? grid = null;
            IReadOnlyList<Vector2I>? cells = null;
            W.SheetSlicer.Show("Assets/sheet.png", new Vector2I(64, 32), "Select Frames — sheet.png", (g, c) => (grid, cells) = (g, c));
            _editor.Tick();
            Assert.True(W.SheetSlicer.Visible);
            Assert.True(W.IsDialogOpen);
            W.SheetSlicer.SetField("cols", 4);
            W.SheetSlicer.SetField("rows", 2);
            W.SheetSlicer.Model!.Toggle(new Vector2I(1, 1));
            W.SheetSlicer.Model.Toggle(new Vector2I(0, 0));
            W.SheetSlicer.Confirm();

            Assert.False(W.SheetSlicer.Visible);
            Assert.Equal(new Vector2I(16, 16), grid!.Value.CellSize);
            Assert.Equal([new(1, 1), new(0, 0)], cells);
            if (W.Session.ProjectRoot is { } root)
                Assert.Equal(4, SheetGridMemory.Get(root, "Assets/sheet.png")!.Columns);

            var mres = FileSystemPanel.CreateSpriteFrames(Path.Combine(assets, "sheet.png"), Sheet, grid.Value, cells!);
            Assert.Equal(Path.Combine(assets, "sheet.mres"), mres);
            Assert.Contains("\"SpriteFrames\"", File.ReadAllText(mres), StringComparison.Ordinal);
            Assert.Equal(Path.Combine(assets, "sheet 2.mres"), FileSystemPanel.CreateSpriteFrames(Path.Combine(assets, "sheet.png"), Sheet, grid.Value, cells!));
        }
        finally
        {
            AssetDatabase.Current = previous;
        }
    }
}

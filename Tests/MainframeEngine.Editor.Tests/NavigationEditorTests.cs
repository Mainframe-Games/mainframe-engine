using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests;

/// <summary>G3a in the editor: Visible Navigation, the grid inspector's Bake/Clear with undo, the agent's gizmo.</summary>
[Collection(nameof(SerialEditor))]
public sealed class NavigationEditorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private NavigationGrid2D AddLevel()
    {
        var grid = new NavigationGrid2D { Name = "Nav", Region = new Rect2I(0, 0, 8, 6), CellSize = new Vector2(10) };
        _editor.Scene.AddNode(grid, _editor.Scene.Root);
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(35, 25) };
        _editor.Scene.AddNode(wall, _editor.Scene.Root);
        _editor.Scene.AddNode(new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = new Vector2(10, 30) } }, wall);
        _editor.Tick(2);
        return grid;
    }

    [Fact]
    public void VisibleNavigationTogglesTheServersDebugDraw()
    {
        var grid = AddLevel();
        var server = W.Tree!.Servers.Get<NavigationServer2D>();
        Assert.NotNull(server);
        Assert.True(W.Viewport.NavigationVisible);
        Assert.True(server.DebugDrawEnabled);
        var button = W.Toolbar.Document.GetElementById("tool-nav");
        Assert.True(button.IsClassSet("active"));
        Assert.Contains("Visible Navigation", button.GetAttribute("data-tooltip") ?? "", StringComparison.Ordinal);

        W.Commands.Execute("view.navigation");
        _editor.Tick(2);
        Assert.False(W.Viewport.NavigationVisible);
        Assert.False(server.DebugDrawEnabled);
        Assert.False(W.Toolbar.Document.GetElementById("tool-nav").IsClassSet("active"));

        W.Commands.Execute("view.navigation");
        _editor.Tick(2);
        Assert.True(server.DebugDrawEnabled);
        Assert.Same(server.Maps[0], grid.Map);
    }

    [Fact]
    public void BakeAndClearReplaceTheDataAsUndoableEdits()
    {
        var grid = AddLevel();
        var inspector = CustomInspectors.Find(typeof(NavigationGrid2D));
        Assert.IsType<NavigationGridInspector>(inspector);
        var header = inspector!.GetHeaderRml(grid)!;
        Assert.Contains(NavigationGridInspector.BakeAction, header, StringComparison.Ordinal);
        Assert.Contains(NavigationGridInspector.ClearAction, header, StringComparison.Ordinal);
        Assert.Contains("icon-wand", header, StringComparison.Ordinal);
        Assert.Contains("not baked", header, StringComparison.Ordinal);

        _editor.Scene.Selection.Set(grid);
        _editor.Tick();
        W.Inspector.RunHeaderAction(NavigationGridInspector.BakeAction);
        _editor.Tick(2);
        Assert.NotNull(grid.Data);
        Assert.Equal(3, grid.Data!.SolidCount); // the wall covers cells (3, 1..3)
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(3, 2)));
        Assert.Contains("3 solid", NavigationGridInspector.Status(grid), StringComparison.Ordinal);
        Assert.Contains("Physics", NavigationGridInspector.Status(grid), StringComparison.Ordinal);
        Assert.True(_editor.Scene.IsDirty);

        var baked = grid.Data;
        W.Inspector.RunHeaderAction(NavigationGridInspector.ClearAction);
        _editor.Tick(2);
        Assert.Null(grid.Data);
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(3, 2)));

        _editor.Scene.History.Undo(); // Clear
        _editor.Tick(2);
        Assert.Same(baked, grid.Data);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(3, 2)));
        _editor.Scene.History.Undo(); // Bake
        _editor.Tick(2);
        Assert.Null(grid.Data);
        _editor.Scene.History.Redo();
        Assert.Same(baked, grid.Data);

        // Moving the wall makes the bake out of date.
        _editor.Scene.Root.GetNode<StaticBody2D>("Wall").Position = new Vector2(55, 25);
        _editor.Tick(2);
        Assert.Contains("out of date", NavigationGridInspector.Status(grid), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheCurrentSourcesBakeRowsShow()
    {
        var grid = AddLevel();
        var inspector = CustomInspectors.Find(typeof(NavigationGrid2D))!;
        var type = TypeRegistry.GetRequired(typeof(NavigationGrid2D));
        bool Shows(string name) => inspector.ShowProperty(grid, type.FindProperty(name)!);
        Assert.True(Shows(nameof(NavigationGrid2D.BakeCollisionMask)));
        Assert.True(Shows(nameof(NavigationGrid2D.AgentRadius)));
        Assert.False(Shows(nameof(NavigationGrid2D.TileMapLayerPath)));
        Assert.False(Shows(nameof(NavigationGrid2D.SolidDataLayer)));
        grid.Source = NavigationGridSource.TileMapLayer;
        Assert.False(Shows(nameof(NavigationGrid2D.AgentRadius)));
        Assert.True(Shows(nameof(NavigationGrid2D.TilePhysicsLayer)));
        Assert.True(Shows(nameof(NavigationGrid2D.Region)));
        Assert.Contains("TileMapLayer", inspector.GetHeaderRml(grid)!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectedAgentDrawsItsArrivalCircles()
    {
        AddLevel();
        var body = new CharacterBody2D { Name = "Chaser", Position = new Vector2(20, 20) };
        _editor.Scene.AddNode(body, _editor.Scene.Root);
        var agent = new NavigationAgent2D { Name = "Agent", TargetPosition = new Vector2(70, 50) };
        _editor.Scene.AddNode(agent, body);
        W.Commands.Execute("view.2d");
        _editor.Tick(2);
        var overlay = _editor.Scene.Viewport.OverlayLines;

        _editor.Scene.Selection.Set(body);
        _editor.Tick();
        overlay.Clear();
        _editor.Tick();
        var without = overlay.LineCount;
        _editor.Scene.Selection.Set(agent);
        _editor.Tick();
        overlay.Clear();
        _editor.Tick();
        Assert.True(overlay.LineCount > without + 40, $"{overlay.LineCount} lines vs {without}"); // three circles and a line
    }
}

using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// G3a in the editor: Visible Navigation, the grid inspector's Bake/Clear with undo, the agent's gizmo; G3.9: the navigation
/// region inspector's Bake/Clear with undo, the region and link gizmos.
/// </summary>
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

    [Fact]
    public void TheSelectedObstacleDrawsItsShapeAndAnAvoidingAgentItsRadius()
    {
        AddLevel();
        var obstacle = new NavigationObstacle2D
        {
            Name = "Pillar",
            Position = new Vector2(60, 60),
            Radius = 12f,
            Vertices = [new(-10, -10), new(10, -10), new(10, 10), new(-10, 10)],
        };
        _editor.Scene.AddNode(obstacle, _editor.Scene.Root);
        var body = new CharacterBody2D { Name = "Chaser", Position = new Vector2(20, 20) };
        _editor.Scene.AddNode(body, _editor.Scene.Root);
        var agent = new NavigationAgent2D { Name = "Agent" };
        _editor.Scene.AddNode(agent, body);
        W.Commands.Execute("view.2d");
        _editor.Tick(2);
        var overlay = _editor.Scene.Viewport.OverlayLines;

        int Lines(Node selected)
        {
            _editor.Scene.Selection.Set(selected);
            _editor.Tick();
            overlay.Clear();
            _editor.Tick();
            return overlay.LineCount;
        }

        var bodyOnly = Lines(body);
        Assert.True(Lines(obstacle) > bodyOnly + 4 * 8, "four edges and a circle per vertex and the radius");
        var agentPlain = Lines(agent);
        agent.AvoidanceEnabled = true;
        Assert.True(Lines(agent) > agentPlain + 8, "the avoidance radius circle");
    }

    private NavigationRegion2D AddMeshLevel()
    {
        var polygon = new NavigationPolygon { BakingRect = new Rect2(0, 0, 200, 100), AgentRadius = 5f };
        var region = new NavigationRegion2D { Name = "Mesh", NavigationPolygon = polygon };
        _editor.Scene.AddNode(region, _editor.Scene.Root);
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(100, 50) };
        _editor.Scene.AddNode(wall, region);
        _editor.Scene.AddNode(new CollisionShape2D { Name = "Shape", Shape = new RectangleShape2D { Size = new Vector2(20, 40) } }, wall);
        _editor.Tick(2);
        return region;
    }

    [Fact]
    public void RegionBakeAndClearAreUndoableEditsOfThePolygon()
    {
        var region = AddMeshLevel();
        var polygon = region.NavigationPolygon!;
        var inspector = CustomInspectors.Find(typeof(NavigationRegion2D));
        Assert.IsType<NavigationRegionInspector>(inspector);
        var header = inspector!.GetHeaderRml(region)!;
        Assert.Contains(NavigationRegionInspector.BakeAction, header, StringComparison.Ordinal);
        Assert.Contains(NavigationRegionInspector.ClearAction, header, StringComparison.Ordinal);
        Assert.Contains("not baked", header, StringComparison.Ordinal);
        Assert.Contains("baking rect", header, StringComparison.Ordinal);

        _editor.Scene.Selection.Set(region);
        _editor.Tick();
        W.Inspector.RunHeaderAction(NavigationRegionInspector.BakeAction);
        _editor.Tick(2);
        Assert.True(polygon.PolygonCount >= 4); // round the wall
        Assert.Contains("polygons", NavigationRegionInspector.Status(region), StringComparison.Ordinal);
        Assert.True(_editor.Scene.IsDirty);
        Assert.Equal(polygon.PolygonCount, region.Map!.PolygonCount);
        var baked = polygon.Polygons;

        W.Inspector.RunHeaderAction(NavigationRegionInspector.ClearAction);
        _editor.Tick(2);
        Assert.Equal(0, polygon.PolygonCount);
        Assert.Equal(0, region.Map.PolygonCount);

        _editor.Scene.History.Undo(); // Clear
        _editor.Tick(2);
        Assert.Same(baked, polygon.Polygons);
        Assert.Equal(baked.Length, region.Map.PolygonCount);
        _editor.Scene.History.Undo(); // Bake
        _editor.Tick(2);
        Assert.Equal(0, polygon.PolygonCount);
        _editor.Scene.History.Redo();
        Assert.Same(baked, polygon.Polygons);

        region.NavigationPolygon = null;
        Assert.Contains("no navigation polygon", NavigationRegionInspector.Status(region), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectedRegionDrawsItsBakeInputAndTheSelectedLinkItsReach()
    {
        var region = AddMeshLevel();
        region.NavigationPolygon!.AddOutline([new(0, 0), new(50, 0), new(50, 50)]);
        var link = new NavigationLink2D { Name = "Link", Position = new Vector2(20, 20), EndPosition = new Vector2(150, 0) };
        _editor.Scene.AddNode(link, _editor.Scene.Root);
        W.Commands.Execute("view.2d");
        _editor.Tick(2);
        var overlay = _editor.Scene.Viewport.OverlayLines;

        int Lines(Node selected)
        {
            _editor.Scene.Selection.Set(selected);
            _editor.Tick();
            overlay.Clear();
            _editor.Tick();
            return overlay.LineCount;
        }

        var wallOnly = Lines(_editor.Scene.Root.GetNode("Mesh/Wall"));
        Assert.True(Lines(region) > wallOnly + 3 + 16, "the outline's edges, a dot per vertex and the dashed baking rect");
        var bidirectional = Lines(link);
        Assert.True(bidirectional > wallOnly + 2 * 16, "a line, two reach circles and two arrow heads");
        link.Bidirectional = false;
        Assert.Equal(bidirectional - 2, Lines(link));
    }
}

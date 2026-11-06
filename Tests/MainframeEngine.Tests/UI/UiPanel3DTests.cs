using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>G11.5/G11.6 (ADRs 0252/0253): <see cref="UiPanel3D"/> contexts, gating and input, headless.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class UiPanel3DTests
{
    private const string Body = """
        <button id="btn">Go</button>
        <input id="field" type="text" value=""/>
        """;

    private sealed class Terminal : UiDocument
    {
        public int Clicks { get; private set; }

        public string ModelName { get; init; } = "terminal";

        protected override void OnReady()
        {
            CreateDataModel(ModelName).Bind("clicks", this, static d => d.Clicks);
            GetElementById("btn")!.Click += _ => Clicks++;
        }
    }

    private sealed class Scene : IDisposable
    {
        public readonly UiTestTree Ui;
        public readonly Camera3D Camera;

        public Scene(bool physics = false)
        {
            Ui = new UiTestTree(viewport: new Vector2(800, 600));
            if (physics)
                Ui.Servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
            Ui.Tree.Root.SetSize(new Vector2(800, 600));
            Camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0, 10), Fov = 60f, Current = true };
            Ui.Tree.Root.AddChild(Camera);
            Camera.LookAt(Vector3.Zero);
            Source = Ui.Write("UI/terminal.rml", UiTestTree.Page(Body));
        }

        public string Source { get; }

        // A 400×300 px panel of 4 × 3 m at the origin, facing the camera 10 m away.
        public (UiPanel3D Panel, Terminal Document) AddPanel(string name = "Screen", Action<UiPanel3D>? configure = null, Node? parent = null)
        {
            var panel = new UiPanel3D { Name = name, Resolution = new Vector2I(400, 300), PixelSize = 0.01f, MaxInteractDistance = 20f };
            configure?.Invoke(panel);
            var document = new Terminal { Name = "Ui", Source = Source, ModelName = "terminal" };
            panel.AddChild(document);
            (parent ?? Ui.Tree.Root).AddChild(panel);
            return (panel, document);
        }

        // The window pixel showing panel pixel (x, y) of a panel at the origin.
        public Vector2 Screen(UiPanel3D panel, float x, float y)
        {
            var size = panel.QuadSize;
            var local = new Vector3((x / panel.Resolution.X - 0.5f) * size.X, (0.5f - y / panel.Resolution.Y) * size.Y, 0f);
            return Camera.UnprojectPosition(panel.GlobalTransform * local);
        }

        public void Click(Vector2 at)
        {
            Ui.Move(at.X, at.Y);
            Ui.Button(MouseButton.Left, true, at.X, at.Y);
            Ui.Button(MouseButton.Left, false, at.X, at.Y);
            Ui.Tick();
        }

        public void Dispose() => Ui.Dispose();
    }

    [Fact]
    public void DocumentsAttachToPanelsWithModelNamesPerPanel()
    {
        using var s = new Scene();
        var (a, docA) = s.AddPanel("A");
        var (b, docB) = s.AddPanel("B", p => p.Position = new Vector3(5, 0, 0)); // same model name, another context
        s.Ui.Tick();

        Assert.Same(a, docA.Panel);
        Assert.Null(docA.Layer);
        Assert.Same(docA, Assert.Single(a.Documents));
        Assert.Same(docB, Assert.Single(b.Documents));
        Assert.True(docA.IsLoaded && docB.IsLoaded);
        Assert.NotNull(a.Context);
        Assert.NotSame(a.Context, b.Context);
        Assert.Equal([a, b], s.Ui.Server.Panels);
        Assert.Equal(1, a.RenderCount);

        b.Free();
        s.Ui.Tick();
        Assert.Equal([a], s.Ui.Server.Panels);
    }

    [Fact]
    public void RaysMapToPanelPixelsOnRotatedAndScaledQuads()
    {
        using var s = new Scene();
        var (panel, _) = s.AddPanel(configure: p =>
        {
            p.Position = new Vector3(1, 2, 3);
            p.RotationDegrees = new Vector3(0, 90, 0); // faces +X
            p.Scale = new Vector3(2, 2, 2);            // an 8 × 6 m quad
        });

        Assert.True(panel.TryRayToPixel(new Vector3(11, 2, 3), -Vector3.UnitX, out var centre, out var distance));
        Assert.Equal(new Vector2(200, 150), centre);
        Assert.Equal(10, distance, 3);

        // 2 m along the quad's right (world -Z) and 1.5 m up: a quarter of the width and height from the centre.
        Assert.True(panel.TryRayToPixel(new Vector3(11, 3.5f, 1), -Vector3.UnitX, out var corner, out _));
        Assert.Equal(300, corner.X, 2);
        Assert.Equal(75, corner.Y, 2);

        Assert.False(panel.TryRayToPixel(new Vector3(11, 9, 3), -Vector3.UnitX, out _, out _)); // above the quad
        Assert.False(panel.TryRayToPixel(new Vector3(-9, 2, 3), Vector3.UnitX, out _, out _));  // from behind
        Assert.False(panel.TryRayToPixel(new Vector3(11, 2, 3), Vector3.UnitX, out _, out _));  // away from it
    }

    [Fact]
    public void TheMouseClicksAPanelButtonAndFocusesThePanel()
    {
        using var s = new Scene();
        var (panel, document) = s.AddPanel();
        var (entered, exited) = (0, 0);
        panel.PointerEntered += () => entered++;
        panel.PointerExited += () => exited++;
        s.Ui.Tick(2);

        var button = s.Screen(panel, 50, 15);
        s.Click(button);
        Assert.Equal(1, document.Clicks);
        Assert.True(panel.HasFocus);
        Assert.Same(panel, s.Ui.Server.HoveredPanel);
        Assert.Equal(1, entered);
        Assert.Empty(s.Ui.Game.Seen.OfType<InputEventMouseButton>()); // the press was on the panel

        // A click off the panel goes to the game and releases the focus.
        s.Click(new Vector2(5, 5));
        Assert.False(panel.HasFocus);
        Assert.Equal(1, exited);
        Assert.Equal(2, s.Ui.Game.Seen.OfType<InputEventMouseButton>().Count());
    }

    [Fact]
    public void ScreenLayersTakeThePointerBeforePanels()
    {
        using var s = new Scene();
        var (panel, document) = s.AddPanel();
        s.Ui.Tick();
        var target = s.Screen(panel, 50, 15);

        // A HUD button drawn over the panel's button takes the click.
        var hud = new TestHud
        {
            Source = s.Ui.Write("UI/hud.rml", UiTestTree.Page("<button id=\"btn\">Hud</button>",
                $"style=\"position: absolute; left: {target.X - 50}px; top: {target.Y - 15}px; width: 120px; height: 40px;\"")),
        };
        var layer = s.Ui.AddLayer(0, hud);
        s.Ui.Tick(2);
        s.Click(target);
        Assert.Equal(1, hud.Clicks);
        Assert.Equal(0, document.Clicks);
        Assert.Null(s.Ui.Server.HoveredPanel);

        layer.Visible = false;
        s.Click(target);
        Assert.Equal(1, document.Clicks);
    }

    [Fact]
    public void FocusedPanelsTakeTextAndEscapeReleasesThem()
    {
        using var s = new Scene();
        var (panel, document) = s.AddPanel();
        s.Ui.Tick(2);
        s.Click(s.Screen(panel, 100, 40)); // the text field below the button
        Assert.True(panel.HasFocus);
        Assert.True(s.Ui.Server.TextInputActive);

        Assert.True(s.Ui.Text('h'));
        Assert.True(s.Ui.Text('i'));
        Assert.True(s.Ui.Key(Key.A, true)); // a focused text field takes every key
        s.Ui.Key(Key.A, false);
        s.Ui.Tick();
        Assert.Equal("hi", document.GetElementById("field")!.Element.GetAttribute("value"));

        // Escape blurs the field first (RmlUi), the next one releases the panel.
        s.Ui.Key(Key.Escape, true);
        s.Ui.Key(Key.Escape, false);
        if (panel.HasFocus)
        {
            document.Document.AsElement().Focus();
            s.Ui.Key(Key.Escape, true);
            s.Ui.Key(Key.Escape, false);
        }

        Assert.False(panel.HasFocus);
        Assert.False(s.Ui.Key(Key.B, true)); // the game gets keys again
    }

    [Fact]
    public void TheCrosshairClickActionClicksAtTheScreenCentre()
    {
        using var s = new Scene();
        s.Ui.Tree.Input.Map.Bind("use", "key:E");
        var (panel, document) = s.AddPanel(configure: p =>
        {
            p.InputMode = PanelInputMode.Crosshair;
            p.ClickAction = "use";
            p.Position = new Vector3(1.5f, -1.35f, 0); // the button (50, 15) at the screen centre
        });
        s.Ui.Tick(2);
        Assert.Same(panel, s.Ui.Server.HoveredPanel);

        Assert.True(s.Ui.Key(Key.E, true));
        Assert.True(s.Ui.Key(Key.E, false));
        s.Ui.Tick();
        Assert.Equal(1, document.Clicks);

        s.Ui.Move(400, 300); // the mouse does not move a crosshair pointer
        Assert.Equal(50, panel.PointerPosition.X, 2);
        Assert.Equal(15, panel.PointerPosition.Y, 2);
    }

    [Fact]
    public void ManualPointersClickAndKeepThePanelPastItsEdgeWhileHeld()
    {
        using var s = new Scene();
        var (panel, document) = s.AddPanel(configure: p => p.InputMode = PanelInputMode.Manual);
        s.Ui.Tick(2);
        var hand = new Vector3(0, 0, 2);
        Vector3 At(float x, float y) => new Vector3((x / 400f - 0.5f) * 4f, (0.5f - y / 300f) * 3f, 0f) - hand;

        // Mouse moves do not reach a manual panel.
        var centre = s.Screen(panel, 200, 150);
        s.Ui.Move(centre.X, centre.Y);
        Assert.Null(s.Ui.Server.HoveredPanel);

        panel.PushPointer(hand, At(50, 15));
        Assert.True(panel.HasPointer);
        panel.PushButton(MouseButton.Left, true);
        Assert.True(panel.HasFocus);
        panel.PushPointer(hand, At(-30, 15)); // off the left edge while held: still on its plane
        Assert.True(panel.HasPointer);
        Assert.Equal(-30, panel.PointerPosition.X, 1);
        panel.PushPointer(hand, At(50, 15));
        panel.PushButton(MouseButton.Left, false);
        s.Ui.Tick();
        Assert.Equal(1, document.Clicks);

        panel.PushPointer(hand, At(-30, 15)); // released: off the panel
        Assert.False(panel.HasPointer);
    }

    [Fact]
    public void LineOfSightIgnoresPanelsBehindBodiesButNotTheirOwn()
    {
        using var s = new Scene(physics: true);
        var terminal = new StaticBody3D { Name = "Terminal" };
        terminal.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 3, 0.4f) }, Position = new Vector3(0, 0, -0.2f) });
        s.Ui.Tree.Root.AddChild(terminal);
        var (panel, _) = s.AddPanel(parent: terminal);
        s.Ui.Tick(2);
        var centre = s.Screen(panel, 200, 150);
        s.Ui.Move(centre.X, centre.Y);
        Assert.Same(panel, s.Ui.Server.HoveredPanel); // its own body does not block it

        var wall = new StaticBody3D { Name = "Wall", Position = new Vector3(0, 0, 5) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2, 2, 0.2f) } });
        s.Ui.Tree.Root.AddChild(wall);
        s.Ui.Tick(2); // physics picks the wall up
        s.Ui.Move(centre.X, centre.Y);
        Assert.Null(s.Ui.Server.HoveredPanel);

        panel.RequireLineOfSight = false;
        s.Ui.Move(centre.X, centre.Y);
        Assert.Same(panel, s.Ui.Server.HoveredPanel);

        panel.MaxInteractDistance = 5; // 10 m away
        s.Ui.Move(centre.X + 1, centre.Y);
        Assert.Null(s.Ui.Server.HoveredPanel);
    }

    [Fact]
    public void UpdateModeDistanceAndTheFrustumGateRenders()
    {
        using var s = new Scene();
        var (always, _) = s.AddPanel("Always");
        var (once, _) = s.AddPanel("Once", p => (p.UpdateMode, p.Position) = (SubViewportUpdateMode.Once, new Vector3(1, 0, 0)));
        var (disabled, _) = s.AddPanel("Disabled", p => (p.UpdateMode, p.Position) = (SubViewportUpdateMode.Disabled, new Vector3(-1, 0, 0)));
        var (far, _) = s.AddPanel("Far", p => p.Position = new Vector3(0, 0, -40));     // 50 m: beyond 30 m
        var (behind, _) = s.AddPanel("Behind", p => p.Position = new Vector3(0, 0, 20)); // outside the frustum
        s.Ui.Tick(); // every panel draws once after its documents load
        Assert.All(new[] { always, once, disabled, far, behind }, p => Assert.Equal(1, p.RenderCount));

        s.Ui.Tick(3);
        Assert.Equal(4, always.RenderCount);
        Assert.Equal(1, once.RenderCount); // the frame after it entered the tree, then never
        Assert.Equal(SubViewportUpdateMode.Disabled, once.UpdateMode);
        Assert.Equal(1, disabled.RenderCount);
        Assert.Equal(1, far.RenderCount);
        Assert.Equal(1, behind.RenderCount);

        far.MaxUpdateDistance = 0; // no limit
        s.Ui.Tick();
        Assert.Equal(2, far.RenderCount);
        always.Visible = false;
        s.Ui.Tick();
        Assert.Equal(5, always.RenderCount);
    }

    [Fact]
    public void AtMostEightPanelsRenderPerFrameNearestFirst()
    {
        using var s = new Scene();
        var panels = new List<UiPanel3D>();
        for (var i = 0; i < 10; i++)
            panels.Add(s.AddPanel($"P{i}", p => p.Position = new Vector3(0, 0, -i * 2)).Panel);
        s.Ui.Tick();
        Assert.Equal(UiServer.MaxPanelRendersPerFrame, s.Ui.Server.PanelsRenderedLastFrame);
        Assert.Equal(0, panels[9].RenderCount); // the farthest wait
        Assert.Equal(0, panels[8].RenderCount);
        Assert.Equal(1, panels[0].RenderCount);

        s.Ui.Tick();
        Assert.Equal(1, panels[9].RenderCount); // their first render is still owed (documents loaded)
    }

    [Fact]
    public void SteadyFramesWithAPanelUpdatingAllocateNothing()
    {
        using var s = new Scene();
        var (panel, document) = s.AddPanel();
        s.Ui.Tick(5);
        var centre = s.Screen(panel, 200, 150);
        s.Ui.Move(centre.X, centre.Y);
        var ui = s.Ui;
        var bytes = AllocationGate.SmallestWindow(() =>
        {
            document.DataModels[0].Dirty("clicks");
            ui.Tick();
        });
        Assert.Equal(0, bytes);
        Assert.True(panel.RenderCount > 5);
    }

    private sealed class TestHud : UiDocument
    {
        public int Clicks { get; private set; }

        protected override void OnReady() => GetElementById("btn")!.Click += _ => Clicks++;
    }
}

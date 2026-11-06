using System.Numerics;

namespace MainframeEngine.Tests.UI;

/// <summary>G11.4 (ADR 0250): <see cref="UiMarker3D"/> rows on a <see cref="UiMarkerHost"/>.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class UiMarkerTests
{
    private sealed class Scene : IDisposable
    {
        public readonly UiTestTree Ui;
        public readonly Camera3D Camera;
        public readonly UiMarkerHost Host;
        public readonly UiLayer Layer;

        public Scene(bool physics = false)
        {
            Ui = new UiTestTree(viewport: new Vector2(800, 600));
            if (physics)
                Ui.Servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
            Ui.Tree.Root.SetSize(new Vector2(800, 600));
            Camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0, 10), Fov = 60f, Current = true };
            Ui.Tree.Root.AddChild(Camera);
            Camera.LookAt(Vector3.Zero);
            Host = new UiMarkerHost { Name = "Markers" };
            Layer = Ui.AddLayer(0, Host);
            Layer.ScaleMode = UiScaleMode.Pixels;
        }

        public UiMarker3D Add(string name, Vector3 position, Action<UiMarker3D>? configure = null)
        {
            var marker = new UiMarker3D { Name = name, Position = position, WorldOffset = Vector3.Zero, Text = name };
            configure?.Invoke(marker);
            Ui.Tree.Root.AddChild(marker);
            return marker;
        }

        public MarkerRow Row(string text) => Host.Rows.Single(r => r.Text == text);

        public void Dispose() => Ui.Dispose();
    }

    [Fact]
    public void AMarkerIsProjectedIntoTheLayerAsARow()
    {
        using var s = new Scene();
        var marker = s.Add("Nell", Vector3.Zero, m => m.ScreenOffset = new Vector2(0, -10));
        s.Ui.Tick(2);

        var row = s.Row("Nell");
        Assert.True(row.Visible);
        Assert.Equal(400, row.X, 1);
        Assert.Equal(290, row.Y, 1);  // 10 dp up at a dp ratio of 1
        Assert.Equal("nameplate", row.Kind);
        Assert.Equal(10, row.Distance, 3);
        Assert.True(marker.IsOnScreen);
        Assert.Same(marker, Assert.Single(s.Host.Markers));
        Assert.NotNull(s.Host.QuerySelector(".nameplate")); // the default document drew the row
    }

    [Fact]
    public void DistanceHidesFadesAndScales()
    {
        using var s = new Scene();
        s.Add("Far", new Vector3(0, 0, -45), m => (m.MaxDistance, m.FadeDistance) = (50, 10));   // 55 m: hidden
        s.Add("Fading", new Vector3(0, 0, -35), m => (m.MaxDistance, m.FadeDistance) = (50, 10)); // 45 m: half faded
        s.Add("Scaled", new Vector3(0, 0, -10), m => (m.ScaleWithDistance, m.ReferenceDistance, m.MinScale) = (true, 10, 0.3f)); // 20 m
        s.Add("Tiny", new Vector3(0, 0, -40), m => (m.ScaleWithDistance, m.MinScale) = (true, 0.3f)); // 50 m: clamped
        s.Ui.Tick();

        Assert.False(s.Row("Far").Visible);
        Assert.Equal(0.5f, s.Row("Fading").Opacity, 3);
        Assert.Equal(0.5f, s.Row("Scaled").Scale, 3);
        Assert.Equal(0.3f, s.Row("Tiny").Scale, 3);
    }

    [Fact]
    public void OffscreenMarkersHideOrClampToTheEdgeWithAnAngle()
    {
        using var s = new Scene();
        s.Add("Hidden", new Vector3(30, 0, 0));
        s.Add("Right", new Vector3(30, 0, 0), m => (m.Offscreen, m.EdgeMargin) = (MarkerOffscreen.ClampToEdge, 20));
        s.Add("Behind", new Vector3(0, -1, 30), m => (m.Offscreen, m.EdgeMargin) = (MarkerOffscreen.ClampToEdge, 20));
        s.Ui.Tick();

        Assert.False(s.Row("Hidden").Visible);
        var right = s.Row("Right");
        Assert.True(right.Visible && right.Edge);
        Assert.Equal(780, right.X, 1);
        Assert.Equal(300, right.Y, 1);
        Assert.Equal(0, right.Angle, 1);

        // Behind the camera and below it: clamped to the bottom edge, the arrow pointing down.
        var behind = s.Row("Behind");
        Assert.True(behind.Visible && behind.Edge);
        Assert.Equal(580, behind.Y, 1);
        Assert.Equal(90, behind.Angle, 1);
    }

    [Fact]
    public void RowsAreSortedFarthestFirstAndSignalsFollowVisibility()
    {
        using var s = new Scene();
        var near = s.Add("Near", new Vector3(0, 0, 5));
        s.Add("Mid", new Vector3(0, 0, 0));
        s.Add("Far", new Vector3(0, 0, -5));
        var (entered, exited) = (0, 0);
        near.ScreenEntered += () => entered++;
        near.ScreenExited += () => exited++;
        s.Ui.Tick();
        Assert.Equal(["Far", "Mid", "Near"], s.Host.Rows.Select(r => r.Text));
        Assert.Equal((1, 0), (entered, exited));

        near.Position = new Vector3(0, 0, 20); // behind the camera, hidden
        s.Ui.Tick();
        Assert.Equal((1, 1), (entered, exited));
        Assert.False(s.Row("Near").Visible);

        near.Visible = false; // invisible nodes take no row
        s.Ui.Tick();
        Assert.DoesNotContain(s.Host.Rows, r => r.Text == "Near");
    }

    [Fact]
    public void PhysicsOcclusionFadesAMarkerBehindABodyButNotItsOwn()
    {
        using var s = new Scene(physics: true);
        var wall = new StaticBody3D { Name = "Wall", Position = new Vector3(-2, 0, 5) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2, 2, 0.5f) } });
        s.Ui.Tree.Root.AddChild(wall);
        var hidden = s.Add("Hidden", new Vector3(-2, 0, 0), m => m.Occlusion = MarkerOcclusion.Physics);

        // A marker inside its own body is not hidden by it.
        var npc = new StaticBody3D { Name = "Npc", Position = new Vector3(2, 0, 0) };
        npc.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1, 1, 1) } });
        s.Ui.Tree.Root.AddChild(npc);
        var own = new UiMarker3D { Name = "Own", WorldOffset = Vector3.Zero, Text = "Own", Occlusion = MarkerOcclusion.Physics };
        npc.AddChild(own);

        s.Host.OcclusionFadeSeconds = 0.1f;
        s.Ui.Tick();
        Assert.True(hidden.IsOccluded);
        Assert.False(own.IsOccluded);
        Assert.True(s.Row("Hidden").Opacity < 1f);
        s.Ui.Tick(10);
        Assert.False(s.Row("Hidden").Visible); // faded out
        Assert.Equal(1f, s.Row("Own").Opacity, 3);

        wall.Position = new Vector3(-2, 50, 5); // moved away: fades back in
        s.Ui.Tick(10);
        Assert.False(hidden.IsOccluded);
        Assert.Equal(1f, s.Row("Hidden").Opacity, 3);
    }

    [Fact]
    public void ElementModeMovesTheTargetInsteadOfTakingARow()
    {
        using var s = new Scene();
        var hud = new UiDocument { Name = "Hud", Rml = UiTestTree.Page("<div id='boss' style='position: absolute;'>Boss</div>") };
        s.Layer.AddChild(hud);
        s.Add("Boss", Vector3.Zero, m => (m.TargetDocument, m.TargetElementId) = (new NodePath("../Layer0/Hud"), "boss"));
        s.Ui.Tick(3);

        Assert.Empty(s.Host.Rows);
        var boss = s.Host.Markers.Single();
        Assert.True(boss.IsShown);
        Assert.Equal(new Vector2(400, 300), boss.ScreenPosition); // the element's transform (RmlUi has no property getter in the shim)
    }

    [Fact]
    public void SteadyFramesAllocateNothing()
    {
        using var s = new Scene();
        for (var i = 0; i < 100; i++)
            s.Add($"M{i}", new Vector3(i % 10 - 5, i / 10 - 5, -i * 0.3f), m =>
            {
                m.Kind = (i % 4) switch { 0 => "nameplate", 1 => "health", 2 => "prompt", _ => "waypoint" };
                m.Offscreen = MarkerOffscreen.ClampToEdge;
                m.ScaleWithDistance = true;
            });
        s.Ui.Tick(5);
        var camera = s.Camera;
        var bytes = AllocationGate.SmallestWindow(() =>
        {
            camera.Position += new Vector3(0.01f, 0, 0); // markers move on screen every frame
            s.Host.Update(s.Ui.Tree, camera, new Vector2(800, 600), 1f, 1f / 60f);
        });
        Assert.Equal(0, bytes);
    }
}

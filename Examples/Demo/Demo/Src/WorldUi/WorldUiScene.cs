using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// World UI (G11): a street at dusk. Shop signs are <see cref="Label3D"/>s (a fixed three-line menu, a billboarded
/// market sign, an autowrapped notice, a <c>FixedSize</c> + <c>NoDepthTest</c> debug tag); six walkers carry nameplate
/// markers (<see cref="UiMarker3D"/>) whose health bars drain and refill; a crate has a "press E" prompt and the gate a
/// waypoint that clamps to the screen edge. Buildings have colliders, so markers behind them fade out (physics
/// occlusion). A terminal (<see cref="UiPanel3D"/>) near the camera opens the gate across the far street; Tab switches it
/// between the mouse and a crosshair. The panel toggles occlusion, distance scale, the debug tags and a slow camera orbit.
/// </summary>
public static class WorldUiScene
{
    private static readonly Color Street = Color.FromArgb(70, 74, 84);
    private static readonly Color Brick = Color.FromArgb(150, 92, 72);
    private static readonly Color Plaster = Color.FromArgb(196, 186, 160);

    public static Node Build()
    {
        var root = new Node3D { Name = "world_ui" };
        Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(-4.5f, 4.5f, 15), RotationDegrees = new Vector3(-12, -8, 0) });
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.1f, CastsShadows = true, RotationDegrees = new Vector3(-40, -35, 0) });
        Box(root, "Street", new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40), Street, collider: false);

        Box(root, "NoodleBar", new Vector3(-7, 2.5f, -4), new Vector3(5, 5, 4), Brick);
        Box(root, "Market", new Vector3(7, 2f, -5), new Vector3(5, 4, 4), Plaster);
        Box(root, "Kiosk", new Vector3(2.5f, 1.25f, 3), new Vector3(2, 2.5f, 1.2f), Brick);

        Add(root, root, new Label3D
        {
            Name = "MenuSign",
            Position = new Vector3(-7, 3.6f, -1.95f),
            Text = "NOODLES\nRamen  9\nUdon  7",
            FontSize = 64,
            OutlineSize = 0,
            LineSpacing = 4,
            Modulate = Color.FromArgb(255, 255, 214, 120),
        });
        Add(root, root, new Label3D
        {
            Name = "MarketSign",
            Position = new Vector3(7, 4.8f, -5),
            Text = "Market",
            FontSize = 96,
            OutlineSize = 16,
            Billboard = BillboardMode.FixedY,
            Modulate = Color.FromArgb(255, 40, 60, 90),
            OutlineModulate = Color.FromArgb(230, 255, 250, 235),
        });
        Add(root, root, new Label3D
        {
            Name = "Notice",
            Position = new Vector3(2.5f, 1.6f, 3.62f),
            Text = "Please keep the gate closed after dark",
            FontSize = 32,
            Width = 340,
            Autowrap = AutowrapMode.Word,
            OutlineSize = 0,
            Modulate = Color.FromArgb(255, 245, 240, 225),
        });

        var walkers = Add(root, root, new Node3D { Name = "Walkers" });
        string[] names = ["Nell", "Whitlock", "Ada", "Brann", "Corin", "Maeve"];
        for (var i = 0; i < names.Length; i++)
        {
            var walker = Add(root, walkers, new WorldUiWalker
            {
                Name = names[i],
                Position = new Vector3(-9 + i * 3.6f, 0.9f, -1 + (i % 3) * 2.5f),
                Range = 2.5f + i % 3,
                Speed = 0.8f + 0.25f * i,
            });
            Add(root, walker, new MeshInstance3D { Name = "Mesh", Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.8f }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = Color.FromArgb(120 + i * 20, 160, 220 - i * 20) } });
            Add(root, walker, new UiMarker3D
            {
                Name = "Nameplate",
                Text = names[i],
                Subtext = i == 0 ? "Trader" : "",
                WorldOffset = new Vector3(0, 1.3f, 0),
                ScaleWithDistance = true,
                ReferenceDistance = 18,
                Occlusion = MarkerOcclusion.Physics,
            });
            Add(root, walker, new Label3D
            {
                Name = "DebugTag",
                Position = new Vector3(0, -1.1f, 0),
                Text = $"#{i + 1} walk",
                FontSize = 18,
                OutlineSize = 6,
                FixedSize = true,
                NoDepthTest = true,
                Billboard = BillboardMode.Enabled,
                Modulate = Color.FromArgb(255, 130, 255, 170),
                Visible = false,
            });
        }

        var crate = Box(root, "Crate", new Vector3(-2.5f, 0.45f, 6), new Vector3(0.9f, 0.9f, 0.9f), Color.FromArgb(170, 128, 70));
        Add(root, crate, new UiMarker3D { Name = "Prompt", Kind = "prompt", Subtext = "E", Text = "Open", WorldOffset = new Vector3(0, 0.8f, 0), MaxDistance = 25 });
        Add(root, root, new UiMarker3D
        {
            Name = "Gate",
            Kind = "waypoint",
            Text = "Gate",
            Position = new Vector3(14, 1, -14),
            WorldOffset = Vector3.Zero,
            MaxDistance = 0,
            Offscreen = MarkerOffscreen.ClampToEdge,
        });

        // G11.6: a gate across the far street and the terminal near the camera that opens it.
        Box(root, "Gate", new Vector3(14, 1.5f, -14), new Vector3(4, 3, 0.4f), Color.FromArgb(90, 96, 110));
        var terminal = Box(root, "Terminal", new Vector3(-3, 0.6f, 9), new Vector3(1.5f, 1.2f, 0.5f), Color.FromArgb(52, 58, 66));
        terminal.RotationDegrees = new Vector3(0, -14, 0);
        var screen = Add(root, terminal, new UiPanel3D
        {
            Name = "Screen",
            Position = new Vector3(0, 1.1f, 0.05f),
            RotationDegrees = new Vector3(-12, 0, 0),
            Resolution = new Vector2I(640, 400),
            PixelSize = 0.0022f,
            Brightness = 1.4f,
            MaxInteractDistance = 14,
            ClickAction = "use",
            Background = Color.FromArgb(12, 22, 18),
        });
        Add(root, screen, new WorldUiTerminal { Name = "Ui" });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new UiMarkerHost { Name = "Markers" });
        Add(root, ui, new WorldUiPanel { Name = "Panel" });
        return root;
    }

    private static Node3D Box(Node root, string name, Vector3 position, Vector3 size, Color color, bool collider = true)
    {
        Node3D body = collider ? new StaticBody3D { Name = name, Position = position } : new Node3D { Name = name, Position = position };
        Add(root, root, body);
        if (collider)
            Add(root, body, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        Add(root, body, new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.BlinnPhong, AlbedoColor = color } });
        return body;
    }
}

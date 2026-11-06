using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// <see cref="Label3D"/>'s G11 options (ADR 0249) on a street of posts: a three-line sign (newlines, right-aligned,
/// line spacing) on a fixed quad, an autowrapped notice, a Cyrillic sign drawn through Lato's Roboto Mono fallback, an
/// alpha-cut label, and two <c>FixedSize</c> + <c>NoDepthTest</c> debug tags at 4 m and 12 m that come out the same size
/// on screen, the far one through a wall.
/// </summary>
public sealed class Label3DLayoutScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 2.2f, 7f);
        Camera.LookAt(new Vector3(0, 1.6f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(30, 30) }, MaterialOverride = Plain(215, 210, 200) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f)));

        var lato = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve(Label3D.DefaultFontPath)));
        lato.Fallbacks = [Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/RobotoMono-Regular.ttf")))];

        scene.AddChild(new Label3D
        {
            Name = "Menu",
            Position = new Vector3(-2.4f, 2.2f, 0),
            Text = "NOODLES\nRamen  9\nUdon  7",
            Font = lato,
            FontSize = 48,
            OutlineSize = 0,
            LineSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Modulate = Color.FromArgb(255, 250, 220, 120),
        });
        scene.AddChild(new Label3D
        {
            Name = "Notice",
            Position = new Vector3(0.2f, 2.3f, 0),
            Text = "Please keep the gate closed after dark",
            Font = lato,
            FontSize = 36,
            Width = 300,
            Autowrap = AutowrapMode.Word,
            OutlineSize = 8,
            Modulate = Color.FromArgb(255, 30, 30, 40),
            OutlineModulate = Color.FromArgb(255, 240, 240, 240),
        });
        scene.AddChild(new Label3D
        {
            Name = "Cyrillic",
            Position = new Vector3(2.4f, 2.6f, 0),
            Text = "Жёлтый",
            Font = lato,
            FontSize = 48,
            OutlineSize = 0,
            Modulate = Color.FromArgb(255, 200, 40, 40),
        });
        scene.AddChild(new Label3D
        {
            Name = "Cutout",
            Position = new Vector3(2.4f, 1.6f, 0),
            Text = "CUT",
            Font = lato,
            FontSize = 64,
            OutlineSize = 0,
            AlphaCut = SpriteAlphaCut.Discard,
            Modulate = Color.FromArgb(255, 30, 120, 60),
        });

        // The same tag near and far: equal on screen; the far one shows through the wall.
        foreach (var (name, position) in new[] { ("NearTag", new Vector3(-1.2f, 0.9f, 3f)), ("FarTag", new Vector3(1.6f, 0.9f, -5f)) })
            scene.AddChild(new Label3D
            {
                Name = name,
                Position = position,
                Text = "#42 idle",
                Font = lato,
                FontSize = 24,
                OutlineSize = 6,
                FixedSize = true,
                NoDepthTest = true,
                Billboard = BillboardMode.Enabled,
                Modulate = Color.FromArgb(255, 120, 255, 160),
            });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(1.6f, 0.6f, -4f),
            Mesh = new BoxMesh { Size = new Vector3(3f, 1.2f, 0.2f) },
            MaterialOverride = Plain(60, 90, 170),
        });
    }
}

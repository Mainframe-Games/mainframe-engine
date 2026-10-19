using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// <see cref="Label3D"/> (ADR 0200) as Buy My Snake Oil's NPC name tags: the default font (Lato) at 64 px with a 14 px translucent cream
/// outline, dark brown text, Y-billboarded over two posts. A blue wall in front hides the lower half of both tags; the left
/// one is <c>NoDepthTest</c> and shows through it, the right one is hidden by it. <c>--count 1</c> turns the left tag's
/// <c>NoDepthTest</c> off too, so only the left half of the frame changes between the two runs.
/// </summary>
public sealed class Label3DScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 2.4f, 6f);
        Camera.LookAt(new Vector3(0, 1.6f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(14, 14) }, MaterialOverride = Plain(215, 210, 200) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f)));

        foreach (var (name, x, noDepthTest) in new[] { ("Nell", -1.6f, Host.Count != 1), ("Whitlock", 1.6f, false) })
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = name + "Post",
                Position = new Vector3(x, 0.9f, 0),
                Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.15f, Height = 1.8f },
                MaterialOverride = Plain(150, 110, 80),
            });
            scene.AddChild(new Label3D
            {
                Name = name + "Tag",
                Position = new Vector3(x, 2.1f, 0),
                Text = name, // no Font: the engine's Lato, as Snake Oil's tags leave it unset
                FontSize = 64,
                OutlineSize = 14,
                Modulate = Color.FromArgb(255, 43, 33, 24),
                OutlineModulate = Color.FromArgb(200, 254, 244, 216),
                Billboard = BillboardMode.FixedY,
                NoDepthTest = noDepthTest,
            });
        }

        // A band in front of both tags, its top edge level with their middles as the camera sees them.
        scene.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(0, 1.88f, 1.2f),
            Mesh = new BoxMesh { Size = new Vector3(6f, 0.56f, 0.2f) },
            MaterialOverride = Plain(60, 90, 170),
        });
    }
}

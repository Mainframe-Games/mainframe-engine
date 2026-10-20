using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Skinned <see cref="Polygon2D"/> (ADR 0202), drawn through the canvas: on top, a textured 180 px strip (a grid of quads
/// over the checker, UVs in texture pixels) at rest; below, the same strip skinned to three chained <see cref="Bone2D"/>s
/// of a <see cref="Skeleton2D"/>, bent 25° at each joint, its weights blending across the joints; on the right, a
/// vertex-coloured square fanned around an internal centre vertex. Godot's canvas_items stretch from 480×270.
/// </summary>
public sealed class Polygon2DSkinScene(HostOptions host) : RenderTestGame(host)
{
    private const int Columns = 13, Rows = 3;
    private const float Width = 180, Height = 36;

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = CanvasServer.GodotDefaultClearColor;
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);
        var texture = CanvasScene.Checker();
        var root = new Node2D { Name = "Canvas" };

        root.AddChild(Strip("Rest", new Vector2(40, 40), texture));

        var skeleton = new Skeleton2D { Name = "Skeleton", Position = new Vector2(40, 150) };
        root.AddChild(skeleton);
        Node2D parent = skeleton;
        for (var b = 0; b < 3; b++)
        {
            var at = new Vector2(b == 0 ? 0 : 60, 0);
            var bone = new Bone2D { Name = $"B{b}", Position = at, Rest = Transform2D.FromTrs(at, 0, Vector2.One) };
            parent.AddChild(bone);
            bone.RotationDegrees = b == 0 ? 0 : -25; // bent at each joint, the free end curling up
            parent = bone;
        }

        var bent = Strip("Bent", new Vector2(40, 150 - Height / 2), texture);
        bent.Skeleton = "../Skeleton";
        string[] paths = ["B0", "B0/B1", "B0/B1/B2"];
        for (var b = 0; b < 3; b++)
        {
            var weights = new float[Columns * Rows];
            for (var i = 0; i < weights.Length; i++)
            {
                var x = i % Columns * (Width / (Columns - 1));
                weights[i] = MathF.Max(0f, 1f - MathF.Abs(x - (30 + 60 * b)) / 60f); // overlapping ramps, normalised when drawn
            }

            bent.AddBone(paths[b], weights);
        }

        root.AddChild(bent);

        root.AddChild(new Polygon2D
        {
            Name = "Fan",
            Position = new Vector2(330, 110),
            Polygon = [new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(50, 50)],
            InternalVertexCount = 1,
            Polygons = [[0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]],
            VertexColors = [Colors.Red, Colors.Yellow, Colors.Green, Colors.Blue, Colors.White],
        });

        Tree.ChangeScene(root);
    }

    // A grid of Columns × Rows points along x, its cells as quads; UVs map the whole strip over the 8 px checker once.
    private static Polygon2D Strip(string name, Vector2 position, Texture2D texture)
    {
        var points = new Vector2[Columns * Rows];
        var uvs = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var (c, r) = (i % Columns, i / Columns);
            points[i] = new Vector2(c * (Width / (Columns - 1)), r * (Height / (Rows - 1)));
            uvs[i] = new Vector2(c * (8f / (Columns - 1)), r * (8f / (Rows - 1)));
        }

        var cells = new List<int[]>();
        for (var r = 0; r < Rows - 1; r++)
            for (var c = 0; c < Columns - 1; c++)
            {
                var i = r * Columns + c;
                cells.Add([i, i + 1, i + Columns + 1, i + Columns]);
            }

        return new Polygon2D { Name = name, Position = position, Polygon = points, UV = uvs, Polygons = [.. cells], Texture = texture };
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

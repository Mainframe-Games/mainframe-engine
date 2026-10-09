using DrawingColor = System.Drawing.Color;
using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Dust motes in the sunlit air (ADR 0178; the references' floating specks in the shafts): G8e.6's
/// <see cref="SprayCards3D"/> (camera-facing cards, lit by the sky and the shadowed sun, brighter looking into it) shrunk
/// to 2 cm specks that drift up slowly and fade in and out, in clouds at the places the walk and the shots look into the
/// light (<see cref="ValleyLayout.DustClouds"/>). A speck's card is a plain disc (a white noise texture). Nothing runs per
/// frame (the cards animate in the vertex shader). There is no particle system yet (G6.3): this is the cheap stand-in, a
/// few hundred quads.
/// </summary>
public static class ForestDust
{
    /// <summary>Specks per cloud (64 per <see cref="SprayCards3D"/>).</summary>
    public const int SpecksPerCloud = 64;

    /// <summary>A speck's size in metres.</summary>
    public const float SpeckSize = 0.018f;

    /// <summary>The clouds as unowned <see cref="SprayCards3D"/> children of a new node, standing on <paramref name="height"/>(x, z).</summary>
    public static Node3D Create(Func<float, float, float> height)
    {
        var root = new Node3D { Name = "Dust" };
        var white = new byte[4 * 4 * 4];
        Array.Fill(white, (byte)255);
        var material = new SprayMaterial3D
        {
            ResourceName = "Dust mote",
            NoiseTexture = Texture2D.FromPixels(4, 4, white, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear }),
            Color = DrawingColor.FromArgb(255, 255, 248, 230),
            Opacity = 0.55f,
            Cycle = 11f,
            Rise = 0.6f,
            Growth = 1f,
            SoftDistance = 0.05f,
        };
        var seed = 1;
        foreach (var (centre, above, extents) in ValleyLayout.DustClouds)
        {
            var position = new Vector3(centre.X, height(centre.X, centre.Y) + above, centre.Y);
            for (var left = SpecksPerCloud; left > 0; left -= 64)
                root.AddChild(new SprayCards3D
                {
                    Name = $"Dust{seed}",
                    Count = Math.Min(64, left),
                    Extents = extents,
                    CardSize = SpeckSize,
                    Drift = 0.8f,
                    Seed = seed++,
                    Material = material,
                    Position = position,
                    CastShadows = false,
                });
        }

        return root;
    }
}

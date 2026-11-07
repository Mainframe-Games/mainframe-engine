using System.Drawing;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Godot 4.7's tonemap and glow (ADR 0124): a small emissive square (HDR, well above the glow threshold) over a dark
/// floor, seen head-on, with Godot's ACES. <c>--count</c>: 0 glow on (Godot's defaults, normalized, strength 0.75: the
/// Driving Range environment); 1 glow off; 2 glow on but the square at an emission the threshold never passes; 3 as 0 with
/// <see cref="GlowQuality.High"/> (ADR 0168); 4 as 2 with <see cref="GlowQuality.High"/>; 5 as 0 with a glow map (G8e.4,
/// ADR 0277) black on the left half of the screen and white on the right, at strength 1: the glow stays on the right only.
/// </summary>
public sealed class GlowScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(GlowScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0, 3) };
        scene.AddChild(camera);

        var bright = Host.Count is not (2 or 4);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Position = new Vector3(0, 0, -1),
            RotationDegrees = new Vector3(90, 0, 0),
            Mesh = new PlaneMesh { Size = new Vector2(20, 20) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 40, 40, 48), ShadingMode = ShadingMode.Unshaded },
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Emitter",
            RotationDegrees = new Vector3(90, 0, 0),
            Mesh = new PlaneMesh { Size = new Vector2(0.4f, 0.4f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.BlinnPhong,
                AlbedoColor = Color.Black,
                EmissionColor = Color.FromArgb(255, 255, 230, 160),
                EmissionEnergy = bright ? 6f : 0.5f,
            },
        });
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            AmbientColor = Vector3.Zero,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.GodotAces,
                GlowEnabled = Host.Count != 1,
                GlowNormalized = true,
                GlowStrength = 0.75f,
                GlowQuality = Host.Count is 3 or 4 ? GlowQuality.High : GlowQuality.Standard,
                GlowMap = Host.Count == 5 ? HalfMap() : null,
                GlowMapStrength = 1f,
            },
        });
        Tree.ChangeScene(scene);
    }

    // 16 × 4: black columns 0–7, white 8–15, clamped and point-sampled so the edge is the screen's middle.
    private static Texture2D HalfMap()
    {
        var rgba = new byte[16 * 4 * 4];
        for (var y = 0; y < 4; y++)
            for (var x = 8; x < 16; x++)
                rgba.AsSpan((y * 16 + x) * 4, 4).Fill(255);
        return Texture2D.FromPixels(16, 4, rgba, new TextureImportSettings { Filter = TextureFilter.Nearest, Wrap = TextureWrap.Clamp, Mipmaps = false });
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

using System.Drawing;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Godot 4.7's tonemap and glow (ADR 0124): a small emissive square (HDR, well above the glow threshold) over a dark
/// floor, seen head-on, with Godot's ACES. <c>--count</c>: 0 glow on (Godot's defaults, normalized, strength 0.75: the
/// Driving Range environment); 1 glow off; 2 glow on but the square at an emission the threshold never passes; 3 as 0 with
/// <see cref="GlowQuality.High"/> (ADR 0168); 4 as 2 with <see cref="GlowQuality.High"/>.
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
                GlowQuality = Host.Count >= 3 ? GlowQuality.High : GlowQuality.Standard,
            },
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

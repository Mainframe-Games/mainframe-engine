using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// G6.5: forward decals on a PBR floor and a Blinn-Phong box under a sun and the procedural sky. A painted badge (albedo
/// with alpha + a normal map) and a polished metal plate (ORM only) lie on the floor; a stripe decal projects down across
/// the box with a normal fade, so the box's top takes it and its sides do not; a glowing sign (emission) sits on a
/// standing slab; a decal past its distance fade is not drawn. Textures are generated in code. <c>--count 1</c> slides the
/// badge every frame (masks change, the atlas does not rebuild). Checks the selection count and that the atlas rebuilt once.
/// </summary>
public sealed class DecalScene(HostOptions host) : RenderTestGame(host)
{
    private Decal _badge = null!;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(DecalScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 4.2f, 6.5f) };
        camera.LookAt(new Vector3(0, 0.3f, 0));
        scene.AddChild(camera);

        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural }, AmbientSource = AmbientSource.Sky });
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 8, 0), Energy = 1.1f, Color = new Vector3(1f, 0.96f, 0.9f) };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.5f, -0.8f, -0.4f)));
        scene.AddChild(sun);

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(12, 12) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = ShadingMode.Pbr,
                AlbedoColor = Color.FromArgb(255, 150, 145, 135),
                Roughness = 0.85f,
            },
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(1.8f, 0.6f, -0.4f),
            Mesh = new BoxMesh { Size = new Vector3(1.6f, 1.2f, 1.6f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 140, 200) },
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Slab",
            Position = new Vector3(-2.2f, 1f, -1.6f),
            RotationDegrees = new Vector3(0, 25, 0),
            Mesh = new BoxMesh { Size = new Vector3(1.8f, 2f, 0.3f) },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = Color.FromArgb(255, 60, 60, 66), Roughness = 0.6f },
        });

        _badge = new Decal
        {
            Name = "Badge",
            Position = new Vector3(-0.6f, 0f, 1.1f),
            Size = new Vector3(1.8f, 0.6f, 1.8f),
            TextureAlbedo = Badge(128),
            TextureNormal = Rings(128),
        };
        scene.AddChild(_badge);
        scene.AddChild(new Decal
        {
            Name = "Plate",
            Position = new Vector3(1.5f, 0f, 1.7f),
            RotationDegrees = new Vector3(0, 30, 0),
            Size = new Vector3(1.4f, 0.4f, 0.9f),
            TextureOrm = Plate(64),
        });
        scene.AddChild(new Decal
        {
            Name = "Stripes",
            Position = new Vector3(1.8f, 1.2f, -0.4f),
            Size = new Vector3(2.4f, 1.4f, 0.8f),
            TextureAlbedo = Stripes(64),
            Modulate = Color.FromArgb(255, 255, 220, 60),
            NormalFade = 0.6f,
        });
        // The sign faces +Z out of the slab: its projection axis (−Y) points into the slab.
        var sign = new Decal
        {
            Name = "Sign",
            Position = new Vector3(-2.2f, 1.2f, -1.6f),
            Size = new Vector3(1.2f, 0.6f, 0.5f),
            TextureEmission = Glow(64),
            EmissionEnergy = 4f,
            UpperFade = 0f,
            LowerFade = 0f,
        };
        sign.RotationDegrees = new Vector3(90, 25, 0);
        scene.AddChild(sign);
        scene.AddChild(new Decal
        {
            Name = "FadedOut",
            Position = new Vector3(0, 0, 3f),
            TextureAlbedo = Stripes(64),
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 1f,
            DistanceFadeLength = 1f,
        });

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count == 1)
            _badge.Position = new Vector3(-0.6f + MathF.Sin(gameTime.FrameCount * 0.05f) * 0.8f, 0f, 1.1f);
        if (gameTime.FrameCount != 4)
            return;
        var meshes = Servers.Render!.Meshes!;
        if (meshes.DecalAtlas is not { Rebuilds: 1 } atlas)
            Fail($"the decal atlas rebuilt {meshes.DecalAtlas?.Rebuilds ?? 0} times, not once");
        var stats = Servers.Render!.MeshStats;
        if (stats.Decals != 4)
            Fail($"{stats.Decals} decals selected, not 4 (the faded one is out)");
        if (stats.DecalInstances < 3)
            Fail($"{stats.DecalInstances} instances took decals (floor, box and slab expected)");
    }

    protected override void DisposeScene()
    {
    }

    // A badge: a light disc with a dark ring and a star-like cut, transparent outside.
    private static Texture2D Badge(int size) => Generate(size, (u, v) =>
    {
        var r = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f));
        if (r > 0.48f)
            return default;
        var angle = MathF.Atan2(v - 0.5f, u - 0.5f);
        var star = r < 0.22f + 0.08f * MathF.Cos(5f * angle);
        var ring = r > 0.38f;
        return ring ? new Vector4(0.12f, 0.12f, 0.15f, 1f) : star ? new Vector4(0.9f, 0.2f, 0.15f, 1f) : new Vector4(0.95f, 0.92f, 0.82f, 1f);
    });

    // Concentric ridges as a tangent-space normal map (OpenGL convention).
    private static Texture2D Rings(int size) => Generate(size, (u, v) =>
    {
        var d = new Vector2(u - 0.5f, v - 0.5f);
        var r = d.Length();
        var slope = MathF.Cos(r * 60f) * 0.6f;
        var dir = r > 1e-4f ? d / r : Vector2.Zero;
        // +Y up the image: v grows downwards, so the y slope flips.
        var n = Vector3.Normalize(new Vector3(dir.X * slope, -dir.Y * slope, 1f));
        return new Vector4(n * 0.5f + new Vector3(0.5f), 1f);
    });

    // ORM: full occlusion, smooth (roughness 0.15), metal (1), with a rough border.
    private static Texture2D Plate(int size) => Generate(size, (u, v) =>
    {
        var border = u < 0.08f || u > 0.92f || v < 0.12f || v > 0.88f;
        return new Vector4(1f, border ? 0.7f : 0.15f, border ? 0f : 1f, 1f);
    });

    // Diagonal hazard stripes, half transparent.
    private static Texture2D Stripes(int size) => Generate(size, (u, v) =>
        ((int)MathF.Floor((u + v) * 6f) & 1) == 0 ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.05f, 0.05f, 0.05f, 0.8f));

    // A glowing rounded bar.
    private static Texture2D Glow(int size) => Generate(size, (u, v) =>
    {
        var inside = u > 0.1f && u < 0.9f && v > 0.25f && v < 0.75f;
        return inside ? new Vector4(0.2f, 0.9f, 1f, 1f) : default;
    });

    private static Texture2D Generate(int size, Func<float, float, Vector4> texel)
    {
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var c = texel((x + 0.5f) / size, (y + 0.5f) / size);
                var o = (y * size + x) * 4;
                rgba[o] = (byte)Math.Clamp(MathF.Round(c.X * 255f), 0f, 255f);
                rgba[o + 1] = (byte)Math.Clamp(MathF.Round(c.Y * 255f), 0f, 255f);
                rgba[o + 2] = (byte)Math.Clamp(MathF.Round(c.Z * 255f), 0f, 255f);
                rgba[o + 3] = (byte)Math.Clamp(MathF.Round(c.W * 255f), 0f, 255f);
            }
        }

        return Texture2D.FromPixels(size, size, rgba);
    }
}

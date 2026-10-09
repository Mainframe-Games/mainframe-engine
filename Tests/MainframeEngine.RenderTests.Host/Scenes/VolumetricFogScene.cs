using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Volumetric fog (ADR 0171): uniform fog under a wide roof with a square hole, lit by a high sun, seen from under the roof
/// against an unshaded black wall beyond the fog's length. The sun reaches the air only through the hole, so a slanted
/// beam stands in shadowed fog (the sun is off screen, above and behind the roof); a little sky ambient lights the shadowed
/// fog. <c>--count</c>: 0 the fog with temporal reprojection; 1 no volumetric fog; 2 the fog with the camera swinging
/// left and right (the allocation run); 3 the fog without temporal reprojection.
/// </summary>
public sealed class VolumetricFogScene(HostOptions host) : ShadowSceneBase(host)
{
    /// <summary>The image row (fraction of the height from the top) the tests measure: rays a few degrees above the
    /// horizon that stay under the roof over the whole fog length.</summary>
    public const float MeasureRow = 0.44f;

    protected override bool HasSky => true;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0f, 3f, 15f);
        Camera.LookAt(new Vector3(0f, 3.4f, 0f));
        Camera.Fov = 60f;

        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(200, 200) }, MaterialOverride = Plain(40, 40, 40, 0f) });
        // A roof 8 m up with a 3 × 3 m hole above (0, −8): four slabs around it.
        var roof = Plain(60, 60, 60, 0f);
        AddSlab(scene, roof, "RoofWest", new Vector3(-31.5f, 8.25f, 0f), new Vector3(60f, 0.5f, 120f));
        AddSlab(scene, roof, "RoofEast", new Vector3(31.5f, 8.25f, 0f), new Vector3(60f, 0.5f, 120f));
        AddSlab(scene, roof, "RoofNorth", new Vector3(0f, 8.25f, -39.75f), new Vector3(3f, 0.5f, 63.5f));
        AddSlab(scene, roof, "RoofSouth", new Vector3(0f, 8.25f, 26.75f), new Vector3(3f, 0.5f, 66.5f));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Backdrop",
            Position = new Vector3(0f, 4f, -32f),
            Mesh = new BoxMesh { Size = new Vector3(120f, 8f, 1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = System.Drawing.Color.Black, ShadingMode = ShadingMode.Unshaded },
        });

        // Light travelling down and towards the camera (+Z) a little: the beam leans towards it.
        var sun = Sun(new Vector3(0.25f, -1f, 0.35f), energy: 16f);
        sun.ShadowMaxDistance = 80f;
        scene.AddChild(sun);

        Environment.AmbientSource = AmbientSource.Sky;
        Environment.VolumetricFogEnabled = Host.Count != 1;
        Environment.VolumetricFogDensity = 0.06f;
        Environment.VolumetricFogAnisotropy = 0.3f;
        Environment.VolumetricFogLength = 40f;
        Environment.VolumetricFogAmbientInject = 0.05f;
        Environment.VolumetricFogNoiseStrength = 0.3f;
        Environment.VolumetricFogTemporalReprojectionEnabled = Host.Count != 3;
    }

    private static void AddSlab(Node3D scene, StandardMaterial3D material, string name, Vector3 position, Vector3 size) =>
        scene.AddChild(new MeshInstance3D { Name = name, Position = position, Mesh = new BoxMesh { Size = size }, MaterialOverride = material });

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Host.Count != 2)
            return;
        // ±12° of yaw over 4 s: the history reprojects every frame.
        var yaw = 12f * MathF.Sin(gameTime.FrameCount * MathF.Tau / 240f);
        Camera.RotationDegrees = new Vector3(Camera.RotationDegrees.X, yaw, 0f);
    }
}

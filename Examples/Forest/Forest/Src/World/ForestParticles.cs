using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Shared pieces for the Forest's particles (the cabin's fire and chimney smoke): a procedural soft round sprite and the
/// additive, unshaded billboard material the flames and embers use.
/// </summary>
public static class ForestParticles
{
    private static Texture2D? _soft;

    /// <summary>A 64² white sprite whose alpha falls off smoothly from the centre (a Gaussian-like puff), made once.</summary>
    public static Texture2D SoftSprite()
    {
        if (_soft is not null)
            return _soft;
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f)) / (size * 0.5f);
                var a = MathF.Exp(-4.5f * d * d) * ValleyNoise.SmoothStep(1f, 0.85f, d);
                var o = (y * size + x) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
                rgba[o + 3] = (byte)(a * 255f + 0.5f);
            }

        return _soft = Texture2D.FromPixels(size, size, rgba, TextureImportSettings.Default);
    }

    /// <summary>Unshaded, additive particle billboards tinted by the particles' colour (HDR above 1 glows).</summary>
    public static StandardMaterial3D Additive(Texture2D? sprite) => new()
    {
        ShadingMode = ShadingMode.Unshaded,
        BlendMode = StandardMaterial3D.BlendModeEnum.Add,
        VertexColorUseAsAlbedo = true,
        BillboardMode = BillboardMode.Particles,
        AlbedoTexture = sprite,
    };

    /// <summary>
    /// Lit, alpha-blended wood smoke rising from a chimney (sorted back to front), drifting with the breeze along
    /// <paramref name="wind"/>.
    /// </summary>
    public static GpuParticles3D ChimneySmoke(Vector3 wind) => new()
    {
        Name = "Smoke",
        Amount = 28,
        Lifetime = 6f,
        Preprocess = 8f,
        DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
        VisibilityAabb = new Aabb(new Vector3(-8f, -1f, -8f), new Vector3(8f, 10f, 8f)),
        DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1f) },
        MaterialOverride = new StandardMaterial3D
        {
            ResourceName = "Chimney smoke",
            Transparency = AlphaMode.Blend,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
            AlbedoTexture = SoftSprite(),
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.12f, 0.05f, 0.12f),
            Direction = Vector3.UnitY,
            Spread = 12f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 0.7f,
            Gravity = Vector3.Normalize(new Vector3(wind.X, 0f, wind.Z)) * 0.2f + new Vector3(0f, 0.05f, 0f),
            DampingMin = 0.05f,
            DampingMax = 0.1f,
            AngleMax = 360f,
            AngularVelocityMin = -15f,
            AngularVelocityMax = 15f,
            ScaleMin = 0.45f,
            ScaleMax = 0.65f,
            ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [0.5f, 3f] },
            Color = new Vector4(0.78f, 0.77f, 0.76f, 1f),
            ColorRamp = new Gradient
            {
                Offsets = [0f, 0.12f, 1f],
                Colors = [new Vector4(1f, 1f, 1f, 0f), new Vector4(1f, 1f, 1f, 0.45f), new Vector4(1f, 1f, 1.05f, 0f)],
            },
        },
    };
}

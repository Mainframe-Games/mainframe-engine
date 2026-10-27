using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The apartment's water: running water from the shower head and the taps, and the shower's steam. Particles have no
/// collision, so each stream's lifetime is the time a drop takes to fall to the basin or floor below it.
/// </summary>
public static class NeonParticles
{
    private static Texture2D? _soft;

    /// <summary>A 64² white sprite whose alpha falls off smoothly from the centre, made once.</summary>
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
                var a = d >= 1f ? 0f : MathF.Exp(-4.5f * d * d) * (1f - d * d);
                var o = (y * size + x) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
                rgba[o + 3] = (byte)(a * 255f + 0.5f);
            }

        return _soft = Texture2D.FromPixels(size, size, rgba, TextureImportSettings.Default);
    }

    /// <summary>
    /// Seconds for a drop leaving at <paramref name="speed"/> m/s straight down to fall <paramref name="drop"/> metres.
    /// </summary>
    public static float FallTime(float drop, float speed)
    {
        const float g = 9.81f;
        return (-speed + MathF.Sqrt(speed * speed + 2f * g * drop)) / g;
    }

    /// <summary>
    /// Falling water: thin streaks from a box <paramref name="spread"/> wide (half extents in X and Z), dying
    /// <paramref name="drop"/> metres below the emitter.
    /// </summary>
    public static GpuParticles3D Water(string name, float drop, float spread, int amount, float speed)
    {
        var lifetime = FallTime(drop, speed);
        return new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = lifetime,
            Emitting = false,
            DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
            VisibilityAabb = new Aabb(new Vector3(-1f, -drop - 0.5f, -1f), new Vector3(1f, 0.5f, 1f)),
            DrawPass1 = new QuadMesh { Size = new Vector2(0.006f, 0.09f) },
            MaterialOverride = new StandardMaterial3D
            {
                ResourceName = name + " drops",
                ShadingMode = ShadingMode.Unshaded,
                Transparency = AlphaMode.Blend,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BillboardMode.Particles,
            },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(spread, 0.005f, spread),
                Direction = -Vector3.UnitY,
                Spread = 3f,
                InitialVelocityMin = speed * 0.9f,
                InitialVelocityMax = speed * 1.1f,
                Gravity = ParticleProcessMaterial.DefaultGravity3D,
                Color = new Vector4(0.8f, 0.88f, 1f, 0.45f),
                ParticleFlagAlignY = true,
            },
        };
    }

    /// <summary>Steam that rises slowly and spreads from a shower tray (lit, alpha-blended soft puffs).</summary>
    public static GpuParticles3D Steam(Vector3 trayHalfExtents) => new()
    {
        Name = "Steam",
        Amount = 40,
        Lifetime = 5f,
        Emitting = false,
        DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
        VisibilityAabb = new Aabb(new Vector3(-2f, -0.5f, -2f), new Vector3(2f, 3.5f, 2f)),
        DrawPass1 = new QuadMesh { Size = new Vector2(0.7f, 0.7f) },
        MaterialOverride = new StandardMaterial3D
        {
            ResourceName = "Steam",
            Transparency = AlphaMode.Blend,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
            AlbedoTexture = SoftSprite(),
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = trayHalfExtents,
            Direction = Vector3.UnitY,
            Spread = 30f,
            InitialVelocityMin = 0.15f,
            InitialVelocityMax = 0.3f,
            Gravity = new Vector3(0f, 0.04f, 0f),
            DampingMin = 0.05f,
            DampingMax = 0.1f,
            AngleMax = 360f,
            AngularVelocityMin = -10f,
            AngularVelocityMax = 10f,
            ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [0.6f, 2.4f] },
            Color = new Vector4(0.9f, 0.92f, 0.95f, 1f),
            ColorRamp = new Gradient
            {
                Offsets = [0f, 0.2f, 1f],
                Colors = [new Vector4(1f, 1f, 1f, 0f), new Vector4(1f, 1f, 1f, 0.16f), new Vector4(1f, 1f, 1f, 0f)],
            },
        },
    };
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The physical sky's atmosphere on the CPU (ADR 0154): transmittance and single scattering of the same Hillaire 2020
/// model the GPU look-up tables integrate (<c>include/atmosphere.slang</c>). The renderer uses it for the sun disc's
/// colour (the transmittance from the camera towards the sun); tests use it as the reference.
/// </summary>
/// <remarks>Positions are kilometres from the planet's centre, +Y up; radiance is per unit sun illuminance.</remarks>
internal static class AtmosphereModel
{
    /// <summary>
    /// Physical radiance per unit illuminance × π is the engine's radiance: the engine's lights light a white Lambert
    /// surface to radiance <c>Energy</c> (no 1/π), so the sky must be π times brighter than the physical value to keep
    /// the same ratio to lit surfaces as in reality.
    /// </summary>
    public const float RadianceScale = MathF.PI;

    /// <summary>
    /// Radiance of the sun disc per unit sun illuminance, in engine units. The physical value (π / solid angle,
    /// ≈ 46 000) would overflow the half-float scene target for a sun of energy 2, and the tonemap shows anything
    /// above ≈ 20 as white anyway; 2 000 still blooms and keeps the disc a finite, filterable value for sky captures.
    /// </summary>
    public const float SunDiscRadiance = 2000f;

    /// <summary>Extinction (scattering + absorption) per kilometre at <paramref name="altitudeKm"/> above the ground.</summary>
    public static Vector3 Extinction(in AtmosphereParameters a, float altitudeKm)
    {
        var rayleigh = MathF.Exp(-altitudeKm / a.RayleighScaleHeight);
        var mie = MathF.Exp(-altitudeKm / a.MieScaleHeight);
        var ozone = MathF.Max(0f, 1f - MathF.Abs(altitudeKm - 25f) / 15f);
        return a.RayleighScattering * rayleigh + a.MieExtinction * mie + a.OzoneAbsorption * ozone;
    }

    /// <summary>
    /// Transmittance from a point <paramref name="radius"/> km from the centre along a ray of zenith cosine
    /// <paramref name="cosZenith"/> to the top of the atmosphere; zero when the ray hits the ground.
    /// </summary>
    public static Vector3 Transmittance(in AtmosphereParameters a, float radius, float cosZenith, int steps = 64)
    {
        var origin = new Vector3(0f, radius, 0f);
        var dir = new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - cosZenith * cosZenith)), cosZenith, 0f);
        // Below the horizon: the ray meets the ground (also from a point on it).
        if (cosZenith < 0f && radius * radius * (cosZenith * cosZenith - 1f) + a.BottomRadius * a.BottomRadius >= 0f)
            return Vector3.Zero;
        return OpticalTransmittance(a, origin, dir, MathF.Max(0f, RaySphere(origin, dir, a.TopRadius)), steps);
    }

    /// <summary>Transmittance from the camera (<see cref="PhysicalSkySettings.AltitudeMeters"/>) towards a world direction.</summary>
    public static Vector3 SunTransmittance(in AtmosphereParameters a, float altitudeMeters, Vector3 sunDirection)
    {
        var dir = Vector3.Normalize(sunDirection);
        return Transmittance(a, ViewRadius(a, altitudeMeters), dir.Y);
    }

    /// <summary>The camera's distance from the planet's centre in km: at least 1 m above the ground (rays from the surface
    /// itself would meet it at distance 0).</summary>
    public static float ViewRadius(in AtmosphereParameters a, float altitudeMeters) =>
        a.BottomRadius + Math.Clamp(altitudeMeters, 1f, 90_000f) / 1000f;

    /// <summary>
    /// Single-scattered radiance (per unit sun illuminance, physical units) seen from <paramref name="altitudeKm"/>
    /// along <paramref name="view"/> with the sun towards <paramref name="sun"/>: Rayleigh and Mie in-scattering of sun
    /// light that reaches each point (none in the planet's shadow), attenuated back to the camera.
    /// </summary>
    public static Vector3 SingleScattering(in AtmosphereParameters a, float altitudeKm, Vector3 view, Vector3 sun, int steps = 64)
    {
        view = Vector3.Normalize(view);
        sun = Vector3.Normalize(sun);
        var origin = new Vector3(0f, a.BottomRadius + altitudeKm, 0f);
        var ground = RaySphere(origin, view, a.BottomRadius);
        var length = ground >= 0f ? ground : MathF.Max(0f, RaySphere(origin, view, a.TopRadius));
        var cosTheta = Vector3.Dot(view, sun);
        var rayleighPhase = RayleighPhase(cosTheta);
        var miePhase = MiePhase(a.MieG, cosTheta);

        var dt = length / steps;
        var throughput = Vector3.One;
        var radiance = Vector3.Zero;
        for (var i = 0; i < steps; i++)
        {
            var p = origin + view * ((i + 0.5f) * dt);
            var r = p.Length();
            var h = r - a.BottomRadius;
            var up = p / r;
            var sunT = Transmittance(a, r, Vector3.Dot(up, sun), 32);
            var rayleigh = a.RayleighScattering * MathF.Exp(-h / a.RayleighScaleHeight);
            var mie = a.MieScattering * MathF.Exp(-h / a.MieScaleHeight);
            var scattering = (rayleigh * rayleighPhase + mie * miePhase) * sunT;
            var extinction = Extinction(a, h);
            var stepT = Exp(-extinction * dt);
            // Analytic integration over the step (Hillaire's energy-conserving form).
            radiance += throughput * Divide(scattering - scattering * stepT, extinction);
            throughput *= stepT;
        }

        return radiance;
    }

    /// <summary>Rayleigh phase function, 3 / (16π) · (1 + cos²θ).</summary>
    public static float RayleighPhase(float cosTheta) => 3f / (16f * MathF.PI) * (1f + cosTheta * cosTheta);

    /// <summary>Cornette–Shanks Mie phase function with anisotropy <paramref name="g"/>.</summary>
    public static float MiePhase(float g, float cosTheta)
    {
        var g2 = g * g;
        var denominator = MathF.Pow(MathF.Max(1f + g2 - 2f * g * cosTheta, 1e-6f), 1.5f);
        return 3f / (8f * MathF.PI) * (1f - g2) * (1f + cosTheta * cosTheta) / ((2f + g2) * denominator);
    }

    /// <summary>Nearest non-negative distance from <paramref name="origin"/> along unit <paramref name="dir"/> to a sphere at the centre, or −1.</summary>
    public static float RaySphere(Vector3 origin, Vector3 dir, float radius)
    {
        var b = Vector3.Dot(origin, dir);
        var c = Vector3.Dot(origin, origin) - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0f)
            return -1f;
        var s = MathF.Sqrt(discriminant);
        var near = -b - s;
        var far = -b + s;
        if (near >= 0f)
            return near;
        return far >= 0f ? far : -1f;
    }

    private static Vector3 OpticalTransmittance(in AtmosphereParameters a, Vector3 origin, Vector3 dir, float length, int steps)
    {
        var dt = length / steps;
        var depth = Vector3.Zero;
        for (var i = 0; i < steps; i++)
        {
            var p = origin + dir * ((i + 0.5f) * dt);
            depth += Extinction(a, p.Length() - a.BottomRadius) * dt;
        }

        return Exp(-depth);
    }

    private static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));

    private static Vector3 Divide(Vector3 a, Vector3 b) =>
        new(a.X / MathF.Max(b.X, 1e-9f), a.Y / MathF.Max(b.Y, 1e-9f), a.Z / MathF.Max(b.Z, 1e-9f));
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Parameters of the physical sky (<see cref="SkyEnvironmentType.Physical"/>, ADR 0154): a Hillaire 2020 atmosphere
/// whose scattering is Earth's scaled by Godot's <c>PhysicalSkyMaterial</c> parameters, so the defaults are Earth's sky.
/// </summary>
/// <remarks>
/// Units: the sky is linear HDR radiance in the scene lighting's units. A sun of illuminance <c>E</c> (a
/// <see cref="DirectionalLight3D"/> of <c>Energy</c> E and white colour) lights a white Lambert surface facing it to
/// radiance E (the engine's lights carry no 1/π), so physical radiance per unit illuminance is multiplied by π
/// (<see cref="AtmosphereModel.RadianceScale"/>). A clear midday zenith comes out at about 0.1–0.2 × E in blue, and the
/// horizon brighter, next to a white surface at E.
/// </remarks>
public readonly record struct PhysicalSkySettings
{
    public static readonly Vector3 DefaultRayleighColor = new(0.3f, 0.405f, 0.6f);
    public static readonly Vector3 DefaultMieColor = new(0.69f, 0.729f, 0.812f);
    public const float DefaultRayleighCoefficient = 2f;
    public const float DefaultMieCoefficient = 0.005f;
    public const float DefaultTurbidity = 10f;

    public PhysicalSkySettings()
    {
    }

    /// <summary>Rayleigh scattering strength (Godot's <c>rayleigh_coefficient</c>; 2 = Earth).</summary>
    public float RayleighCoefficient { get; init; } = DefaultRayleighCoefficient;

    /// <summary>Tint of Rayleigh scattering (Godot's <c>rayleigh_color</c>); the default is Earth's spectrum.</summary>
    public Vector3 RayleighColor { get; init; } = DefaultRayleighColor;

    /// <summary>Mie (aerosol) scattering strength (Godot's <c>mie_coefficient</c>; 0.005 = Earth).</summary>
    public float MieCoefficient { get; init; } = DefaultMieCoefficient;

    /// <summary>Mie phase anisotropy, −1..1 (Godot's <c>mie_eccentricity</c>): how tightly haze glows around the sun.</summary>
    public float MieEccentricity { get; init; } = 0.8f;

    /// <summary>Tint of Mie scattering (Godot's <c>mie_color</c>).</summary>
    public Vector3 MieColor { get; init; } = DefaultMieColor;

    /// <summary>Haze: scales the Mie density (Godot's <c>turbidity</c>; 10 = Earth's default here).</summary>
    public float Turbidity { get; init; } = DefaultTurbidity;

    /// <summary>Multiplies the sun disc's angular radius (Godot's <c>sun_disk_scale</c>).</summary>
    public float SunDiskScale { get; init; } = 1f;

    /// <summary>Ground albedo below the horizon and for multiple scattering, authored in sRGB (Godot's <c>ground_color</c>).</summary>
    public Vector3 GroundColor { get; init; } = SkyEnvironment.DefaultGroundColor;

    /// <summary>Multiplies the sky and the sun disc (Godot's <c>energy_multiplier</c>).</summary>
    public float EnergyMultiplier { get; init; } = 1f;

    /// <summary>The camera's height above sea level, in metres (the scene's own Y is not added).</summary>
    public float AltitudeMeters { get; init; } = 300f;

    /// <summary>The atmosphere these settings describe, in per-kilometre coefficients.</summary>
    internal AtmosphereParameters ToAtmosphere()
    {
        var rayleighTint = Divide(RayleighColor, DefaultRayleighColor);
        var mieTint = Divide(MieColor, DefaultMieColor);
        var mieScale = MathF.Max(0f, MieCoefficient / DefaultMieCoefficient) * MathF.Max(0f, Turbidity / DefaultTurbidity);
        var mieScattering = AtmosphereParameters.EarthMieScattering * mieScale * mieTint;
        return new AtmosphereParameters
        {
            RayleighScattering = AtmosphereParameters.EarthRayleighScattering * MathF.Max(0f, RayleighCoefficient / DefaultRayleighCoefficient) * rayleighTint,
            MieScattering = mieScattering,
            MieExtinction = mieScattering * (AtmosphereParameters.EarthMieExtinction / AtmosphereParameters.EarthMieScattering),
            MieG = Math.Clamp(MieEccentricity, -0.999f, 0.999f),
            GroundAlbedo = Vector3.Clamp(ColorSpace.SrgbToLinear(GroundColor), Vector3.Zero, Vector3.One),
        };
    }

    private static Vector3 Divide(Vector3 a, Vector3 b) =>
        Vector3.Max(Vector3.Zero, new Vector3(a.X / b.X, a.Y / b.Y, a.Z / b.Z));
}

/// <summary>A Hillaire 2020 atmosphere in kilometres (planet and layer radii, per-kilometre coefficients).</summary>
internal readonly record struct AtmosphereParameters
{
    public static readonly Vector3 EarthRayleighScattering = new(5.802e-3f, 13.558e-3f, 33.1e-3f);
    public const float EarthMieScattering = 3.996e-3f;
    public const float EarthMieExtinction = 4.440e-3f;
    public static readonly Vector3 EarthOzoneAbsorption = new(0.650e-3f, 1.881e-3f, 0.085e-3f);

    public AtmosphereParameters()
    {
    }

    public float BottomRadius { get; init; } = 6360f;
    public float TopRadius { get; init; } = 6460f;
    public Vector3 RayleighScattering { get; init; } = EarthRayleighScattering;
    public float RayleighScaleHeight { get; init; } = 8f;
    public Vector3 MieScattering { get; init; } = new(EarthMieScattering);
    public Vector3 MieExtinction { get; init; } = new(EarthMieExtinction);
    public float MieScaleHeight { get; init; } = 1.2f;
    public float MieG { get; init; } = 0.8f;
    public Vector3 OzoneAbsorption { get; init; } = EarthOzoneAbsorption;
    public Vector3 GroundAlbedo { get; init; } = new(0.3f);
}

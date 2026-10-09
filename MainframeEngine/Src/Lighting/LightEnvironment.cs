using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

public class LightEnvironment
{
    // From Content/Shaders/limits.json (generated ShaderLimits / include/limits.slang): C# and the shaders cannot drift.
    public const int MaxDirectional = ShaderLimits.MaxDirectionalLights;
    public const int MaxPoint = ShaderLimits.MaxPointLights;
    public const int MaxSpot = ShaderLimits.MaxSpotLights;

    /// <summary>
    /// Size in bytes of the std140 lights UBO read by <c>Shapes.vk.frag</c> and <c>SpineLit.vk.frag</c>:
    /// header (ambient, camera position, counts) then fixed-size directional, point and spot arrays.
    /// </summary>
    internal const int UboSize =
        UboHeaderSize +
        MaxDirectional * DirectionalStride +
        MaxPoint       * PointStride +
        MaxSpot        * SpotStride; // 1200 bytes

    private const int UboHeaderSize     = 48; // vec4 ambient (w = energy), vec4 cameraPos (w = environment flags), ivec4 counts
    private const int DirectionalStride = 32; // vec4 direction+intensity, vec4 color+shadowOpacity
    private const int PointStride       = 32; // vec4 position+range, vec4 color+intensity
    private const int SpotStride        = 64; // vec4 position+range, vec4 direction+intensity, vec4 color+cosInner, vec4 cosOuter+shadowOpacity

    private Vector3 _ambientColor;
    private Vector3 _ambientLinear;

    /// <summary>Ambient light colour as authored (sRGB); the UBO carries it converted to linear.</summary>
    public Vector3 AmbientColor
    {
        get => _ambientColor;
        set
        {
            _ambientColor = value;
            _ambientLinear = ColorSpace.SrgbToLinear(value);
        }
    }

    /// <summary>
    /// Default ambient (sRGB). Linear ≈ (0.04, 0.04, 0.05): with the ACES tonemap, unlit surfaces read about as
    /// bright as the old gamma-space default of (0.08, 0.08, 0.10) did.
    /// </summary>
    public static readonly Vector3 DefaultAmbientColor = new(0.22f, 0.22f, 0.25f);

    public LightEnvironment()
    {
        AmbientColor = DefaultAmbientColor;
    }

    /// <summary>
    /// Scales the ambient light (<see cref="WorldEnvironment.AmbientEnergy"/>): the UBO carries the ambient colour × this,
    /// and this alone in <c>ambientColor.w</c> for the sky's irradiance. Set by the render server each frame.
    /// </summary>
    internal float AmbientEnergy { get; set; } = 1f;

    /// <summary>
    /// Image-based lighting flags (<c>kEnv*</c> in <c>include/lights_data.slang</c>, written to <c>cameraPosition.w</c>):
    /// <see cref="EnvironmentSkyDiffuse"/>, <see cref="EnvironmentSkySpecular"/>, <see cref="EnvironmentNoSpecular"/>. Set by
    /// the render server each frame from the world's <see cref="WorldEnvironment"/>.
    /// </summary>
    internal int EnvironmentFlags { get; set; }

    /// <summary>Ambient light from the sky's irradiance cube instead of the ambient colour.</summary>
    internal const int EnvironmentSkyDiffuse = 1;

    /// <summary>Reflections from the sky's prefiltered radiance cube instead of a uniform ambient-colour environment.</summary>
    internal const int EnvironmentSkySpecular = 2;

    /// <summary>No reflected light.</summary>
    internal const int EnvironmentNoSpecular = 4;
    internal List<DirectionalLight> DirectionalLights { get; } = [];
    internal List<PointLight> PointLights { get; } = [];
    internal List<SpotLight> SpotLights { get; } = [];

    /// <summary>
    /// Packs the lights into <paramref name="destination"/> using the std140 layout described by
    /// <see cref="UboSize"/>. Lights beyond the per-type maximum are ignored; unused slots are zeroed. Colours are
    /// written linear (<see cref="Light.LinearColor"/>): the shaders light in linear space.
    /// </summary>
    internal void WriteUbo(Span<byte> destination, in Vector3 cameraPosition) => WriteUbo(destination, cameraPosition, shadows: true);

    /// <summary>
    /// <see cref="WriteUbo(Span{byte}, in Vector3)"/>; <paramref name="shadows"/> false sets <c>counts.w</c> = 1, which
    /// tells the lit shaders to skip shadow maps (views of a world the shadow system did not render).
    /// </summary>
    internal void WriteUbo(Span<byte> destination, in Vector3 cameraPosition, bool shadows)
    {
        if (destination.Length < UboSize)
            throw new ArgumentException($"Lights UBO needs {UboSize} bytes.", nameof(destination));

        var ubo = destination[..UboSize];
        ubo.Clear();
        var f = MemoryMarshal.Cast<byte, float>(ubo);
        var i = MemoryMarshal.Cast<byte, int>(ubo);

        int numDir   = Math.Min(DirectionalLights.Count, MaxDirectional);
        int numPoint = Math.Min(PointLights.Count,       MaxPoint);
        int numSpot  = Math.Min(SpotLights.Count,        MaxSpot);

        // Header
        var ambient = _ambientLinear * AmbientEnergy;
        f[0] = ambient.X; f[1] = ambient.Y; f[2] = ambient.Z; f[3] = AmbientEnergy;
        f[4] = cameraPosition.X; f[5] = cameraPosition.Y; f[6] = cameraPosition.Z; f[7] = EnvironmentFlags;
        i[8] = numDir; i[9] = numPoint; i[10] = numSpot; i[11] = shadows ? 0 : 1;

        var o = UboHeaderSize / sizeof(float);
        for (int n = 0; n < numDir; n++, o += DirectionalStride / sizeof(float))
        {
            var l = DirectionalLights[n];
            f[o + 0] = l.Direction.X; f[o + 1] = l.Direction.Y; f[o + 2] = l.Direction.Z;
            f[o + 3] = l.Intensity;
            f[o + 4] = l.LinearColor.X; f[o + 5] = l.LinearColor.Y; f[o + 6] = l.LinearColor.Z;
            f[o + 7] = l.ShadowOpacity;
        }

        o = (UboHeaderSize + MaxDirectional * DirectionalStride) / sizeof(float);
        for (int n = 0; n < numPoint; n++, o += PointStride / sizeof(float))
        {
            var l = PointLights[n];
            f[o + 0] = l.Position.X; f[o + 1] = l.Position.Y; f[o + 2] = l.Position.Z;
            f[o + 3] = l.Range;
            f[o + 4] = l.LinearColor.X; f[o + 5] = l.LinearColor.Y; f[o + 6] = l.LinearColor.Z;
            f[o + 7] = l.Intensity;
        }

        o = (UboHeaderSize + MaxDirectional * DirectionalStride + MaxPoint * PointStride) / sizeof(float);
        for (int n = 0; n < numSpot; n++, o += SpotStride / sizeof(float))
        {
            var l = SpotLights[n];
            f[o + 0] = l.Position.X; f[o + 1] = l.Position.Y; f[o + 2] = l.Position.Z;
            f[o + 3] = l.Range;
            f[o + 4] = l.Direction.X; f[o + 5] = l.Direction.Y; f[o + 6] = l.Direction.Z;
            f[o + 7] = l.Intensity;
            f[o + 8] = l.LinearColor.X; f[o + 9] = l.LinearColor.Y; f[o + 10] = l.LinearColor.Z;
            f[o + 11] = float.Cos(float.DegreesToRadians(l.InnerConeAngle));
            f[o + 12] = float.Cos(float.DegreesToRadians(l.OuterConeAngle));
            f[o + 13] = l.ShadowOpacity;
        }
    }

    /// <summary>
    /// Adds a light to the corresponding collection based on its type.
    /// </summary>
    /// <param name="light">The light to add. Can be of type DirectionalLight, PointLight, or SpotLight.</param>
    public void AddLight(Light light)
    {
        switch (light)
        {
            case DirectionalLight dl:
                DirectionalLights.Add(dl);
                break;
            case PointLight pl:
                PointLights.Add(pl);
                break;
            case SpotLight sl:
                SpotLights.Add(sl);
                break;
        }
    }

    /// <summary>Removes a light added with <see cref="AddLight"/>; returns false if it was not present.</summary>
    public bool RemoveLight(Light light) => light switch
    {
        DirectionalLight dl => DirectionalLights.Remove(dl),
        PointLight pl => PointLights.Remove(pl),
        SpotLight sl => SpotLights.Remove(sl),
        _ => false,
    };

    /// <summary>Number of lights of every type (including ones beyond the per-type UBO limits).</summary>
    public int Count => DirectionalLights.Count + PointLights.Count + SpotLights.Count;
}

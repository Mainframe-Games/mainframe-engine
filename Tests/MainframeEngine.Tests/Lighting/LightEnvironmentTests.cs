using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Lighting;

/// <summary>The lights UBO must match the std140 <c>LightsUBO</c> block in Shapes.vk.frag / SpineLit.vk.frag.</summary>
public class LightEnvironmentTests
{
    private const int HeaderFloats = 12;
    private const int DirFloats = 8;
    private const int PointFloats = 8;
    private const int SpotFloats = 16;

    private static (float[] F, int[] I) Pack(LightEnvironment env, Vector3 cameraPosition = default)
    {
        var bytes = new byte[LightEnvironment.UboSize];
        env.WriteUbo(bytes, cameraPosition);
        return (MemoryMarshal.Cast<byte, float>(bytes).ToArray(), MemoryMarshal.Cast<byte, int>(bytes).ToArray());
    }

    [Fact]
    public void UboSizeIs1200BytesStd140()
    {
        Assert.Equal(1200, LightEnvironment.UboSize);
        Assert.Equal(48 + 4 * 32 + 16 * 32 + 8 * 64, LightEnvironment.UboSize);
        Assert.Equal(0, LightEnvironment.UboSize % 16); // std140 blocks are vec4-aligned
    }

    [Fact]
    public void LimitsMatchTheShaderDefines()
    {
        Assert.Equal(4, LightEnvironment.MaxDirectional);
        Assert.Equal(16, LightEnvironment.MaxPoint);
        Assert.Equal(8, LightEnvironment.MaxSpot);
        Assert.Equal(LightEnvironment.MaxDirectional, ShadowSystem.MaxShadowDir);
        Assert.Equal(LightEnvironment.MaxSpot, ShadowSystem.MaxShadowSpot); // every spot light casts (the atlas freed the samplers)
        Assert.True(ShadowSystem.SamplerCount + 1 <= 16, "shadow samplers + the material sampler fit MoltenVK's 16 per stage");
        Assert.Equal(4, ShadowSystem.MaxShadowPoint);
    }

    [Fact]
    public void AddLightRoutesByType()
    {
        var env = new LightEnvironment();
        env.AddLight(new DirectionalLight());
        env.AddLight(new PointLight());
        env.AddLight(new PointLight());
        env.AddLight(new SpotLight());

        Assert.Single(env.DirectionalLights);
        Assert.Equal(2, env.PointLights.Count);
        Assert.Single(env.SpotLights);
    }

    private static float L(float srgb) => ColorSpace.SrgbToLinear(srgb);

    [Fact]
    public void HeaderHoldsAmbientCameraAndCounts()
    {
        var env = new LightEnvironment { AmbientColor = new Vector3(0.1f, 0.2f, 0.3f) };
        env.AddLight(new DirectionalLight());
        env.AddLight(new PointLight());
        env.AddLight(new SpotLight());
        env.AddLight(new SpotLight());

        var (f, i) = Pack(env, new Vector3(4, 5, 6));

        // sRGB-authored colours are packed linear.
        Assert.Equal([L(0.1f), L(0.2f), L(0.3f), 0f], f[0..4]);
        Assert.Equal([4f, 5f, 6f, 0f], f[4..8]);
        Assert.Equal([1, 1, 2, 0], i[8..12]);
    }

    [Fact]
    public void LightsArePackedAtTheirStd140Offsets()
    {
        var env = new LightEnvironment();
        env.AddLight(new DirectionalLight { Direction = new Vector3(0, -1, 0), Intensity = 0.5f, Color = new Vector3(1, 0.5f, 0.25f), ShadowOpacity = 0.45f });
        env.AddLight(new PointLight { Position = new Vector3(1, 2, 3), Range = 7, Color = new Vector3(0.1f, 0.2f, 0.3f), Intensity = 2 });
        env.AddLight(new SpotLight
        {
            Position = new Vector3(-1, 4, 2),
            Range = 15,
            Direction = new Vector3(0, -1, 0),
            Intensity = 3,
            Color = Vector3.One,
            InnerConeAngle = 60,
            OuterConeAngle = 90,
            ShadowOpacity = 0.25f,
        });

        var (f, _) = Pack(env);

        var dir = HeaderFloats;
        Assert.Equal([0f, -1f, 0f, 0.5f, 1f, L(0.5f), L(0.25f), 0.45f], f[dir..(dir + DirFloats)]); // w = shadow opacity

        var point = HeaderFloats + LightEnvironment.MaxDirectional * DirFloats;
        Assert.Equal([1f, 2f, 3f, 7f, L(0.1f), L(0.2f), L(0.3f), 2f], f[point..(point + PointFloats)]);

        var spot = point + LightEnvironment.MaxPoint * PointFloats;
        Assert.Equal([-1f, 4f, 2f, 15f, 0f, -1f, 0f, 3f, 1f, 1f, 1f], f[spot..(spot + 11)]);
        Assert.Equal(0.5f, f[spot + 11], 1e-6f); // cos(60°)
        Assert.Equal(0f, f[spot + 12], 1e-6f);   // cos(90°)
        Assert.Equal([0.25f, 0f, 0f], f[(spot + 13)..(spot + 16)]); // outerPad.y = shadow opacity
        Assert.Equal(LightEnvironment.UboSize / 4, spot + LightEnvironment.MaxSpot * SpotFloats);
    }

    [Fact]
    public void LightsBeyondTheMaximumAreDropped()
    {
        var env = new LightEnvironment();
        for (var n = 0; n < LightEnvironment.MaxDirectional + 3; n++)
            env.AddLight(new DirectionalLight { Intensity = n + 1 });

        var (f, i) = Pack(env);

        Assert.Equal(LightEnvironment.MaxDirectional, i[8]);
        // The last directional slot holds light #4, and the point array right after it is untouched.
        Assert.Equal(LightEnvironment.MaxDirectional, f[HeaderFloats + (LightEnvironment.MaxDirectional - 1) * DirFloats + 3]);
        Assert.Equal(0f, f[HeaderFloats + LightEnvironment.MaxDirectional * DirFloats + 3]);
    }

    [Fact]
    public void WritingClearsPreviousContents()
    {
        var bytes = new byte[LightEnvironment.UboSize];
        bytes.AsSpan().Fill(0xFF);

        new LightEnvironment { AmbientColor = Vector3.Zero }.WriteUbo(bytes, Vector3.Zero);

        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ShortDestinationIsRejected()
    {
        var env = new LightEnvironment();

        Assert.Throws<ArgumentException>(() => env.WriteUbo(new byte[LightEnvironment.UboSize - 1], Vector3.Zero));
    }
}

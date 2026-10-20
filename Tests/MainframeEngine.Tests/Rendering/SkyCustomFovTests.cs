using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

/// <summary>#106: <see cref="Sky.CustomFov"/> (Godot's <c>sky_custom_fov</c>), the sky pass's view ray scale.</summary>
public sealed class SkyCustomFovTests
{
    private static Matrix4x4 Perspective(float fovDegrees, float aspect = 16f / 9f) =>
        Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(fovDegrees), aspect, 0.1f, 100f);

    // sky.slang's skyRay: the view-space direction through NDC (x, y), after the ray scale.
    private static Vector3 ViewRay(Matrix4x4 projection, Vector2 ndc, float scale)
    {
        Matrix4x4.Invert(projection, out var inverse);
        var p = Vector4.Transform(new Vector4(ndc * scale, 1f, 1f), inverse);
        return Vector3.Normalize(new Vector3(p.X, p.Y, p.Z) / p.W);
    }

    [Fact]
    public void ANarrowCameraWithCustomFovSeesTheWideCamerasRays()
    {
        var narrow = Perspective(20f);
        var wide = Perspective(60f);
        var scale = SkyEnvironment.RayScale(60f, narrow);

        foreach (var ndc in new[] { new Vector2(1, 1), new Vector2(-1, 0.5f), new Vector2(0.25f, -1), Vector2.Zero })
        {
            var expected = ViewRay(wide, ndc, 1f);
            var actual = ViewRay(narrow, ndc, scale);
            Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{ndc}: {actual} vs {expected}");
        }
    }

    [Fact]
    public void ZeroOrOrthographicKeepsTheCamerasRays()
    {
        Assert.Equal(1f, SkyEnvironment.RayScale(0f, Perspective(20f)));
        Assert.Equal(1f, SkyEnvironment.RayScale(60f, Matrix4x4.CreateOrthographic(10f, 10f, 0.1f, 100f)));
        Assert.Equal(1f, SkyEnvironment.RayScale(45f, Perspective(45f)), 1e-5f);
    }

    [Fact]
    public void TheResourceDefaultsToTheCamerasFov() => Assert.Equal(0f, new Sky().CustomFov);
}

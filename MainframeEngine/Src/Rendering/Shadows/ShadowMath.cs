using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// CPU-side shadow math (no GPU state): cascade splits, the rotation-stable bounding sphere of a frustum slice,
/// texel-snapped light matrices, spot and cube-face matrices, texel world sizes and the receiver offsets the shaders
/// apply. All matrices use the System.Numerics row-vector convention with Vulkan's [0, 1] depth.
/// </summary>
public static class ShadowMath
{
    /// <summary>Near plane of spot-light shadow frusta.</summary>
    public const float SpotNear = 0.05f;

    /// <summary>Near plane of point-light cube faces.</summary>
    public const float PointNear = 0.05f;

    /// <summary>Radii are rounded up to this step so float noise in the projection never resizes a cascade.</summary>
    public const float RadiusQuantum = 1f / 16f;

    /// <summary>
    /// Practical split scheme (Zhang et al.): split <c>i</c> of <c>n</c> is
    /// <c>λ·near·(far/near)^(i/n) + (1−λ)·(near + (far−near)·i/n)</c>. Writes the view depth where each cascade ends
    /// (<paramref name="splits"/>.Length cascades; the last is exactly <paramref name="far"/>).
    /// </summary>
    public static void ComputeSplits(float near, float far, float lambda, Span<float> splits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(near);
        if (!(far > near))
            throw new ArgumentOutOfRangeException(nameof(far), far, "far must be greater than near.");
        var count = splits.Length;
        lambda = Math.Clamp(lambda, 0f, 1f);
        for (var i = 1; i <= count; i++)
        {
            var f = (float)i / count;
            var log = near * MathF.Pow(far / near, f);
            var uniform = near + (far - near) * f;
            splits[i - 1] = lambda * log + (1f - lambda) * uniform;
        }

        splits[count - 1] = far;
    }

    /// <summary>
    /// The bounding sphere of the camera frustum between view depths <paramref name="sliceNear"/> and
    /// <paramref name="sliceFar"/>, in <b>view space</b>. It depends only on the projection and the two depths, so
    /// it does not change size or shape when the camera moves or rotates (the radius is rounded up to
    /// <see cref="RadiusQuantum"/>). Works for perspective and orthographic projections.
    /// </summary>
    public static (Vector3 Center, float Radius) SliceSphere(in Matrix4x4 projection, float sliceNear, float sliceFar)
    {
        if (!Matrix4x4.Invert(projection, out var inverse))
            throw new ArgumentException("The projection matrix is not invertible.", nameof(projection));

        // Corners of the camera's near (NDC z 0) and far (z 1) planes in view space; a slice corner at view depth d
        // lies on the segment between corresponding corners (the eye ray for perspective, a parallel line for ortho).
        Span<Vector3> nearCorners = stackalloc Vector3[4];
        Span<Vector3> farCorners = stackalloc Vector3[4];
        for (var i = 0; i < 4; i++)
        {
            var x = (i & 1) == 0 ? -1f : 1f;
            var y = (i & 2) == 0 ? -1f : 1f;
            nearCorners[i] = Unproject(inverse, new Vector3(x, y, 0f));
            farCorners[i] = Unproject(inverse, new Vector3(x, y, 1f));
        }

        Span<Vector3> slice = stackalloc Vector3[8];
        for (var i = 0; i < 4; i++)
        {
            slice[i] = AtDepth(nearCorners[i], farCorners[i], sliceNear);
            slice[i + 4] = AtDepth(nearCorners[i], farCorners[i], sliceFar);
        }

        // Centre on the segment between the two rectangle centres, where the near and far corners are equidistant.
        var p0 = (slice[0] + slice[1] + slice[2] + slice[3]) * 0.25f;
        var p1 = (slice[4] + slice[5] + slice[6] + slice[7]) * 0.25f;
        var r0 = Vector3.DistanceSquared(slice[0], p0);
        var r1 = Vector3.DistanceSquared(slice[4], p1);
        var axisSquared = Vector3.DistanceSquared(p0, p1);
        var t = axisSquared > 1e-12f ? Math.Clamp((axisSquared + r1 - r0) / (2f * axisSquared), 0f, 1f) : 0.5f;
        var center = Vector3.Lerp(p0, p1, t);

        var radiusSquared = 0f;
        foreach (var corner in slice)
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, corner));
        var radius = MathF.Ceiling(MathF.Sqrt(radiusSquared) / RadiusQuantum) * RadiusQuantum;
        return (center, radius);
    }

    private static Vector3 Unproject(in Matrix4x4 inverseProjection, Vector3 ndc)
    {
        var v = Vector4.Transform(new Vector4(ndc, 1f), inverseProjection);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }

    // View-space depth is -z. The point at depth d on the line through two view-space points.
    private static Vector3 AtDepth(Vector3 a, Vector3 b, float depth)
    {
        var da = -a.Z;
        var db = -b.Z;
        var t = MathF.Abs(db - da) > 1e-12f ? (depth - da) / (db - da) : 0f;
        return Vector3.Lerp(a, b, t);
    }

    /// <summary>World up, or +Z when the light points (nearly) straight up/down, where up would be degenerate.</summary>
    public static Vector3 ChooseUp(Vector3 direction)
        => MathF.Abs(Vector3.Dot(Vector3.Normalize(direction), Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

    /// <summary>
    /// The rotation of a directional light's view (no translation): light space looks down <c>-Z</c> along
    /// <paramref name="direction"/>. It depends only on the direction, so the texel grid of a light is fixed in world
    /// space.
    /// </summary>
    public static Matrix4x4 LightRotation(Vector3 direction)
    {
        var dir = Vector3.Normalize(direction);
        return Matrix4x4.CreateLookAt(Vector3.Zero, dir, ChooseUp(dir));
    }

    /// <summary>
    /// The orthographic light view-projection covering a world-space sphere with a
    /// <paramref name="resolution"/>² map. When <paramref name="snap"/> is set, the sphere centre is moved onto the
    /// light-space texel grid (x, y and depth), so the map's texels stay fixed in the world: moving the camera by
    /// less than a texel leaves the matrix unchanged, and larger moves shift it by whole texels (no shimmering
    /// edges). The window is one texel wider than the sphere on every side, so snapping (which moves the centre by up
    /// to one texel) never uncovers part of it. <paramref name="pullback"/> extends the near plane towards the light
    /// to keep casters outside the sphere.
    /// </summary>
    /// <param name="texelWorldSize">World size of one shadow-map texel: 2·h / resolution, h = r·resolution / (resolution − 2).</param>
    public static Matrix4x4 SphereLightMatrix(in Matrix4x4 lightRotation, Vector3 worldCenter, float radius, int resolution,
        float pullback, bool snap, out float texelWorldSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 4);
        // Half-size h with a one-texel margin: h = r + 2h / resolution.
        var half = radius * resolution / (resolution - 2f);
        texelWorldSize = 2f * half / resolution;
        var center = Vector3.Transform(worldCenter, lightRotation);
        if (snap)
            center = SnapToTexel(center, texelWorldSize);

        // Light space looks down -Z: the window spans z in [c - h, c + h]; points nearer the light have larger z.
        // CreateOrthographicOffCenter maps view z = -zNear to depth 0 and z = -zFar to depth 1.
        var view = lightRotation * Matrix4x4.CreateTranslation(-center);
        var projection = Matrix4x4.CreateOrthographicOffCenter(-half, half, -half, half, -(half + Math.Max(0f, pullback)), half);
        return view * projection;
    }

    /// <summary>Rounds each component down to a multiple of <paramref name="texel"/>.</summary>
    public static Vector3 SnapToTexel(Vector3 lightSpace, float texel) => new(
        MathF.Floor(lightSpace.X / texel) * texel,
        MathF.Floor(lightSpace.Y / texel) * texel,
        MathF.Floor(lightSpace.Z / texel) * texel);

    /// <summary>
    /// Distance to pull a cascade's near plane towards the light so that every caster in
    /// <paramref name="casterBounds"/> lies behind it: the casters' light-space front minus the sphere's front, at
    /// least <paramref name="minimum"/> (casters without bounds, such as Spine) and at most <paramref name="maximum"/>
    /// (depth precision); rounded up to whole units so it is stable while casters move slightly.
    /// </summary>
    public static float CasterPullback(in Matrix4x4 lightRotation, in Aabb casterBounds, Vector3 worldCenter, float radius,
        float minimum, float maximum)
    {
        if (casterBounds.IsEmpty)
            return minimum;
        var lightSpace = casterBounds.Transform(lightRotation);
        var sphereFront = Vector3.Transform(worldCenter, lightRotation).Z + radius;
        var needed = MathF.Ceiling(lightSpace.Max.Z - sphereFront);
        return Math.Clamp(needed, minimum, maximum);
    }

    /// <summary>Perspective view-projection of a spot light (field of view = twice the outer cone half-angle).</summary>
    public static Matrix4x4 SpotLightMatrix(Vector3 position, Vector3 direction, float outerConeAngleDegrees, float range)
    {
        var fovY = float.DegreesToRadians(Math.Clamp(outerConeAngleDegrees * 2f, 1f, 179f));
        var dir = Vector3.Normalize(direction);
        var view = Matrix4x4.CreateLookAt(position, position + dir, ChooseUp(dir));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fovY, 1f, SpotNear, Math.Max(range, SpotNear * 2f));
        return view * projection;
    }

    /// <summary>World size of a spot map texel per unit of distance from the light: <c>2·tan(fov/2) / resolution</c>.</summary>
    public static float SpotTexelPerDistance(float outerConeAngleDegrees, int resolution)
    {
        var halfFov = float.DegreesToRadians(Math.Clamp(outerConeAngleDegrees, 0.5f, 89.5f));
        return 2f * MathF.Tan(halfFov) / resolution;
    }

    /// <summary>Look direction and up vector per cube face, in Vulkan face order (+X, −X, +Y, −Y, +Z, −Z).</summary>
    public static (Vector3 Direction, Vector3 Up) CubeFace(int face) => face switch
    {
        0 => (Vector3.UnitX, -Vector3.UnitY),
        1 => (-Vector3.UnitX, -Vector3.UnitY),
        2 => (Vector3.UnitY, Vector3.UnitZ),
        3 => (-Vector3.UnitY, -Vector3.UnitZ),
        4 => (Vector3.UnitZ, -Vector3.UnitY),
        5 => (-Vector3.UnitZ, -Vector3.UnitY),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    /// <summary>View-projection of one point-light cube face (90° field of view, far plane = range).</summary>
    public static Matrix4x4 CubeFaceMatrix(Vector3 position, int face, float range)
    {
        var (dir, up) = CubeFace(face);
        var view = Matrix4x4.CreateLookAt(position, position + dir, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, PointNear, Math.Max(range, PointNear * 2f));
        return view * projection;
    }

    /// <summary>World size of a cube-face texel per unit of distance from the light (90° faces): <c>2 / resolution</c>.</summary>
    public static float CubeTexelPerDistance(int resolution) => 2f / resolution;

    /// <summary>
    /// The receiver position the shaders compare against a shadow map (mirrors <c>shadowReceiver</c> in
    /// <c>include/shadows.glsl</c>): moved towards the light by <paramref name="depthBias"/> texels and along the
    /// normal by <paramref name="normalBias"/> texels × sin(angle to the light), where a texel measures
    /// <paramref name="texelWorldSize"/> at that point.
    /// </summary>
    public static Vector3 ReceiverPosition(Vector3 worldPosition, Vector3 normal, Vector3 toLight, float texelWorldSize,
        float depthBias, float normalBias)
    {
        var cos = Math.Clamp(Vector3.Dot(normal, toLight), 0f, 1f);
        var sin = MathF.Sqrt(1f - cos * cos);
        return worldPosition + toLight * (depthBias * texelWorldSize) + normal * (normalBias * texelWorldSize * sin);
    }
}

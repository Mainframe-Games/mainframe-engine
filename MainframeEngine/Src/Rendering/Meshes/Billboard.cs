using System.Numerics;

namespace MainframeEngine;

/// <summary>How a node faces the camera (Godot's <c>BaseMaterial3D.BillboardMode</c>).</summary>
public enum BillboardMode : byte
{
    /// <summary>The node's own rotation.</summary>
    Disabled,

    /// <summary>Faces the camera: the quad's +Z points back at it and its +Y is the camera's up.</summary>
    Enabled,

    /// <summary>Turns about world Y only to face the camera, staying upright (Godot's <c>BILLBOARD_FIXED_Y</c>).</summary>
    FixedY,

    /// <summary>
    /// Particles (Godot's <c>BILLBOARD_PARTICLES</c>; ADR 0206): faces the camera turned by each particle's angle, or along
    /// its velocity with <see cref="ParticleProcessMaterial.ParticleFlagAlignY"/>. Other nodes treat it as <see cref="Enabled"/>.
    /// </summary>
    Particles,
}

/// <summary>
/// CPU billboarding (ADR 0199): the mesh renderer replaces a billboarded node's model matrix with one facing the view's
/// camera before it culls and writes instance data, so the colour pass, depth prepass, motion vectors, picking and the
/// shadow casters (built from the main view, as Godot does) all see the same quad with no shader support.
/// </summary>
internal static class Billboard
{
    /// <summary>
    /// <paramref name="model"/> turned to face the camera of <paramref name="view"/>: the node's position and scale (the
    /// lengths of its basis rows, Godot's <c>billboard_keep_scale</c>) on a basis from the camera's rotation.
    /// </summary>
    public static Matrix4x4 Face(in Matrix4x4 model, BillboardMode mode, in Matrix4x4 view)
    {
        if (mode == BillboardMode.Disabled)
            return model;

        // The camera's world basis is the transpose of the view rotation (row-vector convention: its columns).
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var back = new Vector3(view.M13, view.M23, view.M33);
        if (mode == BillboardMode.FixedY)
        {
            // Godot: X = cross(up, back), Y = world up, Z = cross(X, Y). Looking straight down, back has no horizontal
            // part; the camera's right (horizontal for a camera without roll) gives the facing instead.
            var x = new Vector3(back.Z, 0f, -back.X);
            if (x.LengthSquared() < 1e-8f)
                x = new Vector3(right.X, 0f, right.Z);
            right = x.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(x);
            up = Vector3.UnitY;
            back = Vector3.Cross(right, up);
        }

        var scale = new Vector3(
            new Vector3(model.M11, model.M12, model.M13).Length(),
            new Vector3(model.M21, model.M22, model.M23).Length(),
            new Vector3(model.M31, model.M32, model.M33).Length());
        return new Matrix4x4(
            right.X * scale.X, right.Y * scale.X, right.Z * scale.X, 0f,
            up.X * scale.Y, up.Y * scale.Y, up.Z * scale.Y, 0f,
            back.X * scale.Z, back.Y * scale.Z, back.Z * scale.Z, 0f,
            model.M41, model.M42, model.M43, 1f);
    }

    /// <summary>
    /// <paramref name="model"/> scaled about its origin so that a mesh built at <paramref name="pixelSize"/> world units
    /// per pixel covers one framebuffer pixel per mesh pixel in this view (<see cref="Label3D.FixedSize"/>):
    /// <paramref name="pixelScale"/> is <see cref="LodSelection.PixelScale"/> (pixels per world unit at depth 1, or at any
    /// depth for an orthographic view).
    /// </summary>
    public static Matrix4x4 FixedSize(in Matrix4x4 model, float pixelSize, in Matrix4x4 view, float pixelScale, bool perspective)
    {
        if (!(pixelSize > 0f && pixelScale > 0f))
            return model;
        var depth = 1f;
        if (perspective)
        {
            // View space looks down −Z (row vectors: z = origin · view's third column).
            depth = -(model.M41 * view.M13 + model.M42 * view.M23 + model.M43 * view.M33 + view.M43);
            depth = MathF.Max(depth, 1e-4f);
        }

        var scale = depth / pixelScale / pixelSize;
        var scaled = model;
        scaled.M11 *= scale; scaled.M12 *= scale; scaled.M13 *= scale;
        scaled.M21 *= scale; scaled.M22 *= scale; scaled.M23 *= scale;
        scaled.M31 *= scale; scaled.M32 *= scale; scaled.M33 *= scale;
        return scaled;
    }
}

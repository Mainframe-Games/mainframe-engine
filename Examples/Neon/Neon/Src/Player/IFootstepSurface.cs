using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Something a footstep can land on that names its surface (<c>"concrete"</c>, <c>"wood"</c>, <c>"metal"</c> for the
/// stairs and the mezzanine grate, <c>"tile"</c>, <c>"rug"</c>). <see cref="FirstPersonController"/> asks the floor
/// collider under the feet; a body that does not implement it falls back to
/// <see cref="FirstPersonController.SurfaceResolver"/> and then <see cref="FirstPersonController.DefaultSurface"/>.
/// </summary>
public interface IFootstepSurface
{
    /// <summary>The surface name at <paramref name="position"/> (world space, on the collider).</summary>
    string GetFootstepSurface(Vector3 position);
}

/// <summary>A static body with a footstep surface name: the apartment's shell is <c>concrete</c>, the doors <c>wood</c>.</summary>
public sealed class SurfaceBody3D : StaticBody3D, IFootstepSurface
{
    /// <summary>The surface footsteps on this body use.</summary>
    [Export] public string Surface { get; set; } = "concrete";

    public string GetFootstepSurface(Vector3 position) => Surface;
}

using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Something a footstep can land on that names its surface (<c>"wood"</c>, <c>"rock"</c>, a terrain layer such as
/// <c>"moss"</c>). <see cref="FirstPersonController"/> asks the floor collider under the feet; a body that does not
/// implement it falls back to <see cref="FirstPersonController.SurfaceResolver"/> (the content wave sets it to the
/// terrain's <c>SurfaceAt</c>) and then <see cref="FirstPersonController.DefaultSurface"/>.
/// </summary>
public interface IFootstepSurface
{
    /// <summary>The surface name at <paramref name="position"/> (world space, on the collider).</summary>
    string GetFootstepSurface(Vector3 position);
}

/// <summary>A static body with a footstep surface name: logs are <c>wood</c>, rocks <c>rock</c>.</summary>
public sealed class SurfaceBody3D : StaticBody3D, IFootstepSurface
{
    /// <summary>The surface footsteps on this body use.</summary>
    [Export] public string Surface { get; set; } = "rock";

    public string GetFootstepSurface(Vector3 position) => Surface;
}

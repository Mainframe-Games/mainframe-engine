using System.Numerics;

namespace MainframeEngine;

/// <summary>What a water body reports at a point: the surface height, the bed-to-surface column and the surface flow.</summary>
/// <param name="SurfaceHeight">World Y of the water surface above the point.</param>
/// <param name="ColumnDepth">Bed-to-surface depth in metres at the point (0 at a shoreline).</param>
/// <param name="Flow">Surface velocity in m/s (world space; zero for still water).</param>
public readonly record struct WaterSample(float SurfaceHeight, float ColumnDepth, Vector3 Flow);

/// <summary>
/// A body of water that answers point queries (<see cref="River3D"/>; later the terrain's water layer and game types).
/// Bodies register with their world's <see cref="World3D.Water"/> while they are in the tree.
/// </summary>
public interface IWaterBody3D
{
    /// <summary>World-space bounds of the water (surface and column); queries outside it in XZ skip the body.</summary>
    Aabb WaterBounds { get; }

    /// <summary>Samples the water above or below <paramref name="position"/>; false when the XZ position is not over this body.</summary>
    bool TrySample(Vector3 position, out WaterSample sample);
}

/// <summary>
/// "Is this point in water?" for one world (<see cref="World3D.Water"/>): depth, surface height and flow over every
/// registered <see cref="IWaterBody3D"/>, plus wading helpers for character controllers. Where bodies overlap, the
/// highest surface wins.
/// </summary>
/// <remarks>Main thread only, like physics queries; no query allocates. A world has a handful of bodies, so the
/// registry is a list tested against <see cref="IWaterBody3D.WaterBounds"/> first.</remarks>
public sealed class WaterQueries
{
    private readonly List<IWaterBody3D> _bodies = [];

    /// <summary>The registered bodies, in registration order.</summary>
    public IReadOnlyList<IWaterBody3D> Bodies => _bodies;

    /// <summary>Adds <paramref name="body"/> (once; registering twice is ignored).</summary>
    public void Register(IWaterBody3D body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!_bodies.Contains(body))
            _bodies.Add(body);
    }

    public bool Unregister(IWaterBody3D body) => _bodies.Remove(body);

    /// <summary>Samples the water at <paramref name="position"/>'s XZ; false when no body is there (dry).</summary>
    public bool TrySample(Vector3 position, out WaterSample sample)
    {
        sample = default;
        var found = false;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            var bounds = body.WaterBounds;
            if (bounds.IsEmpty || position.X < bounds.Min.X || position.X > bounds.Max.X || position.Z < bounds.Min.Z || position.Z > bounds.Max.Z)
                continue;
            if (body.TrySample(position, out var candidate) && (!found || candidate.SurfaceHeight > sample.SurfaceHeight))
            {
                sample = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Bed-to-surface column depth in metres at the point's XZ; 0 when dry.</summary>
    public float WaterDepthAt(Vector3 position) => TrySample(position, out var sample) ? sample.ColumnDepth : 0f;

    /// <summary>World Y of the water surface at the point's XZ; <see cref="float.NaN"/> when dry.</summary>
    public float SurfaceHeightAt(Vector3 position) => TrySample(position, out var sample) ? sample.SurfaceHeight : float.NaN;

    /// <summary>Surface velocity in m/s at the point's XZ; zero when dry or still.</summary>
    public Vector3 FlowAt(Vector3 position) => TrySample(position, out var sample) ? sample.Flow : Vector3.Zero;

    /// <summary>How deep the point is under the surface (surface height − Y); ≤ 0 above the water, 0 when dry.</summary>
    public float ImmersionAt(Vector3 position) => TrySample(position, out var sample) ? sample.SurfaceHeight - position.Y : 0f;

    /// <summary>True when the point is more than <paramref name="margin"/> below a water surface.</summary>
    public bool IsUnderwater(Vector3 position, float margin = 0.05f) =>
        TrySample(position, out var sample) && position.Y < sample.SurfaceHeight - margin;

    /// <summary>
    /// A move-speed multiplier for wading: 1 up to <paramref name="start"/> metres of immersion, easing (smoothstep) to
    /// <paramref name="minScale"/> at <paramref name="full"/> metres and beyond.
    /// </summary>
    public static float WadeSpeedScale(float immersion, float start = 0.3f, float full = 1.2f, float minScale = 0.4f)
    {
        if (!(immersion > start))
            return 1f;
        var t = full > start ? Math.Clamp((immersion - start) / (full - start), 0f, 1f) : 1f;
        var s = t * t * (3f - 2f * t);
        return 1f + (minScale - 1f) * s;
    }
}

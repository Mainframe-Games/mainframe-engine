namespace MainframeEngine;

/// <summary>Puts a curve's points on a terrain (G13.6): the editor's Fit to Ground and <see cref="River3D.FitToTerrain"/>.</summary>
public static class CurveTerrainFit
{
    /// <summary>
    /// Moves every point of <paramref name="curve"/> (in the space of <paramref name="global"/>) straight up or down to
    /// <paramref name="terrain"/>'s height plus <paramref name="heightAbove"/> metres (negative: below), keeping its
    /// handles. Replaces <see cref="Curve3D.Points"/> once. False when the terrain has no data or the curve no points.
    /// </summary>
    public static bool Fit(Curve3D curve, in Transform3D global, Terrain3D terrain, float heightAbove)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(terrain);
        if (terrain.Data is null || curve.PointCount == 0)
            return false;
        var inverse = global.AffineInverse();
        var points = (float[])curve.Points.Clone();
        for (var i = 0; i < curve.PointCount; i++)
        {
            var world = global.TransformPoint(curve.GetPointPosition(i));
            var ground = terrain.HeightAt(world.X, world.Z) + heightAbove;
            var local = inverse.TransformPoint(new System.Numerics.Vector3(world.X, ground, world.Z));
            var o = i * Curve3D.FloatsPerPoint;
            points[o] = local.X;
            points[o + 1] = local.Y;
            points[o + 2] = local.Z;
        }

        curve.Points = points;
        return true;
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>Debug icons for lights (Debug builds; the dev overlay toggles them): dots + range circles, spot cones, sun arrows.</summary>
public static class LightGizmos
{
    private static readonly Vector4 White = new(1, 1, 1, 1);

    public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, LightEnvironment lights, float scale)
    {
        var viewProjection = camera.ViewMatrix * camera.ProjectionMatrix;
        foreach (var point in lights.PointLights)
        {
            var color = new Vector4(point.Color, 1f);
            if (!GizmoProjection.TryProject(point.Position, viewProjection, viewport, out var at))
                continue;
            Icon(batch, at, color, scale);
            var faded = color with { W = 0.5f };
            Circle3D(batch, point.Position, Vector3.UnitX, Vector3.UnitY, point.Range, viewProjection, viewport, faded, scale);
            Circle3D(batch, point.Position, Vector3.UnitX, Vector3.UnitZ, point.Range, viewProjection, viewport, faded, scale);
            Circle3D(batch, point.Position, Vector3.UnitY, Vector3.UnitZ, point.Range, viewProjection, viewport, faded, scale);
        }

        foreach (var spot in lights.SpotLights)
        {
            var color = new Vector4(spot.Color, 1f);
            if (!GizmoProjection.TryProject(spot.Position, viewProjection, viewport, out var apex))
                continue;
            Icon(batch, apex, color, scale);
            var perp1 = Perpendicular(spot.Direction);
            var perp2 = Vector3.Normalize(Vector3.Cross(spot.Direction, perp1));
            var baseCentre = spot.Position + spot.Direction * spot.Range;
            var outer = spot.Range * MathF.Tan(spot.OuterConeAngle * MathF.PI / 180f);
            var inner = spot.Range * MathF.Tan(spot.InnerConeAngle * MathF.PI / 180f);
            Circle3D(batch, baseCentre, perp1, perp2, outer, viewProjection, viewport, color, scale);
            Circle3D(batch, baseCentre, perp1, perp2, inner, viewProjection, viewport, color with { W = 0.5f }, scale);
            for (var i = 0; i < 4; i++)
            {
                var angle = i * MathF.PI / 2f;
                var rim = baseCentre + (perp1 * MathF.Cos(angle) + perp2 * MathF.Sin(angle)) * outer;
                if (GizmoProjection.TryProject(rim, viewProjection, viewport, out var rimPixel))
                    batch.Line(apex, rimPixel, color, 1.2f * scale);
            }
        }

        foreach (var sun in lights.DirectionalLights)
        {
            var color = new Vector4(sun.Color, 1f);
            if (!GizmoProjection.TryProject(sun.Position, viewProjection, viewport, out var origin))
                continue;
            // Direction as seen on screen: project a point one unit along the light direction.
            var dir2d = GizmoProjection.TryProject(sun.Position + sun.Direction, viewProjection, viewport, out var ahead) && Vector2.DistanceSquared(ahead, origin) > 1e-6f
                ? Vector2.Normalize(ahead - origin)
                : new Vector2(0, 1);
            batch.Arrow(origin, origin + dir2d * 40f * scale, color, 2f * scale, 10f * scale);
            Sun(batch, origin, color, scale);
        }
    }

    private static void Icon(ScreenGizmoBatch batch, Vector2 at, Vector4 color, float scale)
    {
        batch.FilledCircle(at, 8f * scale, color);
        batch.Circle(at, 10f * scale, White, 1.5f * scale);
    }

    private static void Sun(ScreenGizmoBatch batch, Vector2 center, Vector4 color, float scale)
    {
        batch.FilledCircle(center, 6f * scale, color, 16);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            batch.Line(center + dir * 8f * scale, center + dir * 12f * scale, color, 1.5f * scale);
        }
    }

    private static void Circle3D(ScreenGizmoBatch batch, Vector3 center, Vector3 right, Vector3 up, float radius,
        in Matrix4x4 viewProjection, Vector2 viewport, Vector4 color, float scale, int segments = 32)
    {
        var hasPrevious = false;
        var previous = Vector2.Zero;
        for (var i = 0; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var world = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * radius;
            if (!GizmoProjection.TryProject(world, viewProjection, viewport, out var pixel))
            {
                hasPrevious = false; // break the polyline where a point is behind the camera
                continue;
            }

            if (hasPrevious)
                batch.Line(previous, pixel, color, 1.2f * scale);
            previous = pixel;
            hasPrevious = true;
        }
    }

    private static Vector3 Perpendicular(Vector3 v)
    {
        var other = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, other));
    }
}

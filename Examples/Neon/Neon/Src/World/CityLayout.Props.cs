using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// What stands on and between the towers: roof clutter, street furniture, the crowd, cables and lanterns over the side
/// streets, sky bridges and the light that spills from the shops. All of it is placed from a stream of its own, so the
/// towers and the traffic do not depend on it, and most of it only close to the apartment, which the player never leaves:
/// <see cref="DetailRadius"/> is also where a tower switches to its <c>_Near</c> mesh.
/// </summary>
public sealed partial class CityLayout
{
    /// <summary>Towers closer than this use the kit's near mesh and get the full roof clutter.</summary>
    public const float DetailRadius = 380f;

    /// <summary>Street furniture and the crowd stop this far from the apartment.</summary>
    public const float StreetRadius = 460f;

    /// <summary>Shop light spills on the pavements out to this distance.</summary>
    private const float SpillRadius = 900f;

    private const float RoofCell = 7f;

    private readonly List<CityProp> _props = [];
    private readonly List<CityWalker> _walkers = [];
    private readonly List<CityCable> _cables = [];
    private readonly List<CitySkybridge> _skybridges = [];
    private readonly List<CitySpill> _spills = [];

    /// <summary>
    /// The flat roofs of each archetype (in <see cref="Archetypes"/>' order): centre, size and kit height, as
    /// <c>build_city_kit.py</c> builds them. The last is the top one.
    /// </summary>
    private static readonly (float X, float Z, float W, float D, float Y)[][] Roofs =
    [
        [(0f, 0f, 30f, 18f, 105f), (0f, 0f, 24f, 13f, 110f)],
        [(0f, 0f, 34f, 34f, 70f), (0f, 0f, 26f, 26f, 115.5f), (0f, 0f, 18f, 18f, 150f)],
        [(0f, 0f, 40f, 30f, 21f), (11f, 0f, 14f, 14f, 87.5f), (-11f, 0f, 14f, 14f, 105f)],
        [(0f, 0f, 18f, 18f, 190f)],
        [(0f, 0f, 44f, 36f, 42f)],
        [(0f, 0f, 12f, 30f, 70f), (0f, 0f, 22f, 12f, 85f)],
        [(0f, 0f, 18f, 18f, 130f)],
        [(0f, 0f, 50f, 50f, 17.5f), (0f, 0f, 38f, 38f, 77f)],
    ];

    /// <summary>Roof clutter and street furniture, unstretched kit props (<c>Assets/Art/City/props.gltf</c>).</summary>
    public IReadOnlyList<CityProp> Props => _props;

    /// <summary>The crowd on the pavements under their umbrellas.</summary>
    public IReadOnlyList<CityWalker> Walkers => _walkers;

    /// <summary>Wires over the side streets, some strung with lanterns.</summary>
    public IReadOnlyList<CityCable> Cables => _cables;

    /// <summary>Enclosed bridges between neighbouring towers.</summary>
    public IReadOnlyList<CitySkybridge> Skybridges => _skybridges;

    /// <summary>Pools of coloured light on the ground: in front of the shops and under the street lamps.</summary>
    public IReadOnlyList<CitySpill> Spills => _spills;

    /// <summary>Whether <paramref name="tower"/> uses the kit's near mesh.</summary>
    public static bool IsNear(in TowerPlacement tower) => new Vector2(tower.Position.X, tower.Position.Z).Length() < DetailRadius;

    /// <summary>The neon palette of the city's shaders (their <c>neon(h)</c>).</summary>
    public static Vector3 NeonColor(float h) => (int)((h - MathF.Floor(h)) * 6f) switch
    {
        0 => new Vector3(1f, 0.12f, 0.62f),
        1 => new Vector3(0.1f, 0.85f, 1f),
        2 => new Vector3(0.55f, 0.22f, 1f),
        3 => new Vector3(1f, 0.55f, 0.12f),
        4 => new Vector3(0.35f, 1f, 0.55f),
        _ => new Vector3(1f, 0.18f, 0.2f),
    };

    private void GenerateProps(Random rng)
    {
        foreach (var tower in _towers)
            PlaceRoofProps(tower, rng);
        foreach (var block in _blocks)
            if (block.Center.Length() < StreetRadius)
                PlaceStreet(block, rng);
        PlaceTowerFronts(rng);
        PlaceCables(rng);
        PlaceSkybridges(rng);
    }

    // ── Roofs ─────────────────────────────────────────────────────────────────────────────────────

    private void PlaceRoofProps(in TowerPlacement tower, Random rng)
    {
        var distance = new Vector2(tower.Position.X, tower.Position.Z).Length();
        var share = distance < DetailRadius ? 0.6 : distance < 700f ? 0.3 : 0.1;
        var roofs = Roofs[tower.Archetype];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, tower.Quarter * MathF.PI * 0.5f);
        for (var r = 0; r < roofs.Length; r++)
        {
            var roof = roofs[r];
            var offset = Vector3.Transform(new Vector3(roof.X, 0f, roof.Z), turn) * tower.Scale;
            var centre = tower.Position + offset with { Y = roof.Y * tower.HeightScale };
            var (w, d) = tower.Quarter % 2 == 0 ? (roof.W * tower.Scale, roof.D * tower.Scale) : (roof.D * tower.Scale, roof.W * tower.Scale);
            var custom = new Vector4(tower.Custom.X, tower.Custom.Y, tower.Custom.Z, tower.Custom.W);

            // The top roof may carry one big thing, which keeps the middle clear.
            var clearCentre = false;
            if (r == roofs.Length - 1 && MathF.Min(w, d) >= 12f)
            {
                var roll = rng.NextDouble();
                if (roll < 0.14 && MathF.Min(w, d) >= 16f && centre.Y > -30f)
                {
                    AddProp(CityPropKind.Helipad, centre, rng.Next(4), Vector3.One, custom);
                    clearCentre = true;
                }
                else if (roll < 0.3)
                {
                    AddProp(CityPropKind.Lattice, centre, 0, new Vector3(w * 0.72f / 12f, 1f, d * 0.72f / 12f), custom);
                    clearCentre = true;
                }
                else if (roll < 0.5)
                    AddProp(CityPropKind.Antenna, centre + new Vector3(Range(rng, -0.25f, 0.25f) * w, 0f, Range(rng, -0.25f, 0.25f) * d), 0, Vector3.One, custom);
                else if (roll < 0.53 && distance < 700f)
                {
                    AddProp(CityPropKind.Crane, centre, rng.Next(4), Vector3.One, custom);
                    clearCentre = true;
                }
            }

            var columns = (int)MathF.Floor((w - 2f) / RoofCell);
            var rows = (int)MathF.Floor((d - 2f) / RoofCell);
            for (var i = 0; i < columns; i++)
            {
                for (var j = 0; j < rows; j++)
                {
                    if (rng.NextDouble() >= share)
                        continue;
                    var at = centre + new Vector3((i + 0.5f - columns * 0.5f) * RoofCell, 0f, (j + 0.5f - rows * 0.5f) * RoofCell);
                    if (clearCentre && MathF.Abs(at.X - centre.X) < 8f && MathF.Abs(at.Z - centre.Z) < 8f)
                        continue;
                    AddProp(RoofClutter(rng), at, rng.Next(4), Vector3.One, custom);
                }
            }
        }
    }

    private static CityPropKind RoofClutter(Random rng) => rng.Next(12) switch
    {
        < 4 => CityPropKind.Hvac,
        4 or 5 => CityPropKind.Cooling,
        6 => CityPropKind.Tank,
        7 => CityPropKind.Bulkhead,
        8 => CityPropKind.Dish,
        9 => CityPropKind.Pipes,
        10 => CityPropKind.Solar,
        _ => CityPropKind.LightMast,
    };

    private void AddProp(CityPropKind kind, Vector3 position, int quarter, Vector3 scale, Vector4 custom) =>
        _props.Add(new CityProp(kind, Transform3D.FromTrs(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, quarter * MathF.PI * 0.5f), scale), custom));

    // ── Streets ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lamps, signals, kiosks and the crowd round a block's four pavements.</summary>
    private void PlaceStreet(CityBlock block, Random rng)
    {
        Vector3 Corner(float x, float z) => new(x, GroundY, z);
        (Vector3 Start, Vector3 End)[] sides =
        [
            (Corner(block.MinX, block.MaxZ), Corner(block.MaxX, block.MaxZ)), // +Z edge, walking +X
            (Corner(block.MaxX, block.MaxZ), Corner(block.MaxX, block.MinZ)), // +X edge, walking −Z
            (Corner(block.MaxX, block.MinZ), Corner(block.MinX, block.MinZ)), // −Z edge, walking −X
            (Corner(block.MinX, block.MinZ), Corner(block.MinX, block.MaxZ)), // −X edge, walking +Z
        ];
        foreach (var (start, end) in sides)
        {
            var length = Vector3.Distance(start, end);
            if (length < 12f)
                continue;
            var along = (end - start) / length;
            var outward = Vector3.Cross(along, Vector3.UnitY); // towards the road
            var yaw = MathF.Atan2(outward.X, outward.Z);
            var alongYaw = MathF.Atan2(along.X, along.Z);

            for (var s = 12f; s < length - 6f; s += 26f)
            {
                var lamp = start + along * s + outward * -0.8f;
                _props.Add(new CityProp(CityPropKind.Lamp, Transform3D.FromTrs(lamp, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), Vector3.One), RandomCustom(rng)));
                _spills.Add(new CitySpill(lamp + outward * 2.2f, outward, 18f, 18f, new Vector3(1f, 0.8f, 0.55f) * 0.075f, Radial: true));
            }

            for (var s = Range(rng, 5f, 18f); s < length - 5f; s += Range(rng, 16f, 40f))
            {
                if (rng.NextDouble() >= 0.42)
                    continue;
                var at = start + along * s + outward * -Range(rng, 2.6f, 3.4f);
                _props.Add(new CityProp(CityPropKind.Kiosk, Transform3D.FromTrs(at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), Vector3.One), RandomCustom(rng)));
                _spills.Add(new CitySpill(at + outward * 1.5f, outward, 7f, 7f, NeonColor((float)rng.NextDouble()) * 0.1f + new Vector3(0.12f, 0.09f, 0.06f), Radial: true));
            }

            if (rng.NextDouble() < 0.6)
            {
                var signal = start + along * 1.4f + outward * -0.7f;
                _props.Add(new CityProp(CityPropKind.Signal, Transform3D.FromTrs(signal, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), Vector3.One), RandomCustom(rng)));
            }

            // People both ways along the pavement.
            var count = (int)(length / 5f);
            for (var i = 0; i < count; i++)
            {
                var forward = rng.NextDouble() < 0.5;
                var inset = Range(rng, 1.6f, 5.2f);
                var origin = (forward ? start : end) - outward * inset;
                var speed = Range(rng, 1.1f, 1.7f);
                var heading = forward ? alongYaw : alongYaw + MathF.PI;
                _walkers.Add(new CityWalker(
                    Transform3D.FromTrs(origin, Quaternion.CreateFromAxisAngle(Vector3.UnitY, heading), Vector3.One),
                    new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), length, speed)));
            }
        }
    }

    /// <summary>Awnings over the shops of the near towers and, further out, the shops' light on the pavement.</summary>
    private void PlaceTowerFronts(Random rng)
    {
        foreach (var tower in _towers)
        {
            var distance = new Vector2(tower.Position.X, tower.Position.Z).Length();
            if (distance > SpillRadius)
                continue;
            var arch = Archetypes[tower.Archetype];
            for (var face = 0; face < 4; face++)
            {
                var (centre, normal, width) = tower.Face(face);
                var right = Vector3.Cross(Vector3.UnitY, normal);
                var colour = Vector3.Lerp(new Vector3(1f, 0.72f, 0.42f), NeonColor((float)rng.NextDouble()), 0.6f) * Range(rng, 0.14f, 0.32f);
                _spills.Add(new CitySpill(centre, normal, width * arch.FaceWidth, Range(rng, 7f, 11f), colour, Radial: false));
                if (distance > StreetRadius || arch.FaceWidth < 0.9f || width < 9f)
                    continue;
                var awnings = rng.Next(1, 4);
                var slot = width * 0.9f / awnings;
                for (var a = 0; a < awnings; a++)
                {
                    if (rng.NextDouble() < 0.3)
                        continue;
                    var run = slot * Range(rng, 0.45f, 0.9f);
                    var at = centre + right * ((a + 0.5f) * slot - width * 0.45f) + Vector3.UnitY * Range(rng, 3f, 3.8f) + normal * 0.05f;
                    var yaw = MathF.Atan2(normal.X, normal.Z);
                    _props.Add(new CityProp(CityPropKind.Awning, Transform3D.FromTrs(at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector3(run, 1f, 1f)), RandomCustom(rng)));
                }
            }
        }
    }

    /// <summary>Wires across the side streets every so often, half of them strung with lanterns.</summary>
    private void PlaceCables(Random rng)
    {
        const float Reach = 420f;
        foreach (var street in _northSouth)
        {
            if (street.Width > 28f || MathF.Abs(street.Center) > Reach)
                continue;
            for (var z = -160f; z < Reach; z += Range(rng, 12f, 30f))
            {
                if (OnStreet(z, _eastWest) || rng.NextDouble() < 0.4)
                    continue;
                var half = street.Width * 0.5f + 1f;
                var y = GroundY + Range(rng, 6.5f, 13f);
                AddCable(new Vector3(street.Center - half, y, z), new Vector3(street.Center + half, y + Range(rng, -1.5f, 1.5f), z + Range(rng, -4f, 4f)), rng);
            }
        }

        foreach (var street in _eastWest)
        {
            if (street.Width > 28f || MathF.Abs(street.Center) > Reach)
                continue;
            for (var x = -Reach; x < Reach; x += Range(rng, 12f, 30f))
            {
                if (OnStreet(x, _northSouth) || rng.NextDouble() < 0.4)
                    continue;
                var half = street.Width * 0.5f + 1f;
                var y = GroundY + Range(rng, 6.5f, 13f);
                AddCable(new Vector3(x, y, street.Center - half), new Vector3(x + Range(rng, -4f, 4f), y + Range(rng, -1.5f, 1.5f), street.Center + half), rng);
            }
        }

        static bool OnStreet(float at, List<CityStreet> lines)
        {
            foreach (var line in lines)
                if (MathF.Abs(at - line.Center) < line.Width * 0.5f + 2f)
                    return true;
            return false;
        }
    }

    private void AddCable(Vector3 from, Vector3 to, Random rng)
    {
        var lanterns = rng.NextDouble() < 0.55;
        var hue = lanterns ? (rng.NextDouble() < 0.6 ? 0.91f : 0.58f) : 0f; // red or amber (the palette's)
        _cables.Add(new CityCable(from, to, Range(rng, 0.6f, 1.6f), lanterns, hue));
    }

    /// <summary>Enclosed bridges between neighbouring near towers that face each other across a small gap.</summary>
    private void PlaceSkybridges(Random rng)
    {
        var near = new List<TowerPlacement>();
        foreach (var tower in _towers)
            if (IsNear(tower) && tower.Position.Z > -100f && tower.RoofY - GroundY > 36f)
                near.Add(tower);
        for (var i = 0; i < near.Count; i++)
        {
            for (var j = i + 1; j < near.Count; j++)
            {
                var (a, b) = (near[i], near[j]);
                var (ha, hb) = (a.HalfExtents, b.HalfExtents);
                var delta = b.Position - a.Position;
                var gapX = MathF.Abs(delta.X) - ha.X - hb.X;
                var gapZ = MathF.Abs(delta.Z) - ha.Y - hb.Y;
                var top = MathF.Min(a.RoofY, b.RoofY) - GroundY;
                var height = GroundY + Range(rng, 16f, top * 0.8f);
                var hue = (float)rng.NextDouble();
                var custom = new Vector4((float)rng.NextDouble(), hue, 0.5f, (float)rng.NextDouble());
                if (gapX is > 2f and < 26f && gapZ < -6f && rng.NextDouble() < 0.5)
                {
                    var z = (MathF.Max(a.Position.Z - ha.Y, b.Position.Z - hb.Y) + MathF.Min(a.Position.Z + ha.Y, b.Position.Z + hb.Y)) * 0.5f;
                    var x0 = MathF.Min(a.Position.X, b.Position.X) + (delta.X > 0f ? ha.X : hb.X) - 1f;
                    var x1 = x0 + gapX + 2f;
                    _skybridges.Add(new CitySkybridge(new Vector3(x0, height, z - 1.7f), new Vector3(x1, height + 3.3f, z + 1.7f), custom));
                }
                else if (gapZ is > 2f and < 26f && gapX < -6f && rng.NextDouble() < 0.5)
                {
                    var x = (MathF.Max(a.Position.X - ha.X, b.Position.X - hb.X) + MathF.Min(a.Position.X + ha.X, b.Position.X + hb.X)) * 0.5f;
                    var z0 = MathF.Min(a.Position.Z, b.Position.Z) + (delta.Z > 0f ? ha.Y : hb.Y) - 1f;
                    var z1 = z0 + gapZ + 2f;
                    _skybridges.Add(new CitySkybridge(new Vector3(x - 1.7f, height, z0), new Vector3(x + 1.7f, height + 3.3f, z1), custom));
                }
            }
        }
    }

    private static Vector4 RandomCustom(Random rng) =>
        new((float)rng.NextDouble(), (float)rng.NextDouble(), 0.5f, (float)rng.NextDouble());
}

public enum CityPropKind
{
    Hvac,
    Tank,
    Dish,
    Antenna,
    Bulkhead,
    Cooling,
    Pipes,
    Solar,
    LightMast,
    Helipad,
    Lattice,
    Crane,
    Lamp,
    Signal,
    Kiosk,
    Awning,
}

/// <summary>A prop: <see cref="Kind"/>'s kit mesh at <see cref="Transform"/>; <see cref="Custom"/> is the facade shader's <c>INSTANCE_CUSTOM</c>.</summary>
public readonly record struct CityProp(CityPropKind Kind, Transform3D Transform, Vector4 Custom);

/// <summary>Someone walking +Z from <see cref="Transform"/>; <see cref="Custom"/> is crowd.mfshader's (phase, hue, span, speed).</summary>
public readonly record struct CityWalker(Transform3D Transform, Vector4 Custom);

/// <summary>A wire from <see cref="From"/> to <see cref="To"/> sagging <see cref="Sag"/> metres, strung with lanterns of <see cref="Hue"/> or bare.</summary>
public readonly record struct CityCable(Vector3 From, Vector3 To, float Sag, bool Lanterns, float Hue);

/// <summary>An enclosed bridge filling the box <see cref="Min"/>..<see cref="Max"/>.</summary>
public readonly record struct CitySkybridge(Vector3 Min, Vector3 Max, Vector4 Custom);

/// <summary>
/// A pool of light on the ground: from a wall at <see cref="Center"/> out along <see cref="Normal"/> by <see cref="Reach"/>
/// over <see cref="Width"/> metres, or, <see cref="Radial"/>, a disc <see cref="Width"/> across round the point.
/// </summary>
public readonly record struct CitySpill(Vector3 Center, Vector3 Normal, float Width, float Reach, Vector3 Color, bool Radial);

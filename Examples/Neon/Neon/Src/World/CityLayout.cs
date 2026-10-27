using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The city around the apartment's tower as data, generated from a seed (no nodes, no GPU, no files: tests and tools use it
/// as it is, <see cref="NeonCity"/> builds the visuals from it and phase N4's traffic follows its loops).
/// </summary>
/// <remarks>
/// <para><b>Space.</b> Everything is in the scene's space, which is the apartment's glTF space: metres, +Y up, the window
/// wall faces +Z (south), the apartment's floor is y = 0. The street level is <see cref="GroundY"/> (−70 m: the apartment
/// is on the 20th floor of a 3.5 m-per-floor tower).</para>
/// <para><b>Streets.</b> A grid of <see cref="EastWest"/> streets (along X, at a constant z) and <see cref="NorthSouth"/>
/// streets (along Z, at a constant x) with seeded spacing and widths. The lines next to the home tower are fixed: a 40 m
/// boulevard runs in front of the window wall (<see cref="BoulevardZ"/>), with the elevated rail over it. The
/// <see cref="Blocks"/> are the rectangles between the lines within <see cref="NearRadius"/>; their towers
/// (<see cref="Towers"/>) are one of eight <see cref="Archetypes"/> each, as built by <c>Tools/city/build_city_kit.py</c>.</para>
/// <para><b>Loops</b> (for traffic): closed polylines, the first point not repeated at the end, densified so
/// <see cref="Curve3D.SetSmoothPoints"/> keeps the straights straight (<see cref="CityLoop.CreateCurve"/>).
/// <see cref="StreetLoops"/> run on street centre lines at the road surface (lanes are offsets across
/// <see cref="CityLoop.Width"/>), <see cref="SkyLanes"/> over street centre lines 40–120 m above the street (between the
/// towers), <see cref="RailLoop"/> 22 m above the street and <see cref="AirshipLoops"/> over the tallest towers.</para>
/// </remarks>
public sealed class CityLayout
{
    public const int DefaultSeed = 2077;

    /// <summary>The street level (the city's ground), in scene space.</summary>
    public const float GroundY = -70f;

    /// <summary>Floor-to-floor height of every tower (the apartment's floor is floor 20).</summary>
    public const float StoreyHeight = 3.5f;

    /// <summary>Blocks whose centre is within this distance of the apartment get real towers.</summary>
    public const float NearRadius = 1000f;

    /// <summary>The street grid's half extent (lines beyond it are not generated).</summary>
    public const float GridExtent = 1100f;

    /// <summary>The far ring's skyline cylinders (radii, metres): <c>skyline.gdshader</c>'s cards beyond the near ring.</summary>
    public static readonly float[] SkylineRadii = [1250f, 1550f, 1880f];

    /// <summary>The boulevard in front of the window wall: its centre line (z) and width.</summary>
    public const float BoulevardZ = 38f;

    public const float BoulevardWidth = 40f;

    /// <summary>No tower top is higher than this above the street (the airships fly over them).</summary>
    public const float MaxTowerTop = 230f;

    /// <summary>The elevated rail's height above the street.</summary>
    public const float RailHeight = 22f;

    /// <summary>
    /// The apartment's own tower (scene space): the apartment is a unit in its front face, whose hole is
    /// <see cref="HomeHoleMin"/>..<see cref="HomeHoleMax"/> (x, y) at z = <see cref="HomeTower"/>.Max.Z.
    /// </summary>
    public static readonly Aabb HomeTower = new(new Vector3(-21f, GroundY, -20f), new Vector3(21f, 38.5f, 5.62f));

    /// <summary>The front face's hole round the apartment's window wall and slabs (x, y).</summary>
    public static readonly Vector2 HomeHoleMin = new(-7.2f, -0.35f);

    public static readonly Vector2 HomeHoleMax = new(7.2f, 6.3f);

    /// <summary>
    /// The kit's towers (<c>Assets/Art/City/towers.gltf</c>, one mesh each, base centred on the origin, front along +Z):
    /// footprint, roof height (antennas and clutter go above it), the height up to which the whole footprint is a flat facade
    /// and how much of a face's width is flat (signs go there). <c>build_city_kit.py</c> builds them to these numbers.
    /// </summary>
    public static readonly TowerArchetype[] Archetypes =
    [
        new("Tower_Slab", 30f, 18f, 110f, 110f, 0.9f),
        new("Tower_Setback", 34f, 34f, 150f, 70f, 0.9f),
        new("Tower_Twin", 40f, 30f, 105f, 22f, 0.9f),
        new("Tower_Needle", 18f, 18f, 190f, 190f, 0.9f),
        new("Tower_Block", 44f, 36f, 42f, 42f, 0.9f),
        new("Tower_Cross", 30f, 30f, 85f, 85f, 0.4f),
        new("Tower_Octagon", 26f, 26f, 130f, 130f, 0.4f),
        new("Tower_Arcology", 50f, 50f, 95f, 18f, 0.9f),
    ];

    /// <summary>The index of the low, wide block in <see cref="Archetypes"/> (also the filler for small lots).</summary>
    public const int FillerArchetype = 4;

    private const float Setback = 6f; // towers keep this far from the block's edge (sky-lane and rail corners cut it)

    private readonly List<CityStreet> _eastWest = [];
    private readonly List<CityStreet> _northSouth = [];
    private readonly List<CityBlock> _blocks = [];
    private readonly List<TowerPlacement> _towers = [];
    private readonly List<CitySign> _signs = [];
    private readonly List<CityHologram> _holograms = [];
    private readonly List<CityLoop> _streetLoops = [];
    private readonly List<CityLoop> _skyLanes = [];
    private readonly List<CityLoop> _airshipLoops = [];

    private CityLayout(int seed)
    {
        Seed = seed;
        var rng = new Random(seed);
        GenerateStreets(rng);
        GenerateBlocks(rng);
        GenerateSigns(rng);
        GenerateHolograms();
        GenerateLoops(rng);
        RailLoop ??= new CityLoop("Rail", CityLoopKind.Rail, [], 0f, 0f);
    }

    public int Seed { get; }

    /// <summary>Streets along X (east–west), by their centre line's z, ascending.</summary>
    public IReadOnlyList<CityStreet> EastWest => _eastWest;

    /// <summary>Streets along Z (north–south), by their centre line's x, ascending.</summary>
    public IReadOnlyList<CityStreet> NorthSouth => _northSouth;

    /// <summary>The blocks between the streets within <see cref="NearRadius"/> (the home tower's and the plaza's included).</summary>
    public IReadOnlyList<CityBlock> Blocks => _blocks;

    /// <summary>The near ring's towers (not the home tower).</summary>
    public IReadOnlyList<TowerPlacement> Towers => _towers;

    /// <summary>Billboards, signboards and blade signs on the towers' faces.</summary>
    public IReadOnlyList<CitySign> Signs => _signs;

    /// <summary>The giant holograms: a koi over the plaza across the boulevard, a dancer and a turning logo on rooftops.</summary>
    public IReadOnlyList<CityHologram> Holograms => _holograms;

    /// <summary>Ground traffic: closed loops round groups of blocks on street centre lines (the boulevard's first).</summary>
    public IReadOnlyList<CityLoop> StreetLoops => _streetLoops;

    /// <summary>Flying traffic: closed loops over street centre lines, 40–120 m above the street (one passes the balcony).</summary>
    public IReadOnlyList<CityLoop> SkyLanes => _skyLanes;

    /// <summary>The elevated train: a closed loop <see cref="RailHeight"/> above the street, along the boulevard.</summary>
    public CityLoop RailLoop { get; private set; }

    /// <summary>Two slow airship loops over the tallest towers.</summary>
    public IReadOnlyList<CityLoop> AirshipLoops => _airshipLoops;

    /// <summary>The block the apartment's tower stands on.</summary>
    public CityBlock HomeBlock => _blocks.First(b => b.Kind == CityBlockKind.Home);

    /// <summary>The street grid's extent in scene space (the ground, the streets and every tower are inside it).</summary>
    public static Aabb Bounds => new(new Vector3(-GridExtent, GroundY, -GridExtent), new Vector3(GridExtent, GroundY + MaxTowerTop + 120f, GridExtent));

    /// <summary>The layout for <paramref name="seed"/>: the same seed always gives the same city.</summary>
    public static CityLayout Generate(int seed = DefaultSeed) => new(seed);

    // ── Streets ───────────────────────────────────────────────────────────────────────────────────

    private void GenerateStreets(Random rng)
    {
        // Fixed round the home tower: side streets, a back street and the boulevard in front.
        _northSouth.Add(new CityStreet(-36f, 16f));
        _northSouth.Add(new CityStreet(36f, 16f));
        _eastWest.Add(new CityStreet(-40f, 16f));
        _eastWest.Add(new CityStreet(BoulevardZ, BoulevardWidth));
        Extend(_northSouth, rng, 36f, 16f, +1);
        Extend(_northSouth, rng, -36f, 16f, -1);
        Extend(_eastWest, rng, BoulevardZ, BoulevardWidth, +1);
        Extend(_eastWest, rng, -40f, 16f, -1);
        _northSouth.Sort((a, b) => a.Center.CompareTo(b.Center));
        _eastWest.Sort((a, b) => a.Center.CompareTo(b.Center));

        static void Extend(List<CityStreet> lines, Random rng, float from, float width, int direction)
        {
            var center = from;
            var lastWidth = width;
            while (true)
            {
                var spacing = Range(rng, 72f, 112f);
                // Every third or so street is an avenue.
                var next = rng.NextDouble() < 0.3 ? Range(rng, 22f, 28f) : Range(rng, 13f, 19f);
                center += direction * (spacing + (lastWidth + next) * 0.25f);
                if (MathF.Abs(center) > GridExtent)
                    return;
                lines.Add(new CityStreet(center, next));
                lastWidth = next;
            }
        }
    }

    // ── Blocks and towers ─────────────────────────────────────────────────────────────────────────

    private void GenerateBlocks(Random rng)
    {
        for (var i = 0; i + 1 < _eastWest.Count; i++)
        {
            for (var j = 0; j + 1 < _northSouth.Count; j++)
            {
                var minX = _northSouth[j].Center + _northSouth[j].Width * 0.5f;
                var maxX = _northSouth[j + 1].Center - _northSouth[j + 1].Width * 0.5f;
                var minZ = _eastWest[i].Center + _eastWest[i].Width * 0.5f;
                var maxZ = _eastWest[i + 1].Center - _eastWest[i + 1].Width * 0.5f;
                var centre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
                if (centre.Length() > NearRadius)
                    continue;
                var kind = centre.X is > -40f and < 40f && centre.Y is > -40f and < BoulevardZ ? CityBlockKind.Home
                    : centre.X is > -40f and < 40f && minZ > BoulevardZ && minZ < BoulevardZ + 40f ? CityBlockKind.Plaza
                    : CityBlockKind.Towers;
                var block = new CityBlock(minX, maxX, minZ, maxZ, kind);
                _blocks.Add(block);
                if (kind == CityBlockKind.Towers)
                    PlaceTowers(block, rng);
                else if (kind == CityBlockKind.Plaza)
                    PlacePlaza(block, rng);
            }
        }
    }

    private void PlaceTowers(CityBlock block, Random rng)
    {
        var w = block.MaxX - block.MinX - 2f * Setback;
        var d = block.MaxZ - block.MinZ - 2f * Setback;
        if (w < 12f || d < 12f)
            return;
        var x0 = block.MinX + Setback;
        var z0 = block.MinZ + Setback;
        var roll = rng.NextDouble();
        if (w >= 40f && d >= 40f && roll < 0.3)
            PlaceTower(x0, z0, w, d, rng);
        else if (w >= 2f * 18f && (w >= d || d < 2f * 18f))
        {
            var split = w * Range(rng, 0.4f, 0.6f);
            PlaceHalf(x0, z0, split - 2f, d, rng);
            PlaceHalf(x0 + split + 2f, z0, w - split - 2f, d, rng);
        }
        else if (d >= 2f * 18f)
        {
            var split = d * Range(rng, 0.4f, 0.6f);
            PlaceHalf(x0, z0, w, split - 2f, rng);
            PlaceHalf(x0, z0 + split + 2f, w, d - split - 2f, rng);
        }
        else
            PlaceTower(x0, z0, w, d, rng);

        void PlaceHalf(float x, float z, float lw, float ld, Random r)
        {
            // A long half splits again across (quarters), so narrow lots get slender towers.
            if (lw >= 2f * 18f && lw > ld * 1.6f)
            {
                var s = lw * 0.5f;
                PlaceTower(x, z, s - 1.5f, ld, r);
                PlaceTower(x + s + 1.5f, z, lw - s - 1.5f, ld, r);
            }
            else if (ld >= 2f * 18f && ld > lw * 1.6f)
            {
                var s = ld * 0.5f;
                PlaceTower(x, z, lw, s - 1.5f, r);
                PlaceTower(x, z + s + 1.5f, lw, ld - s - 1.5f, r);
            }
            else
                PlaceTower(x, z, lw, ld, r);
        }
    }

    /// <summary>The block across the boulevard: low podiums round an open plaza (the koi hologram swims over it).</summary>
    private void PlacePlaza(CityBlock block, Random rng)
    {
        var depth = MathF.Min(24f, (block.MaxZ - block.MinZ) * 0.3f);
        // Two low blocks at the plaza's back corners; the front stays open towards the apartment.
        var w = (block.MaxX - block.MinX - 2f * Setback) * 0.32f;
        PlaceTower(block.MinX + Setback, block.MaxZ - Setback - depth, w, depth, rng, lowOnly: true);
        PlaceTower(block.MaxX - Setback - w, block.MaxZ - Setback - depth, w, depth, rng, lowOnly: true);
    }

    /// <summary>A tower in the lot (x, z)..(x + lw, z + ld): an archetype that fits, turned a quarter turn or not, scaled.</summary>
    private void PlaceTower(float x, float z, float lw, float ld, Random rng, bool lowOnly = false)
    {
        if (lw < 10f || ld < 10f)
            return;
        var centre = new Vector2(x + lw * 0.5f, z + ld * 0.5f);
        var distance = centre.Length();

        // Candidates: archetypes whose footprint fits at 0.75 scale or more, either way round.
        Span<int> fits = stackalloc int[Archetypes.Length * 2];
        var count = 0;
        for (var a = 0; a < Archetypes.Length; a++)
        {
            if (lowOnly && a != FillerArchetype)
                continue;
            var arch = Archetypes[a];
            for (var turn = 0; turn < 2; turn++)
            {
                var (fw, fd) = turn == 0 ? (arch.Width, arch.Depth) : (arch.Depth, arch.Width);
                if (MathF.Min(lw / fw, ld / fd) >= 0.75f)
                    fits[count++] = a * 2 + turn;
            }
        }

        int archetype, quarter;
        float scale;
        if (count == 0)
        {
            // A small lot: the low block, scaled down to fit.
            archetype = FillerArchetype;
            quarter = lw >= ld ? 0 : 1;
            var arch = Archetypes[archetype];
            var (fw, fd) = quarter % 2 == 0 ? (arch.Width, arch.Depth) : (arch.Depth, arch.Width);
            scale = MathF.Min(lw / fw, ld / fd);
            if (scale < 0.3f)
                return;
        }
        else
        {
            var pick = fits[rng.Next(count)];
            archetype = pick / 2;
            quarter = pick % 2 + (rng.NextDouble() < 0.5 ? 2 : 0);
            var arch = Archetypes[archetype];
            var (fw, fd) = quarter % 2 == 0 ? (arch.Width, arch.Depth) : (arch.Depth, arch.Width);
            var maxScale = MathF.Min(1.15f, MathF.Min(lw / fw, ld / fd));
            scale = Range(rng, MathF.Min(0.8f, maxScale), maxScale);
        }

        var archetypeInfo = Archetypes[archetype];
        // Height: lower close to the apartment (the view looks across roofs into the streets), taller downtown (ahead).
        var near = Math.Clamp((distance - 120f) / 500f, 0f, 1f);
        var downtown = MathF.Exp(-Vector2.DistanceSquared(centre, new Vector2(-80f, 620f)) / (2f * 380f * 380f));
        var heightScale = Range(rng, 0.45f, 0.85f) + near * Range(rng, 0.1f, 0.5f) + downtown * Range(rng, 0.2f, 0.7f);
        if (lowOnly)
            heightScale = Range(rng, 0.3f, 0.5f);
        else if (archetype == FillerArchetype)
            heightScale = Range(rng, 0.4f, 1.1f);
        heightScale = MathF.Min(heightScale, MaxTowerTop / archetypeInfo.Height);

        // Jitter in the lot's slack.
        var (ew, ed) = quarter % 2 == 0 ? (archetypeInfo.Width * scale, archetypeInfo.Depth * scale) : (archetypeInfo.Depth * scale, archetypeInfo.Width * scale);
        var position = new Vector3(
            centre.X + Range(rng, -0.5f, 0.5f) * MathF.Max(0f, lw - ew),
            GroundY,
            centre.Y + Range(rng, -0.5f, 0.5f) * MathF.Max(0f, ld - ed));

        var custom = new Vector4(
            (float)rng.NextDouble(),                     // seed: the window pattern
            (float)rng.NextDouble(),                     // hue: the accent colour
            Range(rng, 0.25f, 0.8f),                     // the fraction of lit windows
            (float)rng.NextDouble());                    // style: column width, bands, curtain wall
        _towers.Add(new TowerPlacement(archetype, position, quarter, scale, heightScale, custom));
    }

    // ── Signs and holograms ───────────────────────────────────────────────────────────────────────

    private void GenerateSigns(Random rng)
    {
        foreach (var tower in _towers)
        {
            var arch = Archetypes[tower.Archetype];
            var distance = new Vector2(tower.Position.X, tower.Position.Z).Length();
            if (distance > 800f || tower.Position.Z < -120f)
                continue;
            var faceTop = arch.FaceTop * tower.HeightScale;
            for (var face = 0; face < 4; face++)
            {
                var (centre, normal, width) = tower.Face(face);
                if (centre.Z < -60f && MathF.Abs(centre.X) < 120f)
                    continue; // behind the home tower: never seen
                var usable = width * arch.FaceWidth;
                var right = Vector3.Cross(Vector3.UnitY, normal);
                var basis = new Basis(right, Vector3.UnitY, normal);

                // A billboard (an animated screen) on some faces, high enough to be seen over the street.
                if (rng.NextDouble() < 0.22 && usable >= 10f && faceTop >= 24f)
                {
                    var bw = MathF.Min(usable * 0.9f, Range(rng, 12f, 26f));
                    var bh = bw * Range(rng, 0.5f, 0.62f);
                    var top = MathF.Min(faceTop - 2f, Range(rng, 20f, 70f) + bh);
                    if (top - bh > 8f)
                    {
                        var origin = centre + right * Range(rng, -0.5f, 0.5f) * (usable - bw) + Vector3.UnitY * (top - bh * 0.5f) + normal * 0.5f;
                        _signs.Add(new CitySign(CitySignKind.Billboard, new Transform3D(Scaled(basis, new Vector3(bw, bh, 1f)), origin),
                            new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), Range(rng, 0.8f, 1.2f), rng.Next(4))));
                    }
                }

                // Blade signs: vertical neon words sticking out of the facade at street level, two or three per face.
                var blades = rng.NextDouble() < 0.75 ? rng.Next(1, 4) : 0;
                for (var b = 0; b < blades && usable > 6f; b++)
                {
                    var h = Range(rng, 6f, 11f);
                    var bottom = Range(rng, 5f, 18f);
                    if (bottom + h > faceTop - 1f)
                        continue;
                    var along = Range(rng, -0.48f, 0.48f) * usable;
                    var origin = centre + right * along + Vector3.UnitY * (bottom + h * 0.5f) + normal * 2.4f;
                    // The blade's own +Z faces along the street (its quad's two sides are lit).
                    var bladeBasis = new Basis(normal, Vector3.UnitY, -right);
                    _signs.Add(new CitySign(CitySignKind.Blade, new Transform3D(Scaled(bladeBasis, new Vector3(1.5f, h, 1f)), origin),
                        new Vector4(rng.Next(CitySign.VerticalWords), (float)rng.NextDouble(), Range(rng, 0.7f, 1.3f), (float)rng.NextDouble())));
                }

                // A tall blade high on the facade on some faces: the big vertical names seen over the roofs.
                if (rng.NextDouble() < 0.2 && faceTop > 40f && usable > 8f)
                {
                    var h = Range(rng, 14f, 28f);
                    var bottom = Range(rng, 12f, MathF.Max(12f, MathF.Min(60f, faceTop - h - 3f)));
                    if (bottom + h < faceTop - 1f)
                    {
                        var origin = centre + right * Range(rng, -0.45f, 0.45f) * usable + Vector3.UnitY * (bottom + h * 0.5f) + normal * 3.6f;
                        var bladeBasis = new Basis(normal, Vector3.UnitY, -right);
                        _signs.Add(new CitySign(CitySignKind.Blade, new Transform3D(Scaled(bladeBasis, new Vector3(2.4f, h, 1f)), origin),
                            new Vector4(rng.Next(CitySign.VerticalWords), (float)rng.NextDouble(), Range(rng, 1.1f, 1.6f), (float)rng.NextDouble())));
                    }
                }

                // A signboard (a horizontal word) over the ground floor's shopfront.
                if (rng.NextDouble() < 0.45 && usable >= 8f)
                {
                    var sw = MathF.Min(usable * 0.8f, Range(rng, 7f, 14f));
                    var origin = centre + right * Range(rng, -0.5f, 0.5f) * (usable - sw) + Vector3.UnitY * Range(rng, 4.2f, 6.5f) + normal * 0.35f;
                    _signs.Add(new CitySign(CitySignKind.Board, new Transform3D(Scaled(basis, new Vector3(sw, sw * 0.25f, 1f)), origin),
                        new Vector4(rng.Next(CitySign.HorizontalWords), (float)rng.NextDouble(), Range(rng, 0.7f, 1.3f), (float)rng.NextDouble())));
                }
            }
        }
    }

    private void GenerateHolograms()
    {
        // The koi swims a circle over the plaza across the boulevard, a little above the apartment's eye level.
        var plaza = _blocks.FirstOrDefault(b => b.Kind == CityBlockKind.Plaza);
        var koiCentre = plaza is null ? new Vector3(0f, 8f, 110f) : new Vector3((plaza.MinX + plaza.MaxX) * 0.5f, 8f, MathF.Min(plaza.MinZ + 45f, plaza.MaxZ));
        _holograms.Add(new CityHologram(CityHologramKind.Koi, koiCentre, 34f, 0f));

        // The dancer on the lowest roof ahead and to the right, facing the apartment; the logo over a tower ahead-left whose
        // roof is a little above the apartment (so the logo turns in the window's view).
        TowerPlacement? low = null, tall = null;
        foreach (var t in _towers)
        {
            var p = new Vector2(t.Position.X, t.Position.Z);
            var d = p.Length();
            if (p.X > 60f && p.Y > 140f && d is > 180f and < 420f && t.Archetype != 3 && (low is null || t.RoofY < low.Value.RoofY))
                low = t;
            if (p.X < -60f && p.Y > 200f && d is > 250f and < 600f && t.RoofY > 0f && (tall is null || MathF.Abs(t.RoofY - 40f) < MathF.Abs(tall.Value.RoofY - 40f)))
                tall = t;
        }

        if (low is { } dancer)
        {
            var yaw = MathF.Atan2(-dancer.Position.X, -dancer.Position.Z); // face the apartment
            _holograms.Add(new CityHologram(CityHologramKind.Dancer, dancer.Position with { Y = dancer.RoofY + 1f }, 64f, yaw));
        }

        if (tall is { } logo)
            _holograms.Add(new CityHologram(CityHologramKind.Logo, logo.Position with { Y = logo.RoofY + 26f }, 40f, 0f));
    }

    // ── Loops ─────────────────────────────────────────────────────────────────────────────────────

    private void GenerateLoops(Random rng)
    {
        var road = GroundY + 0.05f;
        var front = IndexOf(_eastWest, BoulevardZ);
        var west = IndexOf(_northSouth, -36f);
        var east = IndexOf(_northSouth, 36f);

        // The boulevard's loop first: along the boulevard in front, a block or two each way and back two streets ahead.
        AddStreetLoop("Street_Boulevard", Clamp(west - 2, _northSouth), Clamp(east + 2, _northSouth), front, Clamp(front + 2, _eastWest), road, 14f);

        // Loops round groups of blocks across the visible half of the city (ahead of the window wall and to the sides).
        var attempts = 0;
        while (_streetLoops.Count < 8 && attempts++ < 200)
        {
            var j0 = rng.Next(_northSouth.Count - 1);
            var j1 = Math.Min(_northSouth.Count - 1, j0 + rng.Next(2, 6));
            var i0 = rng.Next(front, _eastWest.Count - 1);
            var i1 = Math.Min(_eastWest.Count - 1, i0 + rng.Next(2, 6));
            if (!WithinNear(j0, j1, i0, i1, 850f))
                continue;
            AddStreetLoop($"Street_{_streetLoops.Count}", j0, j1, i0, i1, road, Range(rng, 11f, 15f));
        }

        // Sky lanes over the street centre lines; the first passes the balcony along the boulevard at eye level.
        AddSkyLane("Sky_Boulevard", Clamp(west - 1, _northSouth), Clamp(east + 3, _northSouth), front, Clamp(front + 3, _eastWest), GroundY + 74f, 24f);
        attempts = 0;
        float[] heights = [45f, 92f, 118f];
        while (_skyLanes.Count < 4 && attempts++ < 200)
        {
            var j0 = rng.Next(_northSouth.Count - 1);
            var j1 = Math.Min(_northSouth.Count - 1, j0 + rng.Next(3, 8));
            var i0 = rng.Next(Math.Max(0, front - 1), _eastWest.Count - 1);
            var i1 = Math.Min(_eastWest.Count - 1, i0 + rng.Next(3, 8));
            if (!WithinNear(j0, j1, i0, i1, 900f))
                continue;
            AddSkyLane($"Sky_{_skyLanes.Count}", j0, j1, i0, i1, GroundY + heights[_skyLanes.Count - 1], Range(rng, 20f, 30f));
        }

        // The elevated rail: along the boulevard over its centre line, round a wide rectangle ahead.
        var rail = Rectangle(_northSouth[Clamp(west - 3, _northSouth)].Center, _northSouth[Clamp(east + 4, _northSouth)].Center,
            BoulevardZ, _eastWest[Clamp(front + 4, _eastWest)].Center, GroundY + RailHeight, 14f);
        RailLoop = new CityLoop("Rail", CityLoopKind.Rail, rail, 16f, 6f);

        // The airships: slow ellipses over the tallest towers, crossing the view from the window.
        _airshipLoops.Add(new CityLoop("Airship_0", CityLoopKind.Airship, Ellipse(new Vector3(-120f, GroundY + MaxTowerTop + 60f, 680f), 600f, 360f, 24), 9f, 0f));
        _airshipLoops.Add(new CityLoop("Airship_1", CityLoopKind.Airship, Ellipse(new Vector3(260f, GroundY + MaxTowerTop + 90f, 800f), 500f, 260f, 24), 7f, 0f));
    }

    private void AddStreetLoop(string name, int j0, int j1, int i0, int i1, float y, float speed)
    {
        if (j1 <= j0 || i1 <= i0)
            return;
        var width = MathF.Min(MathF.Min(_northSouth[j0].Width, _northSouth[j1].Width), MathF.Min(_eastWest[i0].Width, _eastWest[i1].Width));
        var points = Rectangle(_northSouth[j0].Center, _northSouth[j1].Center, _eastWest[i0].Center, _eastWest[i1].Center, y, width * 0.45f);
        _streetLoops.Add(new CityLoop(name, CityLoopKind.Street, points, speed, width));
    }

    private void AddSkyLane(string name, int j0, int j1, int i0, int i1, float y, float speed)
    {
        if (j1 <= j0 || i1 <= i0)
            return;
        var width = MathF.Min(MathF.Min(_northSouth[j0].Width, _northSouth[j1].Width), MathF.Min(_eastWest[i0].Width, _eastWest[i1].Width));
        var points = Rectangle(_northSouth[j0].Center, _northSouth[j1].Center, _eastWest[i0].Center, _eastWest[i1].Center, y, width * 0.5f + Setback);
        _skyLanes.Add(new CityLoop(name, CityLoopKind.SkyLane, points, speed, width));
    }

    private bool WithinNear(int j0, int j1, int i0, int i1, float radius)
    {
        foreach (var x in (ReadOnlySpan<float>)[_northSouth[j0].Center, _northSouth[j1].Center])
            foreach (var z in (ReadOnlySpan<float>)[_eastWest[i0].Center, _eastWest[i1].Center])
                if (new Vector2(x, z).Length() > radius)
                    return false;
        return j1 > j0 && i1 > i0;
    }

    private static int IndexOf(List<CityStreet> lines, float centre) => lines.FindIndex(l => l.Center == centre);

    private static int Clamp(int index, List<CityStreet> lines) => Math.Clamp(index, 0, lines.Count - 1);

    /// <summary>
    /// A closed rectangle x0..x1 × z0..z1 at height y, counter-clockwise seen from above, its corners rounded with
    /// <paramref name="radius"/> and its sides densified every 40 m or less.
    /// </summary>
    internal static Vector3[] Rectangle(float x0, float x1, float z0, float z1, float y, float radius)
    {
        radius = MathF.Min(radius, MathF.Min(x1 - x0, z1 - z0) * 0.5f);
        var points = new List<Vector3>();
        // Corners in order (seen from above with +X right and +Z down the screen: counter-clockwise is x0z1 → x1z1 → x1z0 → x0z0).
        Vector2[] corners = [new(x0, z1), new(x1, z1), new(x1, z0), new(x0, z0)];
        for (var c = 0; c < 4; c++)
        {
            var corner = corners[c];
            var next = corners[(c + 1) % 4];
            var previous = corners[(c + 3) % 4];
            var inDir = Vector2.Normalize(corner - previous);
            var outDir = Vector2.Normalize(next - corner);
            var start = corner - inDir * radius;
            var end = corner + outDir * radius;
            // The quarter arc from start to end round the corner's inside.
            var centre = start + outDir * radius;
            for (var k = 0; k <= 3; k++)
            {
                var t = k / 3f;
                var a = Vector2.Normalize(Vector2.Lerp(start - centre, end - centre, t)) * radius;
                var p = centre + a;
                points.Add(new Vector3(p.X, y, p.Y));
            }

            // The straight to the next corner's arc.
            var straightEnd = next - outDir * radius;
            var length = Vector2.Distance(end, straightEnd);
            var steps = (int)MathF.Ceiling(length / 40f);
            for (var s = 1; s < steps; s++)
            {
                var p = Vector2.Lerp(end, straightEnd, s / (float)steps);
                points.Add(new Vector3(p.X, y, p.Y));
            }
        }

        return [.. points];
    }

    internal static Vector3[] Ellipse(Vector3 centre, float radiusX, float radiusZ, int count)
    {
        var points = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var a = i * MathF.Tau / count;
            points[i] = centre + new Vector3(MathF.Cos(a) * radiusX, MathF.Sin(a * 2f) * 6f, MathF.Sin(a) * radiusZ);
        }

        return points;
    }

    private static Basis Scaled(Basis basis, Vector3 scale) => new(basis.X * scale.X, basis.Y * scale.Y, basis.Z * scale.Z);

    private static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);
}

/// <summary>A street's centre line (z for <see cref="CityLayout.EastWest"/>, x for <see cref="CityLayout.NorthSouth"/>) and width.</summary>
public readonly record struct CityStreet(float Center, float Width);

public enum CityBlockKind
{
    /// <summary>Towers on lots.</summary>
    Towers,

    /// <summary>The apartment's own tower (<see cref="CityLayout.HomeTower"/>).</summary>
    Home,

    /// <summary>The open plaza across the boulevard (low podiums; the koi swims over it).</summary>
    Plaza,
}

/// <summary>A block between four streets (scene space, at <see cref="CityLayout.GroundY"/>).</summary>
public sealed record CityBlock(float MinX, float MaxX, float MinZ, float MaxZ, CityBlockKind Kind)
{
    public Vector2 Center => new((MinX + MaxX) * 0.5f, (MinZ + MaxZ) * 0.5f);
}

/// <summary>A kit tower: footprint along X and Z, roof height, flat facade height and the flat share of a face's width.</summary>
public readonly record struct TowerArchetype(string Name, float Width, float Depth, float Height, float FaceTop, float FaceWidth);

/// <summary>
/// A tower: <see cref="CityLayout.Archetypes"/>[<see cref="Archetype"/>] at <see cref="Position"/> (its base centre),
/// turned <see cref="Quarter"/> quarter turns about +Y, scaled by <see cref="Scale"/> across and
/// <see cref="HeightScale"/> up. <see cref="Custom"/> is the facade shader's <c>INSTANCE_CUSTOM</c>: (window seed, accent
/// hue, lit fraction, style), each 0..1.
/// </summary>
public readonly record struct TowerPlacement(int Archetype, Vector3 Position, int Quarter, float Scale, float HeightScale, Vector4 Custom)
{
    public Transform3D Transform =>
        Transform3D.FromTrs(Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter * MathF.PI * 0.5f), new Vector3(Scale, HeightScale, Scale));

    /// <summary>The roof's height (scene space; antennas and clutter go higher).</summary>
    public float RoofY => Position.Y + CityLayout.Archetypes[Archetype].Height * HeightScale;

    /// <summary>The footprint's half extents along X and Z after the turn.</summary>
    public Vector2 HalfExtents
    {
        get
        {
            var a = CityLayout.Archetypes[Archetype];
            return Quarter % 2 == 0 ? new Vector2(a.Width, a.Depth) * (Scale * 0.5f) : new Vector2(a.Depth, a.Width) * (Scale * 0.5f);
        }
    }

    /// <summary>Face 0..3 (+X, −X, +Z, −Z): its centre at the base, outward normal and width.</summary>
    public (Vector3 Center, Vector3 Normal, float Width) Face(int face)
    {
        var h = HalfExtents;
        return face switch
        {
            0 => (Position + new Vector3(h.X, 0f, 0f), Vector3.UnitX, h.Y * 2f),
            1 => (Position - new Vector3(h.X, 0f, 0f), -Vector3.UnitX, h.Y * 2f),
            2 => (Position + new Vector3(0f, 0f, h.Y), Vector3.UnitZ, h.X * 2f),
            _ => (Position - new Vector3(0f, 0f, h.Y), -Vector3.UnitZ, h.X * 2f),
        };
    }
}

public enum CitySignKind
{
    /// <summary>An animated screen in a frame (<c>screen.gdshader</c>); the transform's X/Y scale is its size.</summary>
    Billboard,

    /// <summary>A vertical neon word on a blade sticking out of a facade (<c>sign.gdshader</c>); Y scale is its height.</summary>
    Blade,

    /// <summary>A horizontal neon word on a board over a shopfront; X/Y scale is its size.</summary>
    Board,
}

/// <summary>
/// A sign on a tower: its transform (the kit mesh's unit quad faces +Z) and the shader's <c>INSTANCE_CUSTOM</c>. Billboards:
/// (seed, hue, brightness, ad 0..3). Blades and boards: (word index in the atlas, hue, brightness, flicker seed).
/// </summary>
public readonly record struct CitySign(CitySignKind Kind, Transform3D Transform, Vector4 Custom)
{
    /// <summary>Words in the sign atlas (<c>Assets/Art/City/signs.png</c>): the vertical ones and the horizontal ones.</summary>
    public const int VerticalWords = 8;

    public const int HorizontalWords = 8;
}

public enum CityHologramKind
{
    Koi,
    Dancer,
    Logo,
}

/// <summary>A giant hologram: where it stands (its base or centre), its size in metres and its yaw (radians).</summary>
public readonly record struct CityHologram(CityHologramKind Kind, Vector3 Position, float Size, float Yaw);

public enum CityLoopKind
{
    Street,
    SkyLane,
    Rail,
    Airship,
}

/// <summary>
/// A closed loop for traffic (scene space): <see cref="Points"/> in order, the last joining the first. Street loops lie on
/// the road surface along street centre lines and <see cref="Width"/> is the narrowest street's width (lanes are offsets
/// across it; right-hand traffic keeps to the right of the direction of travel). <see cref="Speed"/> is a suggested cruise
/// speed in m/s.
/// </summary>
public sealed class CityLoop
{
    private readonly Vector3[] _points;

    public CityLoop(string name, CityLoopKind kind, Vector3[] points, float speed, float width)
    {
        Name = name;
        Kind = kind;
        _points = points;
        Speed = speed;
        Width = width;
        for (var i = 0; i < points.Length; i++)
            Length += Vector3.Distance(points[i], points[(i + 1) % points.Length]);
    }

    public string Name { get; }

    public CityLoopKind Kind { get; }

    /// <summary>The loop's points (closed: the last joins the first, which is not repeated).</summary>
    public IReadOnlyList<Vector3> Points => _points;

    /// <summary>The polyline's length (the smooth curve's is a little shorter at the rounded corners).</summary>
    public float Length { get; }

    public float Speed { get; }

    public float Width { get; }

    /// <summary>A closed, smooth <see cref="Curve3D"/> through the points (for a <see cref="Path3D"/>).</summary>
    public Curve3D CreateCurve(bool upVectors = false)
    {
        var curve = new Curve3D { UpVectorEnabled = upVectors, BakeInterval = 1f };
        curve.SetSmoothPoints(_points, closed: true);
        return curve;
    }
}

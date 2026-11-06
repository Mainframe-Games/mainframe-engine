using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// One surface of a Faceted terrain's palette (<see cref="TerrainMaterial3D.Surfaces"/>): the colour its cells are drawn
/// with, and an optional second colour for two-tone bands (lawns, crop rows). The surface's index is its id in the
/// terrain's <c>surface.png</c>; ids are append-only. Colours are sRGB. Saved inline with the material.
/// </summary>
[EditorIcon("palette")]
public sealed class TerrainSurface : Resource
{
    private int _version = 1;

    /// <summary>Changes whenever a property changes.</summary>
    public int Version => _version;

    /// <summary>A display name (the Terrain dock's swatches).</summary>
    [Export]
    public string Name { get; set => Set(ref field, value ?? ""); } = "";

    /// <summary>
    /// A game key for the surface (footsteps): <see cref="Terrain3D.SurfaceTagAt"/> returns it for Faceted terrains drawn with
    /// a <see cref="TerrainMaterial3D"/>.
    /// </summary>
    [Export]
    public string Tag { get; set => Set(ref field, value ?? ""); } = "";

    [Export]
    public DrawingColor Color { get; set => Set(ref field, value); } = DrawingColor.FromArgb(255, 110, 150, 70);

    /// <summary>Draw world-aligned two-tone bands of <see cref="Color"/> and <see cref="StripeColor"/> (<see cref="TerrainMaterial3D.StripeWidth"/>).</summary>
    [Export]
    public bool Stripes { get; set => Set(ref field, value); }

    [Export]
    public DrawingColor StripeColor { get; set => Set(ref field, value); } = DrawingColor.FromArgb(255, 125, 168, 80);

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// The Faceted terrain look (G8a.4, docs/design/terrain.md#faceted-look): every cell of the terrain's surface map drawn in
/// its surface's flat palette colour on the exact face normal of the triangle under it (flat facets on shared vertices),
/// with optional two-tone bands and a user-channel tint. A Faceted <see cref="Terrain3D"/> without a material draws with
/// its own default instance (<see cref="CreateDefault"/>); assigning one links it to the terrain (<see cref="Terrain"/>).
/// </summary>
/// <remarks>
/// <para>The renderer draws it with <c>Terrain/TerrainFaceted.vk.frag</c> on the standard mesh pipeline layout: the
/// albedo slot holds the surface map, the normal slot the 32 × 2 palette (<see cref="Palette"/>), the emission slot the
/// user map (when <see cref="TintChannel"/> is set). Bands are box-filtered across the pixel, so far away they fade to the
/// mix of the two colours instead of shimmering. Ids past the palette are drawn magenta. Opaque; casters, the prepass and
/// the object-ID pass draw it like any opaque surface.</para>
/// <para>Lit like <see cref="StandardMaterial3D"/> through <see cref="ShadingMode"/> (PBR with <see cref="Roughness"/>, or
/// Blinn-Phong with a matte specular of 0.05 and shininess 8); flat colours look alike under both.</para>
/// </remarks>
[EditorIcon("palette")]
public sealed class TerrainMaterial3D : Material
{
    /// <summary>Palette entries (the palette texture's width); ids at or past it draw magenta.</summary>
    public const int MaxSurfaces = 32;

    private static readonly DrawingColor Missing = DrawingColor.FromArgb(255, 255, 0, 255);

    private TerrainSurface[] _surfaces = [];
    private Texture2D? _palette;
    private int _paletteStamp = -1;

    /// <summary>The palette, in id order (at most <see cref="MaxSurfaces"/>).</summary>
    [Export]
    public TerrainSurface[] Surfaces
    {
        get => _surfaces;
        set
        {
            value ??= [];
            if (ReferenceEquals(_surfaces, value))
                return;
            foreach (var surface in _surfaces)
                if (surface is not null)
                    surface.Changed -= Touch;
            _surfaces = value;
            foreach (var surface in _surfaces)
                if (surface is not null)
                    surface.Changed += Touch;
            Touch();
        }
    }

    /// <summary>Width of one band, metres.</summary>
    [ExportGroup("Bands")]
    [Export(Range = "0.1,16,0.05")]
    public float StripeWidth { get; set => Set(ref field, Math.Max(0.05f, value)); } = 1f;

    /// <summary>Direction the bands run across (degrees about Y from +X).</summary>
    [Export(Range = "-180,180,1")]
    public float StripeAngleDegrees { get; set => Set(ref field, value); }

    /// <summary>The user channel (0–3) that tints toward <see cref="TintColor"/> and gates the bands; −1: none.</summary>
    [ExportGroup("Tint")]
    [Export(Range = "-1,3,1")]
    public int TintChannel { get; set => Set(ref field, Math.Clamp(value, -1, 3)); } = -1;

    [Export]
    public DrawingColor TintColor { get; set => Set(ref field, value); } = DrawingColor.FromArgb(255, 170, 160, 90);

    /// <summary>How far a full channel (255) moves the colour toward <see cref="TintColor"/>, 0–1.</summary>
    [Export(Range = "0,1,0.01")]
    public float TintStrength { get; set => Set(ref field, Math.Clamp(value, 0f, 1f)); } = 1f;

    /// <summary>With a tint channel, bands show only where the channel is at least this (0–1): grass that turns banded when cut.</summary>
    [Export(Range = "0,1,0.01")]
    public float BandStart { get; set => Set(ref field, Math.Clamp(value, 0f, 1f)); } = 0.5f;

    /// <summary>PBR roughness (matte ground).</summary>
    [ExportGroup("Lighting")]
    [Export(Range = "0,1,0.01")]
    public float Roughness { get; set => Set(ref field, Math.Clamp(value, 0f, 1f)); } = 0.9f;

    [Export]
    public ShadingMode ShadingMode { get; set => Set(ref field, value); } = ShadingMode.Pbr;

    /// <summary>The terrain whose surface and user maps the material reads (set by <see cref="Terrain3D"/>; not saved).</summary>
    public Terrain3D? Terrain
    {
        get;
        internal set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            Touch();
        }
    }

    public override MaterialRenderState RenderState => new(AlphaMode.Opaque, CullMode.Back, false);

    /// <summary>
    /// The palette as a 32 × 2 sRGB texture: row 0 each surface's colour, row 1 its stripe colour with alpha 255 where it
    /// has bands (0 otherwise); entries past <see cref="Surfaces"/> are magenta. Rebuilt when a surface changes.
    /// </summary>
    public Texture2D Palette
    {
        get
        {
            if (_palette is not null && _paletteStamp == Version)
                return _palette;
            Span<byte> pixels = stackalloc byte[MaxSurfaces * 2 * 4];
            for (var i = 0; i < MaxSurfaces; i++)
            {
                var surface = i < _surfaces.Length ? _surfaces[i] : null;
                var color = surface?.Color ?? Missing;
                var stripe = surface?.StripeColor ?? Missing;
                Write(pixels, i, color, 255);
                Write(pixels, MaxSurfaces + i, stripe, surface is { Stripes: true } ? (byte)255 : (byte)0);
            }

            if (_palette is null)
                _palette = Texture2D.FromPixels(MaxSurfaces, 2, pixels, PaletteSettings);
            else
                _palette.SetPixels(pixels);
            _paletteStamp = Version;
            return _palette;
        }
    }

    /// <summary>The colour of surface <paramref name="id"/> (magenta past the palette), for the editor's swatches.</summary>
    public DrawingColor ColorOf(int id) => (uint)id < (uint)_surfaces.Length && _surfaces[id] is { } s ? s.Color : Missing;

    /// <summary>The name of surface <paramref name="id"/> ("Surface 3" when it has none).</summary>
    public string NameOf(int id) => (uint)id < (uint)_surfaces.Length && _surfaces[id] is { Name.Length: > 0 } s ? s.Name : "Surface " + id;

    /// <summary>The material a Faceted terrain draws with when it has none: eight surfaces (grass, meadow, dirt, sand, rock, snow, path, lawn).</summary>
    public static TerrainMaterial3D CreateDefault() => new()
    {
        ResourceName = "Terrain faceted (default)",
        Surfaces =
        [
            new TerrainSurface { Name = "Grass", Tag = "grass", Color = DrawingColor.FromArgb(255, 106, 156, 68) },
            new TerrainSurface { Name = "Meadow", Tag = "grass", Color = DrawingColor.FromArgb(255, 146, 176, 76) },
            new TerrainSurface { Name = "Dirt", Tag = "dirt", Color = DrawingColor.FromArgb(255, 142, 104, 70) },
            new TerrainSurface { Name = "Sand", Tag = "sand", Color = DrawingColor.FromArgb(255, 222, 200, 140) },
            new TerrainSurface { Name = "Rock", Tag = "rock", Color = DrawingColor.FromArgb(255, 128, 126, 122) },
            new TerrainSurface { Name = "Snow", Tag = "snow", Color = DrawingColor.FromArgb(255, 236, 240, 244) },
            new TerrainSurface { Name = "Path", Tag = "dirt", Color = DrawingColor.FromArgb(255, 176, 140, 96) },
            new TerrainSurface
            {
                Name = "Lawn", Tag = "grass", Color = DrawingColor.FromArgb(255, 96, 150, 62), Stripes = true,
                StripeColor = DrawingColor.FromArgb(255, 122, 172, 78),
            },
        ],
    };

    private static void Write(Span<byte> pixels, int index, DrawingColor color, byte alpha)
    {
        var o = index * 4;
        pixels[o] = color.R;
        pixels[o + 1] = color.G;
        pixels[o + 2] = color.B;
        pixels[o + 3] = alpha;
    }

    private static readonly TextureImportSettings PaletteSettings = new()
    {
        ColorSpace = TextureImportColorSpace.Srgb,
        Mipmaps = false,
        Filter = TextureFilter.Nearest,
        Wrap = TextureWrap.Clamp,
    };

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        Touch();
    }
}

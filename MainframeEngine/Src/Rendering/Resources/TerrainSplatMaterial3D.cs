namespace MainframeEngine;

/// <summary>
/// The Realistic terrain's material (ADR 0156): up to eight <see cref="TerrainLayer"/>s painted by the terrain's two
/// splat weight maps (<see cref="TerrainData.GetSplatTexture"/>), shaded PBR. Assign it to <see cref="Terrain3D.Material"/>:
/// the terrain links itself (<see cref="Terrain"/>) so the chunks read its weight maps.
/// </summary>
/// <remarks>
/// <para>The renderer packs the layers' textures into three <see cref="Texture2DArray"/>s of
/// <see cref="LayerTextureSize"/>² (albedo sRGB with the layer's height in alpha, normal and ORM linear; every image
/// resized, full mip chains) and gives the material its own pipeline and descriptor set (the arrays, the two weight
/// maps, two samplers and a parameter block). Per fragment it takes the four strongest weights (two in the far band),
/// blends them by height (<see cref="TerrainLayer.HeightBlendContrast"/>), hex-tiles the near band
/// (<see cref="AntiTiling"/>), projects triplanar on slopes between <see cref="TriplanarStartDegrees"/> and
/// <see cref="TriplanarEndDegrees"/>, samples at <see cref="FarTilingScale"/> × the tiling past
/// <see cref="DetailDistance"/> (fully past <see cref="FarDistance"/>), tints by a low-frequency noise
/// (<see cref="MacroStrength"/>), blends the layers' normals (whiteout on triplanar projections) and lights the result
/// through <c>shadeLightsPbr</c> and the fog.</para>
/// <para>Without a terrain (another mesh, or a Faceted terrain) layer 0 covers everything. Opaque; shadow casters and
/// the object-ID pass draw like any opaque surface.</para>
/// </remarks>
[EditorIcon("mountain")]
public sealed class TerrainSplatMaterial3D : Material
{
    /// <summary>Layers per material: two RGBA8 weight maps.</summary>
    public const int MaxLayers = 8;

    private TerrainLayer[] _layers = [];

    /// <summary>The layers, in splat-channel order (at most <see cref="MaxLayers"/>; extra layers are ignored).</summary>
    [Export]
    public TerrainLayer[] Layers
    {
        get => _layers;
        set
        {
            value ??= [];
            if (ReferenceEquals(_layers, value))
                return;
            foreach (var layer in _layers)
                if (layer is not null)
                    layer.Changed -= OnLayerChanged;
            _layers = value;
            foreach (var layer in _layers)
                if (layer is not null)
                    layer.Changed += OnLayerChanged;
            Touch();
        }
    }

    /// <summary>Width and height (pixels) of every packed layer image; textures of other sizes are resampled.</summary>
    [ExportGroup("Textures")]
    [Export(Range = "64,4096,1")]
    public int LayerTextureSize { get; set => Set(ref field, Math.Clamp(value, 4, 4096)); } = 1024;

    /// <summary>Slope (degrees from flat) where triplanar projection starts to fade in.</summary>
    [ExportGroup("Projection")]
    [Export(Range = "0,90,0.5")]
    public float TriplanarStartDegrees { get; set => Set(ref field, value); } = 35f;

    /// <summary>Slope (degrees) from which the layers are fully triplanar.</summary>
    [Export(Range = "0,90,0.5")]
    public float TriplanarEndDegrees { get; set => Set(ref field, value); } = 45f;

    /// <summary>Hex-tiling (Mikkelsen 2022) in the near band: breaks visible repeats at the cost of three samples per lookup.</summary>
    [Export]
    public bool AntiTiling { get; set => Set(ref field, value); } = true;

    /// <summary>Distance (metres) where the far band starts to blend in.</summary>
    [ExportGroup("Distance")]
    [Export(Range = "0,2000,1")]
    public float DetailDistance { get; set => Set(ref field, value); } = 60f;

    /// <summary>Distance (metres) from which only the far band is drawn (two layers, planar, no hex-tiling).</summary>
    [Export(Range = "0,4000,1")]
    public float FarDistance { get; set => Set(ref field, value); } = 150f;

    /// <summary>The far band's tiling: this many times the layer's <see cref="TerrainLayer.TilingMeters"/>.</summary>
    [Export(Range = "1,16,0.1")]
    public float FarTilingScale { get; set => Set(ref field, value); } = 4f;

    /// <summary>Strength of the low-frequency brightness and hue variation (0 = off).</summary>
    [ExportGroup("Macro variation")]
    [Export(Range = "0,1,0.01")]
    public float MacroStrength { get; set => Set(ref field, value); } = 0.15f;

    /// <summary>Size (metres) of the macro variation's largest features.</summary>
    [Export(Range = "1,1000,1")]
    public float MacroScaleMeters { get; set => Set(ref field, value); } = 48f;

    /// <summary>
    /// The terrain whose splat maps weight the layers. Set by <see cref="Terrain3D"/> when the material is assigned to it
    /// (one terrain per material); runtime only, not saved.
    /// </summary>
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

    /// <summary>The layers the renderer draws: <see cref="Layers"/> up to <see cref="MaxLayers"/> (null entries count as empty layers).</summary>
    public int LayerCount => Math.Min(_layers.Length, MaxLayers);

    public override MaterialRenderState RenderState => new(AlphaMode.Opaque, CullMode.Back, false);

    private void OnLayerChanged() => Touch();

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        Touch();
    }
}

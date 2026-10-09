using System.Numerics;
using MainframeEngine.Trees;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// The inputs of a procedural tree: Ez Tree's options (seed, type, branch levels, bark, leaves) plus the engine's
/// <see cref="Scale"/>, bark UV mode and LowPoly settings. The 15 presets are <c>.mres</c> files of this type
/// (<see cref="TreePresets"/>); Ez Tree JSON imports with <see cref="EzTreeJson"/>. <see cref="ToParams"/> gives the
/// <see cref="TreeGenerator"/> its plain inputs.
/// </summary>
/// <remarks>
/// Every generator input is a <see cref="double"/>, so values like <c>69.60000000000001</c> reach the generator unchanged
/// and seeds give the same trees as in Ez Tree. Setters raise <see cref="Resource.Changed"/>, and so does any change to a
/// <see cref="TreeLevel"/> of <see cref="Level"/> (assign a new array to replace a level). Use <see cref="Clone"/> for an
/// editable copy: <see cref="Resource.Duplicate"/> shares the <see cref="Level"/> array.
/// </remarks>
public sealed class TreeOptions : Resource
{
    private int _version;
    private TreeLevel[] _level = [];

    public TreeOptions()
    {
        Level = TreeLevel.EzTreeDefaults();
    }

    /// <summary>Changes whenever a property (or a level's property) changes.</summary>
    public int Version => _version;

    /// <summary>Ez Tree's <c>seed</c> (integers only).</summary>
    [Export(Range = "0,65536,1")]
    public int Seed { get; set => Set(ref field, value); }

    /// <summary>Ez Tree's <c>type</c>.</summary>
    [Export]
    public TreeType Type { get; set => Set(ref field, value); }

    /// <summary>Ez Tree units to engine units (metres). 0.3 makes Oak Medium about 20 m tall. Never affects the shape.</summary>
    [Export(Range = "0.01,10,0.01")]
    public double Scale { get; set => Set(ref field, value); } = 0.3;

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Branch recursion levels (<c>branch.levels</c>): 0 = trunk only.</summary>
    [ExportGroup("Branches")]
    [Export(Range = "0,3,1")]
    public int Levels { get; set => Set(ref field, value); } = 3;

    /// <summary>The four branch levels (0 = trunk).</summary>
    [Export]
    public TreeLevel[] Level
    {
        get => _level;
        set
        {
            value ??= [];
            if (ReferenceEquals(_level, value))
                return;
            foreach (var level in _level)
                if (level is not null)
                    level.Changed -= OnLevelChanged;
            _level = value;
            foreach (var level in _level)
                if (level is not null)
                    level.Changed += OnLevelChanged;
            Touch();
        }
    }

    /// <summary>The direction branches grow towards (<c>branch.force.direction</c>).</summary>
    [Export]
    public Vector3 GrowthDirection { get; set => Set(ref field, value); } = Vector3.UnitY;

    /// <summary>How strongly branches turn towards <see cref="GrowthDirection"/> (<c>branch.force.strength</c>).</summary>
    [Export(Range = "-0.1,0.1,0.001")]
    public double GrowthForce { get; set => Set(ref field, value); } = 0.01;

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The bark texture set (<c>bark.type</c>, e.g. <c>Bark001</c>: <c>Content/Trees/Bark/Bark001_1K-JPG/</c>).</summary>
    [ExportGroup("Bark")]
    [Export]
    public string BarkTexture { get; set => Set(ref field, value ?? string.Empty); } = "Bark001";

    /// <summary><c>bark.tint</c>: multiplies the bark albedo.</summary>
    [Export]
    public DrawingColor BarkTint { get; set => SetColor(ref field, value); } = DrawingColor.White;

    /// <summary><c>bark.textured</c>.</summary>
    [Export]
    public bool BarkTextured { get; set => Set(ref field, value); } = true;

    /// <summary><c>bark.textureScale</c>: X sets the wraps around a branch (in the UVs), Y the V repeat (material).</summary>
    [Export]
    public Vector2 BarkTextureScale { get; set => Set(ref field, value); } = Vector2.One;

    /// <summary>
    /// How V runs along the bark: <see cref="BarkUvMode.Continuous"/> (default) or Ez Tree's 0/1 ping-pong per ring. In
    /// Continuous mode texels are square at each branch base, so the material's V repeat should be 1.
    /// </summary>
    [Export]
    public BarkUvMode BarkUv { get; set => Set(ref field, value); }

    /// <summary>LowPoly bark colour (untextured).</summary>
    [Export]
    public DrawingColor BarkPaletteColor { get; set => SetColor(ref field, value); } = DrawingColor.FromArgb(255, 0x6B, 0x4A, 0x32);

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The leaf texture (<c>leaves.type</c>: ash, aspen, oak or pine; <c>Content/Trees/Leaves/&lt;name&gt;.png</c>).</summary>
    [ExportGroup("Leaves")]
    [Export]
    public string LeafTexture { get; set => Set(ref field, value ?? string.Empty); } = "oak";

    /// <summary><c>leaves.billboard</c>.</summary>
    [Export]
    public TreeBillboard LeafBillboard { get; set => Set(ref field, value); } = TreeBillboard.Double;

    /// <summary>Leaf angle to the branch, degrees (<c>leaves.angle</c>).</summary>
    [Export(Range = "0,100,1")]
    public double LeafAngle { get; set => Set(ref field, value); } = 10;

    /// <summary>Leaves per last-level branch (<c>leaves.count</c>).</summary>
    [Export(Range = "0,100,1")]
    public int LeafCount { get; set => Set(ref field, value); } = 1;

    /// <summary>Where leaves start along a branch, 0–1 (<c>leaves.start</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public double LeafStart { get; set => Set(ref field, value); }

    /// <summary>Leaf quad size, Ez Tree units (<c>leaves.size</c>).</summary>
    [Export(Range = "0,10,0.1")]
    public double LeafSize { get; set => Set(ref field, value); } = 2.5;

    /// <summary>Random size variation, 0–1 (<c>leaves.sizeVariance</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public double LeafSizeVariance { get; set => Set(ref field, value); } = 0.7;

    /// <summary><c>leaves.tint</c>: multiplies the leaf albedo.</summary>
    [Export]
    public DrawingColor LeafTint { get; set => SetColor(ref field, value); } = DrawingColor.White;

    /// <summary>Alpha cutout threshold (<c>leaves.alphaTest</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float LeafAlphaCutoff { get; set => Set(ref field, value); } = 0.5f;

    /// <summary>Rounded canopy normals (<c>leaves.roundedNormals</c>).</summary>
    [Export]
    public bool LeafRoundedNormals { get; set => Set(ref field, value); } = true;

    /// <summary>LowPoly blob colour (varied ±8 % per blob through the surface's colours).</summary>
    [Export]
    public DrawingColor LeafPaletteColor { get; set => SetColor(ref field, value); } = DrawingColor.FromArgb(255, 0x4E, 0x7A, 0x34);

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>LowPoly keeps every Nth bark ring.</summary>
    [ExportGroup("Low poly")]
    [Export(Range = "1,8,1")]
    public int LowPolySectionStride { get; set => Set(ref field, value); } = 2;

    /// <summary>LowPoly bark sides: <c>max(3, round(segments × factor))</c>.</summary>
    [Export(Range = "0,1,0.01")]
    public double LowPolySegmentFactor { get; set => Set(ref field, value); } = 0.6;

    /// <summary>LowPoly drops branches thinner than this (Ez Tree units): they would hide inside the blobs.</summary>
    [Export(Range = "0,2,0.01")]
    public double LowPolyMinBranchRadius { get; set => Set(ref field, value); } = 0.15;

    /// <summary>Blob shape (evergreens always use <see cref="Trees.BlobShape.Cone"/>).</summary>
    [Export]
    public BlobShape BlobShape { get; set => Set(ref field, value); }

    /// <summary>Icosphere subdivisions: 0 = 20 triangles, 1 = 80.</summary>
    [Export(Range = "0,1,1")]
    public int BlobDetail { get; set => Set(ref field, value); } = 1;

    /// <summary>Grid cell (Ez Tree units) that seeds the blob clusters.</summary>
    [Export(Range = "1,30,0.5")]
    public double BlobSize { get; set => Set(ref field, value); } = 6;

    /// <summary>Most blobs at full detail.</summary>
    [Export(Range = "1,64,1")]
    public int MaxBlobs { get; set => Set(ref field, value); } = 24;

    /// <summary>Lumpiness: vertices move along their normals by up to this fraction of the blob radius.</summary>
    [Export(Range = "0,0.5,0.01")]
    public double BlobJitter { get; set => Set(ref field, value); } = 0.15;

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The generator's inputs. Missing levels (an array shorter than four, or null entries) take Ez Tree's defaults.
    /// </summary>
    public TreeParams ToParams()
    {
        var defaults = TreeLevel.EzTreeDefaults();
        var p = new TreeParams
        {
            Seed = Seed,
            Type = Type,
            Levels = Levels,
            Angle = new double[TreeParams.LevelCount],
            Children = new int[TreeParams.LevelCount],
            Gnarliness = new double[TreeParams.LevelCount],
            Length = new double[TreeParams.LevelCount],
            Radius = new double[TreeParams.LevelCount],
            Sections = new int[TreeParams.LevelCount],
            Segments = new int[TreeParams.LevelCount],
            Start = new double[TreeParams.LevelCount],
            Taper = new double[TreeParams.LevelCount],
            Twist = new double[TreeParams.LevelCount],
            GrowthDirection = GrowthDirection,
            GrowthForce = GrowthForce,
            BarkTextureScaleX = BarkTextureScale.X,
            BarkUv = BarkUv,
            LeafBillboard = LeafBillboard,
            LeafAngle = LeafAngle,
            LeafCount = LeafCount,
            LeafStart = LeafStart,
            LeafSize = LeafSize,
            LeafSizeVariance = LeafSizeVariance,
            LeafRoundedNormals = LeafRoundedNormals,
            Scale = Scale,
            LowPolySectionStride = LowPolySectionStride,
            LowPolySegmentFactor = LowPolySegmentFactor,
            LowPolyMinBranchRadius = LowPolyMinBranchRadius,
            BlobShape = BlobShape,
            BlobDetail = BlobDetail,
            BlobSize = BlobSize,
            MaxBlobs = MaxBlobs,
            BlobJitter = BlobJitter,
        };

        for (var i = 0; i < TreeParams.LevelCount; i++)
        {
            var level = i < _level.Length && _level[i] is { } l ? l : defaults[i];
            p.Angle[i] = level.Angle;
            p.Children[i] = level.Children;
            p.Gnarliness[i] = level.Gnarliness;
            p.Length[i] = level.Length;
            p.Radius[i] = level.Radius;
            p.Sections[i] = level.Sections;
            p.Segments[i] = level.Segments;
            p.Start[i] = level.Start;
            p.Taper[i] = level.Taper;
            p.Twist[i] = level.Twist;
        }

        return p;
    }

    /// <summary>An unsaved deep copy: its own <see cref="TreeLevel"/>s (edits never touch the original or a preset).</summary>
    public TreeOptions Clone()
    {
        var copy = (TreeOptions)Duplicate();
        var levels = new TreeLevel[_level.Length];
        for (var i = 0; i < levels.Length; i++)
            levels[i] = _level[i] is { } level ? (TreeLevel)level.Duplicate() : new TreeLevel();
        copy.Level = levels;
        return copy;
    }

    private void OnLevelChanged() => Touch();

    private void Touch()
    {
        _version++;
        EmitChanged();
    }

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        Touch();
    }

    // Color.Equals also compares the known-colour name (Color.White != FromArgb(255, 255, 255, 255)).
    private void SetColor(ref DrawingColor storage, DrawingColor value)
    {
        if (storage.ToArgb() == value.ToArgb())
            return;
        storage = value;
        Touch();
    }
}

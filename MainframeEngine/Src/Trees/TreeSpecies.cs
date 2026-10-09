using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>
/// One kind of tree in a <see cref="TreeScatter"/>: its options (or a preset), the seeds of its variants and its style,
/// or baked variants (<see cref="Baked"/>, from <see cref="Tree3D.Bake"/>) that skip the generator. Placements pick a
/// variant by a hash of their position, so a forest of one species still varies.
/// </summary>
[EditorIcon("feather")]
public sealed class TreeSpecies : Resource
{
    private TreeMesh[] _generated = [];
    private TreeOptions? _presetOptions;
    private string? _presetName;
    private (TreeOptions? Options, int Version, int[] Seeds, TreeStyle Style, string Preset) _generatedFor;

    /// <summary>The generator's inputs; null: the preset named by <see cref="Preset"/>.</summary>
    [Export]
    public TreeOptions? Options { get; set; }

    /// <summary>A <see cref="TreePresets"/> name, used when <see cref="Options"/> is null.</summary>
    [Export]
    public string Preset { get; set; } = string.Empty;

    /// <summary>The seed of each variant; empty: one variant with the options' own seed.</summary>
    [Export]
    public int[] Seeds { get; set; } = [];

    [Export]
    public TreeStyle Style { get; set; }

    /// <summary>Baked variants: when not empty, these are the variants and nothing is generated.</summary>
    [Export]
    public TreeMesh[] Baked { get; set; } = [];

    /// <summary>Replaces the bark material (null: <see cref="TreeMaterials.Bark"/>).</summary>
    [Export]
    public Material? BarkMaterial { get; set; }

    /// <summary>Replaces the leaf material (null: <see cref="TreeMaterials.Leaves"/>).</summary>
    [Export]
    public Material? LeafMaterial { get; set; }

    /// <summary>How many variants placements choose from (at least 1).</summary>
    public int VariantCount => Baked.Length > 0 ? Baked.Length : Math.Max(1, Seeds.Length);

    /// <summary>The options: <see cref="Options"/>, else a copy of the preset, else the first baked variant's.</summary>
    public TreeOptions? ResolveOptions()
    {
        if (Options is { } options)
            return options;
        if (!string.IsNullOrEmpty(Preset))
        {
            if (_presetOptions is null || _presetName != Preset)
            {
                _presetOptions = TreePresets.Load(Preset);
                _presetName = Preset;
            }

            return _presetOptions;
        }

        return Baked.Length > 0 ? Baked[0].Options : null;
    }

    /// <summary>
    /// Variant <paramref name="index"/>: the baked mesh, or the tree generated for <c>Seeds[index]</c> (generated on first
    /// use and kept until the options, seeds or style change). Null without options.
    /// </summary>
    public TreeMesh? GetVariant(int index, TreeGenerator? generator = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, VariantCount);
        if (Baked.Length > 0)
            return Baked[index];
        if (ResolveOptions() is not { } options)
            return null;

        var stamp = (Options, options.Version, Seeds, Style, Preset);
        if (_generated.Length != VariantCount || _generatedFor != stamp)
        {
            _generated = new TreeMesh[VariantCount];
            _generatedFor = stamp;
        }

        if (_generated[index] is null)
        {
            var seed = Seeds.Length > 0 ? Seeds[index] : options.Seed;
            _generated[index] = TreeMesh.Generate(options, seed, Style, generator);
        }

        return _generated[index];
    }

    /// <summary>The bark material of this species' variants.</summary>
    public Material? ResolveBarkMaterial(TreeMesh variant) =>
        BarkMaterial ?? ((ResolveOptions() ?? variant.Options) is { } options ? TreeMaterials.Bark(options, variant.Style) : null);

    /// <summary>The leaf material of this species' variants.</summary>
    public Material? ResolveLeafMaterial(TreeMesh variant) =>
        LeafMaterial ?? ((ResolveOptions() ?? variant.Options) is { } options ? TreeMaterials.Leaves(options, variant.Style) : null);
}

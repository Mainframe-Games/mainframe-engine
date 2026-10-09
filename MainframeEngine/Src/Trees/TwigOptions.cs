namespace MainframeEngine;

/// <summary>
/// The twig a tree's leaf-cluster cards show (ADR 0172, <see cref="TreeOptions.LeafMode"/> =
/// <see cref="Trees.TreeLeafMode.Cluster"/>): a small Ez Tree branch grown by the same <see cref="Trees.TreeGenerator"/>
/// from the tree's own last branch level and leaves (<see cref="Trees.TreeGenerator.TwigParams"/>), baked by
/// <see cref="TreeClusterBaker"/> in <see cref="Variants"/> variants into a <see cref="Columns"/> × <see cref="Rows"/>
/// atlas (albedo, normal, thickness). Each card stands in for <see cref="LeafSlots"/> leaf slots of the tree.
/// </summary>
[EditorIcon("feather")]
public sealed class TwigOptions : Resource
{
    /// <summary>Leaf slots of the tree one card replaces (fewer, larger cards as it grows).</summary>
    [Export(Range = "1,64,1")]
    public int LeafSlots { get; set => Set(ref field, value); } = 8;

    /// <summary>Leaves on the twig per slot it replaces: above 1 the canopy gets fuller than the single cards.</summary>
    [Export(Range = "0.25,4,0.05")]
    public double LeafDensity { get; set => Set(ref field, value); } = 1.5;

    /// <summary>Seeds the twigs (each variant adds to it).</summary>
    [Export(Range = "0,65536,1")]
    public int Seed { get; set => Set(ref field, value); } = 1;

    /// <summary>Twig variants baked (at most <see cref="Columns"/> × <see cref="Rows"/>).</summary>
    [Export(Range = "1,64,1")]
    public int Variants { get; set => Set(ref field, value); } = 8;

    /// <summary>
    /// Choose the grid from the twigs' shape (on by default): of 4 × 2, 2 × 4, 3 × 3, 8 × 1 and 1 × 8 cells (enough for
    /// <see cref="Variants"/>), the one whose cells best match the twigs' aspect, so round clusters are not squeezed into
    /// tall cells. Off: <see cref="Columns"/> × <see cref="Rows"/>.
    /// </summary>
    [Export]
    public bool AutoLayout { get; set => Set(ref field, value); } = true;

    [Export(Range = "1,16,1")]
    public int Columns { get; set => Set(ref field, value); } = 4;

    [Export(Range = "1,16,1")]
    public int Rows { get; set => Set(ref field, value); } = 2;

    /// <summary>Atlas size in pixels (cells of width / <see cref="Columns"/> × height / <see cref="Rows"/>).</summary>
    [Export(Range = "64,4096,1")]
    public int AtlasWidth { get; set => Set(ref field, value); } = 1024;

    [Export(Range = "64,4096,1")]
    public int AtlasHeight { get; set => Set(ref field, value); } = 1024;

    /// <summary>Samples per pixel along each axis of the bake.</summary>
    [Export(Range = "1,4,1")]
    public int Supersample { get; set => Set(ref field, value); } = 2;

    /// <summary>A key of every value that changes the bake.</summary>
    internal string BakeKey =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{LeafSlots}|{LeafDensity:R}|{Seed}|{Variants}|{AutoLayout}|{Columns}|{Rows}|{AtlasWidth}|{AtlasHeight}|{Supersample}");

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        EmitChanged();
    }
}

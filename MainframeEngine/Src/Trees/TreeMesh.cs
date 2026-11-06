using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>
/// A generated tree ready to draw: one <see cref="ArrayMesh"/> per level of detail (surface 0 bark, surface 1 leaves,
/// with the <c>Custom0</c> wind stream and, for LowPoly blobs, <c>Colors</c>), the trunk capsule, and what it was made
/// from (<see cref="Options"/>, <see cref="Seed"/>, <see cref="Style"/>), which picks its materials
/// (<see cref="TreeMaterials"/>). <see cref="Tree3D.Bake"/> saves one as <c>.mfres</c>, so a shipped game loads the
/// meshes instead of running the generator (ADR 0158). The surfaces carry no materials: the tree applies them.
/// </summary>
[EditorIcon("package")]
public sealed class TreeMesh : Resource
{
    /// <summary>The levels of detail, finest first (three for the default details).</summary>
    [Export]
    public ArrayMesh[] Lods { get; set; } = [];

    /// <summary>The options the meshes were generated from (inline; their bark and leaf names pick the materials).</summary>
    [Export]
    public TreeOptions? Options { get; set; }

    [Export]
    public int Seed { get; set; }

    [Export]
    public TreeStyle Style { get; set; }

    /// <summary>Trunk capsule height (caps included, metres) from the ground to the first side branch.</summary>
    [Export]
    public float TrunkHeight { get; set; }

    /// <summary>Trunk radius 1 m above the ground (metres).</summary>
    [Export]
    public float TrunkRadius { get; set; }

    /// <summary><see cref="TreeGenerator.GeneratorVersion"/> at generation (a different current version marks a bake stale).</summary>
    [Export]
    public int GeneratorVersion { get; set; }

    /// <summary>
    /// What the meshes were made from (G8b.3, ADR 0271): <see cref="ComputeBakeHash"/> of the options, seed, style and
    /// generator version at generation. A bake whose hash differs from its tree's current inputs is stale
    /// (<see cref="IsCurrentFor"/>); empty for bakes made before the hash existed (they read as stale).
    /// </summary>
    [Export]
    public string BakeHash { get; set; } = string.Empty;

    public TreeTrunkCapsule Trunk => new(TrunkHeight, TrunkRadius);

    /// <summary>Whether these meshes are what <paramref name="options"/>, <paramref name="seed"/> and <paramref name="style"/> generate now (same <see cref="BakeHash"/>).</summary>
    public bool IsCurrentFor(TreeOptions options, int seed, TreeStyle style) =>
        BakeHash.Length > 0 && string.Equals(BakeHash, ComputeBakeHash(options, seed, style), StringComparison.Ordinal);

    /// <summary>
    /// A SHA-256 (hex) of every exported value of <paramref name="options"/> (its levels and twig options included, its
    /// <see cref="Resource.ResourceName"/> not), <paramref name="seed"/>, <paramref name="style"/> and
    /// <see cref="TreeGenerator.GeneratorVersion"/>: equal inputs give equal hashes on every machine. Not per-frame work
    /// (it formats the values as text).
    /// </summary>
    public static string ComputeBakeHash(TreeOptions options, int seed, TreeStyle style)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = new StringBuilder(4096);
        text.Append("tree-bake/").Append(TreeGenerator.GeneratorVersion).Append('/').Append(seed.ToString(CultureInfo.InvariantCulture))
            .Append('/').Append(style).Append('/');
        AppendValue(text, options, 0);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AppendValue(StringBuilder text, object? value, int depth)
    {
        switch (value)
        {
            case null:
                text.Append('~');
                break;
            case Resource { IsExternal: true } external:
                text.Append('@').Append(external.Uid ?? external.ResourcePath);
                break;
            case Resource resource when depth < 8 && TypeRegistry.GetNearest(resource.GetType()) is { } info:
                text.Append(info.Name).Append('{');
                foreach (var property in info.Properties)
                {
                    if (property.Name == nameof(ResourceName))
                        continue;
                    text.Append(property.Name).Append('=');
                    AppendValue(text, property.GetValue(resource), depth + 1);
                    text.Append(';');
                }

                text.Append('}');
                break;
            case string s:
                text.Append('"').Append(s.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case System.Drawing.Color color:
                text.Append(color.ToArgb().ToString(CultureInfo.InvariantCulture));
                break;
            case IEnumerable list:
                text.Append('[');
                foreach (var item in list)
                {
                    AppendValue(text, item, depth + 1);
                    text.Append(',');
                }

                text.Append(']');
                break;
            case IFormattable formattable:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                text.Append(value);
                break;
        }
    }

    private readonly Dictionary<(Material?, Material?), TreeImpostor> _impostors = [];
    private readonly Lock _impostorGate = new();

    /// <summary>
    /// The octahedral impostor of the finest level drawn with <paramref name="bark"/> and <paramref name="leaves"/>
    /// (ADR 0172; <see cref="TreeImpostorBaker"/>), baked on first use and kept with this mesh (not saved).
    /// </summary>
    public TreeImpostor GetImpostor(Material? bark, Material? leaves, TreeImpostorOptions? options = null)
    {
        if (Lods.Length == 0)
            throw new InvalidOperationException("The tree has no levels to bake an impostor from.");
        lock (_impostorGate)
        {
            if (_impostors.TryGetValue((bark, leaves), out var impostor))
                return impostor;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            impostor = TreeImpostorBaker.BakeCached(Lods[0], bark, leaves, options);
            Log.Debug($"[Trees] Baked the impostor of '{Options?.ResourceName}' seed {Seed} in {watch.Elapsed.TotalMilliseconds:0} ms.");
            _impostors.Add((bark, leaves), impostor);
            return impostor;
        }
    }

    /// <summary>
    /// Generates every default level of detail of <paramref name="options"/> for <paramref name="seed"/> in
    /// <paramref name="style"/> (<see cref="TreeGenerator.Generate"/>; Oak Medium Realistic takes about 3–5 ms).
    /// Reuse <paramref name="generator"/> across calls on one thread.
    /// </summary>
    public static TreeMesh Generate(TreeOptions options, int seed, TreeStyle style, TreeGenerator? generator = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parameters = options.ToParams();
        parameters.Seed = seed;
        if (style == TreeStyle.Realistic && options.LeafMode == TreeLeafMode.Cluster)
            parameters.ClusterCard = TreeClusterBaker.GetOrBake(options).Card; // ADR 0172: the cards fit the baked atlas
        var lods = (generator ?? new TreeGenerator()).Generate(parameters, style);
        var meshes = new ArrayMesh[lods.Length];
        for (var i = 0; i < lods.Length; i++)
            meshes[i] = lods[i].ToArrayMesh();
        var trunk = lods.Length > 0 ? lods[0].Trunk : default;
        return new TreeMesh
        {
            Lods = meshes,
            Options = options,
            Seed = seed,
            Style = style,
            TrunkHeight = trunk.Height,
            TrunkRadius = trunk.Radius,
            GeneratorVersion = TreeGenerator.GeneratorVersion,
            BakeHash = ComputeBakeHash(options, seed, style),
        };
    }

    /// <summary>
    /// The visibility range of level <paramref name="lod"/> of <paramref name="count"/>: [0, <paramref name="lod1"/>),
    /// [<paramref name="lod1"/>, <paramref name="lod2"/>), [<paramref name="lod2"/>, <paramref name="max"/>) (0 = no
    /// upper bound), so exactly one level draws at any distance. Begin/End as
    /// <see cref="GeometryInstance3D.VisibilityRangeBegin"/>/<see cref="GeometryInstance3D.VisibilityRangeEnd"/>. The last
    /// level runs to <paramref name="max"/>; a level whose range is empty (a distance of 0, or levels past the third)
    /// gets (<see cref="float.MaxValue"/>, <see cref="float.MaxValue"/>): never drawn.
    /// </summary>
    public static (float Begin, float End) LodRange(int lod, int count, float lod1, float lod2, float max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lod, count);
        var begin = lod == 0 ? 0f : lod == 1 ? lod1 : lod2;
        if (lod == count - 1)
            return (begin, max);
        var end = lod == 0 ? lod1 : lod2;
        return end > begin ? (begin, end) : (float.MaxValue, float.MaxValue);
    }
}

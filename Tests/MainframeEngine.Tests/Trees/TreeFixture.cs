using System.Numerics;
using System.Text.Json;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>
/// The Ez Tree reference fixture (<c>Tests/Content/Trees/ez-tree-reference.json</c>, written by
/// <c>build/ez-tree-reference.mjs</c> from Ez Tree's own generator) and the hashes it uses.
/// </summary>
internal static class TreeFixture
{
    private const uint FnvOffset = 2166136261;

    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content", "Trees", "ez-tree-reference.json"))));

    public static JsonElement Root => Document.Value.RootElement;

    public static JsonElement Preset(string name) =>
        Root.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);

    /// <summary>The preset as Ez Tree meshes it: Ez Tree's UV ping-pong and no engine scale.</summary>
    public static TreeParams ParityParams(string name)
    {
        var options = TreePresets.Load(name);
        options.BarkUv = BarkUvMode.EzTree;
        options.Scale = 1;
        return options.ToParams();
    }

    public static uint Fnv(uint hash, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            hash ^= (value >> (8 * i)) & 255;
            hash *= 16777619;
        }

        return hash;
    }

    /// <summary>JavaScript's <c>Math.round(v * 1e4) | 0</c>.</summary>
    public static uint Quantize(double value) => (uint)(int)Math.Floor(value * 1e4 + 0.5);

    public static uint HashIndices(int[] indices)
    {
        var h = FnvOffset;
        foreach (var i in indices)
            h = Fnv(h, (uint)i);
        return h;
    }

    public static uint Hash(Vector3[] values)
    {
        var h = FnvOffset;
        foreach (var v in values)
        {
            h = Fnv(h, Quantize(v.X));
            h = Fnv(h, Quantize(v.Y));
            h = Fnv(h, Quantize(v.Z));
        }

        return h;
    }

    public static uint Hash(Vector2[] values)
    {
        var h = FnvOffset;
        foreach (var v in values)
        {
            h = Fnv(h, Quantize(v.X));
            h = Fnv(h, Quantize(v.Y));
        }

        return h;
    }

    public static uint StartHash => FnvOffset;
}

using System.Globalization;
using System.Numerics;

namespace MainframeEngine.Tests.TestAssets;

/// <summary>
/// Generates the small self-made glTF test model (CC0): <c>test_model.gltf</c> + <c>.bin</c> + <c>checker.png</c>
/// with their <c>.meta</c> files. The committed copy lives in <c>Tests/Content/Models/TestModel</c> (the
/// showcase fixture instances it; the render tests import it); <c>TheCommittedTestModelMatchesTheGenerator</c> keeps the two in sync.
/// </summary>
/// <remarks>
/// Content: node <c>Base</c> (unit box, checker texture, at y 0.5) with child <c>Pillar</c> (the same box mesh,
/// rotated 45° about Y, scaled 0.5×1×0.5) with child <c>Banner</c> (one mesh, two primitives: a red double-sided quad
/// and a blended blue "glass" quad); node <c>Empty</c> at x 2 without a mesh.
/// </remarks>
public static class TestModel
{
    public const string GltfFile = "test_model.gltf";
    public const string BinFile = "test_model.bin";
    public const string TextureFile = "checker.png";
    public const string ModelUid = "mdl_7e57a55e7000";
    public const string TextureUid = "tex_7e57c4ec4e70";

    /// <summary>Writes the model files into <paramref name="directory"/>.</summary>
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var box = new BoxMesh().GetSurface(0);

        // Banner: two quads side by side in XY facing +Z (8 vertices, two index ranges).
        Vector3[] bannerPositions =
        [
            new(-1, 0, 0), new(0, 0, 0), new(0, 1, 0), new(-1, 1, 0),
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        ];
        var bannerNormals = Enumerable.Repeat(Vector3.UnitZ, 8).ToArray();
        Vector2[] bannerUvs = [new(0, 1), new(1, 1), new(1, 0), new(0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
        ushort[] redIndices = [0, 1, 2, 0, 2, 3];
        ushort[] glassIndices = [4, 5, 6, 4, 6, 7];

        var bin = new BinaryBuilder();
        var views = new List<string>();
        var accessors = new List<string>();

        int Vec3(Vector3[] values, bool minMax)
        {
            var offset = bin.Add(values.SelectMany(v => new[] { v.X, v.Y, v.Z }));
            views.Add(View(offset, values.Length * 12, 34962));
            var min = minMax ? $", \"min\": [{F(values.Min(v => v.X))}, {F(values.Min(v => v.Y))}, {F(values.Min(v => v.Z))}], \"max\": [{F(values.Max(v => v.X))}, {F(values.Max(v => v.Y))}, {F(values.Max(v => v.Z))}]" : "";
            accessors.Add($"{{ \"bufferView\": {views.Count - 1}, \"componentType\": 5126, \"count\": {values.Length}, \"type\": \"VEC3\"{min} }}");
            return accessors.Count - 1;
        }

        int Vec2(Vector2[] values)
        {
            var offset = bin.Add(values.SelectMany(v => new[] { v.X, v.Y }));
            views.Add(View(offset, values.Length * 8, 34962));
            accessors.Add($"{{ \"bufferView\": {views.Count - 1}, \"componentType\": 5126, \"count\": {values.Length}, \"type\": \"VEC2\" }}");
            return accessors.Count - 1;
        }

        int Indices(IEnumerable<int> values)
        {
            var list = values.Select(i => (ushort)i).ToArray();
            var offset = bin.Add(list);
            views.Add(View(offset, list.Length * 2, 34963));
            accessors.Add($"{{ \"bufferView\": {views.Count - 1}, \"componentType\": 5123, \"count\": {list.Length}, \"type\": \"SCALAR\" }}");
            return accessors.Count - 1;
        }

        var boxPos = Vec3(box.Positions, minMax: true);
        var boxNrm = Vec3(box.Normals, minMax: false);
        var boxUv = Vec2(box.UVs);
        var boxIdx = Indices(box.Indices);
        var banPos = Vec3(bannerPositions, minMax: true);
        var banNrm = Vec3(bannerNormals, minMax: false);
        var banUv = Vec2(bannerUvs);
        var redIdx = Indices(redIndices.Select(i => (int)i));
        var glassIdx = Indices(glassIndices.Select(i => (int)i));

        var binBytes = bin.ToArray();
        var s = MathF.Sin(MathF.PI / 8); // 45° about Y
        var c = MathF.Cos(MathF.PI / 8);
        var gltf = $$"""
            {
              "asset": { "version": "2.0", "generator": "MainframeEngine.Tests TestModel (CC0)" },
              "scene": 0,
              "scenes": [ { "nodes": [ 0, 3 ] } ],
              "nodes": [
                { "name": "Base", "mesh": 0, "translation": [ 0, 0.5, 0 ], "children": [ 1 ] },
                { "name": "Pillar", "mesh": 0, "translation": [ 0, 1, 0 ], "rotation": [ 0, {{F(s)}}, 0, {{F(c)}} ], "scale": [ 0.5, 1, 0.5 ], "children": [ 2 ] },
                { "name": "Banner", "mesh": 1, "translation": [ 0, 0.5, 0.6 ] },
                { "name": "Empty", "translation": [ 2, 0, 0 ] }
              ],
              "meshes": [
                { "name": "Box", "primitives": [ { "attributes": { "POSITION": {{boxPos}}, "NORMAL": {{boxNrm}}, "TEXCOORD_0": {{boxUv}} }, "indices": {{boxIdx}}, "material": 0 } ] },
                { "name": "Banner", "primitives": [
                  { "attributes": { "POSITION": {{banPos}}, "NORMAL": {{banNrm}}, "TEXCOORD_0": {{banUv}} }, "indices": {{redIdx}}, "material": 1 },
                  { "attributes": { "POSITION": {{banPos}}, "NORMAL": {{banNrm}}, "TEXCOORD_0": {{banUv}} }, "indices": {{glassIdx}}, "material": 2 } ] }
              ],
              "materials": [
                { "name": "Checker", "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 }, "metallicFactor": 0 } },
                { "name": "Red", "pbrMetallicRoughness": { "baseColorFactor": [ 0.8, 0.05, 0.05, 1 ], "metallicFactor": 0 }, "doubleSided": true },
                { "name": "Glass", "pbrMetallicRoughness": { "baseColorFactor": [ 0.2, 0.5, 1, 0.5 ], "metallicFactor": 0 }, "alphaMode": "BLEND" }
              ],
              "textures": [ { "source": 0, "sampler": 0 } ],
              "images": [ { "uri": "{{TextureFile}}" } ],
              "samplers": [ { "magFilter": 9728, "minFilter": 9728, "wrapS": 10497, "wrapT": 10497 } ],
              "accessors": [
                {{string.Join(",\n    ", accessors)}}
              ],
              "bufferViews": [
                {{string.Join(",\n    ", views)}}
              ],
              "buffers": [ { "uri": "{{BinFile}}", "byteLength": {{binBytes.Length}} } ]
            }
            """;

        File.WriteAllText(Path.Combine(directory, GltfFile), gltf.ReplaceLineEndings("\n") + "\n");
        File.WriteAllBytes(Path.Combine(directory, BinFile), binBytes);
        Png.WriteRgba8(Path.Combine(directory, TextureFile), 8, 8, Checker(8));
        File.WriteAllText(Path.Combine(directory, GltfFile + ".meta"),
            $"{{\n  \"uid\": \"{ModelUid}\",\n  \"importer\": \"model\",\n  \"settings\": {{\n    \"scale\": 1\n  }}\n}}\n");
        File.WriteAllText(Path.Combine(directory, TextureFile + ".meta"),
            $"{{\n  \"uid\": \"{TextureUid}\",\n  \"importer\": \"texture\",\n  \"settings\": {{\n    \"filter\": \"nearest\",\n    \"mipmaps\": false\n  }}\n}}\n");
    }

    /// <summary>A size × size checker of light and dark squares (2 × 2 pixels each).</summary>
    public static byte[] Checker(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var light = ((x / 2) + (y / 2)) % 2 == 0;
                var i = (y * size + x) * 4;
                pixels[i] = light ? (byte)230 : (byte)40;
                pixels[i + 1] = light ? (byte)200 : (byte)60;
                pixels[i + 2] = light ? (byte)120 : (byte)90;
                pixels[i + 3] = 255;
            }

        return pixels;
    }

    private static string View(int offset, int length, int target) =>
        $"{{ \"buffer\": 0, \"byteOffset\": {offset}, \"byteLength\": {length}, \"target\": {target} }}";

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class BinaryBuilder
    {
        private readonly List<byte> _bytes = [];

        public int Add(IEnumerable<float> values)
        {
            var offset = Align();
            foreach (var v in values)
                _bytes.AddRange(BitConverter.GetBytes(v)); // little-endian on every supported platform
            return offset;
        }

        public int Add(ushort[] values)
        {
            var offset = Align();
            foreach (var v in values)
                _bytes.AddRange(BitConverter.GetBytes(v));
            return offset;
        }

        private int Align()
        {
            while (_bytes.Count % 4 != 0)
                _bytes.Add(0);
            return _bytes.Count;
        }

        public byte[] ToArray()
        {
            Align();
            return [.. _bytes];
        }
    }
}

using System.Text;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// Scene file format 2: the text layout, stable resource keys and the flat node list (see
/// docs/design/scene-serialization.md).
/// </summary>
[Collection(nameof(SerialResources))]
public sealed class SceneFormatTests
{
    private static T Own<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }

    private static string[] ResourceKeys(JsonElement file) =>
        file.TryGetProperty("resources", out var table) ? table.EnumerateObject().Select(p => p.Name).ToArray() : [];

    // ---------------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void LayoutKeepsShortScalarArraysAndReferencesOnOneLine()
    {
        var input = """
            {"a":[1,2.5,-3E-05,true,null],"b":{"res":"Box_1"},"c":[],"d":{},"e":[[1,2],{"x":1}],
             "f":["x\"y","é"],"g":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17],"h":{"res":"A","other":1}}
            """;
        var expected = """
            {
              "a": [1, 2.5, -3E-05, true, null],
              "b": { "res": "Box_1" },
              "c": [],
              "d": {},
              "e": [
                [1, 2],
                {
                  "x": 1
                }
              ],
              "f": ["x\"y", "é"],
              "g": [
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                8,
                9,
                10,
                11,
                12,
                13,
                14,
                15,
                16,
                17
              ],
              "h": {
                "res": "A",
                "other": 1
              }
            }
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Encoding.UTF8.GetString(SceneFormat.FormatJson(Encoding.UTF8.GetBytes(input))));
    }

    [Fact]
    public void LayoutOnlyChangesWhitespace()
    {
        var root = new AllTypesNode
        {
            Name = "R",
            Tags = ["a", "b"],
            Weights = [0.25f, float.NaN],
            Quat = new System.Numerics.Quaternion(0, 0.70710677f, 0, 0.70710677f),
            Data = new TestResource { Value = 2, Label = "naïve \"quoted\"" },
        };
        var laidOut = SceneSaver.ToJson(root);
        root.Free();

        using var original = JsonDocument.Parse(laidOut);
        using var again = JsonDocument.Parse(SceneFormat.FormatJson(laidOut));
        Assert.True(JsonElement.DeepEquals(original.RootElement, again.RootElement));
        Assert.Equal(laidOut, SceneFormat.FormatJson(laidOut)); // idempotent
        Assert.DoesNotContain((byte)'\r', laidOut);
    }

    // ---------------------------------------------------------------------------------------------
    // Resource keys
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SavingTheSameTreeTwiceWritesTheSameBytes()
    {
        static Node Build()
        {
            var root = new Node3D { Name = "Level" };
            Own(root, root, new AllTypesNode { Name = "A", Data = new TestResource { Value = 1, Next = new TestResource { Value = 2 } } });
            Own(root, root, new AllTypesNode { Name = "B", DataList = [new TestResource { Value = 3 }, new TestResource { Value = 4 }] });
            return root;
        }

        // Two separately built trees (new resource objects): keys come from where the resources are used.
        var first = Build();
        var second = Build();
        var bytes = SceneSaver.ToJson(first);
        Assert.Equal(bytes, SceneSaver.ToJson(second));
        Assert.Equal(bytes, SceneSaver.ToJson(first));

        // Loaded and saved again: the keys come back from the file.
        var copy = PackedScene.Parse(bytes).Instantiate();
        Assert.Equal(bytes, SceneSaver.ToJson(copy));
        first.Free();
        second.Free();
        copy.Free();
    }

    [Fact]
    public void AddingAResourceDoesNotRenameTheOthers()
    {
        var root = new Node3D { Name = "Level" };
        Own(root, root, new AllTypesNode { Name = "A", Data = new TestResource { Value = 1 } });
        Own(root, root, new AllTypesNode { Name = "B", Data = new TestResource { Value = 2 } });
        var before = SceneJson.Parse(SceneSaver.ToJson(root));
        root.Free();

        var loaded = PackedScene.Parse(Encoding.UTF8.GetBytes(before.GetRawText())).Instantiate();
        var first = Own(loaded, loaded, new AllTypesNode { Name = "First", Data = new TestResource { Value = 0 } });
        loaded.MoveChild(first, 0); // discovered before the others
        var after = SceneJson.Parse(SceneSaver.ToJson(loaded));
        loaded.Free();

        var oldKeys = ResourceKeys(before);
        var newKeys = ResourceKeys(after);
        Assert.Equal(3, newKeys.Length);
        Assert.Equal(oldKeys, newKeys.Intersect(oldKeys));
        foreach (var key in oldKeys)
            Assert.True(JsonElement.DeepEquals(before.GetProperty("resources").GetProperty(key), after.GetProperty("resources").GetProperty(key)));
        Assert.Equal(SceneJson.Node(before, "A").GetRawText(), SceneJson.Node(after, "A").GetRawText());
    }

    [Fact]
    public void KeysNameTheTypeAndAreUniqueInTheFile()
    {
        var shared = new TestResource { Value = 9 };
        var root = new AllTypesNode { Name = "R", Data = shared, DataList = [new TestResource(), new TestResource(), shared] };
        var file = SceneJson.Parse(SceneSaver.ToJson(root));
        root.Free();

        var keys = ResourceKeys(file);
        Assert.Equal(3, keys.Length); // the shared resource is written once
        Assert.All(keys, k => Assert.Matches("^TestResource_[0-9a-z]{5}$", k));
        var props = SceneJson.Root(file).GetProperty("props");
        Assert.Equal(props.GetProperty("Data").GetProperty("res").GetString(), props.GetProperty("DataList")[2].GetProperty("res").GetString());
    }

    [Fact]
    public void Format1NumberedKeysAreReplacedOnSave()
    {
        const string json = """
            { "format": 1,
              "resources": { "1": { "type": "TestResource", "props": { "Value": 5, "Next": { "res": "2" } } },
                             "2": { "type": "TestResource", "props": { "Value": 6 } } },
              "root": { "type": "AllTypesNode", "name": "Old", "props": { "Data": { "res": "1" } } } }
            """;
        var root = PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate<AllTypesNode>();
        Assert.Equal(6, root.Data!.Next!.Value);

        var file = SceneJson.Parse(SceneSaver.ToJson(root));
        root.Free();
        Assert.Equal(SceneFormat.Current, file.GetProperty("format").GetInt32());
        Assert.All(ResourceKeys(file), k => Assert.StartsWith("TestResource_", k, StringComparison.Ordinal));
        Assert.Equal("Old", SceneJson.Root(file).GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // Node list
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NodesAreListedInTreeOrderWithTheirParentPaths()
    {
        var root = new Node3D { Name = "Level" };
        var a = Own(root, root, new Node3D { Name = "A" });
        Own(root, a, new PlainNode { Name = "B" });
        Own(root, root, new PlainNode { Name = "C" });
        a.AddChild(new PlainNode { Name = "Runtime" }); // not owned: not saved
        var file = SceneJson.Parse(SceneSaver.ToJson(root));
        root.Free();

        var nodes = file.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(["Level", "A", "B", "C"], nodes.Select(n => n.GetProperty("name").GetString()));
        Assert.False(nodes[0].TryGetProperty("parent", out _));
        Assert.Equal([".", "A", "."], nodes.Skip(1).Select(n => n.GetProperty("parent").GetString()));
        Assert.False(file.TryGetProperty("root", out _));

        var copy = PackedScene.Parse(SceneFormat.FormatJson(Encoding.UTF8.GetBytes(file.GetRawText()))).Instantiate();
        Assert.Equal(["A", "C"], copy.Children.Select(c => c.Name));
        Assert.Same(copy, copy.GetNode("A/B").Owner);
        copy.Free();
    }

    [Theory]
    [InlineData("""[ { "name": "R", "type": "Node3D", "parent": "." } ]""", "the first node is the root")]
    [InlineData("""[ { "name": "R", "type": "Node3D" }, { "name": "A", "type": "Node3D" } ]""", "node 'A' has no \"parent\"")]
    [InlineData("""[ { "name": "R", "type": "Node3D" }, { "name": "A", "type": "Node3D", "parent": "Missing" } ]""", "parent 'Missing' of 'A'")]
    [InlineData("""[ { "name": "R", "type": "Node3D" }, { "name": "A", "type": "Node3D", "parent": "." }, { "name": "A", "type": "Node3D", "parent": "." } ]""", "two nodes are named 'A'")]
    [InlineData("""[ { "name": "R", "type": "Node3D" }, { "name": "A", "type": "Node3D", "parent": "." }, { "name": "X", "type": "Node3D", "parent": "A/Inner" } ]""", "parent 'A/Inner' of 'X'")]
    [InlineData("""[]""", "must be a non-empty array")]
    public void InvalidNodeListsAreReported(string nodes, string message)
    {
        var json = Encoding.UTF8.GetBytes($$"""{ "format": 2, "nodes": {{nodes}} }""");
        var e = Assert.Throws<InvalidDataException>(() => PackedScene.Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerFormatsAreRejected()
    {
        var e = Assert.Throws<InvalidDataException>(() => PackedScene.Parse("""{ "format": 3, "nodes": [] }"""u8.ToArray()));
        Assert.Contains("reads up to 2", e.Message, StringComparison.Ordinal);
    }
}

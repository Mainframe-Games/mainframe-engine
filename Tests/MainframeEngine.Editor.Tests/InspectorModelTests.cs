using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine.Editor.Tests;

public sealed class InspectorModelTests : IDisposable
{
    private readonly AllHintsNode _node = new();
    private readonly InspectorModel _model;

    public InspectorModelTests() => _model = InspectorModel.Build(_node);

    public void Dispose() => _node.Free();

    private InspectorProperty P(string name) => _model.Find(name) ?? throw new InvalidOperationException($"No row {name}.");

    [Theory]
    [InlineData("Count", PropertyEditorKind.IntegerNumber)]
    [InlineData("Speed", PropertyEditorKind.FloatNumber)]
    [InlineData("Precise", PropertyEditorKind.FloatNumber)]
    [InlineData("Volume", PropertyEditorKind.Range)]
    [InlineData("Slots", PropertyEditorKind.Range)]
    [InlineData("Enabled", PropertyEditorKind.Bool)]
    [InlineData("Title", PropertyEditorKind.Text)]
    [InlineData("Notes", PropertyEditorKind.MultilineText)]
    [InlineData("Texture", PropertyEditorKind.FilePath)]
    [InlineData("Folder", PropertyEditorKind.DirectoryPath)]
    [InlineData("Mood", PropertyEditorKind.Enum)]
    [InlineData("Mask", PropertyEditorKind.Flags)]
    [InlineData("MoodAsFlags", PropertyEditorKind.Flags)]
    [InlineData("Offset", PropertyEditorKind.Vector2)]
    [InlineData("Direction", PropertyEditorKind.Vector3)]
    [InlineData("Weights", PropertyEditorKind.Vector4)]
    [InlineData("Orientation", PropertyEditorKind.Quaternion)]
    [InlineData("Tint", PropertyEditorKind.Color)]
    [InlineData("GlowColor", PropertyEditorKind.Color)]
    [InlineData("Frame", PropertyEditorKind.Transform)]
    [InlineData("CameraPath", PropertyEditorKind.NodePath)]
    [InlineData("Settings", PropertyEditorKind.Resource)]
    [InlineData("Samples", PropertyEditorKind.Array)]
    [InlineData("Tags", PropertyEditorKind.Array)]
    [InlineData("Position", PropertyEditorKind.Vector3)]
    [InlineData("ProcessMode", PropertyEditorKind.Enum)]
    public void EveryTypeAndHintGetsItsEditor(string property, PropertyEditorKind kind) => Assert.Equal(kind, P(property).Kind);

    [Fact]
    public void SectionsFollowDeclaringTypesThenExportGroupsInDeclarationOrder()
    {
        var titles = _model.Sections.Select(s => s.Title).ToArray();
        Assert.Equal(["Node", "Node3D", "Numbers", "Text", "Choices", "Math", "References", "AllHintsNode"], titles);
        Assert.Equal(["Count", "Speed", "Volume", "Slots", "Precise"], _model.Sections[2].Properties.Select(p => p.Name));
        Assert.Equal("Ungrouped", _model.Sections[^1].Properties.Single().Name); // an empty group name ends grouping
    }

    [Fact]
    public void RangesEnumsFiltersAndNodeTypesComeFromTheHints()
    {
        var volume = P("Volume");
        Assert.True(volume.HasRange);
        Assert.Equal((0d, 10d, 0.5d), (volume.Min, volume.Max, volume.Step));
        Assert.Equal(1d, P("Slots").Step); // integer ranges step by 1 by default

        Assert.Equal(["Calm", "Happy", "Grumpy"], P("Mood").EnumNames);
        Assert.Equal([("World", 1L), ("Player", 2L), ("Enemies", 4L)], P("Mask").FlagChoices());
        Assert.Equal(["*.png", "*.jpg"], P("Texture").FileFilter);
        Assert.Equal(typeof(Camera3D), P("CameraPath").NodeType);
        Assert.Equal(typeof(TestSettings), P("Settings").ResourceType);
        Assert.Equal(typeof(float), P("Samples").ElementType);
        Assert.Equal(typeof(string), P("Tags").ElementType);
        Assert.True(P("Frame").IsReadOnly);
    }

    [Fact]
    public void LabelsAreHumanized()
    {
        Assert.Equal("Cast Shadows", InspectorProperty.Humanize("CastShadows"));
        Assert.Equal("Rotation Degrees", InspectorProperty.Humanize("RotationDegrees"));
        Assert.Equal("FOV", InspectorProperty.Humanize("FOV"));
        Assert.Equal("UI Scale", InspectorProperty.Humanize("UIScale"));
        Assert.Equal("Mood As Flags", P("MoodAsFlags").Label);
    }

    [Fact]
    public void NumbersParseClampToTheirRangeAndRejectGarbage()
    {
        Assert.True(P("Count").TryParse(" 42 ", out var count));
        Assert.Equal(42, count);
        Assert.True(P("Count").TryParse("2.6", out count));
        Assert.Equal(3, count);
        Assert.True(P("Volume").TryParse("99", out var volume));
        Assert.Equal(10f, volume);
        Assert.True(P("Speed").TryParse("-1.25", out var speed));
        Assert.Equal(-1.25f, speed);
        Assert.False(P("Speed").TryParse("fast", out _));
        Assert.False(P("Speed").TryParse("NaN", out _));
    }

    [Fact]
    public void VectorComponentsEditOneAxisAndKeepTheOthers()
    {
        _node.Direction = new Vector3(1, 2, 3);
        var direction = P("Direction");
        Assert.Equal(3, direction.Components);
        Assert.Equal(["1", "2", "3"], Enumerable.Range(0, 3).Select(i => direction.FormatComponent(i)));

        Assert.True(direction.TryParseComponent(1, "7.5", out var value));
        Assert.Equal(new Vector3(1, 7.5f, 3), value);
        Assert.False(direction.TryParseComponent(0, "x", out _));

        Assert.True(P("Offset").TryParseComponent(0, "4", out var offset));
        Assert.Equal(new Vector2(4, 0), offset);
        Assert.True(P("Weights").TryParseComponent(3, "1", out var weights));
        Assert.Equal(new Vector4(0, 0, 0, 1), weights);
    }

    [Fact]
    public void QuaternionsAreEditedAsEulerDegrees()
    {
        _node.Orientation = EulerAngles.ToQuaternion(new Vector3(0, 90, 0));
        var orientation = P("Orientation");
        Assert.Equal("90", orientation.FormatComponent(1));
        Assert.True(orientation.TryParseComponent(0, "30", out var value));
        var degrees = EulerAngles.FromQuaternion((Quaternion)value!);
        Assert.Equal(30f, degrees.X, 3);
        Assert.Equal(90f, degrees.Y, 3);
    }

    [Fact]
    public void ColoursEditAsHexAndChannels()
    {
        var tint = P("Tint");
        Assert.Equal("#ff0000", tint.Format(_node.Tint));
        Assert.True(tint.TryParse("#00ff0080", out var parsed));
        Assert.Equal(DrawingColor.FromArgb(128, 0, 255, 0).ToArgb(), ((DrawingColor)parsed!).ToArgb());
        Assert.True(tint.TryParseComponent(2, "300", out var blue)); // channels clamp to 0..255
        Assert.Equal(255, ((DrawingColor)blue!).B);
        Assert.False(tint.IsFloatColor);

        var glow = P("GlowColor"); // a Vector3 named ...Color: 0..1 floats, HDR allowed in the fields
        Assert.True(glow.IsFloatColor);
        Assert.Equal(3, glow.Components);
        Assert.Equal("#ff8000", glow.Format(_node.GlowColor));
        Assert.True(glow.TryParseComponent(0, "2.5", out var hdr));
        Assert.Equal(new Vector3(2.5f, 0.5f, 0f), hdr);
        Assert.True(glow.TryParse("#0000ff", out var fromHex));
        Assert.Equal(new Vector3(0, 0, 1), fromHex);
    }

    [Fact]
    public void EnumsFlagsTextAndPathsParse()
    {
        Assert.True(P("Mood").TryParse("grumpy", out var mood));
        Assert.Equal(Mood.Grumpy, mood);
        Assert.True(P("Mask").TryParse("World, Enemies", out var mask));
        Assert.Equal(Layers.World | Layers.Enemies, mask);
        Assert.Equal(Layers.Player | Layers.Enemies, P("Mask").EnumFromRaw(6));
        Assert.True(InspectorProperty.HasFlag(Layers.Everything, 4));
        Assert.False(InspectorProperty.HasFlag(Layers.World, 4));
        Assert.True(P("Enabled").TryParse("false", out var enabled));
        Assert.Equal(false, enabled);
        Assert.True(P("Title").TryParse("  spaced  ", out var title));
        Assert.Equal("  spaced  ", title); // strings are kept as typed
        Assert.True(P("CameraPath").TryParse("../Cam", out var path));
        Assert.Equal("../Cam", ((NodePath)path!).Path);
    }

    [Fact]
    public void ResourcesAndArraysFormatAsSummaries()
    {
        Assert.Equal("None", P("Settings").Format(null));
        Assert.Equal("TestSettings (inline)", P("Settings").Format(new TestSettings()));
        Assert.Equal("2 items", P("Samples").Format(_node.Samples));
        Assert.Equal("1 item", P("Tags").Format(_node.Tags));
    }

    [Fact]
    public void ResourcesGetTheirOwnModel()
    {
        var model = InspectorModel.Build(new TestSettings());
        Assert.Contains(model.Properties, p => p.Name == "Strength" && p.Kind == PropertyEditorKind.FloatNumber);
    }

    [Fact]
    public void CustomInspectorsAreFoundByAttributeAndCanHideRows()
    {
        var model = InspectorModel.Build(new SignalNode());
        Assert.IsType<SignalNodeInspector>(model.CustomInspector);
        Assert.Null(model.Find("ProcessPriority"));
        Assert.NotNull(model.Find("Position"));
        Assert.Contains("custom-header", model.CustomInspector!.GetHeaderRml(model.Target), StringComparison.Ordinal);

        Assert.IsType<MissingNodeInspector>(InspectorModel.Build(new MissingNode()).CustomInspector);
        Assert.Null(InspectorModel.Build(new Node3D()).CustomInspector);
    }

    [Fact]
    public void ValueTextIsInvariantAndCompact()
    {
        Assert.Equal("0.1235", ValueText.Number(0.123456f));
        Assert.Equal("-3", ValueText.Number(-3f));
        Assert.Equal("1, 2", ValueText.Format(new Vector2(1, 2)));
        Assert.True(ValueText.TryParseColorHex("#abc", out var short3));
        Assert.Equal(0xaa / 255f, short3.X, 4);
        Assert.False(ValueText.TryParseColorHex("#12345", out _));
        Assert.Equal("Box.mres (TestSettings)", ValueText.Resource(LoadedResource()));
    }

    private static TestSettings LoadedResource()
    {
        var resource = new TestSettings();
        typeof(Resource).GetProperty(nameof(Resource.ResourcePath))!.SetValue(resource, "Content/Box.mres");
        return resource;
    }
}

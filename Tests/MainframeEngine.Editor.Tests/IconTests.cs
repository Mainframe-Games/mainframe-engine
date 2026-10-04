using System.Buffers.Binary;
using System.Text.RegularExpressions;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Tests;

/// <summary>The icon atlas as generated (Content/icons): names from icons.rcss, the list file, the sheet sizes.</summary>
internal static partial class IconAtlas
{
    /// <summary>Modifier classes of the markup contract (never icon names).</summary>
    public static readonly HashSet<string> Modifiers =
    [
        "sm", "lg", "3d", "2d", "ui", "audio", "physics", "net", "logic", "resource", "dir", "missing", "muted", "accent", "info", "warn",
        "error", "debug",
    ];

    public static string Root { get; } = FindRoot();

    public static string Folder => Path.Combine(Root, "MainframeEngine.Editor", "Content", "icons");

    /// <summary>The icons the 1x sprite sheet defines.</summary>
    public static HashSet<string> Names { get; } = ReadSheet("editor-icons", "i-");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "justfile")) && Directory.Exists(Path.Combine(dir.FullName, "MainframeEngine.Editor")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    public static HashSet<string> ReadSheet(string sheet, string prefix)
    {
        var text = File.ReadAllText(Path.Combine(Folder, "icons.rcss"));
        var start = text.IndexOf($"@spritesheet {sheet} {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"sprite sheet {sheet} missing from icons.rcss");
        var end = text.IndexOf('}', start);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Sprite().Matches(text[start..end]))
            if (m.Groups[1].Value == prefix)
                names.Add(m.Groups[2].Value);
        return names;
    }

    [GeneratedRegex(@"^\s*(i-|l-)([a-z0-9]+(?:-[a-z0-9]+)*):", RegexOptions.Multiline)]
    private static partial Regex Sprite();

    /// <summary>The names in icons.txt (comments and blank lines skipped).</summary>
    public static List<string> Listed() =>
        [.. File.ReadAllLines(Path.Combine(Folder, "icons.txt")).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)];

    /// <summary>Asserts <paramref name="icon"/> is in the atlas (with where it was found).</summary>
    public static void AssertExists(string icon, string where) =>
        Assert.True(Names.Contains(icon), $"icon '{icon}' ({where}) is not in the atlas: add it to MainframeEngine.Editor/Content/icons/icons.txt and run `just editor-icons`");

    /// <summary>The icon names in a class list (<c>icon icon-cube icon-3d</c> → cube).</summary>
    public static IEnumerable<string> IconsInClasses(string classes)
    {
        foreach (var token in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("icon-", StringComparison.Ordinal) && token.Length > 5 && !Modifiers.Contains(token[5..]) &&
                token.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                yield return token[5..];
    }
}

public sealed partial class IconAtlasTests
{
    [Fact]
    public void TheGeneratedAtlasMatchesTheListAndEverySheetHasEveryIcon()
    {
        var listed = IconAtlas.Listed();
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(listed.OrderBy(n => n, StringComparer.Ordinal), IconAtlas.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(IconAtlas.Names, IconAtlas.ReadSheet("editor-icons-lg", "l-"));
        Assert.Equal(IconAtlas.Names, IconAtlas.ReadSheet("editor-icons-2x", "i-"));
        Assert.Equal(IconAtlas.Names, IconAtlas.ReadSheet("editor-icons-lg-2x", "l-"));
        foreach (var name in listed)
            Assert.DoesNotContain(name, IconAtlas.Modifiers);

        // Sheets: 16 columns of 20-unit cells, rendered at 1x, 1.5x, 2x and 3x.
        var rows = (listed.Count + 15) / 16;
        foreach (var (suffix, scale) in new[] { (16, 1.0), (24, 1.5), (32, 2.0), (48, 3.0) })
        {
            var (width, height) = PngSize(Path.Combine(IconAtlas.Folder, $"icons-{suffix}.png"));
            Assert.Equal((int)(16 * 20 * scale), width);
            Assert.Equal((int)(rows * 20 * scale), height);
        }

        // Every listed icon is vendored (the SVG the atlas was built from).
        var vendor = Path.Combine(IconAtlas.Root, "MainframeEngine.Editor", "Icons", "tabler");
        Assert.True(File.Exists(Path.Combine(vendor, "LICENSE")));
        foreach (var name in listed)
        {
            var svg = name.EndsWith("-filled", StringComparison.Ordinal)
                ? Path.Combine(vendor, "filled", name[..^7] + ".svg")
                : Path.Combine(vendor, "outline", name + ".svg");
            Assert.True(File.Exists(svg), $"{svg} is missing");
        }
    }

    private static (int Width, int Height) PngSize(string path)
    {
        var header = new byte[24];
        using (var stream = File.OpenRead(path))
            stream.ReadExactly(header);
        Assert.True(header[1] == 'P' && header[2] == 'N' && header[3] == 'G', $"{path} is not a PNG (Git LFS not pulled?)");
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
    }

    [GeneratedRegex(@"(?<![-\w])class=""([^""]*)""")]
    private static partial Regex ClassAttribute();

    [GeneratedRegex(@"data-class-icon-([a-z0-9]+(?:-[a-z0-9]+)*)=")]
    private static partial Regex DataClassIcon();

    [GeneratedRegex(@"image\((?:i|l)-([a-z0-9]+(?:-[a-z0-9]+)*)\)")]
    private static partial Regex SpriteReference();

    [GeneratedRegex(@"""([^""\n]*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"(?<![a-z-])icon-([a-z0-9]+(?:-[a-z0-9]+)*)")]
    private static partial Regex IconToken();

    [GeneratedRegex(@"(?:EditorIcon\(|Icon\s*=\s*|Icon:\s*)""([a-z0-9]+(?:-[a-z0-9]+)*)""")]
    private static partial Regex IconArgument();

    /// <summary>
    /// Every icon the editor's markup, styles and C# name (RML class lists, sprite references, icon classes in generated
    /// RML, <c>[EditorIcon]</c>, <c>Export(Icon)</c> and <c>Icon:</c> arguments) is in the atlas.
    /// </summary>
    [Fact]
    public void EveryIconReferencedByTheEditorSourcesIsInTheAtlas()
    {
        var found = 0;
        var content = Path.Combine(IconAtlas.Root, "MainframeEngine.Editor", "Content", "Editor");
        foreach (var file in Directory.EnumerateFiles(content, "*.r*"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ClassAttribute().Matches(text))
                foreach (var icon in IconAtlas.IconsInClasses(m.Groups[1].Value))
                {
                    IconAtlas.AssertExists(icon, Path.GetFileName(file));
                    found++;
                }

            foreach (Match m in DataClassIcon().Matches(text))
                IconAtlas.AssertExists(m.Groups[1].Value, Path.GetFileName(file));
            foreach (Match m in SpriteReference().Matches(text))
                IconAtlas.AssertExists(m.Groups[1].Value, Path.GetFileName(file));
        }

        foreach (var folder in new[] { "MainframeEngine.Editor/Src", "MainframeEngine/Src", "MainframeEngine.Sandbox/Src" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(IconAtlas.Root, folder), "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in IconArgument().Matches(text))
                {
                    IconAtlas.AssertExists(m.Groups[1].Value, Path.GetFileName(file));
                    found++;
                }

                foreach (Match literal in StringLiteral().Matches(text))
                    foreach (Match m in IconToken().Matches(literal.Groups[1].Value))
                        if (!IconAtlas.Modifiers.Contains(m.Groups[1].Value))
                            IconAtlas.AssertExists(m.Groups[1].Value, Path.GetFileName(file));
            }
        }

        Assert.True(found > 150, $"only {found} icon references found: is the scan still looking in the right places?");
    }

    [Fact]
    public void EveryEngineNodeAndResourceTypeHasItsOwnIconAndAFamily()
    {
        var engine = TypeRegistry.All.Where(t => t.Type.Assembly == typeof(Node).Assembly).ToList();
        Assert.True(engine.Count > 60);
        foreach (var info in engine)
        {
            var icon = EditorIcons.For(info.Type);
            IconAtlas.AssertExists(icon, info.Name);
            if (info.Type != typeof(Node))
                Assert.True(icon != EditorIcons.Default, $"{info.Name} resolves to the default node icon");
            Assert.NotEqual(EditorIconFamily.Inherit, EditorIcons.FamilyOf(info.Type));
            Assert.StartsWith("icon icon-" + icon + " icon-", EditorIcons.Classes(info.Type), StringComparison.Ordinal);
            if (info.IsResource)
                Assert.Equal(EditorIconFamily.Resource, EditorIcons.FamilyOf(info.Type));
        }
    }

    [Theory]
    [InlineData(typeof(Node), "circle-dot", "icon-logic")]
    [InlineData(typeof(Node3D), "axis-x", "icon-3d")]
    [InlineData(typeof(Node2D), "axis-y", "icon-2d")]
    [InlineData(typeof(MeshInstance3D), "cube", "icon-3d")]
    [InlineData(typeof(Camera3D), "video", "icon-3d")]
    [InlineData(typeof(DirectionalLight3D), "sun", "icon-3d")]
    [InlineData(typeof(SpotLight3D), "lamp", "icon-3d")]
    [InlineData(typeof(AudioPlayer3D), "volume", "icon-audio")]
    [InlineData(typeof(AudioListener3D), "ear", "icon-audio")]
    [InlineData(typeof(RigidBody3D), "ball-bowling", "icon-physics")]
    [InlineData(typeof(CharacterBody2D), "run", "icon-physics")]
    [InlineData(typeof(Area3D), "border-corners", "icon-physics")]
    [InlineData(typeof(CollisionShape3D), "shape", "icon-physics")]
    [InlineData(typeof(UiDocument), "layout", "icon-ui")]
    [InlineData(typeof(NetworkNode), "network", "icon-net")]
    [InlineData(typeof(Timer), "clock", "icon-logic")]
    [InlineData(typeof(SubViewport), "device-desktop", "icon-logic")]
    [InlineData(typeof(SpineNode), "bone", "icon-3d")]
    [InlineData(typeof(StandardMaterial3D), "palette", "icon-resource")]
    [InlineData(typeof(Texture2D), "photo", "icon-resource")]
    [InlineData(typeof(AudioStream), "music", "icon-resource")]
    [InlineData(typeof(PackedScene), "movie", "icon-resource")]
    [InlineData(typeof(AudioBusLayout), "adjustments", "icon-resource")]
    [InlineData(typeof(BoxMesh), "cube", "icon-resource")]
    [InlineData(typeof(SphereShape3D), "sphere", "icon-resource")]
    public void EngineTypesUseTheirDeclaredIconAndFamily(Type type, string icon, string family)
    {
        Assert.Equal(icon, EditorIcons.For(type));
        Assert.Equal(family, EditorIcons.Family(type));
    }

    [Fact]
    public void GameTypesDeclareIconsAndSubclassesInheritThem()
    {
        Assert.Equal("bolt", EditorIcons.For(typeof(IconNode)));
        Assert.Equal("icon-net", EditorIcons.Family(typeof(IconNode)));
        Assert.Equal("bolt", EditorIcons.For(typeof(InheritsIconNode)));
        Assert.Equal("icon-net", EditorIcons.Family(typeof(InheritsIconNode)));
        // No icon of its own: the nearest base's (Node3D), and its family.
        Assert.Equal("axis-x", EditorIcons.For(typeof(AllHintsNode)));
        Assert.Equal("icon-3d", EditorIcons.Family(typeof(AllHintsNode)));
        Assert.Equal("package", EditorIcons.For(typeof(TestSettings)));
        // A missing type is red; the cached class list is the same instance every time (no allocation on refresh).
        Assert.Equal("icon icon-help-hexagon icon-missing", EditorIcons.Classes(new MissingNode()));
        Assert.Same(EditorIcons.Classes(typeof(IconNode)), EditorIcons.Classes(typeof(IconNode)));
        // The generator recorded the attribute on the type info.
        Assert.Equal("bolt", TypeRegistry.Get(typeof(IconNode))!.Icon);
        Assert.Null(TypeRegistry.Get(typeof(InheritsIconNode))!.Icon);
        Assert.Equal("A subclass without an icon: it inherits IconNode's.", TypeRegistry.Get(typeof(InheritsIconNode))!.Description);
    }

    [Theory]
    [InlineData("Content/Scenes/Main.mscene", false, "movie", "icon-3d")]
    [InlineData("a/b/Mat.mres", false, "package", "icon-resource")]
    [InlineData("tex.PNG", false, "photo", "icon-resource")]
    [InlineData("music.ogg", false, "file-music", "icon-audio")]
    [InlineData("model.glb", false, "file-3d", "icon-3d")]
    [InlineData("Lato.ttf", false, "typography", "icon-resource")]
    [InlineData("hud.rml", false, "file-type-html", "icon-ui")]
    [InlineData("hud.rcss", false, "file-type-css", "icon-ui")]
    [InlineData("es.po", false, "language", "")]
    [InlineData("Mesh.vk.frag", false, "file-code", "")]
    [InlineData("Mesh.vk.frag.spv", false, "binary", "")]
    [InlineData("Player.cs", false, "brand-c-sharp", "")]
    [InlineData("project.mfproj", false, "file-settings", "")]
    [InlineData("data.json", false, "braces", "")]
    [InlineData("README.md", false, "file-text", "")]
    [InlineData("thing.xyz", false, "file", "")]
    [InlineData("Content", true, "folder", "icon-dir")]
    public void FilesGetIconsByExtension(string path, bool directory, string icon, string family)
    {
        Assert.Equal(icon, EditorIcons.ForFile(path, directory));
        Assert.Equal(family, EditorIcons.FileFamily(path, directory));
        IconAtlas.AssertExists(icon, path);
    }

    [Fact]
    public void EveryResolverIconIsInTheAtlas()
    {
        foreach (var kind in Enum.GetValues<PropertyEditorKind>())
            IconAtlas.AssertExists(PropertyIcons.KindIcon(kind), kind.ToString());
        foreach (var name in new[]
                 {
                     "Position", "Rotation", "RotationDegrees", "Scale", "Visible", "Text", "Volume", "VolumeDb", "Mass", "Energy", "Range", "Fov",
                     "Size", "CollisionLayer", "ProcessMode", "Autoplay", "Loop", "Bus", "WaitTime", "OneShot", "CastShadows", "Exposure",
                     "AutoTranslateMode", "UniqueNameInOwner", "Current", "GravityScale", "Friction", "Bounce", "Pitch", "Tags", "Name",
                     "AlbedoColor", "LinearVelocity", "MaxSpeed", "ShadowBias",
                 })
            IconAtlas.AssertExists(PropertyIcons.SemanticIcon(name) ?? "?", name);
        foreach (var level in Enum.GetValues<OutputLevel>())
            foreach (var icon in IconAtlas.IconsInClasses(OutputCategories.LevelIcon(level)))
                IconAtlas.AssertExists(icon, level.ToString());
        foreach (var category in new[] { "Render", "Audio", "Physics", "Net", "UI", "L10n", "Game", "Editor", "Scene", "Other" })
            foreach (var icon in IconAtlas.IconsInClasses(OutputCategories.IconOf(category)))
                IconAtlas.AssertExists(icon, category);
    }

    [Fact]
    public void PropertyIconsAreSemanticThenByKindAndDeclaredIconsWin()
    {
        var node = new IconNode();
        var model = InspectorModel.Build(node);
        Assert.Equal("gauge", PropertyIcons.For(model.Find("Charge")!, 2f)); // [Export(Icon = "gauge")]
        Assert.Equal("target", PropertyIcons.For(model.Find("Aim")!, new NodePath(""))); // [EditorIcon] on the member
        Assert.Equal("letter-t", PropertyIcons.For(model.Find("Note")!, ""));
        Assert.Equal("arrows-move", PropertyIcons.For(model.Find("Position")!, null));
        Assert.Equal("rotate", PropertyIcons.For(model.Find("RotationDegrees")!, null));
        Assert.Equal("eye", PropertyIcons.For(model.Find("Visible")!, null));

        var hints = InspectorModel.Build(new AllHintsNode());
        Assert.Equal("hash", PropertyIcons.For(hints.Find("Count")!, 3));
        Assert.Equal("toggle-left", PropertyIcons.For(hints.Find("Enabled")!, true));
        Assert.Equal("list", PropertyIcons.For(hints.Find("Mood")!, Mood.Happy));
        Assert.Equal("list-check", PropertyIcons.For(hints.Find("Mask")!, Layers.World));
        Assert.Equal("axis-x", PropertyIcons.For(hints.Find("Direction")!, null));
        Assert.Equal("brackets", PropertyIcons.For(hints.Find("Samples")!, null));
        Assert.Equal("palette", PropertyIcons.For(hints.Find("GlowColor")!, null));
        Assert.Equal("gauge", PropertyIcons.For(hints.Find("Speed")!, 1f));
        Assert.Equal("volume", PropertyIcons.For(hints.Find("Volume")!, 5f));
        // A NodePath with a node-type hint shows that type's icon; a resource slot its resource type's.
        Assert.Equal("video", PropertyIcons.For(hints.Find("CameraPath")!, new NodePath("")));
        Assert.Equal("icon icon-video icon-3d", PropertyIcons.Classes(hints.Find("CameraPath")!, new NodePath("")));
        Assert.Equal("package", PropertyIcons.For(hints.Find("Settings")!, null));
        Assert.Equal("icon icon-palette icon-resource", PropertyIcons.Classes(
            InspectorModel.Build(new MeshInstance3D()).Find("MaterialOverride")!, new StandardMaterial3D()));
    }

    [Fact]
    public void PropertyTooltipsCarryTheDocSummaryHintsAndType()
    {
        var charge = InspectorModel.Build(new IconNode()).Find("Charge")!;
        Assert.Equal("Charge — How fast the IconNode charges, in units per second.\nCharge: float", charge.Tooltip);
        var volume = InspectorModel.Build(new AllHintsNode()).Find("Volume")!;
        Assert.Equal("Volume\nRange 0 … 10, step 0.5\nVolume: float", volume.Tooltip);
        var texture = InspectorModel.Build(new AllHintsNode()).Find("Texture")!;
        Assert.Contains("Files: *.png, *.jpg", texture.Tooltip, StringComparison.Ordinal);
        var camera = InspectorModel.Build(new AllHintsNode()).Find("CameraPath")!;
        Assert.Contains("Points to a Camera3D", camera.Tooltip, StringComparison.Ordinal);
        // Engine members carry their doc summaries too (generator).
        var position = InspectorModel.Build(new Node3D()).Find("Position")!;
        Assert.StartsWith("Position — ", position.Tooltip, StringComparison.Ordinal);
        Assert.EndsWith("Position: Vector3", position.Tooltip, StringComparison.Ordinal);
        Assert.Equal("List<string>", InspectorProperty.TypeName(typeof(List<string>)));
        Assert.Equal("float[]", InspectorProperty.TypeName(typeof(float[])));
    }
}

/// <summary>The editor UI headless: every icon element names an atlas icon, every icon-only control has a tooltip.</summary>
[Collection(nameof(SerialEditor))]
public sealed class IconLintTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    // Controls that act on a click.
    private static bool IsControl(RmlElement e, string classes) =>
        e.TagName == "button" || classes.Split(' ').Any(c => c is "tool-button" or "flat-button" or "tab-new" or "tab-close" or "eye" or "star" or "source");

    private static string VisibleText(string rml) => Regex.Replace(Regex.Replace(rml, "<[^>]*>", ""), @"\s+", "");

    private static IEnumerable<RmlElement> Walk(RmlElement element)
    {
        yield return element;
        for (var i = 0; i < element.ChildCount; i++)
            foreach (var child in Walk(element.GetChild(i)))
                yield return child;
    }

    /// <summary>Checks every loaded document of the panel and dialog layers; returns how many icons were checked.</summary>
    private int Lint(List<string> problems)
    {
        var icons = 0;
        foreach (var layer in new[] { W.PanelLayer, W.DialogLayer, W.TooltipLayer })
        {
            foreach (var document in layer.Documents)
            {
                if (!document.IsLoaded || !document.Visible)
                    continue;
                foreach (var e in Walk(document.Document.AsElement()))
                {
                    if (e.HasAttribute("data-for"))
                        continue; // a data-for template (its bindings are not evaluated)
                    var classes = e.GetAttribute("class") ?? "";
                    var tokens = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Contains("icon"))
                    {
                        var names = IconAtlas.IconsInClasses(classes).ToList();
                        if (names.Count != 1)
                            problems.Add($"{document.Name}: icon element with {names.Count} icon names: class=\"{classes}\"");
                        foreach (var name in names)
                            if (!IconAtlas.Names.Contains(name))
                                problems.Add($"{document.Name}: unknown icon '{name}'");
                        icons++;
                    }

                    if (IsControl(e, classes) && VisibleText(e.InnerRml).Length == 0 && !e.HasAttribute("data-tooltip"))
                        problems.Add($"{document.Name}: icon-only <{e.TagName} class=\"{classes}\" id=\"{e.Id}\"> has no data-tooltip");
                }
            }
        }

        return icons;
    }

    [Fact]
    public void EveryIconNamesAnAtlasIconAndEveryIconOnlyControlHasATooltip()
    {
        var problems = new List<string>();
        var scene = _editor.Scene;
        var hints = new AllHintsNode { Name = "Hints", Settings = new TestSettings() };
        scene.AddNode(hints, scene.Root);
        scene.AddNode(new CollisionShape3D { Name = "Loose" }, scene.Root); // warning badge
        scene.AddNode(new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh() }, scene.Root);
        _editor.Tick(2);
        scene.Selection.Set(hints);
        W.Inspector.RunAction(W.Inspector.Rows.ToList().FindIndex(r => r.Name == "Settings"), "res-edit");
        _editor.Tick(3);
        var checkedIcons = Lint(problems);

        // Each dialog and menu once.
        W.Commands.Execute("node.add");
        _editor.Tick(3);
        checkedIcons += Lint(problems);
        W.TreePicker.Cancel();
        W.MenuBar.OpenMenu("edit");
        _editor.Tick(2);
        checkedIcons += Lint(problems);
        W.Popup.Close();
        W.Commands.Execute("file.open");
        _editor.Tick(3);
        checkedIcons += Lint(problems);
        W.FilePicker.Cancel();
        W.Commands.Execute("help.about");
        _editor.Tick(2);
        checkedIcons += Lint(problems);
        W.Message.Answer(0);
        _editor.Tick();

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        Assert.True(checkedIcons > 120, $"only {checkedIcons} icons seen");
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void EveryMenuItemHasAnIcon()
    {
        foreach (var menu in new[] { "file", "edit", "view", "project", "run", "help" })
            foreach (var item in W.Commands.MenuItems(menu).Where(i => !i.IsSeparator && !i.IsHeader))
            {
                Assert.False(string.IsNullOrEmpty(item.Icon), $"{menu} › {item.Label} has no icon");
                IconAtlas.AssertExists(item.Icon!, item.Label);
            }
    }
}

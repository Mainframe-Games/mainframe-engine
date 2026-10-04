using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>Tooltips, the scene tree's icons and badges, the inspector's icons and the Output panel, headless.</summary>
[Collection(nameof(SerialEditor))]
public sealed class IconPanelTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private static Vector2 Center(RmlElement element) => new(element.Bounds.X + element.Bounds.Width / 2, element.Bounds.Y + element.Bounds.Height / 2);

    private void MoveTo(Vector2 point)
    {
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = point });
        _editor.Tick();
    }

    private RmlElement Toolbar(string id) => W.Toolbar.Document.GetElementById(id);

    // ── Tooltips ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATooltipShowsAfterTheDelayBelowItsElementAndHidesOnClick()
    {
        var move = Toolbar("tool-translate");
        MoveTo(Center(move));
        _editor.Tick(25); // < 0.5 s
        Assert.False(W.Tooltips!.IsShown);
        Assert.Null(W.Tooltips.Text);

        _editor.Tick(8);
        Assert.True(W.Tooltips.IsShown);
        Assert.Equal("Move (W) — translate selected nodes", W.Tooltips.Text);
        var tip = W.Tooltips.Placement;
        Assert.True(tip.Y >= move.Bounds.Y + move.Bounds.Height, "below the button");
        Assert.True(tip.Width > 40 && tip.Height > 20, $"measured {tip}");

        // A click hides it, and it stays hidden while the pointer stays on the same button.
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = Center(move) });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = Center(move) });
        _editor.Tick(60);
        Assert.False(W.Tooltips.IsShown);

        // Another element shows its own after the delay; a plain area shows none.
        MoveTo(Center(Toolbar("tool-rotate")));
        _editor.Tick(35);
        Assert.Equal("Rotate (E) — rotate selected nodes", W.Tooltips.Text);
        MoveTo(Center(W.ViewportPanel.Document.GetElementById("view")));
        _editor.Tick(40);
        Assert.False(W.Tooltips.IsShown);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TooltipsStayInsideTheWindowAndShowOnKeyboardFocus()
    {
        // The frame-rate readout sits at the right edge: the tooltip is moved left to fit.
        var stats = Toolbar("stats");
        MoveTo(Center(stats));
        _editor.Tick(35);
        Assert.True(W.Tooltips!.IsShown);
        var window = _editor.Host.WindowSize;
        Assert.True(W.Tooltips.Placement.Right <= window.X - TooltipOverlay.Gap + 0.5f, $"{W.Tooltips.Placement} in {window}");
        Assert.True(W.Tooltips.Placement.X >= TooltipOverlay.Gap);

        // Keyboard focus (focus-visible) shows the focused element's tooltip with the pointer elsewhere.
        MoveTo(Center(W.ViewportPanel.Document.GetElementById("view")));
        _editor.Tick(2);
        Toolbar("snap-step").Focus(focusVisible: true);
        _editor.Tick(35);
        Assert.StartsWith("Snap Step — ", W.Tooltips.Text, StringComparison.Ordinal);
        Toolbar("snap-step").Blur();
    }

    [Fact]
    public void TooltipTextBecomesATitleAKeyCapAndLines()
    {
        var rml = TooltipOverlay.ToRml("Save (Ctrl+S) — write the scene\nSecond line");
        Assert.Contains("<span class=\"tip-title\">Save</span>", rml, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsMacOS() ? ">Cmd+S<" : ">Ctrl+S<", rml, StringComparison.Ordinal);
        Assert.Contains("<div class=\"tip-desc\">write the scene</div>", rml, StringComparison.Ordinal);
        Assert.Contains("<div class=\"tip-line\">Second line</div>", rml, StringComparison.Ordinal);
        Assert.Equal("<div class=\"tip-line\">Just text &amp; more</div>", TooltipOverlay.ToRml("Just text & more"));
        Assert.Contains("<span class=\"tip-title\">Position</span><", TooltipOverlay.ToRml("Position — where it is"), StringComparison.Ordinal);
    }

    [Fact]
    public void IdleFramesWithATooltipShownAllocateNothing()
    {
        MoveTo(Center(Toolbar("tool-translate")));
        _editor.Tick(120);
        Assert.True(W.Tooltips!.IsShown);
        var best = long.MaxValue;
        for (var window = 0; window < 3; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _editor.Tick(100);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best == 0, $"{best} B allocated over 100 idle frames with a tooltip shown");
    }

    // ── Scene tree ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TreeRowsShowTypeIconsBadgesAndAnUndoableVisibilityToggle()
    {
        var scene = _editor.Scene;
        var mesh = (MeshInstance3D)scene.AddNode(new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh() }, scene.Root);
        var loose = scene.AddNode(new CollisionShape3D { Name = "Loose" }, scene.Root);
        var body = scene.AddNode(new StaticBody3D { Name = "Body" }, scene.Root);
        var game = scene.AddNode(new AllHintsNode { Name = "Game" }, scene.Root);
        var timer = scene.AddNode(new Timer { Name = "Clock" }, scene.Root);
        _editor.Tick();
        SceneTreeRow Row(Node node) => W.SceneTree.Model.Rows.Single(r => ReferenceEquals(r.Node, node));

        Assert.Equal("icon icon-cube icon-3d", Row(mesh).Icon);
        Assert.Equal("Mesh — MeshInstance3D", Row(mesh).Tooltip);
        Assert.Equal("", Row(mesh).Warning);
        Assert.Contains("only works as a child", Row(loose).Warning, StringComparison.Ordinal);
        Assert.Contains("No Shape", Row(loose).Warning, StringComparison.Ordinal);
        Assert.Contains("add a CollisionShape3D", Row(body).Warning, StringComparison.Ordinal);
        Assert.Equal("icon icon-sm icon-code", Row(game).Script); // a game (non-engine) type: script badge
        Assert.Equal("", Row(mesh).Script);
        Assert.Null(Row(timer).VisibleProperty); // no Visible flag: no eye
        Assert.NotNull(Row(mesh).VisibleProperty);

        var index = W.SceneTree.Model.IndexOf(mesh);
        Assert.True(W.SceneTree.ToggleVisible(index));
        _editor.Tick();
        Assert.False(mesh.Visible);
        Assert.False(Row(mesh).Shown);
        scene.History.Undo();
        _editor.Tick();
        Assert.True(mesh.Visible);
        Assert.Empty(_editor.RmlMessages);
    }

    // ── Inspector ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInspectorShowsTheTypeIconAndAnIconAndTooltipOnEveryRow()
    {
        var scene = _editor.Scene;
        var node = scene.AddNode(new IconNode { Name = "Thing" }, scene.Root);
        scene.Selection.Set(node);
        _editor.Tick(2);

        var document = W.Inspector.Document;
        var head = document.QuerySelector(".insp-icon");
        Assert.Contains("icon-bolt", head.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Contains("icon-net", head.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Contains("t-net", document.QuerySelector(".insp-type-name").GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal(W.Inspector.Rows.Count, document.AsElement().QuerySelectorAll(".prop-icon").Length);
        var labels = document.AsElement().QuerySelectorAll(".prop-label");
        Assert.Equal(W.Inspector.Rows.Count, labels.Length);
        Assert.All(labels, l => Assert.False(string.IsNullOrEmpty(l.GetAttribute("data-tooltip"))));
        Assert.Contains(labels, l => l.GetAttribute("data-tooltip")!.StartsWith("Charge — How fast", StringComparison.Ordinal));
        // Section headers carry their declaring type's icon.
        Assert.Contains(document.AsElement().QuerySelectorAll(".section-icon"), e => e.GetAttribute("class")!.Contains("icon-axis-x", StringComparison.Ordinal));
    }

    // ── Output ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OutputLinesHaveLevelAndCategoryIconsAndRepeatsCollapse()
    {
        W.Output.Clear();
        W.Output.Add(OutputLevel.Info, "[Audio] device started");
        W.Output.Add(OutputLevel.Warning, "same");
        W.Output.Add(OutputLevel.Warning, "same");
        W.Output.Add(OutputLevel.Warning, "same");
        W.Output.Add(OutputLevel.Error, "boom");
        _editor.Tick();
        var panel = W.OutputPanel;

        var audio = panel.VisibleMessages[0];
        Assert.Equal("Audio", audio.Category);
        Assert.Equal("device started", audio.Text);
        Assert.Contains("icon-volume", audio.CategoryIcon, StringComparison.Ordinal);
        Assert.Contains("icon-info-circle", audio.LevelIcon, StringComparison.Ordinal);
        Assert.Contains("icon-alert-circle", panel.VisibleMessages[^1].LevelIcon, StringComparison.Ordinal);
        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(3, panel.CountOf(OutputLevel.Warning));

        panel.SetCollapseDuplicates(true);
        Assert.Equal(3, panel.Rows.Count);
        Assert.Equal(3, panel.Rows[1].Count);
        Assert.True(W.Layout.Settings.OutputCollapse); // persisted
        W.Output.Add(OutputLevel.Error, "boom");
        _editor.Tick();
        Assert.Equal(2, panel.Rows[^1].Count); // a new repeat folds into the last line

        panel.SetQuery("DEVICE");
        Assert.Equal(["device started"], panel.VisibleMessages.Select(m => m.Text));
        panel.SetQuery("audio"); // the category matches too
        Assert.Single(panel.Rows);
        panel.SetQuery("");

        var copied = panel.Copy();
        Assert.Contains("INFO [Audio] device started", copied, StringComparison.Ordinal);
        Assert.Contains("WARN same (×3)", copied, StringComparison.Ordinal);
        Assert.Equal(copied, _editor.Server.ClipboardText);

        panel.SetFollow(false);
        Assert.False(W.Layout.Settings.OutputFollow);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void OutputLinesLinkToTheLoggingSourceLine()
    {
        string? opened = null;
        var openedLine = 0;
        W.SourceOpener = (file, line) =>
        {
            opened = file;
            openedLine = line;
            return true;
        };
        W.Output.Clear();
        Log.Warning("output-source-link-test");
        _editor.Tick(2);
        var panel = W.OutputPanel;
        var index = panel.Rows.ToList().FindIndex(r => r.Message.Text == "output-source-link-test");
        Assert.True(index >= 0);
        var message = panel.Rows[index].Message;
        Assert.True(message.HasSource);
        Assert.EndsWith("IconPanelTests.cs", message.CallerFile, StringComparison.Ordinal);
        Assert.StartsWith("Open IconPanelTests.cs:", message.SourceText, StringComparison.Ordinal);
        Assert.True(panel.OpenSource(index));
        Assert.Equal(message.CallerFile, opened);
        Assert.Equal(message.CallerLine, openedLine);

        // Messages without a known source have no link.
        W.Output.Add(OutputLevel.Info, "no source");
        _editor.Tick();
        Assert.False(panel.OpenSource(panel.Rows.Count - 1));
    }

    [Theory]
    [InlineData("Render", "icon-brush")]
    [InlineData("Vulkan", "icon-brush")]
    [InlineData("Audio", "icon-volume")]
    [InlineData("Physics", "icon-atom")]
    [InlineData("Net", "icon-network")]
    [InlineData("EditorLink", "icon-network")]
    [InlineData("UI", "icon-layout")]
    [InlineData("RmlUi", "icon-layout")]
    [InlineData("L10n", "icon-language")]
    [InlineData("Game", "icon-device-gamepad-2")]
    [InlineData("Editor", "icon-tool")]
    [InlineData("Scene", "icon-package")]
    [InlineData("Whatever", "icon-point")]
    public void LogCategoriesMapToSubsystemIcons(string category, string icon) =>
        Assert.Contains(icon + " ", OutputCategories.IconOf(category) + " ", StringComparison.Ordinal);

    [Theory]
    [InlineData("[Editor] Saved x", "Editor", "Saved x")]
    [InlineData("[A B] not a category", "", "[A B] not a category")]
    [InlineData("plain", "", "plain")]
    [InlineData("[x]no space", "", "[x]no space")]
    public void ALeadingBracketedNameIsTheCategory(string text, string category, string rest) =>
        Assert.Equal((category, rest), OutputCategories.SplitPrefix(text));

    // ── Menus and history ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UndoHistoryEntriesShowWhatTheActionDid()
    {
        var scene = _editor.Scene;
        var node = (Node3D)scene.AddNode(new Node3D { Name = "N" }, scene.Root);
        scene.SetProperty(node, Serialization.TypeRegistry.GetNearest(typeof(Node3D))!.FindProperty("Position")!, new Vector3(1, 2, 3));
        scene.Rename(node, "M");
        scene.Delete([node]);
        var icons = scene.History.Actions.Select(EditorCommands.HistoryIcon).ToArray();
        Assert.Equal(["circle-plus", "arrows-move", "pencil", "trash"], icons);
    }
}

/// <summary>The create dialog's tree model, without UI.</summary>
public sealed class PickerTreeTests
{
    private static PickerTree Types() => new(
    [
        new PickerEntry("Node", "Node", "icon icon-circle-dot", null, 1),
        new PickerEntry("Node3D", "Node3D", "icon icon-axis-x", "Node", 2),
        new PickerEntry("Light3D", "Light3D", "icon icon-bulb", "Node3D", null) { Selectable = false },
        new PickerEntry("OmniLight3D", "OmniLight3D", "icon icon-bulb", "Light3D", 3),
        new PickerEntry("SpotLight3D", "SpotLight3D", "icon icon-lamp", "Light3D", 4),
        new PickerEntry("MeshInstance3D", "MeshInstance3D", "icon icon-cube", "Node3D", 5),
        new PickerEntry("Timer", "Timer", "icon icon-clock", "Node", 6),
        new PickerEntry("Orphan", "Orphan", "icon icon-point", "NotListed", 7), // unknown parent: a root
    ]);

    [Fact]
    public void EntriesFormASortedTreeAndCollapse()
    {
        var tree = Types();
        Assert.Equal(["Node", "Node3D", "Light3D", "OmniLight3D", "SpotLight3D", "MeshInstance3D", "Timer", "Orphan"], tree.Rows.Select(r => r.Entry.Label));
        Assert.Equal([0, 1, 2, 3, 3, 2, 1, 0], tree.Rows.Select(r => r.Depth));
        tree.Toggle(2); // collapse Light3D
        Assert.DoesNotContain("OmniLight3D", tree.Rows.Select(r => r.Entry.Label));
        tree.Select(tree.Find("SpotLight3D")); // selecting reveals it
        Assert.Contains("SpotLight3D", tree.Rows.Select(r => r.Entry.Label));
        Assert.True(tree.Rows[tree.SelectedRow].Selected);
    }

    [Fact]
    public void SearchKeepsAncestorsAndSelectsTheBestMatch()
    {
        var tree = Types();
        tree.SetQuery("light");
        Assert.Equal(["Node", "Node3D", "Light3D", "OmniLight3D", "SpotLight3D"], tree.Rows.Select(r => r.Entry.Label));
        Assert.Equal([false, false, true, true, true], tree.Rows.Select(r => r.Match));
        Assert.Equal("OmniLight3D", tree.Selected?.Label); // Light3D matches best but cannot be created
        tree.SetQuery("mi3"); // fuzzy: M…I…3
        Assert.Equal("MeshInstance3D", tree.Selected?.Label);
        tree.SetQuery("zzz");
        Assert.Empty(tree.Rows);
        Assert.Null(tree.Selected);
        tree.SetQuery("");
        Assert.Equal("Node", tree.Selected?.Label);
    }

    [Fact]
    public void MovingSkipsStructureOnlyRows()
    {
        var tree = Types();
        tree.Select(tree.Find("Node3D"));
        tree.Move(1);
        Assert.Equal("OmniLight3D", tree.Selected?.Label); // Light3D skipped
        tree.Move(-1);
        Assert.Equal("Node3D", tree.Selected?.Label);
        Assert.Equal(["Node", "Node3D", "Light3D", "OmniLight3D"], tree.Chain(tree.Find("OmniLight3D")!).Select(e => e.Label));
    }

    [Theory]
    [InlineData("omni", "OmniLight3D", "SpotLight3D")]
    [InlineData("Node", "Node", "Node3D")]
    [InlineData("node3", "Node3D", "MeshInstance3D")]
    [InlineData("light", "Light3D", "OmniLight3D")]
    [InlineData("mesh", "MeshInstance3D", "Timer")]
    public void ScoresRankExactPrefixWordAndFuzzyMatches(string query, string better, string worse) =>
        Assert.True(PickerTree.Score(better, query) > PickerTree.Score(worse, query), $"{better} {PickerTree.Score(better, query)} vs {worse} {PickerTree.Score(worse, query)}");

    [Fact]
    public void NodeTypesIncludeGameTypesUnderTheirEngineBase()
    {
        var entries = PickerSources.NodeTypes();
        var game = Assert.Single(entries, e => e.Id == "InheritsIconNode");
        Assert.Equal("IconNode", game.ParentId);
        Assert.Contains("icon-bolt", game.Icon, StringComparison.Ordinal);
        Assert.Contains("game type", game.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(entries, e => e.Id == "MissingNode");
        Assert.Equal("Node", Assert.Single(entries, e => e.Id == "SubViewport").ParentId); // SceneViewport is left out
        var resources = PickerSources.ResourceTypes(typeof(Mesh));
        Assert.Contains(resources, e => e.Id == "BoxMesh" && e.Selectable);
        Assert.Null(Assert.Single(resources, e => e.Id == "Mesh").ParentId);
        Assert.DoesNotContain(resources, e => e.Id == "Texture2D");
    }
}

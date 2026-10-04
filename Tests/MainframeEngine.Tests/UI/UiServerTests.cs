using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>A HUD document as a game writes one: a data model bound in OnReady and a button wired to a C# event.</summary>
public class HudTestDocument : UiDocument
{
    public int Health { get; set; } = 42;
    public int Clicks { get; private set; }
    public int Pings { get; private set; }
    public RmlDataModel Model { get; private set; } = null!;

    protected override void OnReady()
    {
        Model = CreateDataModel("hud")
            .Bind("health", this, static d => d.Health)
            .Event("ping", () => Pings++);
        GetElementById("btn")!.Click += _ => Clicks++;
    }
}

/// <summary>Records the input the game (scene tree) receives after the UI had its turn.</summary>
public class GameInputNode : Node
{
    public List<InputEvent> Seen { get; } = [];

    protected override void OnInput(InputEvent inputEvent) => Seen.Add(inputEvent.Clone());
}

/// <summary>A headless UI server in a scene tree, documents loaded from a temporary source content folder.</summary>
public sealed class UiTestTree : IDisposable
{
    public UiTestTree(UiServerOptions? options = null, Vector2? viewport = null)
    {
        ContentDirectory = Directory.CreateTempSubdirectory("mf-ui-test").FullName;
        Directory.CreateDirectory(Path.Combine(ContentDirectory, "UI"));
        Server = new UiServer(options: (options ?? new UiServerOptions()) with
        {
            HotReload = false,
            SourceContentDirectories = [ContentDirectory],
            HeadlessViewport = viewport ?? new Vector2(800, 600),
        });
        Servers.Register(Server);
        Tree = new SceneTree(Servers);
        Game = new GameInputNode { Name = "Game" };
        Tree.Root.AddChild(Game);
    }

    public string ContentDirectory { get; }
    public ServerRegistry Servers { get; } = new();
    public UiServer Server { get; }
    public SceneTree Tree { get; }
    public GameInputNode Game { get; }
    public uint FrameCount { get; private set; }

    public string Write(string relativePath, string text)
    {
        var path = Path.Combine(ContentDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return "Content/" + relativePath.Replace('\\', '/');
    }

    public UiLayer AddLayer(int layer, params UiDocument[] documents)
    {
        var node = new UiLayer { Name = $"Layer{layer}", Layer = layer };
        foreach (var document in documents)
            node.AddChild(document);
        Tree.Root.AddChild(node);
        return node;
    }

    public void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            Tree.Tick(new GameTime { DeltaTime = 1f / 60f, FrameCount = ++FrameCount });
    }

    public bool Move(float x, float y) => Tree.PushInput(new InputEventMouseMotion { Position = new Vector2(x, y) });

    public bool Button(MouseButton button, bool pressed, float x, float y) =>
        Tree.PushInput(new InputEventMouseButton { Button = button, Pressed = pressed, Position = new Vector2(x, y) });

    public bool Key(Key key, bool pressed) => Tree.PushInput(new InputEventKey { Key = key, Pressed = pressed });

    public bool Text(char c) => Tree.PushInput(new InputEventText { Character = c });

    public bool Pad(ButtonName button, bool pressed) => Tree.PushInput(new InputEventGamepadButton { Button = button, Pressed = pressed });

    public void Dispose()
    {
        Tree.Shutdown();
        Servers.Dispose();
        try
        {
            Directory.Delete(ContentDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public const string Style = """
        <style>
        body { font-family: LatoLatin; font-size: 16px; color: #ffffff; width: 400px; height: 300px; }
        div, p { display: block; }
        button { display: block; width: 100px; height: 30px; background-color: #335; tab-index: auto; nav: auto; pointer-events: auto; }
        input { display: block; width: 200px; height: 20px; pointer-events: auto; }
        </style>
        """;

    public static string Page(string body, string? bodyAttributes = null) =>
        $"<rml><head>{Style}</head><body {bodyAttributes}>{body}</body></rml>";
}

[Collection(nameof(SerialRmlUi))]
public sealed class UiServerTests
{
    [Fact]
    public void ServerOwnsRmlUiAndLoadsTheBundledFonts()
    {
        using (var ui = new UiTestTree())
        {
            Assert.True(RmlCore.IsInitialised);
            Assert.IsType<NullUiRenderer>(ui.Server.RenderInterface);
            Assert.Null(ui.Server.Renderer);
            Assert.Throws<InvalidOperationException>(() => new UiServer()); // one per process
        }

        Assert.False(RmlCore.IsInitialised);
    }

    [Fact]
    public void DocumentLoadsLazilyWithModelsBoundInOnReady()
    {
        using var ui = new UiTestTree();
        var source = ui.Write("UI/hud.rml", UiTestTree.Page("""
            <p id="hp">HP {{ health }}</p>
            <button id="btn" data-event-click="ping">Go</button>
            """, "data-model='hud'"));
        var doc = new HudTestDocument { Name = "Hud", Source = source };
        var layer = ui.AddLayer(0, doc);

        Assert.True(doc.IsLoaded); // GetElementById in OnReady loaded it, after the model existed
        Assert.Same(layer, doc.Layer);
        Assert.NotNull(layer.Context);
        ui.Tick();
        Assert.Contains("HP 42", doc.GetElementById("hp")!.InnerRml, StringComparison.Ordinal);

        doc.Health = 7;
        doc.Model.Dirty("health");
        ui.Tick();
        Assert.Contains("HP 7", doc.GetElementById("hp")!.InnerRml, StringComparison.Ordinal);

        doc.GetElementById("btn")!.PerformClick();
        Assert.Equal(1, doc.Clicks);
        Assert.Equal(1, doc.Pings);
        Assert.Same(doc.GetElementById("btn"), doc.GetElementById("btn")); // cached wrapper
        Assert.Null(doc.GetElementById("missing"));
    }

    [Fact]
    public void DocumentWithoutCodeLoadsBeforeTheFirstFrameAndUnloadsOnExit()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Plain", Source = ui.Write("UI/plain.rml", UiTestTree.Page("<p id='x'>x</p>")) };
        var layer = ui.AddLayer(0, doc);
        Assert.False(doc.IsLoaded);
        var loaded = 0;
        doc.Loaded += () => loaded++;

        ui.Tick();
        Assert.True(doc.IsLoaded);
        Assert.Equal(1, loaded);
        Assert.Equal(1, layer.Context!.DocumentCount);

        layer.RemoveChild(doc);
        Assert.False(doc.IsLoaded);
        Assert.Null(doc.Layer);
        ui.Tick();
        Assert.Equal(0, layer.Context.DocumentCount);
        doc.Free();
    }

    [Fact]
    public void MissingDocumentFailsOnceWithAnError()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Missing", Source = "Content/UI/missing.rml" };
        ui.AddLayer(0, doc);
        ui.Tick(3);
        Assert.False(doc.IsLoaded);
        Assert.Null(doc.GetElementById("x"));
    }

    [Fact]
    public void DocumentOutsideALayerIsInert()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Orphan", Rml = UiTestTree.Page("<p>x</p>") };
        ui.Tree.Root.AddChild(doc);
        ui.Tick();
        Assert.False(doc.IsLoaded);
        Assert.Null(doc.Layer);
    }

    [Fact]
    public void ModelCreatedAfterLoadingReloadsTheDocument()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Late", Rml = UiTestTree.Page("<p id='v'>{{ late }}</p>", "data-model='late'") };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.True(doc.IsLoaded);

        doc.CreateDataModel("late").Bind("late", static () => "bound");
        var reloaded = 0;
        doc.Reloaded += () => reloaded++;
        ui.Tick(2);
        Assert.Equal(1, reloaded);
        Assert.Contains("bound", doc.GetElementById("v")!.InnerRml, StringComparison.Ordinal);
    }

    [Fact]
    public void LayersSortByLayerAndHiddenLayersDoNotUpdate()
    {
        using var ui = new UiTestTree();
        var top = ui.AddLayer(10, new UiDocument { Name = "A", Rml = UiTestTree.Page("<p>a</p>") });
        var bottom = ui.AddLayer(-5, new UiDocument { Name = "B", Rml = UiTestTree.Page("<p>b</p>") });
        ui.Tick();
        Assert.Equal([bottom, top], ui.Server.Layers);

        top.Layer = -10;
        Assert.Equal([top, bottom], ui.Server.Layers);

        top.Visible = false;
        ui.Tick();
        Assert.True(top.Documents[0].IsLoaded); // loaded before it was hidden
    }

    [Fact]
    public void UiConsumesInputOverElementsAndTheGameGetsTheRest()
    {
        using var ui = new UiTestTree();
        var doc = new HudTestDocument
        {
            Name = "Hud",
            Source = ui.Write("UI/input.rml", UiTestTree.Page("<button id='btn'>Go</button>", "data-model='hud' style='pointer-events: none;'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick();
        var b = doc.GetElementById("btn")!.Bounds;
        var (bx, by) = (b.X + 5, b.Y + 5);

        // Over the button: consumed, the game never sees it.
        Assert.True(ui.Move(bx, by));
        Assert.True(ui.Button(MouseButton.Left, true, bx, by));
        Assert.True(ui.Button(MouseButton.Left, false, bx, by));
        Assert.Equal(1, doc.Clicks);
        Assert.Empty(ui.Game.Seen);

        // Over empty space (body has pointer-events: none): the game gets it.
        Assert.False(ui.Move(600, 500));
        Assert.False(ui.Button(MouseButton.Left, true, 600, 500));
        Assert.Equal(2, ui.Game.Seen.Count);

        // A drag that started in the game stays with the game while it crosses the UI.
        Assert.False(ui.Move(bx, by));
        Assert.False(ui.Button(MouseButton.Left, false, bx, by));
        Assert.Equal(1, doc.Clicks);

        // Keys nothing in the UI handles reach the game.
        Assert.False(ui.Key(Key.W, true));
        Assert.Contains(ui.Game.Seen, e => e is InputEventKey { Key: Key.W });
    }

    [Fact]
    public void FocusedTextFieldTakesAllKeysAndText()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Form", Rml = UiTestTree.Page("<input id='name' type='text' value=''/>") };
        ui.AddLayer(0, doc);
        ui.Tick();
        var field = doc.GetElementById("name")!;

        Assert.False(ui.Text('x')); // nothing focused: typing goes to the game
        Assert.True(field.Focus());
        Assert.True(ui.Server.TextInputActive);
        Assert.True(ui.Key(Key.W, true));  // a focused field takes keys it does not use, too
        Assert.True(ui.Text('w'));
        Assert.True(ui.Key(Key.W, false));
        Assert.True(ui.Text('\uD83D')); // surrogate pair: one emoji
        Assert.True(ui.Text('\uDE00'));
        Assert.Equal("w\U0001F600", field.Value);
        Assert.DoesNotContain(ui.Game.Seen, e => e is InputEventKey or InputEventText { Character: 'w' });
    }

    [Fact]
    public void ModalDocumentBlocksLowerLayersAndTheGame()
    {
        using var ui = new UiTestTree();
        var hudDoc = new HudTestDocument
        {
            Name = "Hud",
            Source = ui.Write("UI/hud2.rml", UiTestTree.Page("<button id='btn'>Go</button>", "data-model='hud'")),
        };
        ui.AddLayer(0, hudDoc);
        var modal = new UiDocument { Name = "Pause", Modal = true, Rml = UiTestTree.Page("<p>paused</p>", "style='pointer-events: none;'") };
        var menu = ui.AddLayer(10, modal);
        ui.Tick();
        Assert.True(menu.HasModalDocument);

        var b = hudDoc.GetElementById("btn")!.Bounds;
        Assert.True(ui.Move(b.X + 5, b.Y + 5));
        Assert.True(ui.Button(MouseButton.Left, true, b.X + 5, b.Y + 5));
        Assert.True(ui.Button(MouseButton.Left, false, b.X + 5, b.Y + 5));
        Assert.Equal(0, hudDoc.Clicks);
        Assert.True(ui.Key(Key.W, true));
        Assert.Empty(ui.Game.Seen);

        modal.Visible = false;
        Assert.False(menu.HasModalDocument);
        Assert.False(ui.Key(Key.W, true));
        Assert.Single(ui.Game.Seen);
    }

    [Fact]
    public void GamepadNavigatesFocusAndActivates()
    {
        using var ui = new UiTestTree();
        var doc = new HudTestDocument
        {
            Name = "Menu",
            Source = ui.Write("UI/menu.rml", UiTestTree.Page("<button id='play'>Play</button><button id='btn'>Options</button>", "data-model='hud'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick();
        var context = doc.Layer!.Context!;

        Assert.True(ui.Pad(ButtonName.DPadDown, true)); // first press focuses the first element
        Assert.Equal(doc.GetElementById("play")!.Element, context.FocusElement);
        ui.Pad(ButtonName.DPadDown, false);
        Assert.True(ui.Pad(ButtonName.DPadDown, true)); // nav: auto moves down
        Assert.Equal(doc.GetElementById("btn")!.Element, context.FocusElement);
        ui.Pad(ButtonName.DPadDown, false);
        Assert.True(ui.Pad(ButtonName.A, true)); // A = Return: clicks the focused button
        Assert.Equal(1, doc.Clicks);
        Assert.Empty(ui.Game.Seen);

        // Held D-pad repeats after a delay.
        ui.Pad(ButtonName.DPadUp, true);
        Assert.Equal(doc.GetElementById("play")!.Element, context.FocusElement);
        ui.Tree.PushInput(new InputEventGamepadAxis { Axis = GamepadAxis.LeftStick, Value = new Vector2(0, 0.9f) });
        Assert.Equal(doc.GetElementById("btn")!.Element, context.FocusElement); // stick down
    }

    [Fact]
    public void DebuggerKeyTogglesTheVisualDebugger()
    {
        using var ui = new UiTestTree();
        ui.AddLayer(0, new UiDocument { Name = "D", Rml = UiTestTree.Page("<p>x</p>") });
        ui.Tick();
        Assert.False(ui.Server.DebuggerVisible);
        Assert.True(ui.Key(Key.F8, true));
        Assert.True(ui.Server.DebuggerVisible);
        ui.Tick();
        Assert.True(ui.Key(Key.F8, true));
        Assert.False(ui.Server.DebuggerVisible);
        Assert.Empty(ui.Game.Seen);
    }

    [Fact]
    public void HotReloadAppliesEditsAndKeepsSubscriptionsAndModels()
    {
        using var ui = new UiTestTree();
        var source = ui.Write("UI/reload.rml", UiTestTree.Page("<p id='hp'>v1 {{ health }}</p><button id='btn'>Go</button>", "data-model='hud'"));
        var doc = new HudTestDocument { Name = "Hud", Source = source };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.Contains("v1 42", doc.GetElementById("hp")!.InnerRml, StringComparison.Ordinal);

        ui.Write("UI/reload.rml", UiTestTree.Page("<p id='hp'>v2 {{ health }}</p><button id='btn' data-event-click='ping'>Go</button>", "data-model='hud'"));
        var reloaded = 0;
        doc.Reloaded += () => reloaded++;
        ui.Server.Reload(UiHotReload.Classify("reload.rml"));
        ui.Tick();
        Assert.Equal(1, reloaded);
        Assert.Contains("v2 42", doc.GetElementById("hp")!.InnerRml, StringComparison.Ordinal);

        doc.GetElementById("btn")!.PerformClick(); // the OnReady subscription was re-attached
        Assert.Equal(1, doc.Clicks);
        Assert.Equal(1, doc.Pings); // and the data model (C# state) still serves the new document
    }

    [Fact]
    public void HotReloadRecoversADocumentThatFailedToLoad()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Later", Source = "Content/UI/later.rml" };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.False(doc.IsLoaded);

        ui.Write("UI/later.rml", UiTestTree.Page("<p id='x'>now</p>"));
        ui.Server.Reload(UiReloadKind.Documents);
        Assert.True(doc.IsLoaded);
    }

    [Fact]
    public void StyleSheetReloadKeepsTheDom()
    {
        using var ui = new UiTestTree();
        ui.Write("UI/styled.rcss", "p { color: #ff0000; }");
        var doc = new UiDocument
        {
            Name = "Styled",
            Source = ui.Write("UI/styled.rml", "<rml><head><link type='text/rcss' href='styled.rcss'/>" + UiTestTree.Style +
                                               "</head><body><input id='f' type='text' value='kept'/></body></rml>"),
        };
        ui.AddLayer(0, doc);
        ui.Tick();
        var field = doc.GetElementById("f")!;
        field.Value = "typed";
        var element = field.Element;

        ui.Write("UI/styled.rcss", "p { color: #00ff00; }");
        ui.Server.Reload(UiReloadKind.StyleSheets);
        ui.Tick();
        Assert.Equal(element, field.Element); // same DOM
        Assert.Equal("typed", field.Value);
    }

    [Fact]
    public void TranslatorHookTranslatesUiText()
    {
        using var ui = new UiTestTree();
        ui.Server.Translator = static (source, output) =>
        {
            if (!source.SequenceEqual("[play]"u8))
                return false;
            output.Set("Jouer"u8);
            return true;
        };
        var doc = new UiDocument { Name = "T", Rml = UiTestTree.Page("<p id='t'>[play]</p>") };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.Contains("Jouer", doc.GetElementById("t")!.InnerRml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void WidgetDemoLoadsWithThePanelTemplateAndTitle(float dpRatio)
    {
        using var ui = new UiTestTree(viewport: new Vector2(1280, 1280));
        var doc = new UiDocument { Name = "Demo", Source = "Content/UI/widgets/demo.rml" };
        var layer = ui.AddLayer(0, doc);
        layer.ScaleMode = UiScaleMode.ReferenceResolution;
        layer.ReferenceResolution = new Vector2(1280 / dpRatio, 1280 / dpRatio);
        ui.Tick(2);
        Assert.True(doc.IsLoaded);
        Assert.Contains("Widget library", doc.GetElementById(UiDocument.PanelTitleId)!.InnerRml, StringComparison.Ordinal);

        var body = doc.Document.AsElement().Bounds;
        var bar = doc.GetElementById("mf-panel-bar")!.Bounds;
        var content = doc.GetElementById("mf-panel-content")!.Bounds;
        Assert.True(Math.Abs(bar.Width - content.Width) < 0.5f, $"bar {bar}, content {content}, body {body}");
        Assert.True(Math.Abs(body.Width - 2 - bar.Width) < 0.5f, $"bar {bar}, content {content}, body {body}"); // 1 px border each side
        foreach (var id in (string[])["primary", "volume", "quality", "name", "vsync", "mode-a", "loading", "health", "notes"])
            Assert.NotNull(doc.GetElementById(id));
    }

    [Fact]
    public void EngineCreditsCarryTheFreeTypeCredit()
    {
        using var ui = new UiTestTree();
        var doc = new UiDocument { Name = "Credits", Source = "Content/UI/credits.rml" };
        ui.AddLayer(0, doc);
        ui.Tick();
        Assert.True(doc.IsLoaded);
        Assert.Contains("Credits", doc.GetElementById(UiDocument.PanelTitleId)!.InnerRml, StringComparison.Ordinal);
        Assert.Contains("The FreeType Project", doc.Document.AsElement().InnerRml, StringComparison.Ordinal);
    }

    [Fact]
    public void SteadyStateHudFramesDoNotAllocate()
    {
        using var ui = new UiTestTree();
        var doc = new HudTestDocument
        {
            Name = "Hud",
            Source = ui.Write("UI/alloc.rml", UiTestTree.Page("""
                <p>HP {{ health }}</p><div style="width: 200px; height: 10px; background-color: #f00;" data-style-width="health + 'px'"></div>
                <button id="btn">Go</button>
                """, "data-model='hud'")),
        };
        ui.AddLayer(0, doc);
        ui.Tick(30);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            doc.Health = i % 100;
            doc.Model.Dirty("health");
            ui.Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

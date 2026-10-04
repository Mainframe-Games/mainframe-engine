using System.Runtime.CompilerServices;
using MainframeEngine.Localization;
using MainframeEngine.Serialization;
using MainframeEngine.Tests.Localization;
using MainframeEngine.Tests.UI;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// Code reload × M9 localization × M8 game UI: after a game's nodes are freed, <see cref="GameAssemblyLoader.Unload"/>
/// must leave nothing in <see cref="Tr"/> (locale-change subscribers, translatable type metadata) or the
/// <see cref="UiServer"/> (data-model bindings, element listeners, documents waiting for RmlUi's deferred unload) that
/// keeps the collectible context alive — including subscriptions and models the game forgot to release.
/// </summary>
/// <remarks>
/// Serial (<see cref="SerialRmlUi"/> disables parallelization): RmlUi, <see cref="Tr"/>, the type registry and the
/// loader cache are process-wide.
/// </remarks>
[Collection(nameof(SerialRmlUi))]
public sealed class GameUnloadLeakTests : IDisposable
{
    private const string Game = """
        using MainframeEngine;
        using MainframeEngine.Localization;

        namespace LeakGame;

        // [Export(Translatable)] text shown through Atr, re-translated on locale changes — and a forgotten
        // Tr.LocaleChanged / SceneTree.LocaleChanged subscription, the classic leak.
        public sealed class GameLabel : Node
        {
            [Export(Translatable = true)] public string Title { get; set; } = "Hello";
            public string Shown { get; private set; } = "";
            public int Changes { get; private set; }

            protected override void OnReady()
            {
                Shown = Atr(Title);
                Tr.LocaleChanged += OnTrLocaleChanged;
                Tree!.LocaleChanged += OnTreeLocaleChanged;
            }

            protected override void OnLocaleChanged() => Shown = Atr(Title);

            private void OnTrLocaleChanged(object? sender, LocaleChangedEventArgs e) => Changes++;

            private void OnTreeLocaleChanged() => Changes++;
        }

        // A HUD as games write it: a data model bound to game state, a data event and an element listener.
        public sealed class GameHud : UiDocument
        {
            public int Health { get; set; } = 42;

            protected override void OnReady()
            {
                CreateDataModel("game")
                    .Bind("health", this, static d => d.Health)
                    .Event("hit", () => Health--);
                GetElementById("btn")!.Click += _ => Health++;
            }
        }

        // A node that binds a model straight on its layer's context and never disposes it.
        public sealed class RawModelOwner : Node
        {
            public int Score { get; set; } = 7;

            protected override void OnReady()
            {
                var layer = (UiLayer)Parent!;
                layer.Context!.CreateDataModel("raw").Bind("score", () => Score);
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mf-unload-tests", Guid.NewGuid().ToString("N"));
    private readonly LocaleFixture _locales = new();

    public GameUnloadLeakTests()
    {
        _locales.AddCatalog("es", LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Hola"

            """);
        _locales.Use("en");
    }

    public void Dispose()
    {
        _locales.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private GameAssemblyLoader LoadGame(string version)
    {
        var loader = new GameAssemblyLoader(GameCompiler.Compile(Game, "LeakGame", Path.Combine(_root, version)));
        loader.Load();
        return loader;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UseTranslatableNode(SceneTree tree)
    {
        var info = TypeRegistry.Get("GameLabel")!;
        Assert.Contains(info.TranslatableProperties, p => p.Name == "Title");
        var label = TypeRegistry.CreateNode("GameLabel");
        tree.Root.AddChild(label);
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Equal("Hello", info.Type.GetProperty("Shown")!.GetValue(label));

        Tr.SetLocale("es"); // OnLocaleChanged, Tr.LocaleChanged and SceneTree.LocaleChanged all reach game code
        Assert.Equal("Hola", info.Type.GetProperty("Shown")!.GetValue(label));
        Assert.Equal(2, info.Type.GetProperty("Changes")!.GetValue(label));
        label.Free(); // the game forgets to unsubscribe
    }

    [Fact]
    public void TranslatableGameTypesAndForgottenLocaleSubscriptionsDoNotKeepTheGameLoaded()
    {
        var tree = new SceneTree();
        using var loader = LoadGame("l10n");
        UseTranslatableNode(tree);

        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)), "Tr or the scene tree kept the unloaded game assembly alive");
        Assert.Null(TypeRegistry.Get("GameLabel"));

        // The engine's own subscribers still run; nothing points into the unloaded code any more.
        Tr.SetLocale("en");
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        tree.Shutdown();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UiLayer UseGameUi(UiTestTree ui)
    {
        var hud = (UiDocument)TypeRegistry.CreateNode("GameHud");
        hud.Name = "Hud";
        hud.Source = ui.Write("UI/game.rml", UiTestTree.Page("""
            <p id="hp">Health {{ health }}</p>
            <button id="btn" data-event-click="hit">Hit</button>
            """, "data-model='game'"));
        var owner = TypeRegistry.CreateNode("RawModelOwner");
        var layer = ui.AddLayer(0, hud);
        layer.AddChild(owner);
        ui.Tick(2);
        Assert.Equal("Health 42", hud.GetElementById("hp")!.InnerRml.Trim());
        Tr.SetLocale("es"); // the server reloads the document with the game's models bound
        ui.Tick(2);
        Assert.Equal("Health 42", hud.GetElementById("hp")!.InnerRml.Trim());
        return layer;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FreeGameNodes(UiLayer layer)
    {
        foreach (var child in layer.Children.ToArray())
            child.Free();
    }

    [Fact]
    public void UiDataModelsAndListenersOfGameTypesAreReleasedOnUnload()
    {
        using var ui = new UiTestTree();
        using var loader = LoadGame("ui");
        var layer = UseGameUi(ui);

        // The editor's reload cycle: free the game's nodes, then unload at once — no UI frame runs in between, so
        // RmlUi has not yet destroyed the closed document (its listeners) and the raw model was never disposed.
        FreeGameNodes(layer);
        Assert.True(loader.Unload(TimeSpan.FromSeconds(10)), "the UI server kept the unloaded game assembly alive");
        Assert.DoesNotContain(layer.Context!.DataModels, m => m.Name == "raw");

        // The UI keeps working with engine documents after the unload.
        var doc = new UiDocument { Name = "After", Rml = UiTestTree.Page("<p id='a'>Hello</p>") };
        layer.AddChild(doc);
        ui.Tick(2);
        Assert.Equal("Hola", doc.GetElementById("a")!.InnerRml.Trim());
    }
}

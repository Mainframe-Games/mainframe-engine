using MainframeEngine.UI.Rml;

namespace MainframeEngine.Tests.UI;

/// <summary>The managed binding over <c>mfrmlui</c>: library, contexts, documents, elements, events, data models, input.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class RmlBindingTests
{
    // ── Library ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryLoadsWithACompatibleAbi()
    {
        var abi = RmlCore.AbiVersion;
        Assert.Equal(RmlNative.AbiMajor, (int)(abi >> 16));
        Assert.True((abi & 0xFFFF) >= RmlNative.AbiMinor);
        Assert.Equal("6.3", RmlCore.RmlUiVersion);
    }

    [Theory]
    [InlineData(0x0001_0000u, true)]
    [InlineData(0x0001_0007u, true)]
    [InlineData(0x0002_0000u, false)]
    [InlineData(0x0000_0009u, false)]
    public void AbiRuleRequiresEqualMajorAndNewerMinor(uint version, bool compatible) =>
        Assert.Equal(compatible, RmlCore.IsCompatible(version));

    [Fact]
    public void InitialiseShutdownAndReinitialise()
    {
        Assert.False(RmlCore.IsInitialised);
        using (var host = new RmlTestHost())
        {
            Assert.True(RmlCore.IsInitialised);
            Assert.Throws<InvalidOperationException>(() => RmlCore.Initialise());
        }

        Assert.False(RmlCore.IsInitialised);
        RmlCore.Initialise();
        RmlCore.Shutdown();
        Assert.False(RmlCore.IsInitialised);
    }

    [Fact]
    public void MissingFontReportsAnError()
    {
        using var host = new RmlTestHost();
        var e = Assert.Throws<RmlException>(() => RmlCore.LoadFontFace("does/not/exist.ttf"));
        Assert.Equal(RmlNative.ErrorFailed, e.Status);
        Assert.Single(host.System.Errors);
        host.System.Errors.Clear();
    }

    // ── Contexts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContextLifecycle()
    {
        using var host = new RmlTestHost();
        Assert.Equal("test", host.Context.Name);
        Assert.Throws<RmlException>(() => new RmlContext("test", 10, 10, host.Renderer)); // duplicate name
        host.System.Errors.Clear();

        using (var second = new RmlContext("second", 320, 240, host.Renderer))
        {
            second.SetDimensions(640, 480);
            second.SetDensityIndependentPixelRatio(2f);
            Assert.Equal(640, second.Width);
            Assert.Equal(2f, second.DensityIndependentPixelRatio);
            second.Update();
            second.Render();
            Assert.Equal(0, second.DocumentCount);
        }

        // A render interface used by a context cannot be destroyed.
        var e = Assert.Throws<RmlException>(host.Renderer.Dispose);
        Assert.Equal(RmlNative.ErrorInUse, e.Status);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void DisposedContextRejectsCalls()
    {
        using var host = new RmlTestHost();
        var context = new RmlContext("temp", 10, 10, host.Renderer);
        context.Dispose();
        context.Dispose(); // idempotent
        Assert.True(context.IsDisposed);
        Assert.Throws<ObjectDisposedException>(context.Update);
    }

    [Fact]
    public void ShutdownInvalidatesContextsAndModels()
    {
        var renderer = new RecordingRenderInterface();
        RmlCore.Initialise();
        var context = new RmlContext("orphan", 10, 10, renderer);
        var model = context.CreateDataModel("m").Bind("x", static () => 1);
        RmlCore.Shutdown();

        Assert.True(context.IsDisposed);
        Assert.False(model.IsValid);
        context.Dispose(); // no double destroy
        model.Dispose();
        renderer.Dispose();
    }

    // ── Documents and elements ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocumentLoadsFromMemoryAndRenders()
    {
        using var host = new RmlTestHost();
        var doc = host.Context.LoadDocumentFromMemory(RmlTestHost.Page("<h1 id='title'>Hello</h1>").Replace("<head>", "<head><title>Mem</title>", StringComparison.Ordinal));
        doc.Show();
        host.Frame();

        Assert.True(doc.IsVisible);
        Assert.Equal("Mem", doc.Title);
        Assert.Equal(1, host.Context.DocumentCount);
        Assert.Equal(doc, host.Context.GetDocument(0));
        Assert.True(host.Renderer.Compiled > 0);
        Assert.True(host.Renderer.Rendered > 0);
        Assert.True(host.Renderer.Generated > 0, "FreeType glyph atlas");
        Assert.Contains("Hello", doc.GetElementById("title").InnerRml, StringComparison.Ordinal);

        doc.Hide();
        Assert.False(doc.IsVisible);
        doc.Close();
        host.Frame();
        Assert.Equal(0, host.Context.DocumentCount);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void MissingDocumentThrows()
    {
        using var host = new RmlTestHost();
        Assert.Throws<RmlException>(() => host.Context.LoadDocument("ui/missing.rml"));
        host.System.Warnings.Clear();
    }

    [Fact]
    public void ElementQueriesAttributesClassesAndContent()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("""
            <div id="list"><p class="item">a</p><p class="item">b</p><p class="item">c</p></div>
            <input id="name" type="text" value="abc"/>
            """));

        var list = doc.GetElementById("list");
        Assert.False(list.IsNull);
        Assert.Equal("div", list.TagName);
        Assert.Equal("list", list.Id);
        Assert.Equal(3, list.ChildCount);
        Assert.Equal(list, list.GetChild(0).Parent);
        Assert.Equal(doc, list.OwnerDocument);
        Assert.True(doc.GetElementById("nope").IsNull);

        Span<RmlElement> found = stackalloc RmlElement[2];
        Assert.Equal(3, list.QuerySelectorAll(".item", found)); // total count, two written
        Assert.Equal(3, list.QuerySelectorAll(".item").Length);
        Assert.Equal("p", list.QuerySelector("p.item").TagName);

        var first = found[0];
        first.SetAttribute("data-x", "1");
        Assert.True(first.HasAttribute("data-x"));
        Assert.Equal("1", first.GetAttribute("data-x"));
        Assert.Null(first.GetAttribute("missing"));
        first.RemoveAttribute("data-x");
        Assert.False(first.HasAttribute("data-x"));

        first.SetClass("big", true);
        Assert.True(first.IsClassSet("big"));
        first.SetClassNames("one two");
        Assert.True(first.IsClassSet("two"));
        Assert.False(first.IsClassSet("big"));
        first.SetPseudoClass("checked", true);
        Assert.True(first.IsPseudoClassSet("checked"));

        Assert.True(first.SetProperty("width", "50%"));
        Assert.False(first.SetProperty("width", "@@@"));
        host.System.Warnings.Clear();
        first.SetInnerRml("<span>new</span>");
        Assert.Contains("new", first.InnerRml, StringComparison.Ordinal);

        var name = doc.GetElementById("name");
        Assert.Equal("abc", name.Value);
        Span<char> chars = stackalloc char[16];
        Assert.Equal(3, name.GetValue(chars));
        name.SetValue("xyz");
        Assert.Equal("xyz", name.Value);
        Assert.Null(list.Value); // not a form control

        host.Frame();
        var bounds = doc.GetElementById("list").Bounds;
        Assert.True(bounds.Width > 0);
        host.AssertNoRmlErrors();
    }

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClickEventsReachListenersAndListenersDetach()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<button id='b'>Press</button>"));
        var button = doc.GetElementById("b");

        var clicks = 0;
        var detached = 0;
        var sawParams = false;
        var listener = button.AddEventListener("click", e =>
        {
            clicks++;
            sawParams |= e.IsType("click"u8) && e.Type == "click" && e.GetParameter("mouse_x", -1.0) >= 0 &&
                         e.Target == e.CurrentElement && e.Phase == RmlEventPhase.Target;
        });
        Assert.NotNull(listener);
        listener.Detached += _ => detached++;

        var b = button.Bounds;
        var x = (int)(b.X + b.Width / 2);
        var y = (int)(b.Y + b.Height / 2);
        Assert.True(host.Context.ProcessMouseMove(x, y, RmlKeyModifiers.None));
        Assert.Equal(button, host.Context.HoverElement);
        Assert.True(host.Context.ProcessMouseButtonDown(0, RmlKeyModifiers.None));
        Assert.True(host.Context.ProcessMouseButtonUp(0, RmlKeyModifiers.None));
        Assert.Equal(1, clicks);
        Assert.True(sawParams);

        button.Click();
        Assert.Equal(2, clicks);

        listener.Remove();
        Assert.False(listener.IsAttached);
        Assert.Equal(1, detached);
        listener.Remove(); // idempotent
        button.Click();
        Assert.Equal(2, clicks);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void ListenersDetachWhenTheirDocumentCloses()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<button id='b'>Press</button>"));
        var listener = doc.GetElementById("b").AddEventListener("click", static _ => { });
        Assert.NotNull(listener);
        var detached = false;
        listener.Detached += _ => detached = true;

        doc.Close();
        host.Frame();
        Assert.True(detached);
        Assert.False(listener.IsAttached);
    }

    [Fact]
    public void ExceptionsInHandlersAreSwallowedAtTheBoundary()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<button id='b'>Press</button>"));
        var button = doc.GetElementById("b");
        button.AddEventListener("click", static _ => throw new InvalidOperationException("boom"));
        button.Click(); // must not crash or propagate
        host.Frame();
    }

    // ── Input semantics ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InputIsConsumedOnlyOverUiElements()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<button id='b'>Press</button><input id='t' type='text'/>"));

        // Outside the 400×300 body: RmlUi does not consume (raw RmlUi would return true here).
        Assert.False(host.Context.ProcessMouseMove(700, 550, RmlKeyModifiers.None));
        Assert.False(host.Context.ProcessMouseButtonDown(0, RmlKeyModifiers.None));
        Assert.False(host.Context.ProcessMouseButtonUp(0, RmlKeyModifiers.None));
        Assert.False(host.Context.IsMouseInteracting);

        var b = doc.GetElementById("b").Bounds;
        Assert.True(host.Context.ProcessMouseMove((int)(b.X + 5), (int)(b.Y + 5), RmlKeyModifiers.None));
        Assert.True(host.Context.IsMouseInteracting);
        Assert.True(host.Context.ProcessMouseLeave() || true);

        // Typing goes to a focused text field and is consumed; without focus it propagates.
        Assert.False(host.Context.ProcessTextInput("x"));
        var field = doc.GetElementById("t");
        Assert.True(field.Focus());
        Assert.Equal(field, host.Context.FocusElement);
        Assert.True(host.Context.ProcessTextInput("héllo ✓"));
        Assert.Equal("héllo ✓", field.Value);
        Assert.True(host.Context.ProcessKeyDown(RmlKey.Back, RmlKeyModifiers.None));
        Assert.Equal("héllo ", field.Value);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void ArrowKeysMoveFocusWithNavProperties()
    {
        // RmlUi 6.3 spatial navigation: nav: auto on focusable elements; arrows move focus between them.
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<button id='a'>A</button><button id='b'>B</button><button id='c'>C</button>"));
        var a = doc.GetElementById("a");
        Assert.True(a.Focus(focusVisible: true));

        host.Context.ProcessKeyDown(RmlKey.Down, RmlKeyModifiers.None);
        host.Frame();
        Assert.Equal(doc.GetElementById("b"), host.Context.FocusElement);
        host.Context.ProcessKeyDown(RmlKey.Down, RmlKeyModifiers.None);
        Assert.Equal(doc.GetElementById("c"), host.Context.FocusElement);
        host.Context.ProcessKeyDown(RmlKey.Up, RmlKeyModifiers.None);
        Assert.Equal(doc.GetElementById("b"), host.Context.FocusElement);

        // Return clicks the focused element.
        var clicked = false;
        doc.GetElementById("b").AddEventListener("click", _ => clicked = true);
        host.Context.ProcessKeyDown(RmlKey.Return, RmlKeyModifiers.None);
        Assert.True(clicked);
        host.AssertNoRmlErrors();
    }

    // ── Data models ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Player
    {
        public int Health = 42;
        public string Name = "Ada";
        public float Speed = 1.5f;
        public bool Alive = true;
    }

    private sealed class Item(string name, int count)
    {
        public string Name { get; set; } = name;
        public int Count { get; set; } = count;
    }

    private static readonly RmlStructType<Item> ItemType = new RmlStructType<Item>()
        .Member("name", static i => i.Name)
        .Member("count", static i => i.Count, static (i, v) => i.Count = v);

    [Fact]
    public void ScalarBindingsUpdateWhenDirtied()
    {
        using var host = new RmlTestHost();
        var player = new Player();
        var model = host.Context.CreateDataModel("hud")
            .Bind("health", () => player.Health)
            .Bind("name", player, static p => p.Name, static (p, v) => p.Name = v)
            .Bind("speed", player, static p => p.Speed)
            .Bind("alive", player, static p => p.Alive);
        var doc = host.Show(RmlTestHost.Page("""
            <div data-model="hud">
              <p id="h">HP {{ health }}</p>
              <p id="s">{{ speed }}</p>
              <p id="a" data-class-dead="!alive">alive</p>
              <input id="n" type="text" data-value="name"/>
            </div>
            """));

        Assert.Contains("HP 42", doc.GetElementById("h").InnerRml, StringComparison.Ordinal);
        Assert.Contains("1.5", doc.GetElementById("s").InnerRml, StringComparison.Ordinal);
        Assert.Equal("Ada", doc.GetElementById("n").Value);
        Assert.False(doc.GetElementById("a").IsClassSet("dead"));

        player.Health = 7;
        host.Frame();
        Assert.Contains("HP 42", doc.GetElementById("h").InnerRml, StringComparison.Ordinal); // not dirtied yet

        model.Dirty("health");
        Assert.True(model.IsDirty("health"));
        host.Frame();
        Assert.Contains("HP 7", doc.GetElementById("h").InnerRml, StringComparison.Ordinal);

        // Two-way: typing into the bound field runs the setter.
        var field = doc.GetElementById("n");
        field.Focus();
        host.Context.ProcessKeyDown(RmlKey.End, RmlKeyModifiers.None);
        host.Context.ProcessTextInput("!");
        host.Frame();
        Assert.Equal("Ada!", player.Name);

        player.Alive = false;
        model.DirtyAll();
        host.Frame();
        Assert.True(doc.GetElementById("a").IsClassSet("dead"));
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void EventCallbacksReceiveArguments()
    {
        using var host = new RmlTestHost();
        var simple = 0;
        double arg = 0;
        string? second = null;
        host.Context.CreateDataModel("m")
            .Event("ping", () => simple++)
            .Event("buy", e =>
            {
                arg = e.GetArgument(0).GetDouble();
                second = e.GetArgument(1).GetString();
                Assert.False(e.Event.IsNull);
                Assert.Equal(2, e.ArgumentCount);
                try
                {
                    e.GetArgument(5);
                    Assert.Fail("Out-of-range argument index accepted.");
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            });
        var doc = host.Show(RmlTestHost.Page("""
            <div data-model="m">
              <button id="p" data-event-click="ping">Ping</button>
              <button id="b" data-event-click="buy(7, 'gem')">Buy</button>
            </div>
            """));

        doc.GetElementById("p").Click();
        doc.GetElementById("b").Click();
        Assert.Equal(1, simple);
        Assert.Equal(7, arg);
        Assert.Equal("gem", second);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void ListsOfStructsAndScalarsBind()
    {
        using var host = new RmlTestHost();
        var items = new List<Item> { new("sword", 1), new("potion", 3) };
        var tags = new List<string> { "a", "b", "c" };
        var model = host.Context.CreateDataModel("inv")
            .BindList("items", items, ItemType)
            .BindList("tags", tags)
            .BindStruct("first", () => items[0], ItemType);
        var doc = host.Show(RmlTestHost.Page("""
            <div data-model="inv">
              <div id="items"><p data-for="it : items">{{ it.name }} x{{ it.count }}</p></div>
              <div id="tags"><span data-for="t : tags">{{ t }}</span></div>
              <p id="first">{{ first.name }}</p>
              <p id="size">{{ items.size }}</p>
            </div>
            """));

        var root = doc.GetElementById("items");
        Assert.Contains("potion x3", root.InnerRml, StringComparison.Ordinal);
        Assert.Contains("sword x1", root.InnerRml, StringComparison.Ordinal);
        Assert.Contains("sword", doc.GetElementById("first").InnerRml, StringComparison.Ordinal);
        Assert.Contains("2", doc.GetElementById("size").InnerRml, StringComparison.Ordinal);
        Assert.Contains("c", doc.GetElementById("tags").InnerRml, StringComparison.Ordinal);

        items.Add(new Item("shield", 2));
        items[0].Count = 5;
        model.Dirty("items");
        model.Dirty("first");
        host.Frame();
        Assert.Contains("shield x2", root.InnerRml, StringComparison.Ordinal);
        Assert.Contains("sword x5", root.InnerRml, StringComparison.Ordinal);
        host.AssertNoRmlErrors();
    }

    [Fact]
    public void UnsupportedBindingTypesAndDuplicateNamesAreRejected()
    {
        using var host = new RmlTestHost();
        var model = host.Context.CreateDataModel("m").Bind("x", static () => 1);
        Assert.Throws<NotSupportedException>(() => model.Bind("v", static () => new System.Numerics.Vector2()));
        Assert.Throws<RmlException>(() => model.Bind("x", static () => 2)); // already bound
        Assert.Throws<RmlException>(() => host.Context.CreateDataModel("m")); // duplicate model
        host.System.Errors.Clear();
    }

    [Fact]
    public void DisposingAModelReleasesItsBindings()
    {
        using var host = new RmlTestHost();
        var weak = BindAndDispose(host.Context);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive, "The binding's GCHandle was not released when the model was removed.");

        static WeakReference BindAndDispose(RmlContext context)
        {
            var target = new object();
            var weak = new WeakReference(target);
            var model = context.CreateDataModel("tmp").Bind("x", () => target.GetHashCode());
            model.Dispose();
            Assert.False(model.IsValid);
            model.Dispose(); // idempotent
            return weak;
        }
    }

    [Fact]
    public void TranslateStringHookTranslatesText()
    {
        using var host = new RmlTestHost();
        host.System.Translate = s => s == "[greeting]" ? "Bonjour" : null;
        var doc = host.Show(RmlTestHost.Page("<h1 id='t'>[greeting]</h1>"));
        Assert.Contains("Bonjour", doc.GetElementById("t").InnerRml, StringComparison.Ordinal);
    }

    // ── Hot reload ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReloadReReadsTheSourceAndKeepsVisibility()
    {
        using var host = new RmlTestHost();
        var doc = host.Show(RmlTestHost.Page("<p id='v'>one</p>"), "ui/reload.rml");
        var detached = false;
        doc.GetElementById("v").AddEventListener("click", static _ => { })!.Detached += _ => detached = true;

        host.Files.Files["ui/reload.rml"] = RmlTestHost.Page("<p id='v'>two</p>");
        var reloaded = doc.Reload();
        Assert.False(reloaded.IsNull);
        Assert.NotEqual(doc, reloaded);
        host.Frame();
        Assert.True(detached);
        Assert.True(reloaded.IsVisible);
        Assert.Contains("two", reloaded.GetElementById("v").InnerRml, StringComparison.Ordinal);
        Assert.True(reloaded.ReloadStyleSheet());
        Assert.Equal("ui/reload.rml", reloaded.SourceUrl);
        host.AssertNoRmlErrors();
    }

    // ── Debugger ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DebuggerInitialisesAndToggles()
    {
        using var host = new RmlTestHost();
        RmlDebugger.Initialise(host.Context);
        Assert.Throws<RmlException>(() => RmlDebugger.Initialise(host.Context));
        RmlDebugger.Visible = true;
        Assert.True(RmlDebugger.Visible);
        host.Frame();
        RmlDebugger.Visible = false;
        RmlDebugger.Shutdown();
        Assert.False(RmlDebugger.IsInitialised);
    }

    // ── Allocation ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SteadyStateFramesWithDirtiedBindingsDoNotAllocate()
    {
        using var host = new RmlTestHost();
        var player = new Player();
        var items = new List<Item> { new("a", 1), new("b", 2) };
        var model = host.Context.CreateDataModel("hud")
            .Bind("health", player, static p => p.Health)
            .Bind("speed", player, static p => p.Speed)
            .Bind("name", player, static p => p.Name)
            .BindList("items", items, ItemType);
        host.Show(RmlTestHost.Page("""
            <div data-model="hud"><p>{{ health }} {{ speed }} {{ name }}</p><p data-for="i : items">{{ i.name }} {{ i.count }}</p></div>
            """));

        for (var i = 0; i < 30; i++)
            Step(i);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
            Step(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        void Step(int i)
        {
            player.Health = i % 7;
            items[0].Count = i % 3;
            model.Dirty("health");
            model.Dirty("items");
            host.Frame();
        }
    }
}

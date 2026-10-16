using System.Globalization;
using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>One resolved splash card: its <c>project.mfproj</c> entry, the image shown, its alt text and its size in pixels.</summary>
public readonly record struct SplashCard(string Entry, string ImagePath, string Alt, Vector2I Size);

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192): the project's <c>splash</c> cards (Mainframe, BRUTAL as its licence
/// requires, then the game's own) one after another on an engine <see cref="UiLayer"/> above every game layer
/// (<see cref="DefaultLayer"/>, under the developer overlay), each faded in, held and faded out
/// (<see cref="SplashTimeline"/>), while <see cref="GameHost"/> loads the start scene behind it. Every card's image fits the
/// same square box (<see cref="BoxFraction"/> of the shorter side, aspect kept), so no logo is less prominent than another.
/// A fresh key, click or gamepad press skips a hold once it has run <see cref="SplashTimeline.SkipFloorSeconds"/>; no input
/// reaches the game while the splash is up. It ends when the last card has faded out and <see cref="Load"/> has completed
/// (a spinner shows while it has not), raises <see cref="Finished"/> and frees itself.
/// </summary>
/// <remarks>
/// While it is up the tree reports <see cref="SceneTree.IsLoading"/> (<see cref="HoldsLoading"/>), like the loading screen,
/// so hosts count frames and take screenshots once the game is visible. Its clock is the frame's delta and it processes
/// while the tree is paused (<see cref="ProcessMode.Always"/>). Polled input (<see cref="Input.IsActionPressed"/>) still
/// sees keys held through the splash; only the events are consumed.
/// </remarks>
public sealed class SplashSequence : UiLayer
{
    /// <summary>The engine's splash document.</summary>
    public const string DefaultSource = "Content/UI/splash/splash.rml";

    /// <summary>Above every game layer and the loading screen, under the developer overlay (<c>int.MaxValue</c>).</summary>
    public const int DefaultLayer = int.MaxValue - 1;

    /// <summary>Every card fits a square box of this share of the shorter screen side.</summary>
    public const float BoxFraction = 0.4f;

    public const string MainframeLogo = "Content/Brand/logo-512.png";
    public const string BrutalLogoWhite = "Content/Brand/brutal/LOGO_BRUTAL_WHT.png";
    public const string BrutalLogoBlack = "Content/Brand/brutal/LOGO_BRUTAL_BLK.png";

    private readonly bool[] _keys = new bool[InputState.KeyCount];
    private readonly bool[] _mouseButtons = new bool[InputState.MouseButtonCount];
    private readonly bool[] _padButtons = new bool[InputState.MaxGamepads * InputState.GamepadButtonCount];
    private UiDocument? _document;
    private SplashCard[] _cards = [];
    private bool _primed;
    private int _shownCard = -1;
    private int _announcedCard = -1;
    private bool _cardVisible;
    private bool _spinnerVisible;
    private Vector2 _laidOut = new(-1, -1);
    private bool _registered;

    public SplashSequence()
    {
        Name = "SplashSequence";
        Layer = DefaultLayer;
        ProcessMode = ProcessMode.Always;
        InputBeforeUi = true; // every event, before any game UI can take it
    }

    /// <summary>The project's <c>splash</c> section (read in <see cref="Node.OnReady"/>).</summary>
    public SplashProjectSettings Settings { get; set; } = new();

    /// <summary>The start scene's load the last card waits for; null when there is none (it counts as completed).</summary>
    public SceneLoad? Load { get; set; }

    /// <summary>The tree reports <see cref="SceneTree.IsLoading"/> while the splash is up (default true; QA runs turn it off).</summary>
    public bool HoldsLoading { get; set; } = true;

    /// <summary>Overrides <see cref="SplashProjectSettings.Skippable"/> (QA captures are never skipped); null uses the settings.</summary>
    public bool? Skippable { get; set; }

    /// <summary>The document; default <see cref="DefaultSource"/>.</summary>
    public string Source { get; set; } = DefaultSource;

    /// <summary>The clock (null before <see cref="Node.OnReady"/>).</summary>
    public SplashTimeline? Timeline { get; private set; }

    /// <summary>The cards shown: <see cref="SplashProjectSettings.ResolvedCards"/> whose image exists.</summary>
    public IReadOnlyList<SplashCard> Cards => _cards;

    /// <summary>The splash document (null before <see cref="Node.OnReady"/>).</summary>
    public UiDocument? Document => _document;

    /// <summary>The size the current card was laid out for (the layer's region, else the UI viewport), in pixels.</summary>
    public Vector2 LayoutSize { get; private set; }

    /// <summary>The current card's size on screen, in pixels.</summary>
    public Vector2 CardSize { get; private set; }

    /// <summary>The sequence has ended (<see cref="Finished"/> was raised).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Raised with a card's index when it is halfway through its hold, fully visible (QA screenshots).</summary>
    public event Action<int>? CardShown;

    /// <summary>Raised once when the sequence ends, just before the splash frees itself.</summary>
    public event Action? Finished;

    /// <summary>
    /// The cards of <paramref name="settings"/> whose image <paramref name="resolve"/> finds and reads as an image, in order.
    /// A missing game card is skipped with a warning; a missing built-in one is an error (the engine's content is incomplete).
    /// </summary>
    public static IReadOnlyList<SplashCard> ResolveCards(SplashProjectSettings settings, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolve);
        var cards = new List<SplashCard>();
        foreach (var entry in settings.ResolvedCards)
        {
            var (image, alt) = entry switch
            {
                SplashProjectSettings.MainframeCard => (MainframeLogo, "Mainframe Engine"),
                SplashProjectSettings.BrutalCard => (settings.UsesWhiteLogo ? BrutalLogoWhite : BrutalLogoBlack, "BRUTAL"),
                _ => (entry, Path.GetFileNameWithoutExtension(entry)),
            };
            if (resolve(image) is not { } file || ReadImageSize(file) is not { } size)
            {
                if (entry.StartsWith('@'))
                    Log.Error($"[Splash] The engine's {entry} card image '{image}' is missing or unreadable: the engine's Content/ is incomplete.");
                else
                    Log.Warning($"[Splash] Splash card '{entry}' is missing or not a PNG; skipped.");
                continue;
            }

            cards.Add(new SplashCard(entry, image, alt, size));
        }

        return cards;
    }

    /// <summary>An image file's pixel size from its header, or null when it is not an image or cannot be read.</summary>
    public static Vector2I? ReadImageSize(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        try
        {
            using var stream = File.OpenRead(file);
            return ImageInfo.FromStream(stream) is { Width: > 0, Height: > 0 } info ? new Vector2I(info.Width, info.Height) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="image"/> scaled to fit a <paramref name="box"/>-sided square, aspect kept (zero for an empty image).</summary>
    public static Vector2 Fit(float box, Vector2I image)
    {
        if (image.X <= 0 || image.Y <= 0 || !(box > 0f))
            return Vector2.Zero;
        var scale = MathF.Min(box / image.X, box / image.Y);
        return new Vector2(image.X * scale, image.Y * scale);
    }

    /// <summary><see cref="Fit"/> into the splash box of a <paramref name="layout"/>-sized screen (<see cref="BoxFraction"/>).</summary>
    public static Vector2 FitBox(Vector2 layout, Vector2I image) => Fit(BoxFraction * MathF.Min(layout.X, layout.Y), image);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (HoldsLoading && Tree is { } tree && !_registered)
        {
            tree.AddLoadingScreen();
            _registered = true;
        }
    }

    protected override void OnExitTree()
    {
        if (_registered)
        {
            Tree?.RemoveLoadingScreen();
            _registered = false;
        }

        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        Func<string, string?> resolve = Server is { } server
            ? server.Files.ResolvePath
            : static path => ContentPaths.Resolve(path) is var full && File.Exists(full) ? full : null;
        _cards = [.. ResolveCards(Settings, resolve)];
        Timeline = new SplashTimeline(_cards.Length, Settings.HoldSeconds, Skippable ?? Settings.Skippable);
        if (Tree is { } tree)
            SeedHeldInput(tree.Input);
        _document = new UiDocument { Name = "Document", Source = Source, Modal = true, AutoFocus = false };
        AddChild(_document);
        Log.Info($"[Splash] Showing the splash: {_cards.Length.ToString(CultureInfo.InvariantCulture)} cards " +
                 $"({string.Join(", ", _cards.Select(c => c.Entry))}), {Timeline.HoldSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s each.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (IsFinished || Timeline is not { } timeline)
            return;
        if (!_primed)
        {
            // The first frame loads the document and lays out the first card hidden, so its fade in is a transition.
            if (timeline.IsDone || _document is null || !_document.EnsureLoaded())
            {
                if (!timeline.IsDone)
                    Log.Error($"[Splash] The splash document '{Source}' did not load; the splash is skipped.");
                Finish();
                return;
            }

            _primed = true;
            _document.Document.AsElement().SetProperty("background-color", Settings.Background);
            Present(timeline, allowVisible: false);
            return;
        }

        if (Load is { Stage: SceneLoadStage.Failed or SceneLoadStage.Canceled })
            timeline.Abort(); // the host takes the failed load's path (GameHost quits with an error)
        else
            timeline.Advance(gameTime.DeltaTime, Load is null || Load.IsCompleted);

        if (timeline.Phase == SplashPhase.Hold && timeline.PhaseTime >= timeline.HoldSeconds * 0.5f && _announcedCard != timeline.CardIndex)
        {
            _announcedCard = timeline.CardIndex;
            CardShown?.Invoke(_announcedCard);
        }

        if (timeline.IsDone)
        {
            Finish();
            return;
        }

        Present(timeline, allowVisible: true);
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        var fresh = inputEvent switch
        {
            InputEventKey key => Press(_keys, (int)key.Key, key.Pressed),
            InputEventMouseButton button => Press(_mouseButtons, (int)button.Button, button.Pressed),
            InputEventGamepadButton pad => (uint)pad.Device < InputState.MaxGamepads
                                           && Press(_padButtons, pad.Device * InputState.GamepadButtonCount + (int)pad.Button, pad.Pressed),
            _ => false,
        };
        if (fresh)
            Timeline?.TrySkip();
        GetViewport()?.SetInputAsHandled(); // the game never sees input while the splash is up
    }

    // A press counts only when that key or button was up: held from launch, or a key's auto-repeat, never skips.
    private static bool Press(bool[] held, int index, bool pressed)
    {
        if ((uint)index >= (uint)held.Length)
            return false;
        var wasHeld = held[index];
        held[index] = pressed;
        return pressed && !wasHeld;
    }

    private void SeedHeldInput(InputState input)
    {
        for (var i = 0; i < _keys.Length; i++)
            _keys[i] = input.IsKeyPressed((Key)i);
        for (var i = 0; i < _mouseButtons.Length; i++)
            _mouseButtons[i] = input.IsMouseButtonPressed((MouseButton)i);
        for (var device = 0; device < InputState.MaxGamepads; device++)
            for (var button = 0; button < InputState.GamepadButtonCount; button++)
                _padButtons[device * InputState.GamepadButtonCount + button] = input.IsGamepadButtonPressed(device, (ButtonName)button);
    }

    // Touches the DOM only when the card, the layout size or a visibility changes.
    private void Present(SplashTimeline timeline, bool allowVisible)
    {
        if (_document is not { IsLoaded: true } document || _cards.Length == 0)
            return;
        var card = document.Document.GetElementById("card");
        var spinner = document.Document.GetElementById("spinner");
        if (card.IsNull || spinner.IsNull)
            return;
        var index = Math.Clamp(timeline.CardIndex, 0, _cards.Length - 1);
        if (index != _shownCard)
        {
            _shownCard = index;
            card.SetAttribute("src", "/" + _cards[index].ImagePath);
            card.SetAttribute("alt", _cards[index].Alt);
            spinner.SetClass("on-light", !Settings.UsesWhiteLogo);
            _laidOut = new Vector2(-1, -1);
        }

        var layout = Region is { } region ? new Vector2(region.Width, region.Height) : Server?.ViewportSize ?? Vector2.Zero;
        if (layout != _laidOut)
        {
            _laidOut = layout;
            Layout(card, spinner, layout, _cards[index].Size);
        }

        var visible = allowVisible && timeline.Phase is SplashPhase.FadeIn or SplashPhase.Hold or SplashPhase.WaitingForLoad;
        if (visible != _cardVisible)
        {
            _cardVisible = visible;
            card.SetClass("visible", visible);
        }

        var waiting = timeline.Phase == SplashPhase.WaitingForLoad;
        if (waiting != _spinnerVisible)
        {
            _spinnerVisible = waiting;
            spinner.SetClass("visible", waiting);
        }
    }

    private void Layout(RmlElement card, RmlElement spinner, Vector2 layout, Vector2I image)
    {
        LayoutSize = layout;
        var size = CardSize = FitBox(layout, image);
        card.SetProperty("width", Px(size.X));
        card.SetProperty("height", Px(size.Y));
        card.SetProperty("margin-left", Px(-size.X * 0.5f));
        card.SetProperty("margin-top", Px(-size.Y * 0.5f));
        spinner.SetProperty("top", Px(layout.Y * 0.5f + size.Y * 0.5f + 0.05f * MathF.Min(layout.X, layout.Y)));
    }

    private static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private void Finish()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        Finished?.Invoke();
        if (!IsQueuedForDeletion)
            QueueFree();
    }
}

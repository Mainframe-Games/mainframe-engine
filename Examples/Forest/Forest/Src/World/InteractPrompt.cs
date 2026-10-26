using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Forest;

/// <summary>
/// The interaction hint under the crosshair's place (<c>Assets/UI/prompt.rml</c>): the key and what it does ("E  Open"),
/// shown while something usable is in reach. Its values change only when they differ (no work or allocation per frame).
/// </summary>
public sealed class InteractPrompt : UiDocument
{
    public const string Open = "Open";
    public const string Close = "Close";

    private RmlDataModel? _model;

    public InteractPrompt()
    {
        Source = "Assets/UI/prompt.rml";
        AutoFocus = false;
    }

    /// <summary>The verb shown next to the key.</summary>
    public string Verb { get; private set; } = Open;

    /// <summary>Whether the prompt is up.</summary>
    public bool Shown { get; private set; }

    /// <summary>The interact action's first keyboard key, as the player bound it.</summary>
    public string Key { get; private set; } = "E";

    /// <summary>Adds a prompt on its own UI layer under <paramref name="parent"/>.</summary>
    public static InteractPrompt Attach(Node parent)
    {
        var layer = new UiLayer { Name = "PromptLayer", Layer = 20 };
        var prompt = new InteractPrompt { Name = "Prompt" };
        layer.AddChild(prompt);
        parent.AddChild(layer);
        return prompt;
    }

    /// <summary>Shows (or hides) the prompt with <paramref name="verb"/>.</summary>
    public void Show(bool shown, string verb)
    {
        if (shown == Shown && ReferenceEquals(verb, Verb))
            return;
        Shown = shown;
        Verb = verb;
        _model?.Dirty("shown");
        _model?.Dirty("verb");
    }

    protected override void OnReady()
    {
        if (Tree?.Input.Map.GetAction("interact") is { } action)
            foreach (var binding in action.Bindings)
                if (binding.Kind == InputBindingKind.Key)
                {
                    Key = binding.DisplayName();
                    break;
                }

        _model = CreateDataModel("prompt")
            .Bind("shown", this, static p => p.Shown)
            .Bind("verb", this, static p => p.Verb)
            .Bind("key", this, static p => p.Key);
    }
}

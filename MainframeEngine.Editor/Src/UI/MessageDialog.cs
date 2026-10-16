using System.Text;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>What a <see cref="MessageDialog"/> shows and how it answers.</summary>
public sealed record MessageRequest
{
    public string Title { get; init; } = "Message";

    /// <summary>Plain text (escaped; line breaks kept).</summary>
    public string Message { get; init; } = "";

    public IReadOnlyList<string> Buttons { get; init; } = ["OK"];

    /// <summary>The button Enter presses (styled primary).</summary>
    public int DefaultButton { get; init; }

    /// <summary>The button Escape presses (-1: the last button).</summary>
    public int CancelButton { get; init; } = -1;

    /// <summary>When set, a text field pre-filled with this value (prompts such as Rename).</summary>
    public string? Input { get; init; }

    /// <summary>Called with the button index and the text field's value.</summary>
    public Action<int, string>? Callback { get; init; }

    /// <summary>The title's icon (a Tabler name or full icon classes); null picks one from the title (error, warning, rename …).</summary>
    public string? Icon { get; init; }

    /// <summary>The icon classes the dialog shows for this request.</summary>
    public string IconClasses => Icon is { Length: > 0 } icon
        ? icon.Contains(' ', StringComparison.Ordinal) ? icon : "icon icon-" + icon
        : Title switch
        {
            "Error" => "icon icon-alert-circle icon-error",
            _ when Title.Contains("unsaved", StringComparison.OrdinalIgnoreCase) || Title.Contains("overwrite", StringComparison.OrdinalIgnoreCase) ||
                   Title.Contains("replace", StringComparison.OrdinalIgnoreCase) => "icon icon-alert-triangle icon-warn",
            _ when Title.StartsWith("Rename", StringComparison.Ordinal) => "icon icon-pencil",
            _ when Title.Contains("Shortcut", StringComparison.Ordinal) => "icon icon-keyboard",
            _ => "icon icon-info-circle",
        };
}

/// <summary>
/// A modal message box / prompt (unsaved-changes questions, errors, rename): title, text, an optional text field and a
/// row of buttons. Enter presses the default button, Escape the cancel button. Requests arriving while one is open are
/// queued.
/// </summary>
public sealed class MessageDialog : EditorDocument
{
    private readonly Queue<MessageRequest> _queue = new();
    private RmlEventListener? _keyListener;

    public MessageDialog(EditorWorkspace workspace)
        : base(workspace, "message.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The request on screen, or null.</summary>
    public MessageRequest? Current { get; private set; }

    public void Show(MessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Current is not null)
        {
            _queue.Enqueue(request);
            return;
        }

        Current = request;
        Visible = true;
        if (!EnsureLoaded())
            return;

        SetText("title-text", request.Title);
        Document.GetElementById("title-icon").SetClassNames(request.IconClasses);
        var message = Document.GetElementById("message");
        if (!message.IsNull)
            message.SetInnerRml(RmlText.Escape(request.Message).Replace("\n", "<br/>", StringComparison.Ordinal));

        var inputRow = Document.GetElementById("input-row");
        var input = Document.GetElementById("input");
        if (!inputRow.IsNull)
            inputRow.SetProperty("display", request.Input is null ? "none" : "flex");
        if (!input.IsNull && request.Input is not null)
        {
            input.SetValue(request.Input);
            input.Focus(focusVisible: true);
        }

        var buttons = new StringBuilder();
        for (var i = 0; i < request.Buttons.Count; i++)
            buttons.Append("<button data-button=\"").Append(i).Append('"')
                .Append(i == request.DefaultButton ? " class=\"primary\"" : "")
                .Append('>').Append(RmlText.Escape(request.Buttons[i])).Append("</button>");
        Document.GetElementById("buttons").SetInnerRml(buttons.ToString());
    }

    /// <summary>Answers the open request as if button <paramref name="button"/> was clicked.</summary>
    public void Answer(int button)
    {
        if (Current is not { } request)
            return;
        var text = IsLoaded && !Document.GetElementById("input").IsNull ? Document.GetElementById("input").Value ?? "" : "";
        Current = null;
        HideAndReleaseFocus();
        try
        {
            request.Callback?.Invoke(button, text);
        }
        finally
        {
            if (Current is null && _queue.Count > 0)
                Show(_queue.Dequeue());
        }
    }

    protected override void OnAttach(RmlDocument document)
    {
        _keyListener?.Remove();
        _keyListener = document.AsElement().AddEventListener("keydown", OnKeyDown);
        if (Current is { } pending && Visible)
        {
            Current = null;
            Show(pending); // re-render after a hot reload
        }
    }

    protected override void OnClickElement(RmlEvent e)
    {
        var button = FindAttribute(e.Target, "data-button");
        if (button is not null && int.TryParse(button, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
            Answer(index);
    }

    private void OnKeyDown(RmlEvent e)
    {
        if (Current is not { } request)
            return;
        var key = (RmlKey)e.GetParameter("key_identifier", 0);
        if (key is RmlKey.Return or RmlKey.NumpadEnter)
            Answer(request.DefaultButton);
        else if (key == RmlKey.Escape)
            Answer(request.CancelButton >= 0 ? request.CancelButton : request.Buttons.Count - 1);
    }
}

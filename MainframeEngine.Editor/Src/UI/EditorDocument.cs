using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Base of the editor's RmlUi documents (panels and dialogs): loads <c>Content/Editor/&lt;file&gt;.rml</c>, positions
/// its body from the <see cref="EditorLayout"/> rectangle, and routes clicks on elements with
/// <c>data-command="id"</c> to <see cref="EditorCommands"/>. Listeners are attached on load and again after a hot reload.
/// </summary>
public abstract class EditorDocument : UiDocument
{
    private RmlEventListener? _clickListener;
    private LayoutRect _appliedRect;

    protected EditorDocument(EditorWorkspace workspace, string file)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Source = $"Content/Editor/{file}";
        AutoFocus = false;
        Reloaded += OnDocumentReloaded;
    }

    public EditorWorkspace Workspace { get; }

    /// <summary>The panel's rectangle (dp); null for documents that size themselves (dialogs, overlays).</summary>
    public LayoutRect? Rect { get; private set; }

    /// <summary>Places the document's body at <paramref name="rect"/> (only when it changed).</summary>
    public void SetRect(LayoutRect rect)
    {
        Rect = rect;
        ApplyRect();
    }

    private void ApplyRect()
    {
        if (Rect is not { } rect || !IsLoaded || rect == _appliedRect)
            return;
        _appliedRect = rect;
        var body = Document.AsElement();
        body.SetProperty("left", RmlText.Dp(rect.X));
        body.SetProperty("top", RmlText.Dp(rect.Y));
        body.SetProperty("width", RmlText.Dp(rect.Width));
        body.SetProperty("height", RmlText.Dp(rect.Height));
        OnRectApplied(rect);
    }

    /// <summary>Called after the body moved or resized.</summary>
    protected virtual void OnRectApplied(LayoutRect rect)
    {
    }

    protected sealed override void OnLoaded()
    {
        _appliedRect = default;
        Attach();
        ApplyRect();
    }

    private void OnDocumentReloaded()
    {
        _appliedRect = default;
        Attach();
        ApplyRect();
    }

    private void Attach()
    {
        _clickListener?.Remove();
        _clickListener = Document.AsElement().AddEventListener("click", OnClick);
        OnAttach(Document);
    }

    /// <summary>Attach element listeners here (runs after every load and hot reload).</summary>
    protected virtual void OnAttach(RmlDocument document)
    {
    }

    private void OnClick(RmlEvent e)
    {
        var command = FindAttribute(e.Target, "data-command");
        if (command is not null)
        {
            e.StopPropagation();
            Workspace.Commands.Execute(command);
            return;
        }

        OnClickElement(e);
    }

    /// <summary>A click that did not hit a <c>data-command</c> element.</summary>
    protected virtual void OnClickElement(RmlEvent e)
    {
    }

    /// <summary>The value of <paramref name="attribute"/> on <paramref name="element"/> or its nearest ancestor that has it.</summary>
    protected static string? FindAttribute(RmlElement element, string attribute)
    {
        for (var e = element; !e.IsNull; e = e.Parent)
        {
            if (e.HasAttribute(attribute))
                return e.GetAttribute(attribute);
            if (e.TagName == "body")
                break;
        }

        return null;
    }

    /// <summary>The nearest element (self or ancestor) that has <paramref name="attribute"/>.</summary>
    protected static RmlElement FindWithAttribute(RmlElement element, string attribute)
    {
        for (var e = element; !e.IsNull; e = e.Parent)
        {
            if (e.HasAttribute(attribute))
                return e;
            if (e.TagName == "body")
                break;
        }

        return default;
    }

    /// <summary>
    /// Hides the document and gives up keyboard focus: a hidden dialog must not keep a text field focused, or the UI would
    /// keep taking every key and the editor's shortcuts would stop working.
    /// </summary>
    protected void HideAndReleaseFocus()
    {
        if (IsLoaded && Layer?.Context is { IsDisposed: false } context)
        {
            var focus = context.FocusElement;
            if (!focus.IsNull && focus.OwnerDocument == Document)
                focus.Blur();
        }

        Visible = false;
    }

    /// <summary>Sets an element's text content (escaped), when the element exists.</summary>
    protected void SetText(string id, string text)
    {
        if (!IsLoaded)
            return;
        var element = Document.GetElementById(id);
        if (!element.IsNull)
            element.SetInnerRml(RmlText.Escape(text));
    }
}

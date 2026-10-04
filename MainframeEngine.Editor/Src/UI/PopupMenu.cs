using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// An entry of a <see cref="PopupMenu"/>: an item (command id) with an optional leading icon (a Tabler name, or full icon
/// classes when it contains a space), a separator or a header.
/// </summary>
public sealed record MenuItem(string Label, string? Command, string? Shortcut = null, bool Enabled = true, string? Css = null, string? Icon = null)
{
    public static readonly MenuItem Separator = new("", null) { IsSeparator = true };

    public static MenuItem Header(string label) => new(label, null) { IsHeader = true };

    public bool IsSeparator { get; private init; }
    public bool IsHeader { get; private init; }
}

/// <summary>
/// A popup menu (menu bar dropdowns, the scene tree's context menu, the undo history): items generated as RML at the
/// requested position over a transparent full-window backdrop; a click on an item runs its command, a click anywhere
/// else (or Escape) closes it.
/// </summary>
public sealed class PopupMenu : EditorDocument
{
    private Action<string>? _onCommand;
    private Action? _onClose;

    public PopupMenu(EditorWorkspace workspace)
        : base(workspace, "popup_menu.rml")
    {
        Visible = false;
    }

    /// <summary>The items shown (for tests and QA scripts).</summary>
    public IReadOnlyList<MenuItem> Items { get; private set; } = [];

    /// <summary>
    /// Shows <paramref name="items"/> with the top-left corner at (<paramref name="x"/>, <paramref name="y"/>) dp.
    /// <paramref name="onCommand"/> handles the chosen command (default: <see cref="EditorCommands.Execute"/>).
    /// </summary>
    public void Show(IReadOnlyList<MenuItem> items, float x, float y, Action<string>? onCommand = null, Action? onClose = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
        _onCommand = onCommand;
        _onClose = onClose;
        Visible = true;
        if (!EnsureLoaded())
            return;
        var menu = Document.GetElementById("menu");
        if (menu.IsNull)
            return;

        var rml = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsSeparator)
            {
                rml.Append("<div class=\"menu-separator\"></div>");
                continue;
            }

            if (item.IsHeader)
            {
                rml.Append("<div class=\"menu-header\">").Append(RmlText.Escape(item.Label)).Append("</div>");
                continue;
            }

            rml.Append("<div class=\"menu-item").Append(item.Enabled ? "" : " disabled");
            if (item.Css is { } css)
                rml.Append(' ').Append(css);
            rml.Append("\" data-index=\"").Append(i).Append("\"><span class=\"");
            if (item.Icon is { Length: > 0 } icon)
                rml.Append(icon.Contains(' ', StringComparison.Ordinal) ? icon : "icon icon-" + icon).Append(" menu-icon");
            else
                rml.Append("menu-icon none");
            rml.Append("\"></span><span class=\"menu-label\">").Append(RmlText.Escape(item.Label)).Append("</span>");
            if (item.Shortcut is { } shortcut)
                rml.Append("<span class=\"menu-shortcut\">").Append(RmlText.Escape(ShortcutText(shortcut))).Append("</span>");
            rml.Append("</div>");
        }

        menu.SetInnerRml(rml.ToString());

        // Keep the menu inside the window.
        var window = Workspace.Host.WindowSize;
        var estimatedHeight = items.Count * 24f + 10f;
        var left = MathF.Max(0, MathF.Min(x, window.X - 260));
        var top = y + estimatedHeight > window.Y ? MathF.Max(0, window.Y - estimatedHeight) : y;
        menu.SetProperty("left", RmlText.Dp(left));
        menu.SetProperty("top", RmlText.Dp(top));
    }

    /// <summary>On macOS shortcuts read Cmd instead of Ctrl.</summary>
    private static string ShortcutText(string shortcut) =>
        OperatingSystem.IsMacOS() ? shortcut.Replace("Ctrl+", "Cmd+", StringComparison.Ordinal) : shortcut;

    public void Close()
    {
        if (!Visible)
            return;
        HideAndReleaseFocus();
        var onClose = _onClose;
        _onClose = null;
        _onCommand = null;
        onClose?.Invoke();
    }

    /// <summary>Runs item <paramref name="index"/> as if clicked (QA scripts, tests).</summary>
    public bool Choose(int index)
    {
        if ((uint)index >= (uint)Items.Count || Items[index] is not { Enabled: true, Command: { } command } || Items[index].IsSeparator)
            return false;
        var handler = _onCommand;
        Close();
        if (handler is not null)
            handler(command);
        else
            Workspace.Commands.Execute(command);
        return true;
    }

    /// <summary>Runs the item with command <paramref name="command"/> (QA scripts).</summary>
    public bool Choose(string command)
    {
        for (var i = 0; i < Items.Count; i++)
            if (string.Equals(Items[i].Command, command, StringComparison.Ordinal))
                return Choose(i);
        return false;
    }

    protected override void OnClickElement(RmlEvent e)
    {
        var index = FindAttribute(e.Target, "data-index");
        if (index is not null && int.TryParse(index, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
        {
            if (Items[i].Enabled)
                Choose(i);
            return;
        }

        for (var element = e.Target; !element.IsNull; element = element.Parent)
            if (element.Id == "menu")
                return; // a separator, header or disabled item
        Close(); // the backdrop
    }
}

namespace MainframeEngine.Editor;

/// <summary>
/// A tab of the centre panel (<see cref="EditorSession.Tabs"/>): an <see cref="EditedScene"/>, or a <see cref="UiPreview"/>
/// of an <c>.rml</c> document.
/// </summary>
public interface IEditorTab : IDisposable
{
    /// <summary>Absolute path of the tab's file, or null for a new scene never saved.</summary>
    string? FilePath { get; }

    /// <summary>The file name (or "Untitled").</summary>
    string DisplayName { get; }

    /// <summary>Tab title: the file name with <c>*</c> while dirty.</summary>
    string Title { get; }

    /// <summary>Unsaved changes (closing asks first).</summary>
    bool IsDirty { get; }

    /// <summary>The tab icon's class list, e.g. <c>"icon icon-layout icon-ui"</c>.</summary>
    string IconClasses { get; }

    /// <summary>The tab's tooltip: name, state and file.</summary>
    string Tooltip { get; }
}

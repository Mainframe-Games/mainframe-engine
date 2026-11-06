using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// An owner of one RmlUi context and the <see cref="UiDocument"/>s below it (G11.5): a screen <see cref="UiLayer"/> or a
/// world-space <see cref="UiPanel3D"/>. Documents attach to the nearest host above them; data model names are unique per
/// host.
/// </summary>
internal interface IUiContextHost
{
    /// <summary>The host's context while it is in a tree with a UI server.</summary>
    RmlContext? Context { get; }

    /// <summary>The UI server owning <see cref="Context"/>.</summary>
    UiServer? Server { get; }

    /// <summary>The host node's name (log messages).</summary>
    string Name { get; }

    /// <summary>The documents attached to the host, in attach order.</summary>
    List<UiDocument> DocumentList { get; }

    void AddDocument(UiDocument document);

    void RemoveDocument(UiDocument document);

    /// <summary>A document of the host loaded or reloaded (panels in the editor render again).</summary>
    void OnDocumentLoaded(UiDocument document);
}

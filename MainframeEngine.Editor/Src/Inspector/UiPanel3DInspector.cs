using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="UiPanel3D"/> inspector (G11.7): above the generated rows, an icon button that opens the panel's first
/// document in a UI preview tab at the panel's <see cref="UiPanel3D.Resolution"/> (<see cref="UiPreview.FixedSize"/>),
/// with the preview's stand-in data.
/// </summary>
[CustomInspector(typeof(UiPanel3D))]
public sealed class UiPanel3DInspector : ICustomInspector, IWorkspaceInspector
{
    public const string PreviewAction = "panel-preview";

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target)
    {
        var panel = (UiPanel3D)target;
        var source = DocumentSource(panel);
        var tip = source is null
            ? "Open document in UI preview — add a UiDocument with a Source below the panel first"
            : $"Open document in UI preview — {source} at {panel.Resolution.X}×{panel.Resolution.Y}";
        return new StringBuilder(256)
            .Append("<div class=\"probe-bake\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(PreviewAction).Append("\" data-tooltip=\"").Append(RmlText.Escape(tip))
            .Append("\"><span class=\"icon icon-sm icon-layout\"></span></button>")
            .Append("</div>")
            .ToString();
    }

    /// <summary>The <c>Source</c> of the panel's first document (its attached documents, else its first child document).</summary>
    internal static string? DocumentSource(UiPanel3D panel)
    {
        foreach (var document in panel.Documents)
            if (document.Source is { Length: > 0 } source)
                return source;
        foreach (var child in panel.Children)
            if (child is UiDocument { Source: { Length: > 0 } source })
                return source;
        return null;
    }

    public void OnAction(object target, string action, IInspectorContext context)
    {
        if (action != PreviewAction || Workspace is not { } workspace)
            return;
        var panel = (UiPanel3D)target;
        if (DocumentSource(panel) is not { } source)
        {
            Log.Warning($"[Editor] UiPanel3D '{panel.Name}' has no document with a Source to preview.");
            return;
        }

        var path = ContentPaths.Resolve(source);
        if (!File.Exists(path))
        {
            Log.Warning($"[Editor] '{source}' of UiPanel3D '{panel.Name}' was not found ({path}).");
            return;
        }

        var preview = workspace.Session.OpenUiPreview(path);
        preview.FixedSize = panel.Resolution;
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }
}

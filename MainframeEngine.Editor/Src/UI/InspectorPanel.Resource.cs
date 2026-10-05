using System.Text;

namespace MainframeEngine.Editor;

/// <summary>
/// The inspector on a resource file (a <c>.mres</c> double-clicked in the FileSystem panel, or the project's audio bus
/// layout from Project Settings): the resource's rows and custom inspector, edits through its own history
/// (<see cref="EditedResource"/>), a Save and a Close button in the header. Selecting a node returns to the scene.
/// </summary>
public sealed partial class InspectorPanel
{
    private EditedResource? _resource;

    /// <summary>The resource file being inspected, or null (the inspector shows the selection).</summary>
    public EditedResource? InspectedResource => _resource;

    /// <summary>Inspects the resource file at <paramref name="path"/> (loaded through the resource loader).</summary>
    public bool InspectResourceFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Resource resource;
        try
        {
            resource = ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(path));
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError($"Could not open {Path.GetFileName(path)}", e);
            return false;
        }

        CloseResource();
        _resource = new EditedResource(resource, path);
        _resource.History.Changed += OnResourceChanged;
        SelectTab(InspectorTab.Properties);
        Rebuild();
        return true;
    }

    /// <summary>Leaves resource mode (unsaved edits stay in memory and are reported).</summary>
    public void CloseResource()
    {
        if (_resource is not { } resource)
            return;
        if (resource.IsDirty)
            Log.Warning($"[Editor] {Path.GetFileName(resource.FilePath)} has unsaved changes (open it again and Save to keep them).");
        resource.History.Changed -= OnResourceChanged;
        _resource = null;
        resource.Dispose();
        Rebuild();
    }

    /// <summary>Saves the inspected resource file.</summary>
    public bool SaveResource()
    {
        if (_resource is not { } resource)
            return false;
        try
        {
            resource.Save();
            Log.Info($"[Editor] Saved {Workspace.Session.DisplayPath(resource.FilePath)}");
            Rebuild();
            return true;
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError($"Could not save {Path.GetFileName(resource.FilePath)}", e);
            return false;
        }
    }

    private void OnResourceChanged()
    {
        if (_rebuildPending || _model?.CustomInspector is not null)
            Rebuild();
        else
            RefreshValues();
        UpdateResourceTitle();
    }

    private static void AppendResourceHeader(StringBuilder rml, EditedResource resource)
    {
        var type = Serialization.TypeRegistry.GetNearest(resource.Resource.GetType())?.Name ?? resource.Resource.GetType().Name;
        rml.Append("<div class=\"insp-head\"><div class=\"insp-type\"><span class=\"icon icon-sm icon-")
            .Append(EditorIcons.For(resource.Resource.GetType())).Append(" icon-resource\"></span> ").Append(RmlText.Escape(type))
            .Append(" · resource file</div><div class=\"insp-name-row\"><span class=\"res-file mono\" id=\"res-file\">")
            .Append(RmlText.Escape(Path.GetFileName(resource.FilePath) + (resource.IsDirty ? " *" : "")))
            .Append("</span><button class=\"tool-button small\" data-action=\"inspector-save\" data-tooltip=\"Save — write the resource file\">")
            .Append("<span class=\"icon icon-sm icon-device-floppy\"></span></button><button class=\"tool-button small\" data-action=\"inspector-close\" ")
            .Append("data-tooltip=\"Close — back to the selected node\"><span class=\"icon icon-sm icon-x\"></span></button></div></div>");
    }

    private void UpdateResourceTitle()
    {
        if (_resource is { } resource)
            SetText("res-file", Path.GetFileName(resource.FilePath) + (resource.IsDirty ? " *" : ""));
    }

    // Header buttons of the resource mode (handled before custom inspectors see the click).
    private bool HandleResourceAction(string action)
    {
        switch (action)
        {
            case "inspector-save":
                SaveResource();
                return true;
            case "inspector-close":
                CloseResource();
                return true;
            default:
                return false;
        }
    }

    // The context edits go to: the inspected resource file, else the scene of the inspected node.
    private IInspectorContext? EditContext =>
        _resource is not null ? _resource : _scene is { } scene && Workspace.Session.Scenes.Contains(scene) ? scene : null;
}

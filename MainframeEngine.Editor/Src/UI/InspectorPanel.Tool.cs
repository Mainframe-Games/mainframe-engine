using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The inspector's tool tab (docs/design/editor.md#viewport-tool-api): while a viewport tool is active (a terrain, a
/// river selected alone) the header shows its icon as a third tab and the tab shows the tool's RML. The tab opens when
/// a tool activates and closes back to Properties when it deactivates. Clicks on <c>data-tool-action</c> elements and
/// changes of <c>data-tool-field</c> inputs go to the tool; the body is rebuilt when the tool's dock version changes,
/// except for the field edit that is being made (a slider keeps its drag).
/// </summary>
public sealed partial class InspectorPanel
{
    private int _toolsVersion = -1;
    private int _toolDockShown = -1;
    private IEditorViewportTool? _toolShown;

    /// <summary>The tool tab's body markup on screen, for tests.</summary>
    public string? ToolDockRml { get; private set; }

    /// <summary>Per frame: follows tool activation and rebuilds the tool tab when its content changed.</summary>
    public void TickTool()
    {
        var tools = Workspace.Tools;
        if (tools.Version != _toolsVersion)
        {
            _toolsVersion = tools.Version;
            if (IsLoaded)
                ApplyToolTab(Document);
            if (tools.Active is not null)
                SelectTab(InspectorTab.Tool);
            else if (Tab == InspectorTab.Tool)
                SelectTab(InspectorTab.Properties);
            _toolDockShown = -1;
        }

        var tool = tools.Active;
        if (tool is null || Tab != InspectorTab.Tool)
            return;
        if (ReferenceEquals(tool, _toolShown) && tool.DockVersion == _toolDockShown)
            return;
        RebuildToolDock();
    }

    private void RebuildToolDock()
    {
        var tool = Workspace.Tools.Active;
        _toolShown = tool;
        _toolDockShown = tool?.DockVersion ?? -1;
        ToolDockRml = tool?.BuildDockRml();
        if (!IsLoaded || Document.GetElementById("tool-body") is not { IsNull: false } body)
            return;
        var suppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            body.SetInnerRml(ToolDockRml ?? "");
        }
        finally
        {
            _suppressEvents = suppressed;
        }
    }

    private void ApplyToolTab(RmlDocument document)
    {
        var info = Workspace.Tools.ActiveInfo;
        var tab = document.GetElementById("tab-tool");
        if (tab.IsNull)
            return;
        tab.SetClass("hidden", info is null);
        if (info is null)
            return;
        tab.SetAttribute("data-tooltip", RmlText.Escape(info.Title) + " — edit the selected node in the view with this tool");
        tab.SetInnerRml("<span class=\"icon icon-" + RmlText.Escape(info.Icon) + " icon-sm\"></span>");
    }

    // A click on a data-tool-action element of the tool tab.
    private bool HandleToolClick(RmlEvent e)
    {
        if (Tab != InspectorTab.Tool || Workspace.Tools.Active is not { } tool)
            return false;
        var element = FindWithAttribute(e.Target, "data-tool-action");
        if (element.IsNull)
            return false;
        var action = element.GetAttribute("data-tool-action");
        if (string.IsNullOrEmpty(action))
            return false;
        tool.OnDockAction(action, element.GetAttribute("data-tool-value"));
        return true;
    }

    // A data-tool-field input changed: sliders, checkboxes and dropdowns at once, text fields on Enter (and on blur).
    private bool HandleToolChange(RmlEvent e, bool blur)
    {
        var target = e.Target;
        if (target.GetAttribute("data-tool-field") is not { Length: > 0 } field || Workspace.Tools.Active is not { } tool)
            return false;
        var type = target.GetAttribute("type");
        var text = target.TagName == "input" && type is not ("range" or "checkbox");
        if (text && !blur && !e.GetParameter("linebreak", false))
            return true;
        var value = type == "checkbox" ? (target.HasAttribute("checked") ? "true" : "false") : blur ? target.Value ?? "" : e.Value ?? target.Value ?? "";
        var before = tool.DockVersion;
        tool.OnDockField(field, value);
        // The edit being made does not rebuild the body (a slider keeps its drag); anything else it changed shows next time.
        if (type is "range" && ReferenceEquals(tool, _toolShown) && _toolDockShown == before)
            _toolDockShown = tool.DockVersion;
        return true;
    }
}

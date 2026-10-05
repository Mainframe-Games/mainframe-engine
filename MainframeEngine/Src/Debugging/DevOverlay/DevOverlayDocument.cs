using System.Text;

namespace MainframeEngine;

/// <summary>The overlay's single document: its RML is composed from the panels; models are created before it loads.</summary>
internal sealed class DevOverlayDocument : UiDocument
{
    private readonly DevOverlay _overlay;

    public DevOverlayDocument(DevOverlay overlay)
    {
        _overlay = overlay;
        Name = "Panels";
        AutoFocus = false;
        Rml = Compose(overlay.Panels);
    }

    internal bool IsReady { get; private set; }

    protected override void OnReady()
    {
        foreach (var panel in _overlay.Panels)
            BindPanel(panel);
        IsReady = true;
    }

    internal void BindPanel(DevOverlayPanel panel)
    {
        panel.Model = CreateDataModel(panel.ModelName)
            .Bind("open", panel, static p => p.Expanded, static (p, v) => p.Expanded = v);
        panel.Bind?.Invoke(panel);
    }

    internal void Recompose()
    {
        Rml = Compose(_overlay.Panels);
        Reload();
    }

    internal static string Compose(IReadOnlyList<DevOverlayPanel> panels)
    {
        var rml = new StringBuilder("""
            <rml><head><title>Developer overlay</title>
            <link type="text/rcss" href="/Content/UI/widgets/widgets.rcss"/>
            <link type="text/rcss" href="/Content/UI/dev/overlay.rcss"/></head>
            <body class="dev-overlay"><div id="dev-panel">
            """);
        foreach (var panel in panels)
            rml.Append("<div class=\"dev-section\" data-model=\"").Append(panel.ModelName)
                .Append("\"><div class=\"dev-title\" data-event-click=\"open = !open\">")
                .Append(System.Security.SecurityElement.Escape(panel.Title))
                .Append("</div><div class=\"dev-body\" data-if=\"open\">").Append(panel.Rml).Append("</div></div>");
        rml.Append("</div></body></rml>");
        return rml.ToString();
    }
}

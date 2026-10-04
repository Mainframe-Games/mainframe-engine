using System.Globalization;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>The inspector's tabs.</summary>
public enum InspectorTab
{
    Properties,
    Signals,
}

/// <summary>
/// The inspector's Signals tab (Godot's Node dock): the selected node's <c>[Signal]</c>s with their saved connections,
/// a connect button per signal (opens the <see cref="ConnectSignalDialog"/>) and a disconnect button per connection.
/// Both go through the scene's undo history; the scene saves the connections.
/// </summary>
public sealed partial class InspectorPanel
{
    private IReadOnlyList<SignalRow> _signalRows = [];
    private int _signalsVersion = -1;
    private EditedScene? _signalsScene;

    /// <summary>The tab on screen.</summary>
    public InspectorTab Tab { get; private set; } = InspectorTab.Properties;

    /// <summary>The Signals tab's rows (the primary selection's signals), for tests and QA.</summary>
    public IReadOnlyList<SignalRow> SignalRows => _signalRows;

    private void AttachSignals(RmlDocument document) => ApplyTab(document);

    /// <summary>Shows the Properties or the Signals tab.</summary>
    public void SelectTab(InspectorTab tab)
    {
        if (Tab == tab)
            return;
        Tab = tab;
        if (IsLoaded)
            ApplyTab(Document);
        RebuildSignals();
    }

    private void ApplyTab(RmlDocument document)
    {
        document.GetElementById("tab-properties").SetClass("active", Tab == InspectorTab.Properties);
        document.GetElementById("tab-signals").SetClass("active", Tab == InspectorTab.Signals);
        document.GetElementById("inspector-body").SetClass("hidden", Tab != InspectorTab.Properties);
        document.GetElementById("signals-body").SetClass("hidden", Tab != InspectorTab.Signals);
        document.GetElementById("label-splitter").SetClass("hidden", Tab != InspectorTab.Properties);
        SetText("inspector-title", Tab == InspectorTab.Properties ? "Inspector" : "Signals");
    }

    /// <summary>Re-reads the signals of the inspected node (after a selection or scene change).</summary>
    private void RebuildSignals()
    {
        var scene = _scene;
        _signalsScene = scene;
        _signalsVersion = scene?.Version ?? -1;
        _signalRows = _target is Node { IsFreed: false } node && _targets.Count == 1 ? SignalModel.For(node, scene?.Root) : [];
        if (!IsLoaded || Tab != InspectorTab.Signals)
            return;
        var body = Document.GetElementById("signals-body");
        if (body.IsNull)
            return;
        if (_target is not Node target || scene is null)
        {
            body.SetInnerRml("<div class=\"empty\">Select a node to see its signals.</div>");
            return;
        }

        if (_targets.Count > 1)
        {
            body.SetInnerRml("<div class=\"empty\">Signals are connected one node at a time: select a single node.</div>");
            return;
        }

        var rml = new StringBuilder(2048);
        if (target is MissingNode)
            rml.Append("<div class=\"notice\">The node's type is not loaded; its signals appear once the game assembly is built and loaded.</div>");
        for (var i = 0; i < _signalRows.Count; i++)
        {
            var row = _signalRows[i];
            var parameters = row.Signature.AsSpan(row.Name.Length);
            rml.Append("<div class=\"sig-row\"><span class=\"icon icon-antenna-bars-5 icon-sm icon-logic\"></span><span class=\"sig-name\">")
                .Append(RmlText.Escape(row.Name)).Append("<span class=\"sig-params\">").Append(RmlText.Escape(parameters.ToString()))
                .Append("</span></span><button class=\"tool-button small\" data-signal-action=\"connect\" data-signal=\"").Append(i)
                .Append("\" data-tooltip=\"Connect ").Append(RmlText.Escape(row.Name))
                .Append(" — call a method of a node in this scene when it is emitted\"><span class=\"icon icon-plug icon-sm\"></span></button></div>");
            for (var j = 0; j < row.Connections.Count; j++)
            {
                var connection = row.Connections[j];
                rml.Append("<div class=\"sig-conn\"><span class=\"icon icon-arrow-forward icon-sm\"></span><span class=\"grow\">")
                    .Append(RmlText.Escape(PathText(scene, connection.Target))).Append(" · ").Append(RmlText.Escape(connection.Method)).Append("()</span>");
                var flags = connection.Flags & ~ConnectFlags.Persist;
                if (flags != ConnectFlags.None)
                    rml.Append("<span class=\"sig-flags\">").Append(RmlText.Escape(flags.ToString())).Append("</span>");
                rml.Append("<button class=\"tool-button small\" data-signal-action=\"disconnect\" data-signal=\"").Append(i).Append("\" data-conn=\"")
                    .Append(j).Append("\" data-tooltip=\"Disconnect — remove this connection from the scene\"><span class=\"icon icon-plug-x icon-sm\"></span></button></div>");
            }
        }

        if (_signalRows.Count == 0)
            rml.Append("<div class=\"empty\">This node has no signals.</div>");
        body.SetInnerRml(rml.ToString());
    }

    private static string PathText(EditedScene scene, Node node) =>
        ReferenceEquals(node, scene.Root) ? node.Name : scene.Root.GetPathTo(node).Path;

    /// <summary>Rebuilds the Signals tab when the scene changed since it was drawn (connect, disconnect, undo).</summary>
    private void RefreshSignals()
    {
        if (!ReferenceEquals(_signalsScene, _scene) || _signalsVersion != (_scene?.Version ?? -1))
            RebuildSignals();
    }

    private bool HandleSignalClick(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-tab") is { } tab)
        {
            SelectTab(tab == "signals" ? InspectorTab.Signals : InspectorTab.Properties);
            return true;
        }

        var element = FindWithAttribute(e.Target, "data-signal-action");
        if (element.IsNull)
            return false;
        var signal = ParseInt(element.GetAttribute("data-signal"));
        switch (element.GetAttribute("data-signal-action"))
        {
            case "connect":
                ConnectSignal(signal);
                break;
            case "disconnect":
                DisconnectSignal(signal, ParseInt(element.GetAttribute("data-conn")));
                break;
        }

        return true;
    }

    private static int ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    /// <summary>Opens the connect dialog for signal <paramref name="index"/> of the Signals tab.</summary>
    public void ConnectSignal(int index)
    {
        if ((uint)index >= (uint)_signalRows.Count || _target is not Node source || _scene is not { } scene)
            return;
        Workspace.SignalDialog.Show(scene, source, _signalRows[index].Signal);
    }

    /// <summary>Removes connection <paramref name="connection"/> of signal <paramref name="index"/> (undoable).</summary>
    public void DisconnectSignal(int index, int connection)
    {
        if ((uint)index >= (uint)_signalRows.Count || _scene is not { } scene)
            return;
        var connections = _signalRows[index].Connections;
        if ((uint)connection < (uint)connections.Count)
            scene.DisconnectSignal(connections[connection]);
    }
}

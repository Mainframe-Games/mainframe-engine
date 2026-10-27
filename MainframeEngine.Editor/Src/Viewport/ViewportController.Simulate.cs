using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The view during Simulate (G7.5): the simulated copy's <see cref="SubViewport"/> is sized, rendered through the tab's
/// editor camera and published instead of the edited one; the selection boxes are drawn on the simulated counterparts; a
/// click picks in the copy and selects the edited node at the same path. Gizmos, handles and box selection wait until
/// Stop; the camera keeps moving.
/// </summary>
public sealed partial class ViewportController
{
    /// <summary>True while <paramref name="scene"/> simulates.</summary>
    private bool IsSimulating(EditedScene scene) => _workspace.Simulation.IsSimulating(scene);

    /// <summary>The view shown for <paramref name="scene"/>: the simulated copy's while it simulates, else its own.</summary>
    public SubViewport ViewOf(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return _workspace.Simulation.ViewportOf(scene) ?? scene.Viewport;
    }

    /// <summary>Cancels a drag in progress (a gizmo or handle drag is put back) — a simulation starting or stopping.</summary>
    public void CancelInteraction() => CancelDrag();

    /// <summary>A simulation started or stopped: forget picks in flight and publish the other view.</summary>
    public void OnSimulationChanged()
    {
        CancelDrag();
        _pick = default;
        _boxPick = default;
        _boxNodes.Clear();
        _hoverHandle = -1;
        _simulatedSelection.Clear();
        _simulatedSelectionRoot = null;
        _simulatedSelectionVersion = -1;
        _workspace.ViewportPanel?.SetHandleLabel(null, 0, 0);
        _handleLabelShown = false;
        // The stopped copy's target is gone: the UI must not sample it.
        if (_registered is not null)
            Tree?.Servers.Get<UiServer>()?.UnregisterTexture(ViewportPanel.TextureName);
        _registered = null;
        // The image drops its source until the other view's target is published (RmlUi would keep a failed load).
        _workspace.ViewportPanel?.UpdateImage(false);
    }

    // The grid lives in the shown view (the copy brings none of its own).
    private void EnsureGrid(EditedScene scene, SubViewport viewport)
    {
        if (_grid is { IsFreed: false } grid && ReferenceEquals(grid.Parent, viewport))
            return;
        if (_grid is { IsFreed: false } old)
            old.Free();
        _grid = new Grid3D { Name = "EditorGrid", Visible = GridVisible && !scene.Camera.Is2D };
        viewport.AddChild(_grid);
    }

    // The selected nodes' simulated counterparts, looked up (a NodePath each) only when the selection or the copy changes.
    private readonly List<Node> _simulatedSelection = [];
    private Node? _simulatedSelectionRoot;
    private int _simulatedSelectionVersion = -1;

    private List<Node> SimulatedSelection(EditedScene scene)
    {
        var root = _workspace.Simulation.Root;
        if (ReferenceEquals(root, _simulatedSelectionRoot) && _simulatedSelectionVersion == scene.Selection.Version)
            return _simulatedSelection;
        _simulatedSelectionRoot = root;
        _simulatedSelectionVersion = scene.Selection.Version;
        _simulatedSelection.Clear();
        var selected = scene.Selection.Nodes;
        for (var i = 0; i < selected.Count; i++)
            if (_workspace.Simulation.CounterpartOf(selected[i]) is { } counterpart)
                _simulatedSelection.Add(counterpart);
        return _simulatedSelection;
    }

    // Selection boxes on the simulated counterparts (and the 2D grid); no icons, gizmo or handles. 0 B per frame: the
    // counterparts are cached (game code may still allocate).
    private void DrawSimulationVisuals(EditedScene scene, SubViewport viewport, Vector2 pixels)
    {
        var counterparts = SimulatedSelection(scene);
        var overlay = viewport.OverlayLines;
        var camera = scene.Camera;
        if (camera.Is2D)
        {
            if (GridVisible)
                DrawGrid2D(viewport.DebugLines, camera, pixels);
            var unit = 1f / camera.Zoom2D;
            var marker = MarkerPoints * ViewScale * unit;
            for (var i = 0; i < counterparts.Count; i++)
            {
                if (counterparts[i] is not Node2D { IsFreed: false, IsInsideTree: true } simulated)
                    continue;
                if (simulated is CollisionShape2D { Shape: { } shape } shapeNode)
                {
                    DrawShapeSelection(overlay, shapeNode, shape, unit);
                    continue;
                }

                var p = new Vector3(simulated.GlobalPosition, 0f);
                overlay.AddLine(p - new Vector3(marker, 0, 0), p + new Vector3(marker, 0, 0), SelectionColor);
                overlay.AddLine(p - new Vector3(0, marker, 0), p + new Vector3(0, marker, 0), SelectionColor);
            }

            return;
        }

        var displayHeight = pixels.Y * _workspace.Host.PixelScale / ViewScale;
        for (var i = 0; i < counterparts.Count; i++)
            if (counterparts[i] is Node3D { IsFreed: false, IsInsideTree: true } simulated)
                DrawSelection(overlay, camera, displayHeight, simulated);
    }
}

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
        _workspace.ViewportPanel?.SetHandleLabel(null, 0, 0);
        _handleLabelShown = false;
        // The stopped copy's target is gone: the UI must not sample it.
        if (_registered is not null)
            Tree?.Servers.Get<UiServer>()?.UnregisterTexture(ViewportPanel.TextureName);
        _registered = null;
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

    // Selection boxes on the simulated counterparts (and the 2D grid); no icons, gizmo or handles.
    private void DrawSimulationVisuals(EditedScene scene, SubViewport viewport, Vector2 pixels)
    {
        var overlay = viewport.OverlayLines;
        var camera = scene.Camera;
        if (camera.Is2D)
        {
            if (GridVisible)
                DrawGrid2D(viewport.DebugLines, camera, pixels);
            var unit = 1f / camera.Zoom2D;
            var marker = MarkerPoints * ViewScale * unit;
            var selected2D = scene.Selection.Nodes;
            for (var i = 0; i < selected2D.Count; i++)
            {
                if (_workspace.Simulation.CounterpartOf(selected2D[i]) is not Node2D { IsInsideTree: true } simulated)
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
        var selected = scene.Selection.Nodes;
        for (var i = 0; i < selected.Count; i++)
            if (_workspace.Simulation.CounterpartOf(selected[i]) is Node3D { IsInsideTree: true } simulated)
                DrawSelection(overlay, camera, displayHeight, simulated);
    }
}

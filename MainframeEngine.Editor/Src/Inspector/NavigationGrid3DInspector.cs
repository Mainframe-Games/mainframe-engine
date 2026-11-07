using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="NavigationGrid3D"/> inspector (G3.11), like the 2D grid's: icon buttons Bake (solid cells from 3D static
/// bodies) and Clear (every cell walkable), each one undoable change of <see cref="NavigationGrid3D.Data"/>, and a status
/// line — size, solid and weighted cells, the last bake's time, "out of date" when a static body in the region changed.
/// </summary>
[CustomInspector(typeof(NavigationGrid3D))]
public sealed class NavigationGrid3DInspector : ICustomInspector, IWorkspaceInspector
{
    public const string BakeAction = "navigation-grid3d-bake";
    public const string ClearAction = "navigation-grid3d-clear";

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target) =>
        new StringBuilder(512)
            .Append("<div class=\"probe-bake\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(BakeAction)
            .Append("\" data-tooltip=\"Bake — solid cells from 3D static bodies between the climb and agent heights\">")
            .Append("<span class=\"icon icon-sm icon-wand\"></span></button>")
            .Append("<button class=\"tool-button\" data-action=\"").Append(ClearAction)
            .Append("\" data-tooltip=\"Clear — make every cell walkable\"><span class=\"icon icon-sm icon-trash\"></span></button>")
            .Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status((NavigationGrid3D)target))).Append("</span></div>")
            .ToString();

    /// <summary>"20×12 · 9 solid · baked in 1.2 ms · out of date".</summary>
    internal static string Status(NavigationGrid3D grid)
    {
        var region = grid.Region;
        var text = new StringBuilder(96);
        text.Append(CultureInfo.InvariantCulture, $"{region.Size.X}×{region.Size.Y}");
        if (grid.Data is { } data)
        {
            text.Append(CultureInfo.InvariantCulture, $" · {data.SolidCount:N0} solid");
            if (data.WeightedCount > 0)
                text.Append(CultureInfo.InvariantCulture, $" · {data.WeightedCount:N0} weighted");
            if (grid.LastBakeMilliseconds > 0)
                text.Append(CultureInfo.InvariantCulture, $" · baked in {grid.LastBakeMilliseconds:0.0} ms");
            if (grid.IsBakeOutOfDate() == true)
                text.Append(" · out of date");
        }
        else
        {
            text.Append(" · not baked: every cell walkable");
        }

        return text.ToString();
    }

    public bool ShowProperty(object target, ExportPropertyInfo property) => true;

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grid = (NavigationGrid3D)target;
        switch (action)
        {
            case BakeAction:
                var data = grid.BakeData(out var error);
                if (error is not null)
                {
                    Log.Warning($"[Editor] '{grid.Name}' was not baked: {error}.");
                    break;
                }

                SetData(grid, data, context, "Bake Navigation");
                break;
            case ClearAction:
                if (grid.Data is not null)
                    SetData(grid, null, context, "Clear Navigation");
                break;
        }

        Workspace?.Inspector.Rebuild();
    }

    public void OnPropertyChanged(object target, ExportPropertyInfo? property, IInspectorContext context)
    {
        if (property is null)
            Workspace?.Inspector.Rebuild();
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private static void SetData(NavigationGrid3D grid, NavigationGridData? data, IInspectorContext context, string name)
    {
        var property = TypeRegistry.GetRequired(typeof(NavigationGrid3D)).FindProperty(nameof(NavigationGrid3D.Data))
                       ?? throw new InvalidOperationException("NavigationGrid3D.Data is not exported.");
        context.History.Commit(new CompositeAction(name, new SetPropertyAction(grid, property, grid.Data, data)));
    }
}

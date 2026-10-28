using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="NavigationGrid2D"/> inspector (G3a): above the generated rows, icon buttons Bake (solid cells from the
/// grid's <see cref="NavigationGrid2D.Source"/>) and Clear (every cell walkable), each one undoable change of
/// <see cref="NavigationGrid2D.Data"/>, and a status line — size, solid and weighted cells, the source, the last bake's
/// time and "out of date" when a static body in the region changed since the bake. The bake rows of the other sources
/// are hidden (the physics mask and radius, the tile layer's fields).
/// </summary>
[CustomInspector(typeof(NavigationGrid2D))]
public sealed class NavigationGridInspector : ICustomInspector, IWorkspaceInspector
{
    public const string BakeAction = "navigation-bake";
    public const string ClearAction = "navigation-clear";

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target)
    {
        var grid = (NavigationGrid2D)target;
        var bakeTip = grid.Source switch
        {
            NavigationGridSource.Physics => "Bake — solid cells from static bodies (Bake Mask)",
            NavigationGridSource.TileMapLayer => "Bake — solid cells from the TileMapLayer",
            _ => "Bake — a Manual grid is painted from code: baking clears it",
        };
        return new StringBuilder(512)
            .Append("<div class=\"probe-bake\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(BakeAction).Append("\" data-tooltip=\"").Append(RmlText.Escape(bakeTip))
            .Append("\"><span class=\"icon icon-sm icon-wand\"></span></button>")
            .Append("<button class=\"tool-button\" data-action=\"").Append(ClearAction)
            .Append("\" data-tooltip=\"Clear — make every cell walkable\"><span class=\"icon icon-sm icon-trash\"></span></button>")
            .Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status(grid))).Append("</span></div>")
            .ToString();
    }

    /// <summary>"18×11 · 34 solid · 6 weighted · Physics · baked in 2.1 ms · out of date".</summary>
    internal static string Status(NavigationGrid2D grid)
    {
        var region = grid.Region;
        var text = new StringBuilder(96);
        text.Append(CultureInfo.InvariantCulture, $"{region.Size.X}×{region.Size.Y}");
        if (grid.Data is { } data)
        {
            text.Append(CultureInfo.InvariantCulture, $" · {data.SolidCount:N0} solid");
            if (data.WeightedCount > 0)
                text.Append(CultureInfo.InvariantCulture, $" · {data.WeightedCount:N0} weighted");
            text.Append(" · ").Append(SourceName(data.BakedFrom));
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

    private static string SourceName(NavigationGridSource source) => source switch
    {
        NavigationGridSource.Physics => "Physics",
        NavigationGridSource.TileMapLayer => "TileMapLayer",
        _ => "Manual",
    };

    public bool ShowProperty(object target, ExportPropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var source = ((NavigationGrid2D)target).Source;
        return property.Name switch
        {
            nameof(NavigationGrid2D.BakeCollisionMask) or nameof(NavigationGrid2D.AgentRadius) => source == NavigationGridSource.Physics,
            nameof(NavigationGrid2D.TileMapLayerPath) or nameof(NavigationGrid2D.TilePhysicsLayer) or nameof(NavigationGrid2D.SolidDataLayer)
                or nameof(NavigationGrid2D.WeightDataLayer) => source == NavigationGridSource.TileMapLayer,
            _ => true,
        };
    }

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grid = (NavigationGrid2D)target;
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
        // Source shows other rows; undo and redo may have changed it too.
        if (property is null || property.Name == nameof(NavigationGrid2D.Source))
            Workspace?.Inspector.Rebuild();
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private static void SetData(NavigationGrid2D grid, NavigationGridData? data, IInspectorContext context, string name)
    {
        var property = TypeRegistry.GetRequired(typeof(NavigationGrid2D)).FindProperty(nameof(NavigationGrid2D.Data))
                       ?? throw new InvalidOperationException("NavigationGrid2D.Data is not exported.");
        context.History.Commit(new CompositeAction(name, new SetPropertyAction(grid, property, grid.Data, data)));
    }
}

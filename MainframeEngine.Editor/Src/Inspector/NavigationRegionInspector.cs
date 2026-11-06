using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="NavigationRegion2D"/> inspector (G3.9): above the generated rows, icon buttons Bake (the polygon from the
/// scene: its outlines or baking rectangle minus the parsed obstructions) and Clear (no polygons), each one undoable
/// change of the <see cref="NavigationPolygon"/>'s vertices and polygons, and a status line — polygons, vertices,
/// outlines, the last bake's time.
/// </summary>
[CustomInspector(typeof(NavigationRegion2D))]
public sealed class NavigationRegionInspector : ICustomInspector, IWorkspaceInspector
{
    public const string BakeAction = "navigation-region-bake";
    public const string ClearAction = "navigation-region-clear";

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target)
    {
        var region = (NavigationRegion2D)target;
        return new StringBuilder(512)
            .Append("<div class=\"probe-bake\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(BakeAction)
            .Append("\" data-tooltip=\"Bake — polygons from the outlines or Baking Rect, minus static bodies, tiles and carving obstacles\">")
            .Append("<span class=\"icon icon-sm icon-wand\"></span></button>")
            .Append("<button class=\"tool-button\" data-action=\"").Append(ClearAction)
            .Append("\" data-tooltip=\"Clear — remove the polygons (the outlines stay)\"><span class=\"icon icon-sm icon-trash\"></span></button>")
            .Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status(region))).Append("</span></div>")
            .ToString();
    }

    /// <summary>"12 polygons · 40 vertices · 1 outline · baked in 1.4 ms".</summary>
    internal static string Status(NavigationRegion2D region)
    {
        if (region.NavigationPolygon is not { } polygon)
            return "no navigation polygon: assign one, then Bake";
        var text = new StringBuilder(96);
        if (polygon.PolygonCount == 0)
            text.Append("not baked");
        else
            text.Append(CultureInfo.InvariantCulture, $"{polygon.PolygonCount:N0} polygons · {polygon.Vertices.Length:N0} vertices");
        text.Append(polygon.OutlineCount switch
        {
            0 when polygon.BakingRect.Size is { X: > 0f, Y: > 0f } => " · baking rect",
            0 => " · no outlines or baking rect",
            1 => " · 1 outline",
            var n => string.Create(CultureInfo.InvariantCulture, $" · {n} outlines"),
        });
        if (region.LastBakeMilliseconds > 0)
            text.Append(CultureInfo.InvariantCulture, $" · baked in {region.LastBakeMilliseconds:0.0} ms");
        return text.ToString();
    }

    public bool ShowProperty(object target, ExportPropertyInfo property) => true;

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var region = (NavigationRegion2D)target;
        if (region.NavigationPolygon is not { } polygon)
        {
            Log.Warning($"[Editor] '{region.Name}' has no navigation polygon to bake: assign one first.");
            return;
        }

        switch (action)
        {
            case BakeAction:
                var mesh = region.BakeData(out var error);
                if (mesh is null)
                {
                    Log.Warning($"[Editor] '{region.Name}' was not baked: {error}.");
                    break;
                }

                SetMesh(polygon, mesh.Value.Vertices, mesh.Value.Polygons, context, "Bake Navigation Polygon");
                break;
            case ClearAction:
                if (polygon.PolygonCount > 0 || polygon.Vertices.Length > 0)
                    SetMesh(polygon, [], [], context, "Clear Navigation Polygon");
                break;
        }

        Workspace?.Inspector.Rebuild();
    }

    public void OnPropertyChanged(object target, ExportPropertyInfo? property, IInspectorContext context)
    {
        if (property is null || property.Name == nameof(NavigationRegion2D.NavigationPolygon))
            Workspace?.Inspector.Rebuild();
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private static void SetMesh(NavigationPolygon polygon, Vector2[] vertices, int[][] polygons, IInspectorContext context, string name)
    {
        var info = TypeRegistry.GetRequired(typeof(NavigationPolygon));
        var verticesProperty = info.FindProperty(nameof(NavigationPolygon.Vertices))
                               ?? throw new InvalidOperationException("NavigationPolygon.Vertices is not exported.");
        var polygonsProperty = info.FindProperty(nameof(NavigationPolygon.Polygons))
                               ?? throw new InvalidOperationException("NavigationPolygon.Polygons is not exported.");
        context.History.Commit(new CompositeAction(name,
            new SetPropertyAction(polygon, verticesProperty, polygon.Vertices, vertices),
            new SetPropertyAction(polygon, polygonsProperty, polygon.Polygons, polygons)));
    }
}

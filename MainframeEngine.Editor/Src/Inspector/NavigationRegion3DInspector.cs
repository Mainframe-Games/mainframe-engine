using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="NavigationRegion3D"/> inspector (G3.12): icon buttons Bake (Recast over the region's meshes and static
/// collision) and Clear, each one undoable change of the <see cref="NavigationMesh"/>'s baked vertices, polygons and Detour
/// data, and a status line — polygons, vertices, the last bake's time.
/// </summary>
[CustomInspector(typeof(NavigationRegion3D))]
public sealed class NavigationRegion3DInspector : ICustomInspector, IWorkspaceInspector
{
    public const string BakeAction = "navigation-region3d-bake";
    public const string ClearAction = "navigation-region3d-clear";

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target) =>
        new StringBuilder(512)
            .Append("<div class=\"probe-bake\">")
            .Append("<button class=\"tool-button\" data-action=\"").Append(BakeAction)
            .Append("\" data-tooltip=\"Bake — a navigation mesh from the region's meshes and static collision (Recast)\">")
            .Append("<span class=\"icon icon-sm icon-wand\"></span></button>")
            .Append("<button class=\"tool-button\" data-action=\"").Append(ClearAction)
            .Append("\" data-tooltip=\"Clear — remove the baked mesh (the settings stay)\"><span class=\"icon icon-sm icon-trash\"></span></button>")
            .Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status((NavigationRegion3D)target))).Append("</span></div>")
            .ToString();

    /// <summary>"42 polygons · 96 vertices · baked in 12.0 ms".</summary>
    internal static string Status(NavigationRegion3D region)
    {
        if (region.NavigationMesh is not { } mesh)
            return "no navigation mesh: assign one, then Bake";
        var text = new StringBuilder(96);
        text.Append(mesh.IsBaked
            ? string.Create(CultureInfo.InvariantCulture, $"{mesh.PolygonCount:N0} polygons · {mesh.Vertices.Length:N0} vertices")
            : "not baked");
        if (region.LastBakeMilliseconds > 0)
            text.Append(CultureInfo.InvariantCulture, $" · baked in {region.LastBakeMilliseconds:0.0} ms");
        return text.ToString();
    }

    public bool ShowProperty(object target, ExportPropertyInfo property) => true;

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var region = (NavigationRegion3D)target;
        if (region.NavigationMesh is not { } mesh)
        {
            Log.Warning($"[Editor] '{region.Name}' has no navigation mesh to bake: assign one first.");
            return;
        }

        switch (action)
        {
            case BakeAction:
                var baked = region.BakeData(out var error);
                if (baked is not { } data)
                {
                    Log.Warning($"[Editor] '{region.Name}' was not baked: {error}.");
                    break;
                }

                Set(mesh, data.Vertices, data.Polygons, data.DetourData, context, "Bake Navigation Mesh");
                break;
            case ClearAction:
                if (mesh.IsBaked || mesh.PolygonCount > 0)
                    Set(mesh, [], [], "", context, "Clear Navigation Mesh");
                break;
        }

        Workspace?.Inspector.Rebuild();
    }

    public void OnPropertyChanged(object target, ExportPropertyInfo? property, IInspectorContext context)
    {
        if (property is null || property.Name == nameof(NavigationRegion3D.NavigationMesh))
            Workspace?.Inspector.Rebuild();
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private static void Set(NavigationMesh mesh, Vector3[] vertices, int[][] polygons, string detour, IInspectorContext context, string name)
    {
        var info = TypeRegistry.GetRequired(typeof(NavigationMesh));
        ExportPropertyInfo Property(string n) => info.FindProperty(n) ?? throw new InvalidOperationException($"NavigationMesh.{n} is not exported.");
        context.History.Commit(new CompositeAction(name,
            new SetPropertyAction(mesh, Property(nameof(NavigationMesh.Vertices)), mesh.Vertices, vertices),
            new SetPropertyAction(mesh, Property(nameof(NavigationMesh.Polygons)), mesh.Polygons, polygons),
            new SetPropertyAction(mesh, Property(nameof(NavigationMesh.DetourData)), mesh.DetourData, detour)));
    }
}

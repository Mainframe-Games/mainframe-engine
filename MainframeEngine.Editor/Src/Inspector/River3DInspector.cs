using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="River3D"/> inspector (G8c.4, docs/design/water.md#editor): above the generated rows, Carve, Re-carve,
/// Uncarve and Fit to ground as icon buttons with a status line. Carving goes through the river's
/// <see cref="River3D.LastCarveChange"/> (heights, the terrain's carve record and <see cref="River3D.CarveId"/> in one
/// undo entry); Fit to ground moves every point 0.3 m under the terrain as one curve change. The points, banks and depth
/// are edited in the view by the curve tool.
/// </summary>
[CustomInspector(typeof(River3D))]
public sealed class River3DInspector : ICustomInspector, IWorkspaceInspector
{
    /// <summary>How far under the ground Fit to ground puts the water surface (the carve then makes the channel).</summary>
    public const float FitDepth = 0.3f;

    private static ExportPropertyInfo? _points;

    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target)
    {
        var river = (River3D)target;
        var carved = !string.IsNullOrEmpty(river.CarveId);
        var rml = new StringBuilder(1024);
        rml.Append("<div class=\"joint-actions\">");
        Button(rml, "river-carve", "arrow-bar-to-down", carved
            ? "Carve — put the ground back and cut the channel for the current curve (as Re-carve)"
            : "Carve — cut the river's channel and banks into the terrain under it (one undo step)");
        Button(rml, "river-recarve", "refresh", "Re-carve — put the ground back as it was before the last carve, then carve the current curve",
            disabled: !carved);
        Button(rml, "river-uncarve", "arrow-back-up", "Uncarve — put the ground back as it was before the river was carved", disabled: !carved);
        Button(rml, "river-fit", "magnet",
            "Fit to ground — move every point 0.3 m under the terrain below it (the surface sits in the ground, so a carve makes a channel)");
        rml.Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status(river))).Append("</span></div>");
        return rml.ToString();
    }

    private static void Button(StringBuilder rml, string action, string icon, string tooltip, bool disabled = false) =>
        rml.Append("<button class=\"tool-button\" data-action=\"").Append(action).Append('"').Append(disabled ? " disabled" : "")
            .Append(" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-sm icon-").Append(icon)
            .Append("\"></span></button>");

    /// <summary>One line on the river: points, length and whether it is carved.</summary>
    internal static string Status(River3D river)
    {
        var points = river.Curve?.PointCount ?? 0;
        if (points < 2)
            return "Draw the river with the curve tool: Cmd/Ctrl+click adds points on the ground.";
        var length = river.Length.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(river.CarveId) ? $"{points} points, {length} m · not carved" : $"{points} points, {length} m · carved";
    }

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var river = (River3D)target;
        switch (action)
        {
            case "river-carve":
                Commit(context, river, river.Carve(), "Carve River");
                break;
            case "river-recarve":
                Commit(context, river, river.Recarve(), "Re-carve River");
                break;
            case "river-uncarve":
                Commit(context, river, river.Uncarve(), "Uncarve River");
                break;
            case "river-fit":
                FitToGround(context, river);
                break;
            default:
                return;
        }

        Workspace?.Inspector.Rebuild();
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private static void Commit(IInspectorContext context, River3D river, bool done, string name)
    {
        if (!done)
        {
            Log.Warning($"[Editor] {name}: no terrain under '{river.Name}' (set its Terrain Path, or put its first point over a Terrain3D).");
            return;
        }

        if (river.LastCarveChange is { } change)
            context.History.Commit(new RiverCarveAction(name, change), alreadyApplied: true);
    }

    private static void FitToGround(IInspectorContext context, River3D river)
    {
        if (river.Curve is not { } curve)
            return;
        var property = _points ??= TypeRegistry.GetNearest(typeof(Curve3D))!.FindProperty("Points")!;
        var before = (float[])((float[])property.GetValue(curve)!).Clone();
        if (!river.FitToTerrain(FitDepth))
        {
            Log.Warning($"[Editor] Fit to ground: no terrain under '{river.Name}'.");
            return;
        }

        var after = (float[])property.GetValue(curve)!;
        if (!before.AsSpan().SequenceEqual(after))
            context.History.Commit(new SetCurvePointsAction("Fit River to Ground", curve, property, before, (float[])after.Clone()), alreadyApplied: true);
    }
}

/// <summary>A carve, re-carve or uncarve as a history entry (<see cref="RiverCarveChange"/>).</summary>
public sealed class RiverCarveAction(string name, RiverCarveChange change) : IEditorAction
{
    public string Name { get; } = name;

    public RiverCarveChange Change { get; } = change ?? throw new ArgumentNullException(nameof(change));

    public void Do() => Change.Redo();

    public void Undo() => Change.Undo();
}

using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The inspector's property-name column: sized to the longest name on screen (between <see cref="MinLabelWidth"/> and
/// half the panel) unless the user dragged its splitter (persisted in the layout file); names that still do not fit end
/// with "…" (the row's tooltip has the full name).
/// </summary>
public sealed partial class InspectorPanel
{
    /// <summary>Narrowest name column, in dp.</summary>
    public const float MinLabelWidth = 70f;

    // Lato at 12dp averages ~6.2dp per character; the icon and its margin take 20dp.
    private const float CharWidth = 6.2f;
    private const float IconWidth = 20f;

    private RmlEventListener? _labelDrag;
    private RmlEventListener? _labelDragEnd;
    private RmlEventListener? _labelReset;
    private float _appliedLabelWidth = -1;

    /// <summary>The name column's width in dp (the persisted width, else the auto size).</summary>
    public float LabelWidth
    {
        get
        {
            var max = MathF.Max(MinLabelWidth, PanelWidth * 0.55f);
            var stored = Workspace.Layout.Settings.InspectorLabelWidth;
            if (stored > 0)
                return Math.Clamp(stored, MinLabelWidth, max);
            var longest = 0;
            foreach (var row in _rows)
                longest = Math.Max(longest, row.Property.Label.Length + row.Depth * 2);
            return Math.Clamp(IconWidth + longest * CharWidth + 8f, MinLabelWidth, MathF.Min(max, PanelWidth * 0.5f));
        }
    }

    private float PanelWidth => Rect?.Width ?? 330f;

    protected override void OnRectApplied(LayoutRect rect)
    {
        _appliedLabelWidth = -1;
        ApplyLabelWidth();
    }

    private void AttachLabelSplitter(RmlDocument document)
    {
        _labelDrag?.Remove();
        _labelDragEnd?.Remove();
        var handle = document.GetElementById("label-splitter");
        if (handle.IsNull)
            return;
        _labelDrag = handle.AddEventListener("drag", e =>
        {
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            var x = (float)e.GetParameter("mouse_x", 0.0) / scale - (Rect?.X ?? 0) - 12f;
            SetLabelWidth(x, persist: false);
        });
        _labelDragEnd = handle.AddEventListener("dragend", _ => Workspace.SaveLayout());
        _labelReset?.Remove();
        _labelReset = handle.AddEventListener("dblclick", _ => SetLabelWidth(0));
    }

    /// <summary>Sets the name column's width (dp; 0 returns to the auto size) and applies it to the rows.</summary>
    public void SetLabelWidth(float width, bool persist = true)
    {
        Workspace.Layout.SetInspectorLabelWidth(width <= 0 ? 0 : Math.Clamp(width, MinLabelWidth, MathF.Max(MinLabelWidth, PanelWidth * 0.55f)));
        _appliedLabelWidth = -1;
        ApplyLabelWidth();
        if (persist)
            Workspace.SaveLayout();
    }

    /// <summary>Applies <see cref="LabelWidth"/> to every name cell and positions the splitter; shortens names that do not fit.</summary>
    private void ApplyLabelWidth()
    {
        if (!IsLoaded)
            return;
        var width = LabelWidth;
        if (MathF.Abs(width - _appliedLabelWidth) < 0.5f)
            return;
        _appliedLabelWidth = width;
        var text = RmlText.Dp(width);
        var body = Document.GetElementById("inspector-body");
        if (!body.IsNull)
        {
            foreach (var label in body.QuerySelectorAll(".prop-label"))
                label.SetProperty("width", text);
            var names = body.QuerySelectorAll(".prop-name");
            for (var i = 0; i < names.Length && i < _rows.Count; i++)
                names[i].SetInnerRml(RmlText.Escape(Fit(_rows[i].Property.Label, width - IconWidth - _rows[i].Depth * 12f)));
        }

        var handle = Document.GetElementById("label-splitter");
        if (!handle.IsNull)
            handle.SetProperty("left", RmlText.Dp(12f + width - 3f));
    }

    /// <summary><paramref name="label"/>, shortened with "…" to fit <paramref name="width"/> dp.</summary>
    public static string Fit(string label, float width)
    {
        ArgumentNullException.ThrowIfNull(label);
        var fits = (int)MathF.Floor(width / CharWidth);
        if (label.Length <= fits)
            return label;
        if (fits <= 1)
            return "…";
        return new StringBuilder(label, 0, fits - 1, fits).Append('…').ToString();
    }
}

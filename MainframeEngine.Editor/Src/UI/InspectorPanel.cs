using System.Collections;
using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The inspector dock: the primary selection's <see cref="InspectorModel"/> rendered as generated RML — one section per
/// declaring type or <c>[ExportGroup]</c>, one row per <c>[Export]</c> property with a widget chosen by
/// <see cref="PropertyEditorKind"/> — plus the node name field and a custom inspector's header. Edits go through the
/// scene's undo history: text fields commit on Enter or focus loss, sliders merge their drag into one entry, checkboxes,
/// dropdowns and buttons commit at once. When the scene changes (undo, gizmo drag) only the values that changed are
/// written back into the existing elements; the RML is regenerated only when the selection or structure changes.
/// </summary>
public sealed class InspectorPanel : EditorDocument
{
    private const int MaxResourceDepth = 3;

    private sealed class RowView
    {
        public required InspectorProperty Property { get; init; }
        public required int Depth { get; init; }

        /// <summary>The value the row shows (boxed); compared on refresh.</summary>
        public object? Shown { get; set; }
    }

    private readonly List<RowView> _rows = [];
    private readonly HashSet<string> _collapsedSections = new(StringComparer.Ordinal);
    private readonly HashSet<(object Target, string Property)> _expanded = [];
    private RmlEventListener? _change;
    private RmlEventListener? _blur;
    private RmlEventListener? _mouseUp;
    private InspectorModel? _model;
    private object? _target;
    private bool _rebuildPending;
    private bool _rebuilding;

    public InspectorPanel(EditorWorkspace workspace)
        : base(workspace, "inspector.rml")
    {
    }

    /// <summary>The object being inspected (the primary selection), or null.</summary>
    public object? Target => _target;

    /// <summary>The property model on screen.</summary>
    public InspectorModel? Model => _model;

    /// <summary>Rows on screen in order (nested resource rows included).</summary>
    public IReadOnlyList<InspectorProperty> Rows => _rows.Select(r => r.Property).ToArray();

    protected override void OnAttach(RmlDocument document)
    {
        var root = document.AsElement();
        _change?.Remove();
        _blur?.Remove();
        _mouseUp?.Remove();
        _change = root.AddEventListener("change", OnChange);
        _blur = root.AddEventListener("blur", OnBlur, inCapturePhase: true);
        _mouseUp = root.AddEventListener("mouseup", _ => Workspace.Session.Active?.History.EndMerge());
        Rebuild();
    }

    // ── Building ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Regenerates the inspector for the active scene's primary selection.</summary>
    public void Rebuild()
    {
        // A field being edited commits to its own row first (the new rows must not receive its blur).
        if (IsLoaded && Layer?.Context is { IsDisposed: false } context)
        {
            var focus = context.FocusElement;
            if (!focus.IsNull && focus.OwnerDocument == Document && focus.HasAttribute("data-row"))
                focus.Blur();
        }

        _rebuilding = true;
        try
        {
            RebuildNow();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RebuildNow()
    {
        _rebuildPending = false;
        var scene = Workspace.Session.Active;
        var node = scene?.Selection.Primary;
        _target = node;
        _rows.Clear();
        _model = node is null ? null : InspectorModel.Build(node);
        if (!IsLoaded)
            return;
        var body = Document.GetElementById("inspector-body");
        if (body.IsNull)
            return;
        if (scene is null || node is null)
        {
            body.SetInnerRml("<div class=\"empty\">Select a node to edit its properties.</div>");
            return;
        }

        var rml = new StringBuilder(4096);
        AppendHeader(rml, scene, node);
        if (_model!.CustomInspector?.GetHeaderRml(node) is { } custom)
            rml.Append(custom);
        AppendSections(rml, _model, 0);
        body.SetInnerRml(rml.ToString());
    }

    private static void AppendHeader(StringBuilder rml, EditedScene scene, Node node)
    {
        var type = TypeRegistry.GetNearest(node.GetType())?.Name ?? node.GetType().Name;
        if (node is MissingNode missing)
            type = $"{missing.OriginalType} (missing)";
        rml.Append("<div class=\"insp-head\"><div class=\"insp-type\">").Append(RmlText.Escape(type));
        if (!ReferenceEquals(node, scene.Root) && node.SceneFilePath is { } instance)
            rml.Append(" · instance of ").Append(RmlText.Escape(instance));
        if (scene.Selection.Count > 1)
            rml.Append(" · ").Append(scene.Selection.Count).Append(" selected (showing the last)");
        rml.Append("</div><div class=\"insp-name-row\"><input type=\"text\" class=\"text\" id=\"node-name\" value=\"")
            .Append(RmlText.Escape(node.Name)).Append("\"/></div></div>");
    }

    private void AppendSections(StringBuilder rml, InspectorModel model, int depth)
    {
        foreach (var section in model.Sections)
        {
            var key = $"{model.TypeInfo?.Name}/{section.Title}";
            var collapsed = depth == 0 && _collapsedSections.Contains(key);
            if (depth == 0)
            {
                rml.Append("<div class=\"section-title\" data-section=\"").Append(RmlText.Escape(key)).Append("\"><span class=\"arrow")
                    .Append(collapsed ? " collapsed" : "").Append("\"></span><span>").Append(RmlText.Escape(section.Title)).Append("</span></div>");
            }

            if (collapsed)
                continue;
            foreach (var property in section.Properties)
                AppendRow(rml, property, depth);
        }
    }

    private void AppendRow(StringBuilder rml, InspectorProperty property, int depth)
    {
        var index = _rows.Count;
        var value = property.GetValue();
        _rows.Add(new RowView { Property = property, Depth = depth, Shown = Snapshot(value) });
        var nested = depth switch { 0 => "", 1 => " nested", _ => " nested2" };
        rml.Append("<div class=\"prop").Append(nested).Append("\"><div class=\"prop-label\" title=\"")
            .Append(RmlText.Escape(property.Name)).Append("\">").Append(RmlText.Escape(property.Label))
            .Append("</div><div class=\"prop-editor\">");
        AppendEditor(rml, property, index, value);
        rml.Append("</div></div>");

        if (property.Kind == PropertyEditorKind.Color && _expanded.Contains((property.Target, property.Name)))
            AppendColorPicker(rml, property, index, value);
        if (property.Kind == PropertyEditorKind.Resource && value is Resource resource && !resource.IsExternal &&
            depth + 1 < MaxResourceDepth && _expanded.Contains((property.Target, property.Name)))
            AppendSections(rml, InspectorModel.Build(resource), depth + 1);
    }

    private void AppendEditor(StringBuilder rml, InspectorProperty p, int row, object? value)
    {
        switch (p.Kind)
        {
            case PropertyEditorKind.Bool:
                rml.Append("<input type=\"checkbox\" class=\"checkbox\" id=\"").Append(FieldId(row, 0)).Append("\" data-row=\"").Append(row).Append('"')
                    .Append(value is true ? " checked=\"\"" : "").Append("/>");
                break;

            case PropertyEditorKind.Range:
                rml.Append("<input type=\"range\" class=\"range\" id=\"").Append(SliderId(row, 0)).Append("\" data-row=\"").Append(row)
                    .Append("\" data-comp=\"0\" data-slider=\"1\" min=\"").Append(Num(p.Min)).Append("\" max=\"").Append(Num(p.Max))
                    .Append("\" step=\"").Append(Num(p.Step)).Append("\" value=\"").Append(RmlText.Escape(p.FormatComponent(value, 0))).Append("\"/>");
                AppendText(rml, row, 0, p.FormatComponent(value, 0));
                break;

            case PropertyEditorKind.IntegerNumber or PropertyEditorKind.FloatNumber or PropertyEditorKind.Text or PropertyEditorKind.NodePath:
                AppendText(rml, row, 0, p.FormatComponent(value, 0));
                if (p.Kind == PropertyEditorKind.NodePath)
                    AppendButton(rml, row, "pick-node", "Pick");
                break;

            case PropertyEditorKind.FilePath or PropertyEditorKind.DirectoryPath:
                AppendText(rml, row, 0, p.FormatComponent(value, 0));
                AppendButton(rml, row, "browse", "…");
                break;

            case PropertyEditorKind.MultilineText:
                rml.Append("<textarea id=\"").Append(FieldId(row, 0)).Append("\" data-row=\"").Append(row).Append("\" data-comp=\"0\">")
                    .Append(RmlText.Escape(value as string)).Append("</textarea>");
                break;

            case PropertyEditorKind.Enum:
                rml.Append("<select id=\"").Append(FieldId(row, 0)).Append("\" data-row=\"").Append(row).Append("\" data-enum=\"1\">");
                var current = value is null ? "" : value.ToString();
                foreach (var name in p.EnumNames)
                    rml.Append("<option value=\"").Append(name).Append('"').Append(name == current ? " selected=\"\"" : "")
                        .Append('>').Append(RmlText.Escape(InspectorProperty.Humanize(name))).Append("</option>");
                rml.Append("</select>");
                break;

            case PropertyEditorKind.Flags:
                rml.Append("<div class=\"flags\">");
                var flag = 0;
                foreach (var (name, bits) in p.FlagChoices())
                {
                    rml.Append("<span class=\"flag\"><input type=\"checkbox\" class=\"checkbox\" id=\"").Append(FieldId(row, flag++))
                        .Append("\" data-row=\"").Append(row).Append("\" data-flag=\"").Append(bits.ToString(CultureInfo.InvariantCulture)).Append('"')
                        .Append(InspectorProperty.HasFlag(value, bits) ? " checked=\"\"" : "").Append("/>").Append(RmlText.Escape(name)).Append("</span>");
                }

                rml.Append("</div>");
                break;

            case PropertyEditorKind.Vector2 or PropertyEditorKind.Vector3 or PropertyEditorKind.Vector4 or PropertyEditorKind.Quaternion:
                for (var c = 0; c < p.Components; c++)
                {
                    rml.Append("<span class=\"axis ").Append("xyzw"[c]).Append("\">").Append("XYZW"[c]).Append("</span>");
                    AppendText(rml, row, c, p.FormatComponent(value, c));
                }

                break;

            case PropertyEditorKind.Color:
                rml.Append("<div class=\"swatch\" id=\"").Append(SwatchId(row)).Append("\" data-row=\"").Append(row)
                    .Append("\" data-action=\"color\" style=\"background-color: ").Append(ValueText.ColorHex(value)).Append(";\"></div>");
                AppendText(rml, row, 9, ValueText.ColorHex(value));
                break;

            case PropertyEditorKind.Resource:
                rml.Append("<div class=\"res-label\" id=\"").Append(FieldId(row, 0)).Append("\">").Append(RmlText.Escape(p.Format(value))).Append("</div>");
                if (value is Resource { IsExternal: false } && RowDepth(row) + 1 < MaxResourceDepth)
                    AppendButton(rml, row, "res-edit", _expanded.Contains((p.Target, p.Name)) ? "Hide" : "Edit");
                AppendButton(rml, row, "res-load", "Load");
                AppendButton(rml, row, "res-new", "New");
                if (value is not null)
                    AppendButton(rml, row, "res-clear", "×");
                break;

            case PropertyEditorKind.Array:
                AppendArray(rml, p, row, value as IList);
                break;

            default:
                rml.Append("<span class=\"readonly\" id=\"").Append(FieldId(row, 0)).Append("\">").Append(RmlText.Escape(p.Format(value))).Append("</span>");
                break;
        }
    }

    private static void AppendArray(StringBuilder rml, InspectorProperty p, int row, IList? list)
    {
        rml.Append("<div class=\"array-box\"><div class=\"array-item\"><span class=\"readonly\">").Append(RmlText.Escape(p.Format(list)))
            .Append("</span>");
        AppendButton(rml, row, "arr-add", "+ Add");
        rml.Append("</div>");
        if (list is not null)
        {
            for (var i = 0; i < list.Count; i++)
            {
                rml.Append("<div class=\"array-item\"><span class=\"array-index\">").Append(i).Append("</span>");
                var text = ValueText.Format(list[i]);
                var editable = ArrayElementEditable(p.ElementType);
                if (editable)
                    rml.Append("<input type=\"text\" class=\"text\" data-row=\"").Append(row).Append("\" data-elem=\"").Append(i)
                        .Append("\" value=\"").Append(RmlText.Escape(text)).Append("\"/>");
                else
                    rml.Append("<span class=\"readonly\">").Append(RmlText.Escape(text)).Append("</span>");
                rml.Append("<button class=\"small\" data-row=\"").Append(row).Append("\" data-elem=\"").Append(i)
                    .Append("\" data-action=\"arr-remove\">×</button></div>");
            }
        }

        rml.Append("</div>");
    }

    private static void AppendColorPicker(StringBuilder rml, InspectorProperty p, int row, object? value)
    {
        var rgba = ValueText.ToRgba(value);
        rml.Append("<div class=\"color-picker\">");
        for (var c = 0; c < p.Components; c++)
        {
            var channel = c switch { 0 => rgba.X, 1 => rgba.Y, 2 => rgba.Z, _ => rgba.W };
            rml.Append("<div class=\"prop\"><div class=\"prop-label\">").Append("RGBA"[c]).Append("</div><div class=\"prop-editor\">")
                .Append("<input type=\"range\" class=\"range\" id=\"").Append(SliderId(row, c)).Append("\" data-row=\"").Append(row)
                .Append("\" data-comp=\"").Append(c).Append("\" data-slider=\"1\" min=\"0\" max=\"1\" step=\"0.004\" value=\"")
                .Append(Num(Math.Clamp(channel, 0, 1))).Append("\"/></div></div>");
        }

        rml.Append("</div>");
    }

    private static void AppendText(StringBuilder rml, int row, int component, string text) =>
        rml.Append("<input type=\"text\" class=\"text\" id=\"").Append(FieldId(row, component)).Append("\" data-row=\"").Append(row)
            .Append("\" data-comp=\"").Append(component).Append("\" value=\"").Append(RmlText.Escape(text)).Append("\"/>");

    private static void AppendButton(StringBuilder rml, int row, string action, string label) =>
        rml.Append("<button class=\"small\" data-row=\"").Append(row).Append("\" data-action=\"").Append(action).Append("\">")
            .Append(RmlText.Escape(label)).Append("</button>");

    private int RowDepth(int row) => row < _rows.Count ? _rows[row].Depth : 0;

    private static string FieldId(int row, int component) => $"p{row}c{component}";
    private static string SliderId(int row, int component) => $"p{row}s{component}";
    private static string SwatchId(int row) => $"p{row}w";
    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool ArrayElementEditable(Type? type) =>
        type is not null && (type == typeof(string) || type == typeof(bool) || type == typeof(float) || type == typeof(double) ||
                             InspectorProperty.IsIntegerType(type) || type.IsEnum || type == typeof(NodePath));

    // Value-type values are compared by value on refresh; lists by content count (a changed count rebuilds).
    private static object? Snapshot(object? value)
    {
        if (value is not IList list)
            return value;
        var builder = new StringBuilder().Append(list.Count);
        foreach (var item in list)
            builder.Append('\u001f').Append(ValueText.Format(item));
        return builder.ToString();
    }

    // ── Refresh after scene changes ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The scene changed (edit, undo, redo): update values in place, or rebuild when the structure changed.</summary>
    public void OnSceneEdited()
    {
        var scene = Workspace.Session.Active;
        if (!ReferenceEquals(scene?.Selection.Primary, _target) || _rebuildPending)
        {
            Rebuild();
            return;
        }

        RefreshValues();
    }

    /// <summary>Writes changed values into the existing elements (rebuilds when a resource or array changed shape).</summary>
    public void RefreshValues()
    {
        if (_target is Node { IsFreed: true })
        {
            Rebuild();
            return;
        }

        if (!IsLoaded)
            return;
        if (_target is Node node && Document.GetElementById("node-name") is { IsNull: false } name &&
            !string.Equals(name.Value, node.Name, StringComparison.Ordinal) && !IsFocused(name))
            name.SetValue(node.Name);

        for (var row = 0; row < _rows.Count; row++)
        {
            var view = _rows[row];
            var value = view.Property.GetValue();
            var snapshot = Snapshot(value);
            if (Equals(snapshot, view.Shown))
                continue;
            if (view.Property.Kind is PropertyEditorKind.Resource or PropertyEditorKind.Array)
            {
                Rebuild(); // different resource or element count: the rows below change
                return;
            }

            view.Shown = snapshot;
            WriteValue(row, view.Property, value);
        }
    }

    private void WriteValue(int row, InspectorProperty p, object? value)
    {
        var document = Document;
        switch (p.Kind)
        {
            case PropertyEditorKind.Bool:
                SetChecked(document.GetElementById(FieldId(row, 0)), value is true);
                break;
            case PropertyEditorKind.Flags:
                var flag = 0;
                foreach (var (_, bits) in p.FlagChoices())
                    SetChecked(document.GetElementById(FieldId(row, flag++)), InspectorProperty.HasFlag(value, bits));
                break;
            case PropertyEditorKind.Enum:
                SetField(document.GetElementById(FieldId(row, 0)), value?.ToString() ?? "");
                break;
            case PropertyEditorKind.Color:
                SetField(document.GetElementById(FieldId(row, 9)), ValueText.ColorHex(value));
                document.GetElementById(SwatchId(row)).SetProperty("background-color", ValueText.ColorHex(value));
                var rgba = ValueText.ToRgba(value);
                for (var c = 0; c < p.Components; c++)
                    SetField(document.GetElementById(SliderId(row, c)), Num(Math.Clamp(c switch { 0 => rgba.X, 1 => rgba.Y, 2 => rgba.Z, _ => rgba.W }, 0, 1)));
                break;
            case PropertyEditorKind.Range:
                SetField(document.GetElementById(SliderId(row, 0)), p.FormatComponent(value, 0));
                SetField(document.GetElementById(FieldId(row, 0)), p.FormatComponent(value, 0));
                break;
            case PropertyEditorKind.Transform or PropertyEditorKind.Unsupported:
                var text = document.GetElementById(FieldId(row, 0));
                if (!text.IsNull)
                    text.SetInnerRml(RmlText.Escape(p.Format(value)));
                break;
            default:
                for (var c = 0; c < p.Components; c++)
                    SetField(document.GetElementById(FieldId(row, c)), p.FormatComponent(value, c));
                break;
        }
    }

    private static void SetField(RmlElement element, string value)
    {
        if (element.IsNull || IsFocused(element))
            return; // never overwrite what the user is typing
        if (!string.Equals(element.Value, value, StringComparison.Ordinal))
            element.SetValue(value);
    }

    private static void SetChecked(RmlElement element, bool on)
    {
        if (element.IsNull)
            return;
        if (on)
            element.SetAttribute("checked", "");
        else
            element.RemoveAttribute("checked");
    }

    private static bool IsFocused(RmlElement element) => element.IsPseudoClassSet("focus");

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnChange(RmlEvent e)
    {
        if (_rebuilding)
            return;
        var target = e.Target;
        if (target.Id == "node-name")
        {
            if (e.GetParameter("linebreak", false))
                CommitName(e.Value);
            return;
        }

        if (!TryRow(target, out var row))
            return;
        var p = _rows[row].Property;

        if (target.HasAttribute("data-slider"))
        {
            var component = IntAttribute(target, "data-comp");
            CommitSlider(row, component, e.Value);
            return;
        }

        if (target.HasAttribute("data-flag"))
        {
            CommitFlags(row);
            return;
        }

        if (target.HasAttribute("data-enum"))
        {
            Commit(row, 0, e.Value);
            return;
        }

        if (p.Kind == PropertyEditorKind.Bool)
        {
            SetValue(p, target.HasAttribute("checked"), null);
            return;
        }

        // Text fields commit on Enter; focus loss is handled by OnBlur.
        if (e.GetParameter("linebreak", false))
        {
            if (target.HasAttribute("data-elem"))
                CommitArrayElement(row, IntAttribute(target, "data-elem"), e.Value);
            else
                Commit(row, IntAttribute(target, "data-comp"), e.Value);
        }
    }

    private void OnBlur(RmlEvent e)
    {
        if (_rebuilding)
            return;
        var target = e.Target;
        if (target.TagName is not ("input" or "textarea") || target.GetAttribute("type") is "checkbox" or "range")
            return;
        var text = target.Value ?? "";
        if (target.Id == "node-name")
        {
            CommitName(text);
            return;
        }

        if (!TryRow(target, out var row))
            return;
        if (target.HasAttribute("data-elem"))
            CommitArrayElement(row, IntAttribute(target, "data-elem"), text);
        else if (_rows[row].Property.Kind != PropertyEditorKind.Range || !target.HasAttribute("data-slider"))
            Commit(row, IntAttribute(target, "data-comp"), text);
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-section") is { } section)
        {
            if (!_collapsedSections.Remove(section))
                _collapsedSections.Add(section);
            Rebuild();
            return;
        }

        var actionElement = FindWithAttribute(e.Target, "data-action");
        if (actionElement.IsNull)
            return;
        var action = actionElement.GetAttribute("data-action") ?? "";
        if (!TryRow(actionElement, out var row))
        {
            if (_model?.CustomInspector is { } custom && _target is not null && Workspace.Session.Active is { } scene)
                custom.OnAction(_target, action, scene);
            return;
        }

        RunAction(row, action, IntAttribute(actionElement, "data-elem"));
    }

    /// <summary>Runs a row button (<c>browse</c>, <c>pick-node</c>, <c>color</c>, <c>res-*</c>, <c>arr-*</c>) — also for tests.</summary>
    public void RunAction(int row, string action, int element = 0)
    {
        if ((uint)row >= (uint)_rows.Count)
            return;
        var p = _rows[row].Property;
        switch (action)
        {
            case "color":
            case "res-edit":
                if (!_expanded.Remove((p.Target, p.Name)))
                    _expanded.Add((p.Target, p.Name));
                Rebuild();
                break;
            case "browse":
                Browse(p);
                break;
            case "pick-node":
                PickNode(p);
                break;
            case "res-clear":
                SetValue(p, null, null);
                break;
            case "res-load":
                LoadResource(p);
                break;
            case "res-new":
                NewResource(p);
                break;
            case "arr-add":
                EditArray(p, list => list.Add(DefaultElement(p.ElementType)));
                break;
            case "arr-remove":
                EditArray(p, list =>
                {
                    if ((uint)element < (uint)list.Count)
                        list.RemoveAt(element);
                });
                break;
        }
    }

    private bool TryRow(RmlElement element, out int row)
    {
        row = -1;
        var attribute = FindAttribute(element, "data-row");
        return attribute is not null && int.TryParse(attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out row) &&
               (uint)row < (uint)_rows.Count;
    }

    private static int IntAttribute(RmlElement element, string name) =>
        element.GetAttribute(name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // ── Commits ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies text <paramref name="text"/> to field <paramref name="component"/> of row <paramref name="row"/> (component 9: colour hex).</summary>
    public bool Commit(int row, int component, string text, string? mergeKey = null)
    {
        if ((uint)row >= (uint)_rows.Count)
            return false;
        var p = _rows[row].Property;
        if (p.IsReadOnly)
            return false;
        var current = p.GetValue();
        var parsed = component == 9 ? p.TryParse(text, out var value) : p.TryParseComponent(component, text, out value);
        if (!parsed)
        {
            Log.Warning($"[Editor] '{text}' is not a valid {p.Label}.");
            _rows[row].Shown = new object(); // force the field back to the current value
            RefreshValues();
            return false;
        }

        if (Equals(Snapshot(current), Snapshot(value)) && mergeKey is null)
            return false;
        SetValue(p, value, mergeKey);
        return true;
    }

    private void CommitSlider(int row, int component, string text)
    {
        var p = _rows[row].Property;
        var mergeKey = $"inspector:{p.Name}:{component}";
        if (p.Kind == PropertyEditorKind.Color)
        {
            if (!ValueText.TryParseFloat(text, out var channel))
                return;
            var rgba = ValueText.ToRgba(p.GetValue());
            rgba = component switch { 0 => rgba with { X = channel }, 1 => rgba with { Y = channel }, 2 => rgba with { Z = channel }, _ => rgba with { W = channel } };
            var hex = $"#{(byte)MathF.Round(rgba.X * 255):x2}{(byte)MathF.Round(rgba.Y * 255):x2}{(byte)MathF.Round(rgba.Z * 255):x2}{(byte)MathF.Round(rgba.W * 255):x2}";
            if (p.IsFloatColor)
            {
                // Keep HDR (> 1) components of the others: set only the dragged channel.
                if (p.TryParseComponent(component, ValueText.Number(channel), out var vector))
                    SetValue(p, vector, mergeKey);
                return;
            }

            if (p.TryParse(hex, out var color))
                SetValue(p, color, mergeKey);
            return;
        }

        Commit(row, component, text, mergeKey);
    }

    private void CommitFlags(int row)
    {
        var p = _rows[row].Property;
        long raw = 0;
        var flag = 0;
        foreach (var (_, bits) in p.FlagChoices())
            if (Document.GetElementById(FieldId(row, flag++)) is { IsNull: false } box && box.HasAttribute("checked"))
                raw |= bits;
        SetValue(p, p.EnumFromRaw(raw), null);
    }

    private void CommitName(string text)
    {
        if (Workspace.Session.Active is { } scene && _target is Node node && !node.IsFreed)
            scene.Rename(node, text);
    }

    private void CommitArrayElement(int row, int element, string text)
    {
        var p = _rows[row].Property;
        var type = p.ElementType;
        if (type is null || !TryParseElement(type, text, out var parsed))
            return;
        EditArray(p, list =>
        {
            if ((uint)element < (uint)list.Count)
                list[element] = parsed;
        });
    }

    private void SetValue(InspectorProperty p, object? value, string? mergeKey)
    {
        if (Workspace.Session.Active is not { } scene)
            return;
        scene.SetProperty(p.Target, p.Info, value, mergeKey);
    }

    // Arrays are values: edit a copy and set it (undoable as one property change).
    private void EditArray(InspectorProperty p, Action<IList> edit)
    {
        var current = p.GetValue() as IList;
        IList copy;
        if (p.ValueType.IsArray)
        {
            var list = new List<object?>();
            if (current is not null)
                foreach (var item in current)
                    list.Add(item);
            var proxy = new ArrayList(list);
            edit(proxy);
            var array = Array.CreateInstance(p.ElementType!, proxy.Count);
            proxy.CopyTo(array);
            copy = array;
        }
        else
        {
            copy = (IList)Activator.CreateInstance(p.ValueType)!;
            if (current is not null)
                foreach (var item in current)
                    copy.Add(item);
            edit(copy);
        }

        _rebuildPending = true;
        SetValue(p, copy, null);
    }

    private static object? DefaultElement(Type? type) =>
        type is null ? null : type == typeof(string) ? "" : type == typeof(NodePath) ? new NodePath("") : type.IsValueType ? Activator.CreateInstance(type) : null;

    private static bool TryParseElement(Type type, string text, out object? value)
    {
        value = null;
        text = text.Trim();
        if (type == typeof(string))
        {
            value = text;
            return true;
        }

        if (type == typeof(NodePath))
        {
            value = new NodePath(text);
            return true;
        }

        if (type == typeof(bool))
        {
            if (!bool.TryParse(text, out var b))
                return false;
            value = b;
            return true;
        }

        if (type.IsEnum)
            return Enum.TryParse(type, text, ignoreCase: true, out value);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;
        try
        {
            value = Convert.ChangeType(type == typeof(float) || type == typeof(double) ? d : Math.Round(d), type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    // ── Pickers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void Browse(InspectorProperty p)
    {
        var start = Workspace.Session.ProjectRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mode = p.Kind == PropertyEditorKind.DirectoryPath ? FilePickerMode.Folder : FilePickerMode.Open;
        var model = new FilePickerModel(mode, start, p.FileFilter);
        Workspace.FilePicker.Show(model, $"Choose {p.Label}", "Choose", path => SetValue(p, ProjectRelative(path), null));
    }

    /// <summary>Paths inside the project are stored project-relative (<c>Content/…</c>), like every content reference.</summary>
    private string ProjectRelative(string path)
    {
        if (Workspace.Session.ProjectRoot is { } root)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative;
        }

        return path;
    }

    private void PickNode(InspectorProperty p)
    {
        if (Workspace.Session.Active is not { } scene || p.Target is not Node owner)
            return;
        var items = new List<ListPickerItem>();
        void Add(Node node)
        {
            if (p.NodeType is null || p.NodeType.IsInstanceOfType(node))
                items.Add(new ListPickerItem(node.Name, scene.Root.GetPathTo(node).Path is { Length: > 0 } path ? path : ".",
                    SceneTreeModel.IconOf(node), node));
            foreach (var child in node.Children)
                if (scene.IsEditable(child))
                    Add(child);
        }

        Add(scene.Root);
        Workspace.ListPicker.Show($"Pick {p.Label}", items, "Pick", payload =>
        {
            if (payload is Node target && Node.IsInstanceValid(target) && Node.IsInstanceValid(owner))
                SetValue(p, owner.GetPathTo(target), null);
        });
    }

    private void LoadResource(InspectorProperty p)
    {
        var start = Workspace.Session.ProjectRoot is { } root && Directory.Exists(Path.Combine(root, "Content"))
            ? Path.Combine(root, "Content")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, start, ["*.mres", "*.png", "*.jpg", "*.gltf", "*.glb", "*.ogg", "*.wav", "*.mp3", "*.flac"]);
        Workspace.FilePicker.Show(model, $"Load {p.Label}", "Load", path =>
        {
            try
            {
                var resource = ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(path));
                if (p.ResourceType is { } type && !type.IsInstanceOfType(resource))
                {
                    resource.Release();
                    throw new InvalidOperationException($"{Path.GetFileName(path)} is a {resource.GetType().Name}, not a {type.Name}.");
                }

                SetValue(p, resource, null);
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Workspace.Commands.ReportError($"Could not load {Path.GetFileName(path)}", e);
            }
        });
    }

    private void NewResource(InspectorProperty p)
    {
        var baseType = p.ResourceType ?? typeof(Resource);
        var items = TypeRegistry.All
            .Where(t => t.IsResource && !t.IsAbstract && baseType.IsAssignableFrom(t.Type) && t.Type != typeof(PackedScene) && t.Type != typeof(MissingResource))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ListPickerItem(t.Name, t.Base?.Name ?? "", "node", t))
            .ToArray();
        if (items.Length == 1)
        {
            CreateResource(p, (NodeTypeInfo)items[0].Payload);
            return;
        }

        Workspace.ListPicker.Show($"New {p.Label}", items, "Create", payload => CreateResource(p, (NodeTypeInfo)payload));
    }

    private void CreateResource(InspectorProperty p, NodeTypeInfo info)
    {
        _expanded.Add((p.Target, p.Name));
        SetValue(p, info.CreateInstance(), null);
    }
}

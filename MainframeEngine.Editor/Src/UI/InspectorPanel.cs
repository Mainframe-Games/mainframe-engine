using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
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
public sealed partial class InspectorPanel : EditorDocument
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
    // [ExportGroup] sections of nested resources ("Type/Group") the user opened: collapsed by default (Godot's sub-resources).
    private readonly HashSet<string> _openNestedSections = new(StringComparer.Ordinal);
    private const string NestedSectionPrefix = "nested:";
    private readonly HashSet<(object Target, string Property)> _expanded = [];
    private RmlEventListener? _change;
    private RmlEventListener? _blur;
    private RmlEventListener? _mouseUp;
    private InspectorModel? _model;
    private object? _target;
    private List<object> _targets = [];
    private bool _rebuildPending;
    private bool _rowEdit;
    private ExportPropertyInfo? _mergedEdit;
    // Identity hashes, not references: game objects must not outlive a code reload through the inspector.
    private (int History, int Position, int Top) _historyMark;
    // Set while the panel writes into its own elements (rebuild, value refresh): RmlUi raises change events for
    // programmatic value/attribute changes (sliders, checkboxes), which must not be taken for user edits.
    private bool _suppressEvents;
    private EditedScene? _scene; // the scene the inspected object belongs to

    public InspectorPanel(EditorWorkspace workspace)
        : base(workspace, "inspector.rml")
    {
    }

    /// <summary>The object being inspected (the primary selection), or null.</summary>
    public object? Target => _target;

    /// <summary>Every inspected object: the selection (several when multi-selecting, the primary last).</summary>
    public IReadOnlyList<object> Targets => _targets;

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
        _mouseUp = root.AddEventListener("mouseup", OnMouseUp);
        AttachSignals(document);
        AttachLabelSplitter(document);
        Rebuild();
    }

    private void OnMouseUp(RmlEvent e)
    {
        EndDrag();
        if (Workspace.FileDrag is { } file && TryRow(e.Target, out var row))
            DropFile(row, file);
    }

    /// <summary>
    /// A file dragged from the FileSystem panel onto row <paramref name="row"/>: a resource row takes the loaded resource
    /// (when its type fits the slot), a file path row takes the project path. False when the row cannot take it.
    /// </summary>
    public bool DropFile(int row, string file)
    {
        Workspace.EndFileDrag();
        if ((uint)row >= (uint)_rows.Count)
            return false;
        var p = _rows[row].Property;
        if (p.Kind is PropertyEditorKind.FilePath)
        {
            SetValue(p, ProjectRelative(file), null);
            return true;
        }

        if (p.Kind != PropertyEditorKind.Resource)
            return false;
        Resource resource;
        try
        {
            resource = ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(file));
        }
        catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
        {
            Log.Warning($"[Editor] {Path.GetFileName(file)} is not a resource ({ex.Message}).");
            return false;
        }

        if (p.ResourceType is { } type && !type.IsInstanceOfType(resource))
        {
            resource.Release();
            Log.Warning($"[Editor] {Path.GetFileName(file)} is a {resource.GetType().Name}; {p.Label} takes a {type.Name}.");
            return false;
        }

        SetValue(p, resource, null);
        return true;
    }

    /// <summary>Forgets every inspected object (before a code reload unloads game types).</summary>
    public void ReleaseReferences()
    {
        _expanded.Clear();
        _mergedEdit = null;
        _rows.Clear();
        _model = null;
        _target = null;
        _targets = [];
        _signalRows = [];
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

        var suppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            RebuildNow();
        }
        finally
        {
            _suppressEvents = suppressed;
        }
    }

    private void RebuildNow()
    {
        _rebuildPending = false;
        var scene = Workspace.Session.Active;
        if (_resource is { } resource)
        {
            RebuildResource(resource, scene);
            return;
        }

        _targets = SelectedTargets(scene);
        var node = _targets.Count > 0 ? (Node)_targets[^1] : null;
        _target = node;
        _scene = scene;
        _rows.Clear();
        _model = _targets.Count == 0 ? null : InspectorModel.Build(_targets);
        RebuildSignals();
        if (!IsLoaded)
            return;
        var body = Document.GetElementById("inspector-body");
        if (body.IsNull)
            return;
        if (scene is null || node is null)
        {
            body.SetInnerRml(EmptyRml);
            return;
        }

        var rml = new StringBuilder(4096);
        AppendHeader(rml, scene, node, _targets);
        if (Custom?.GetHeaderRml(node) is { } custom)
            rml.Append(custom);
        AppendSections(rml, _model!, 0);
        body.SetInnerRml(rml.ToString());
        _appliedLabelWidth = -1;
        ApplyLabelWidth();
    }

    /// <summary>The body shown without a selection.</summary>
    public const string EmptyRml =
        "<div class=\"empty\"><span class=\"icon icon-lg icon-pointer icon-muted\"></span><div>Select a node to edit its properties.</div></div>";

    private void RebuildResource(EditedResource resource, EditedScene? scene)
    {
        _targets = [resource.Resource];
        _target = resource.Resource;
        _scene = scene;
        _rows.Clear();
        _model = InspectorModel.Build(resource.Resource);
        RebuildSignals();
        if (!IsLoaded || Document.GetElementById("inspector-body") is not { IsNull: false } body)
            return;
        var rml = new StringBuilder(4096);
        AppendResourceHeader(rml, resource);
        if (Custom?.GetHeaderRml(resource.Resource) is { } custom)
            rml.Append(custom);
        AppendSections(rml, _model, 0);
        body.SetInnerRml(rml.ToString());
        _appliedLabelWidth = -1;
        ApplyLabelWidth();
    }

    // The selection as inspector targets (nodes that still exist), in selection order: the primary is last.
    private static List<object> SelectedTargets(EditedScene? scene)
    {
        if (scene is null || scene.Selection.Count == 0)
            return [];
        var nodes = scene.Selection.Nodes;
        var list = new List<object>(nodes.Count);
        foreach (var n in nodes)
            if (!n.IsFreed)
                list.Add(n);
        return list;
    }

    private static bool SameTargets(List<object> a, IReadOnlyList<Node> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i]))
                return false;
        return true;
    }

    private static void AppendHeader(StringBuilder rml, EditedScene scene, Node node, List<object> targets)
    {
        if (targets.Count > 1)
        {
            // Multi-selection: the shared type, no name field (names stay unique per node).
            var common = InspectorModel.CommonType(targets)?.Name ?? "Node";
            rml.Append("<div class=\"insp-head\"><div class=\"insp-type\" data-tooltip=\"").Append(targets.Count)
                .Append(" Selected — edits apply to every selected node as one undo step; differing values show as —\">")
                .Append("<span class=\"icon icon-lg icon-stack-2 insp-icon\"></span><span class=\"insp-type-name\">").Append(targets.Count)
                .Append(" × ").Append(RmlText.Escape(common)).Append("</span></div><div class=\"insp-multi\"><span>Shared properties of ")
                .Append(targets.Count).Append(" nodes; differing values show as —.</span></div></div>");
            return;
        }

        var info = TypeRegistry.GetNearest(node.GetType());
        var type = info?.Name ?? node.GetType().Name;
        var family = node is MissingNode ? "icon-missing" : EditorIcons.Family(node.GetType());
        var tooltip = new StringBuilder(type);
        if (node is MissingNode missing)
        {
            type = $"{missing.OriginalType} (missing)";
            tooltip.Clear().Append(missing.OriginalType).Append(" — this type is not loaded");
        }
        else if (info?.Description is { } description)
        {
            tooltip.Append(" — ").Append(description);
        }

        for (var t = info?.Base; t is not null; t = t.Base)
            tooltip.Append(t == info?.Base ? "\nInherits " : " › ").Append(t.Name);

        rml.Append("<div class=\"insp-head\"><div class=\"insp-type\" data-tooltip=\"").Append(RmlText.Escape(tooltip.ToString())).Append("\"><span class=\"")
            .Append(EditorIcons.Classes(node)).Append(" icon-lg insp-icon\"></span><span class=\"insp-type-name ").Append(family.Replace("icon-", "t-", StringComparison.Ordinal))
            .Append("\">").Append(RmlText.Escape(type)).Append("</span>");
        rml.Append("</div>");
        if (!ReferenceEquals(node, scene.Root) && node.SceneFilePath is { } instance)
            rml.Append("<div class=\"insp-instance\" data-tooltip=\"Instanced Scene — its own nodes are edited in that scene\"><span class=\"icon icon-sm icon-movie icon-info\"></span>")
                .Append("<span>").Append(RmlText.Escape(instance)).Append("</span></div>");
        rml.Append("<div class=\"insp-name-row\"><span class=\"icon icon-sm icon-tag icon-muted name-icon\" data-tooltip=\"Name — unique among its siblings (Enter renames)\"></span>")
            .Append("<input type=\"text\" class=\"text\" id=\"node-name\" value=\"").Append(RmlText.Escape(node.Name)).Append("\"/></div></div>");
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
                    .Append(collapsed ? " collapsed" : "").Append("\"></span><span class=\"").Append(section.IconClasses).Append(" icon-sm section-icon\"></span><span>")
                    .Append(RmlText.Escape(section.Title)).Append("</span></div>");
            }
            else if (section.IsGroup)
            {
                // A nested resource's [ExportGroup] (a post-processing profile's Glow, SSAO, ...): a collapsible title,
                // closed until opened.
                collapsed = !_openNestedSections.Contains(key);
                rml.Append("<div class=\"section-title ").Append(depth == 1 ? "nested" : "nested2").Append("\" data-section=\"")
                    .Append(RmlText.Escape(NestedSectionPrefix + key)).Append("\"><span class=\"arrow").Append(collapsed ? " collapsed" : "")
                    .Append("\"></span><span class=\"").Append(section.IconClasses).Append(" icon-sm section-icon\"></span><span>")
                    .Append(RmlText.Escape(section.Title)).Append("</span></div>");
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
        _rows.Add(new RowView { Property = property, Depth = depth, Shown = SnapshotOf(property) });
        var nested = depth switch { 0 => "", 1 => " nested", _ => " nested2" };
        rml.Append("<div class=\"prop").Append(nested).Append("\"><div class=\"prop-label\" data-tooltip=\"")
            .Append(RmlText.Escape(property.Tooltip)).Append("\"><span class=\"").Append(PropertyIcons.Classes(property, value))
            .Append(" icon-sm prop-icon\"></span><span class=\"prop-name\">").Append(RmlText.Escape(property.Label))
            .Append("</span></div><div class=\"prop-editor\">");
        AppendEditor(rml, property, index, value);
        rml.Append("</div></div>");

        if (property.Kind == PropertyEditorKind.Color && _expanded.Contains((property.Target, property.Name)))
            AppendColorPicker(rml, property, index, value);
        if (property.Kind == PropertyEditorKind.Resource && value is Resource resource && IsEditableInline(resource) && !property.IsMulti &&
            depth + 1 < MaxResourceDepth && _expanded.Contains((property.Target, property.Name)))
            AppendSections(rml, InspectorModel.Build(resource), depth + 1);
        // Arrays of sub-resources (a BuildingDef's levels): each expanded element's properties below the array, under its index
        // (a header without a .prop-name: the label pass fits one .prop-name per row).
        if (property.Kind == PropertyEditorKind.Array && IsResourceElement(property) && !property.IsMulti && depth + 1 < MaxResourceDepth &&
            value is IList elements)
        {
            for (var i = 0; i < elements.Count; i++)
            {
                if (elements[i] is not Resource { IsExternal: false } element || !_expanded.Contains((property.Target, ElementKey(property, i))))
                    continue;
                rml.Append("<div class=\"prop nested element-title\"><div class=\"prop-label\"><span class=\"").Append(EditorIcons.Classes(element.GetType()))
                    .Append(" icon-sm prop-icon\"></span><span class=\"element-name\">").Append(RmlText.Escape($"{property.Label} [{i}]"))
                    .Append("</span></div><div class=\"prop-editor\"><span class=\"readonly\">").Append(RmlText.Escape(element.GetType().Name))
                    .Append("</span></div></div>");
                AppendSections(rml, InspectorModel.Build(element), depth + 1);
            }
        }
    }

    private static bool IsResourceElement(InspectorProperty p) => p.ElementType is { } t && typeof(Resource).IsAssignableFrom(t);

    private static string ElementKey(InspectorProperty p, int index) => $"{p.Name}[{index}]";

    private void AppendEditor(StringBuilder rml, InspectorProperty p, int row, object? value)
    {
        switch (p.Kind)
        {
            case PropertyEditorKind.Bool:
                rml.Append("<input type=\"checkbox\" class=\"checkbox").Append(p.IsMixed ? " mixed" : "").Append("\" id=\"").Append(FieldId(row, 0))
                    .Append("\" data-row=\"").Append(row).Append('"').Append(value is true && !p.IsMixed ? " checked=\"\"" : "").Append("/>");
                break;

            case PropertyEditorKind.Range:
                rml.Append("<input type=\"range\" class=\"range\" id=\"").Append(SliderId(row, 0)).Append("\" data-row=\"").Append(row)
                    .Append("\" data-comp=\"0\" data-slider=\"1\" min=\"").Append(Num(p.Min)).Append("\" max=\"").Append(Num(p.Max))
                    .Append("\" step=\"").Append(Num(p.Step)).Append("\" value=\"").Append(RmlText.Escape(p.FormatComponent(value, 0))).Append("\"/>");
                AppendText(rml, row, 0, p, value);
                break;

            case PropertyEditorKind.IntegerNumber or PropertyEditorKind.FloatNumber or PropertyEditorKind.Text or PropertyEditorKind.NodePath:
                AppendText(rml, row, 0, p, value);
                if (p.Kind == PropertyEditorKind.NodePath)
                    AppendButton(rml, row, "pick-node", "crosshair", "Pick Node — choose the target from the scene");
                break;

            case PropertyEditorKind.FilePath or PropertyEditorKind.DirectoryPath:
                AppendText(rml, row, 0, p, value);
                AppendButton(rml, row, "browse", "folder-open",
                    p.Kind == PropertyEditorKind.DirectoryPath ? "Browse — choose a folder" : "Browse — choose a file");
                break;

            case PropertyEditorKind.MultilineText:
                rml.Append("<textarea id=\"").Append(FieldId(row, 0)).Append("\" data-row=\"").Append(row).Append("\" data-comp=\"0\">")
                    .Append(RmlText.Escape(value as string)).Append("</textarea>");
                break;

            case PropertyEditorKind.Enum:
                rml.Append("<select id=\"").Append(FieldId(row, 0)).Append("\" data-row=\"").Append(row).Append("\" data-enum=\"1\">");
                var current = value is null ? "" : value.ToString();
                if (p.IsMixed)
                {
                    rml.Append("<option value=\"\" selected=\"\">—</option>");
                    current = null;
                }

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
                    rml.Append("<span class=\"flag\"><input type=\"checkbox\" class=\"checkbox").Append(FlagMixed(p, bits) ? " mixed" : "").Append("\" id=\"").Append(FieldId(row, flag++))
                        .Append("\" data-row=\"").Append(row).Append("\" data-flag=\"").Append(bits.ToString(CultureInfo.InvariantCulture)).Append('"')
                        .Append(InspectorProperty.HasFlag(value, bits) ? " checked=\"\"" : "").Append("/>").Append(RmlText.Escape(name)).Append("</span>");
                }

                rml.Append("</div>");
                break;

            case PropertyEditorKind.Vector2 or PropertyEditorKind.Vector3 or PropertyEditorKind.Vector4 or PropertyEditorKind.Quaternion:
                for (var c = 0; c < p.Components; c++)
                {
                    rml.Append("<span class=\"axis ").Append("xyzw"[c]).Append("\">").Append("XYZW"[c]).Append("</span>");
                    AppendText(rml, row, c, p, value);
                }

                break;

            case PropertyEditorKind.Color:
                rml.Append("<div class=\"swatch\" id=\"").Append(SwatchId(row)).Append("\" data-row=\"").Append(row)
                    .Append("\" data-action=\"color\" data-tooltip=\"Color Picker — click for RGBA sliders\" style=\"background-color: ")
                    .Append(ValueText.ColorHex(value)).Append(";\"></div>");
                AppendText(rml, row, 9, p, value);
                break;

            case PropertyEditorKind.Resource:
                {
                    var resourceType = value?.GetType() ?? p.ResourceType ?? typeof(Resource);
                    rml.Append("<div class=\"res-label\" id=\"").Append(FieldId(row, 0)).Append("\">");
                    if (value is not null)
                        rml.Append("<span class=\"").Append(EditorIcons.Classes(resourceType)).Append(" icon-sm res-icon\"></span>");
                    rml.Append("<span class=\"res-name\">").Append(RmlText.Escape(p.IsMixed ? "— (differs)" : p.Format(value))).Append("</span></div>");
                    if (value is AudioStream sound && !p.IsMulti)
                        AppendPreviewButton(rml, row, Workspace.AudioPreview.IsPlaying(sound));
                    var expanded = _expanded.Contains((p.Target, p.Name));
                    if (value is Resource editable && IsEditableInline(editable) && !p.IsMulti && RowDepth(row) + 1 < MaxResourceDepth)
                        AppendButton(rml, row, "res-edit", expanded ? "chevron-up" : "pencil",
                            expanded ? "Fold — hide the resource's properties"
                            : editable.IsExternal ? $"Edit — show {Path.GetFileName(editable.ResourcePath)}'s properties below (saved to the file when the scene is saved)"
                            : "Edit — show the resource's properties below", expanded);
                    if (value is Resource { IsExternal: false } inline && inline is not PackedScene && !p.IsMulti)
                        AppendButton(rml, row, "res-save", "device-floppy", "Save as .mres — move the resource to its own file, shared by every slot that uses it");
                    AppendButton(rml, row, "res-load", "folder-open", "Load — use a resource file (.mres, image, model, sound)");
                    AppendButton(rml, row, "res-new", "circle-plus", $"New — create an inline {(p.ResourceType ?? typeof(Resource)).Name}");
                    if (value is not null)
                        AppendButton(rml, row, "res-clear", "x", "Clear — empty the slot");
                    break;
                }

            case PropertyEditorKind.Array when p.IsMulti:
                // Arrays are per-node values: edit them one node at a time.
                rml.Append("<span class=\"readonly\" id=\"").Append(FieldId(row, 0)).Append("\">")
                    .Append(RmlText.Escape(p.IsMixed ? "— (differs)" : p.Format(value))).Append("</span>");
                break;

            case PropertyEditorKind.Array:
                AppendArray(rml, p, row, value as IList);
                break;

            default:
                rml.Append("<span class=\"readonly\" id=\"").Append(FieldId(row, 0)).Append("\">").Append(RmlText.Escape(p.Format(value))).Append("</span>");
                break;
        }
    }

    private void AppendArray(StringBuilder rml, InspectorProperty p, int row, IList? list)
    {
        if (IsResourceElement(p))
        {
            AppendResourceArray(rml, p, row, list);
            return;
        }

        rml.Append("<div class=\"array-box\"><div class=\"array-item\"><span class=\"readonly grow\">").Append(RmlText.Escape(p.Format(list)))
            .Append("</span>");
        AppendButton(rml, row, "arr-add", "plus", "Add Element — append a default value");
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
                rml.Append("<button class=\"tool-button small\" data-row=\"").Append(row).Append("\" data-elem=\"").Append(i)
                    .Append("\" data-action=\"arr-remove\" data-tooltip=\"Remove Element — delete item ").Append(i)
                    .Append("\"><span class=\"icon icon-sm icon-x\"></span></button></div>");
            }
        }

        rml.Append("</div>");
    }

    // An array of sub-resources: per element its icon and name, Edit (fold), Load, New, Clear and Remove.
    private void AppendResourceArray(StringBuilder rml, InspectorProperty p, int row, IList? list)
    {
        rml.Append("<div class=\"array-box\"><div class=\"array-item\"><span class=\"readonly grow\">").Append(RmlText.Escape(p.Format(list)))
            .Append("</span>");
        AppendButton(rml, row, "arr-add", "plus", $"Add Element — append an empty {p.ElementType!.Name} slot");
        rml.Append("</div>");
        if (list is not null)
        {
            var nestable = RowDepth(row) + 1 < MaxResourceDepth;
            for (var i = 0; i < list.Count; i++)
            {
                var element = list[i] as Resource;
                rml.Append("<div class=\"array-item\"><span class=\"array-index\">").Append(i).Append("</span><div class=\"res-label grow\" data-tooltip=\"")
                    .Append(RmlText.Escape(element is null ? $"{p.Label} [{i}] — empty" : $"{p.Label} [{i}] — {ValueText.Format(element)} ({element.GetType().Name})")).Append("\">");
                if (element is not null)
                    rml.Append("<span class=\"").Append(EditorIcons.Classes(element.GetType())).Append(" icon-sm res-icon\"></span>");
                rml.Append("<span class=\"res-name\">").Append(RmlText.Escape(element is null ? "(empty)" : ValueText.Format(element))).Append("</span></div>");
                if (element is { IsExternal: false } && nestable)
                {
                    var expanded = _expanded.Contains((p.Target, ElementKey(p, i)));
                    AppendElementButton(rml, row, i, "elem-edit", expanded ? "chevron-up" : "pencil",
                        expanded ? "Fold — hide this element's properties" : "Edit — show this element's properties below", expanded);
                }

                AppendElementButton(rml, row, i, "elem-load", "folder-open", "Load — use a resource file for this element");
                AppendElementButton(rml, row, i, "elem-new", "circle-plus", $"New — create an inline {p.ElementType!.Name} here");
                if (element is not null)
                    AppendElementButton(rml, row, i, "elem-clear", "x", "Clear — empty this element");
                AppendElementButton(rml, row, i, "arr-remove", "trash", $"Remove Element — delete item {i}");
                rml.Append("</div>");
            }
        }

        rml.Append("</div>");
    }

    private static void AppendElementButton(StringBuilder rml, int row, int element, string action, string icon, string tooltip, bool active = false) =>
        rml.Append("<button class=\"tool-button small").Append(active ? " active" : "").Append("\" data-row=\"").Append(row).Append("\" data-elem=\"")
            .Append(element).Append("\" data-action=\"").Append(action).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip))
            .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span></button>");

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

    // A field of property p: empty with a "—" placeholder when the selected nodes differ in that field.
    private static void AppendText(StringBuilder rml, int row, int component, InspectorProperty p, object? value)
    {
        var mixed = IsFieldMixed(p, component);
        var text = component == 9 ? ValueText.ColorHex(value) : p.FormatComponent(value, component);
        rml.Append("<input type=\"text\" class=\"text").Append(mixed ? " mixed" : "").Append("\" id=\"").Append(FieldId(row, component))
            .Append("\" data-row=\"").Append(row).Append("\" data-comp=\"").Append(component).Append('"');
        if (mixed)
            rml.Append(" placeholder=\"—\" value=\"\"/>");
        else
            rml.Append(" value=\"").Append(RmlText.Escape(text)).Append("\"/>");
    }

    // Component 9 is the colour's hex field (the whole value).
    private static bool IsFieldMixed(InspectorProperty p, int component) =>
        p.IsMulti && (component == 9 || p.Components == 1 ? p.IsMixed : p.IsComponentMixed(component));

    private static bool FlagMixed(InspectorProperty p, long bits)
    {
        if (!p.IsMulti)
            return false;
        var first = InspectorProperty.HasFlag(p.Info.GetValue(p.Target), bits);
        foreach (var target in p.Targets)
            if (InspectorProperty.HasFlag(p.Info.GetValue(target), bits) != first)
                return true;
        return false;
    }

    // An icon-only row button: the tooltip names it and says what it does.
    private static void AppendButton(StringBuilder rml, int row, string action, string icon, string tooltip, bool active = false) =>
        rml.Append("<button class=\"tool-button small").Append(active ? " active" : "").Append("\" data-row=\"").Append(row).Append("\" data-action=\"")
            .Append(action).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-sm icon-").Append(icon)
            .Append("\"></span></button>");

    private int RowDepth(int row) => row < _rows.Count ? _rows[row].Depth : 0;

    private static string FieldId(int row, int component) => $"p{row}c{component}";
    private static string SliderId(int row, int component) => $"p{row}s{component}";
    private static string SwatchId(int row) => $"p{row}w";
    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool ArrayElementEditable(Type? type) =>
        type is not null && (type == typeof(string) || type == typeof(bool) || type == typeof(float) || type == typeof(double) ||
                             InspectorProperty.IsIntegerType(type) || type.IsEnum || type == typeof(NodePath));

    // What a row shows: the value for one target; for several, every target's value (so the mixed state is covered).
    private static object? SnapshotOf(InspectorProperty p)
    {
        if (!p.IsMulti)
            return Snapshot(p.GetValue());
        var builder = new StringBuilder();
        foreach (var target in p.Targets)
        {
            var value = p.Info.GetValue(target);
            builder.Append('\u001e');
            if (value is Resource resource)
                builder.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resource));
            else
                builder.Append(value is IList ? Snapshot(value) as string : ValueText.Format(value));
        }

        return builder.ToString();
    }

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
        if (_resource is not null)
            return; // a resource file is inspected; its own history refreshes it
        var scene = Workspace.Session.Active;
        if (scene is not null && ReferenceEquals(scene.Selection.Primary, _target))
            NotifyHistoryChange(scene.History);
        // A custom inspector's header shows values too: regenerate it with the rows (after a merged drag, not per tick).
        if (!ReferenceEquals(scene?.Selection.Primary, _target) || _rebuildPending ||
            (_model?.CustomInspector is not null && scene?.History.IsMerging != true) ||
            (scene is not null && !SameTargets(_targets, scene.Selection.Nodes)))
        {
            Rebuild();
            return;
        }

        RefreshValues();
        RefreshSignals();
    }

    /// <summary>Writes changed values into the existing elements (rebuilds when a resource or array changed shape).</summary>
    public void RefreshValues()
    {
        var suppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            RefreshValuesNow();
        }
        finally
        {
            _suppressEvents = suppressed;
        }
    }

    private void RefreshValuesNow()
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
            var snapshot = SnapshotOf(view.Property);
            if (Equals(snapshot, view.Shown))
                continue;
            // Several nodes: the mixed state of any field may change; regenerate (multi-selections are small).
            if (view.Property.Kind is PropertyEditorKind.Resource or PropertyEditorKind.Array or PropertyEditorKind.NodePath || view.Property.IsMulti)
            {
                Rebuild(); // different resource or element count: the rows below change; a node path's icon is its target's
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
        if (_suppressEvents)
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
        if (_suppressEvents)
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
        if (HandleSignalClick(e))
            return;
        if (FindAttribute(e.Target, "data-section") is { } section)
        {
            ToggleSection(section);
            return;
        }

        var actionElement = FindWithAttribute(e.Target, "data-action");
        if (actionElement.IsNull)
            return;
        var action = actionElement.GetAttribute("data-action") ?? "";
        if (!TryRow(actionElement, out var row))
        {
            RunHeaderAction(action);
            return;
        }

        RunAction(row, action, IntAttribute(actionElement, "data-elem"));
    }

    /// <summary>Runs a header button: Save/Close of a resource file, else the custom inspector's <c>data-action</c> — also for QA and tests.</summary>
    public void RunHeaderAction(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (HandleResourceAction(action))
            return;
        if (Custom is { } custom && _target is not null && EditContext is { } context)
            custom.OnAction(_target, action, context);
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
            case "res-play":
                if (p.GetValue() is AudioStream stream)
                    Workspace.AudioPreview.Toggle(stream);
                break;
            case "res-load":
                LoadResource(p);
                break;
            case "res-new":
                NewResource(p);
                break;
            case "res-save":
                SaveResourceAs(p);
                break;
            case "arr-add":
                EditArray(p, list => list.Add(DefaultElement(p.ElementType)));
                break;
            case "elem-edit":
                if (!_expanded.Remove((p.Target, ElementKey(p, element))))
                    _expanded.Add((p.Target, ElementKey(p, element)));
                Rebuild();
                break;
            case "elem-load":
                LoadResourceInto(p.Label, p.ElementType, resource => SetElement(p, element, resource));
                break;
            case "elem-new":
                NewResourceInto(p.Label, p.ElementType, resource =>
                {
                    _expanded.Add((p.Target, ElementKey(p, element)));
                    SetElement(p, element, resource);
                });
                break;
            case "elem-clear":
                SetElement(p, element, null);
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
        // An untouched mixed field ("—") commits nothing.
        if (p.IsMulti && text.Trim().Length == 0 && IsFieldMixed(p, component))
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

        if (!p.IsMulti)
        {
            if (Equals(Snapshot(current), Snapshot(value)))
                return false;
            SetValue(p, value, mergeKey);
            return true;
        }

        // Several nodes: a component edit keeps each node's other components (set X on all, Y/Z stay theirs).
        var whole = component == 9 || p.Components == 1;
        SetValueEach(p, target => whole || !p.TryParseComponent(p.Info.GetValue(target), component, text, out var own) ? value : own, mergeKey);
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
                // Keep HDR (> 1) components of the others: set only the dragged channel (on every selected node).
                var channelText = ValueText.Number(channel);
                if (p.TryParseComponent(component, channelText, out _))
                    SetValueEach(p, target => p.TryParseComponent(p.Info.GetValue(target), component, channelText, out var own) ? own : p.Info.GetValue(target), mergeKey);
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

    // Edits go to the history of the scene the inspected object belongs to (not whatever tab is active now).
    private void SetValue(InspectorProperty p, object? value, string? mergeKey) => SetValueEach(p, _ => value, mergeKey);

    // One value per edited object (multi-selection: one undo step for all of them).
    private void SetValueEach(InspectorProperty p, Func<object, object?> valueFor, string? mergeKey)
    {
        if (EditContext is not { } context)
            return;
        var history = context.History;
        var before = (history.Position, history.UndoAction);
        var editing = _rowEdit;
        _rowEdit = true;
        try
        {
            if (!p.IsMulti || context is not EditedScene scene)
            {
                context.SetProperty(p.Target, p.Info, valueFor(p.Target), mergeKey);
            }
            else
            {
                var values = new object?[p.Targets.Count];
                for (var i = 0; i < values.Length; i++)
                    values[i] = valueFor(p.Targets[i]);
                scene.SetProperties(p.Targets, p.Info, values, mergeKey);
            }
        }
        finally
        {
            _rowEdit = editing;
        }

        // One OnPropertyChanged per history entry: now, or when a merged drag ends (EndMergedEdit).
        if (mergeKey is not null && history.IsMerging)
            _mergedEdit = p.Info;
        else if (before != (history.Position, history.UndoAction))
            NotifyPropertyChanged(p.Info);
    }

    // ── ICustomInspector.OnPropertyChanged ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mouse was released: closes the merge window of a slider drag, rebuilds a custom header skipped while it ran
    /// and sends the drag's one <see cref="ICustomInspector.OnPropertyChanged"/>.
    /// </summary>
    public void EndDrag()
    {
        (EditContext?.History ?? Workspace.Session.Active?.History)?.EndMerge();
        EndMergedEdit();
    }

    private void EndMergedEdit()
    {
        if (_mergedEdit is not { } property)
            return;
        _mergedEdit = null;
        if (_model?.CustomInspector is not null)
            Rebuild();
        NotifyPropertyChanged(property);
    }

    /// <summary>
    /// A history change the rows did not make (undo, redo, a custom inspector's action): notifies the custom inspector
    /// once with no property. Changes that leave the history position alone (save, a merged drag tick) are ignored.
    /// </summary>
    private void NotifyHistoryChange(UndoRedo history)
    {
        var mark = (RuntimeHelpers.GetHashCode(history), history.Position, history.UndoAction is { } top ? RuntimeHelpers.GetHashCode(top) : 0);
        var same = _historyMark == mark;
        _historyMark = mark;
        if (!same && !_rowEdit && !history.IsMerging)
            NotifyPropertyChanged(null);
    }

    private void NotifyPropertyChanged(ExportPropertyInfo? property)
    {
        if (Custom is { } custom && _target is not null && EditContext is { } context)
            custom.OnPropertyChanged(_target, property, context);
    }

    // The model's custom inspector, bound to this workspace when it needs the editor (previews, dialogs).
    private ICustomInspector? Custom
    {
        get
        {
            var custom = _model?.CustomInspector;
            if (custom is IWorkspaceInspector bound)
                bound.Workspace = Workspace;
            return custom;
        }
    }

    // ── Audio previews ───────────────────────────────────────────────────────────────────────────────────────────

    private static string PreviewButtonId(int row) => $"res-play-{row}";

    private static void AppendPreviewButton(StringBuilder rml, int row, bool playing) =>
        rml.Append("<button class=\"tool-button small").Append(playing ? " active" : "").Append("\" id=\"").Append(PreviewButtonId(row))
            .Append("\" data-row=\"").Append(row).Append("\" data-action=\"res-play\" data-tooltip=\"")
            .Append(playing ? PreviewStopTip : PreviewPlayTip).Append("\"><span class=\"icon icon-sm icon-")
            .Append(playing ? "player-stop" : "player-play").Append("\"></span></button>");

    private const string PreviewPlayTip = "Play — preview this sound in the editor";
    private const string PreviewStopTip = "Stop — stop the preview";

    /// <summary>The preview started or ended: the Play/Stop buttons of AudioStream rows (and the custom header's) follow, in place.</summary>
    public void RefreshPreviewButtons()
    {
        if (!IsLoaded)
            return;
        var document = Document;
        for (var row = 0; row < _rows.Count; row++)
        {
            var p = _rows[row].Property;
            if (p.Kind != PropertyEditorKind.Resource || p.IsMulti || p.GetValue() is not AudioStream stream)
                continue;
            var button = document.GetElementById(PreviewButtonId(row));
            if (button.IsNull)
                continue;
            var playing = Workspace.AudioPreview.IsPlaying(stream);
            button.SetClass("active", playing);
            button.SetAttribute("data-tooltip", playing ? PreviewStopTip : PreviewPlayTip);
            button.SetInnerRml(playing ? "<span class=\"icon icon-sm icon-player-stop\"></span>" : "<span class=\"icon icon-sm icon-player-play\"></span>");
        }

        if (Custom is IWorkspaceInspector header && _target is not null)
            header.RefreshPreview(_target, document);
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
                    EditorIcons.Classes(node), node));
            foreach (var child in node.Children)
                if (scene.IsEditable(child))
                    Add(child);
        }

        Add(scene.Root);
        Workspace.ListPicker.Show($"Pick {p.Label}", items, "Pick", payload =>
        {
            if (payload is Node target && Node.IsInstanceValid(target) && Node.IsInstanceValid(owner))
                SetValueEach(p, t => t is Node each && Node.IsInstanceValid(each) ? each.GetPathTo(target) : owner.GetPathTo(target), null);
        });
    }

    private void LoadResource(InspectorProperty p) => LoadResourceInto(p.Label, p.ResourceType, resource => SetValue(p, resource, null));

    // Element `index` of an array of sub-resources (one undoable property change, like any array edit).
    private void SetElement(InspectorProperty p, int index, Resource? resource) =>
        EditArray(p, list =>
        {
            if ((uint)index < (uint)list.Count)
                list[index] = resource;
        });

    private void LoadResourceInto(string label, Type? slotType, Action<Resource> assign)
    {
        var start = Workspace.Session.ProjectRoot is { } root && Directory.Exists(Path.Combine(root, "Content"))
            ? Path.Combine(root, "Content")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, start, ["*.mres", "*.png", "*.jpg", "*.gltf", "*.glb", "*.ogg", "*.wav", "*.mp3", "*.flac"]);
        Workspace.FilePicker.Show(model, $"Load {label}", "Load", path =>
        {
            try
            {
                var resource = ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(path));
                if (slotType is { } type && !type.IsInstanceOfType(resource))
                {
                    resource.Release();
                    throw new InvalidOperationException($"{Path.GetFileName(path)} is a {resource.GetType().Name}, not a {type.Name}.");
                }

                assign(resource);
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Workspace.Commands.ReportError($"Could not load {Path.GetFileName(path)}", e);
            }
        });
    }

    /// <summary>
    /// Opens or closes a section title: <c>Type/Title</c> (top level, open by default) or <c>nested:Type/Title</c> (an
    /// [ExportGroup] of a nested resource, closed by default) — also for tests and QA scripts.
    /// </summary>
    public void ToggleSection(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.StartsWith(NestedSectionPrefix, StringComparison.Ordinal))
        {
            var key = section[NestedSectionPrefix.Length..];
            if (!_openNestedSections.Remove(key))
                _openNestedSections.Add(key);
        }
        else if (!_collapsedSections.Remove(section))
        {
            _collapsedSections.Add(section);
        }

        Rebuild();
    }

    // Inline resources, and resource files (.mres) edited in place: their changes are saved with the scene (ADR 0169).
    // Imported assets (a texture, a model, a LUT) are external but keep their data in the source file: not editable here.
    private static bool IsEditableInline(Resource resource) =>
        !resource.IsExternal || resource.ResourcePath!.EndsWith(".mres", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Save as .mres (editor-viewport-tools G7.2, the minimal path of ADR 0169): a deep copy of the inline resource in row
    /// <paramref name="p"/>'s slot is saved to a file the user picks, and every slot of the edited scene that held the
    /// inline resource gets the file's (one undo step; undo puts the inline resource back, the file stays).
    /// </summary>
    private void SaveResourceAs(InspectorProperty p)
    {
        if (p.GetValue() is not Resource { IsExternal: false } original || EditContext is not EditedScene scene)
            return;
        var start = Workspace.Session.ProjectRoot is { } root && Directory.Exists(Path.Combine(root, "Content"))
            ? Path.Combine(root, "Content")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = original.ResourceName is { Length: > 0 } named ? named : original.GetType().Name;
        var model = new FilePickerModel(FilePickerMode.Save, start, ["*.mres"], name + ".mres");
        Workspace.FilePicker.Show(model, $"Save {p.Label} as", "Save", path => SaveResourceAs(scene, original, path));
    }

    /// <summary>Saves <paramref name="original"/> (inline in <paramref name="scene"/>) as <paramref name="path"/> and points every slot holding it at the file.</summary>
    public bool SaveResourceAs(EditedScene scene, Resource original, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(original);
        try
        {
            var projectPath = AssetDatabase.Current.ToProjectPath(path);
            if (AssetDatabase.Current.GetUid(projectPath) is { } uid && ResourceLoader.IsCached(uid))
                throw new InvalidOperationException($"{Path.GetFileName(path)} is in use; choose another name.");
            var copy = original.Duplicate(deep: true);
            ResourceSaver.Save(copy, path);
            var slots = scene.SlotsHolding(original);
            var actions = new IEditorAction[slots.Count];
            for (var i = 0; i < slots.Count; i++)
                actions[i] = new SetPropertyAction(slots[i].Target, slots[i].Property, original, copy);
            if (actions.Length > 0)
                scene.History.Commit(new CompositeAction($"Save {original.GetType().Name} as {Path.GetFileName(path)}", actions));
            Log.Info($"[Editor] Saved {projectPath}");
            return true;
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Workspace.Commands.ReportError($"Could not save {Path.GetFileName(path)}", e);
            return false;
        }
    }

    private void NewResource(InspectorProperty p) => NewResourceInto(p.Label, p.ResourceType, resource =>
    {
        _expanded.Add((p.Target, p.Name));
        SetValue(p, resource, null);
    });

    // The create dialog for a slot of `slotType` (straight to the type when only one is creatable).
    private void NewResourceInto(string label, Type? slotType, Action<Resource> assign)
    {
        var entries = PickerSources.ResourceTypes(slotType ?? typeof(Resource));
        var creatable = entries.Where(e => e.Selectable).ToArray();
        if (creatable.Length == 1)
        {
            assign((Resource)((NodeTypeInfo)creatable[0].Payload!).CreateInstance());
            return;
        }

        Workspace.TreePicker.Show(new TreePickerRequest
        {
            Kind = "resource",
            Title = $"New {label}",
            OkLabel = "Create",
            Entries = entries,
            OnAccept = entry => assign((Resource)((NodeTypeInfo)entry.Payload!).CreateInstance()),
        });
    }
}

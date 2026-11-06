using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The document that draws <see cref="UiMarker3D"/>s on a screen <see cref="UiLayer"/> (G11.4, ADR 0254): every marker of
/// its camera's viewport is one row of the <c>markers</c> data model (<c>data-for="m : markers"</c>), so a hundred
/// nameplates are one document with no per-marker documents, model names or created elements. Each frame, just before
/// the layer's context updates, it projects the markers, applies distance fade and scale, clamps waypoints to the
/// edge and casts up to <see cref="OcclusionRaysPerFrame"/> occlusion rays round robin. When a row's content changes
/// (markers added or removed, text, kind, value, visibility, edge state) or every <see cref="ContentRefreshSeconds"/>,
/// it sorts the rows (nearer on top) and dirties the model; on other frames it only moves the row elements RmlUi
/// generated (their <c>transform</c> and <c>opacity</c>), which skips re-evaluating every row's data views. Rows hold the
/// markers' own strings, so a steady frame allocates nothing.
/// </summary>
/// <remarks>
/// Row members: <c>x</c>, <c>y</c> (layer pixels), <c>scale</c>, <c>opacity</c>, <c>visible</c>, <c>edge</c>,
/// <c>angle</c> (degrees), <c>distance</c> (m), <c>kind</c>, <c>text</c>, <c>subtext</c>, <c>value</c>, <c>icon</c>.
/// <see cref="UiDocument.Source"/> defaults to the engine's <see cref="DefaultSource"/> (four kinds); a game copies it
/// or points <c>Source</c> at its own document with a <c>markers</c> model.
/// </remarks>
[EditorIcon("map-pin", Family = EditorIconFamily.Ui)]
public class UiMarkerHost : UiDocument
{
    /// <summary>The engine's marker document.</summary>
    public const string DefaultSource = "Assets/UI/markers/markers.rml";

    /// <summary>The data model's name.</summary>
    public const string ModelName = "markers";

    private const string HostGroup = "__ui_marker_hosts";

    private static readonly RmlStructType<MarkerRow> RowType = new RmlStructType<MarkerRow>()
        .Member("x", static r => r.X)
        .Member("y", static r => r.Y)
        .Member("scale", static r => r.Scale)
        .Member("opacity", static r => r.Opacity)
        .Member("visible", static r => r.Visible)
        .Member("edge", static r => r.Edge)
        .Member("angle", static r => r.Angle)
        .Member("distance", static r => r.Distance)
        .Member("kind", static r => r.Kind)
        .Member("text", static r => r.Text)
        .Member("subtext", static r => r.Subtext)
        .Member("value", static r => r.Value)
        .Member("icon", static r => r.Icon);

    // Farther rows first: RmlUi draws later siblings on top.
    private readonly struct FarthestFirst : IComparer<UiMarker3D>
    {
        public int Compare(UiMarker3D? a, UiMarker3D? b) => b!.Row.Distance.CompareTo(a!.Row.Distance);
    }

    private readonly List<MarkerRow> _rows = [];
    private readonly List<UiMarker3D> _rowMarkers = []; // the marker of each row, in row order
    private readonly List<UiMarker3D> _markers = [];
    private float _sinceRefresh = float.PositiveInfinity;
    private int _stamp;
    private RmlDataModel? _model;
    private int _occlusionCursor;

    public UiMarkerHost()
    {
        Source = DefaultSource;
    }

    /// <summary>The camera markers are projected with; empty = the active camera of the layer's viewport.</summary>
    [Export(NodeType = typeof(Camera3D))]
    public NodePath Camera { get; set; }

    /// <summary>Nearer markers draw on top of farther ones.</summary>
    [Export]
    public bool SortByDistance { get; set; } = true;

    /// <summary>At most this many occlusion rays per frame, shared round robin by the markers with <see cref="MarkerOcclusion.Physics"/>.</summary>
    [Export(Range = "1,256,1")]
    public int OcclusionRaysPerFrame { get; set; } = 16;

    /// <summary>Seconds an occluded marker takes to fade out (and back in), so a ray flickering through foliage does not blink it.</summary>
    [Export]
    public float OcclusionFadeSeconds { get; set; } = 0.15f;

    /// <summary>
    /// How often the rows are rebuilt and re-sorted although no content changed, so distances shown in text and the
    /// draw order follow a moving camera (0 = every frame).
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float ContentRefreshSeconds { get; set; } = 0.1f;

    /// <summary>The markers drawn as rows in the last frame (element-mode markers included).</summary>
    public IReadOnlyList<UiMarker3D> Markers => _markers;

    /// <summary>How many times the rows were rebuilt and the model dirtied (tests, the overlay).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>The rows as last written (tests, debugging).</summary>
    internal IReadOnlyList<MarkerRow> Rows => _rows;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        AddToGroup(HostGroup);
    }

    protected override void OnExitTree()
    {
        RemoveFromGroup(HostGroup);
        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (Layer is not null)
            _model = CreateDataModel(ModelName).BindList(ModelName, _rows, RowType);
    }

    internal override void PrepareFrame()
    {
        base.PrepareFrame();
        if (_model is null || Layer?.Context is not { } context || Tree is not { } tree)
            return;
        var camera = Camera.IsEmpty ? GetViewport()?.ActiveCamera3D : GetNodeOrNull<Camera3D>(Camera);
        Update(tree, camera, new Vector2(context.Width, context.Height), context.DensityIndependentPixelRatio, tree.ProcessDeltaTime);
    }

    /// <summary>One frame of projection for a layer of <paramref name="layerSize"/> pixels (also called by tests).</summary>
    internal void Update(SceneTree tree, Camera3D? camera, Vector2 layerSize, float dpRatio, float delta)
    {
        _markers.Clear();
        _sinceRefresh += delta;
        _stamp++;
        var all = tree.GetNodesInGroup(UiMarker3D.Group);
        if (camera is null || !camera.IsInsideTree)
        {
            for (var i = 0; i < all.Count; i++)
                if (all[i] is UiMarker3D hidden && Owns(hidden, camera))
                    hidden.SetShown(false);
            if (_rows.Count > 0)
            {
                _rows.Clear();
                _rowMarkers.Clear();
                _model!.Dirty(ModelName);
            }

            return;
        }

        var viewportSize = camera.GetViewport()?.Size ?? layerSize;
        if (!(viewportSize.X > 0 && viewportSize.Y > 0))
            viewportSize = layerSize;
        var render = camera.SyncRenderCamera(viewportSize.X / viewportSize.Y);
        var viewProjection = render.ViewMatrix * render.ProjectionMatrix;
        var cameraPosition = render.Position;
        var forward = render.Forward;
        var toLayer = layerSize / viewportSize;
        var center = layerSize * 0.5f;
        var space = camera.GetWorld3D()?.DirectSpaceState;

        for (var i = 0; i < all.Count; i++)
            if (all[i] is UiMarker3D marker && Owns(marker, camera) && marker.IsVisibleInTree())
            {
                marker.HostStamp = _stamp;
                _markers.Add(marker);
            }

        // Occlusion rays, round robin over the markers that want them.
        var rays = 0;
        var count = _markers.Count;
        for (var n = 0; n < count && rays < OcclusionRaysPerFrame; n++)
        {
            var marker = _markers[(_occlusionCursor + n) % count];
            if (marker.Occlusion != MarkerOcclusion.Physics || space is null)
                continue;
            var point = marker.GlobalPosition + marker.WorldOffset;
            marker.IsOccluded = space.RayCast(cameraPosition, point, out _, marker.OcclusionMask, NearestBody(marker), hitFromInside: false);
            rays++;
            if (n + 1 == count || rays == OcclusionRaysPerFrame)
                _occlusionCursor = (_occlusionCursor + n + 1) % Math.Max(1, count);
        }

        var fadeStep = OcclusionFadeSeconds > 0f ? delta / OcclusionFadeSeconds : 1f;
        for (var i = 0; i < count; i++)
        {
            var marker = _markers[i];
            var point = marker.GlobalPosition + marker.WorldOffset;
            var distance = Vector3.Distance(cameraPosition, point);
            marker.Distance = distance;
            if (marker.Occlusion != MarkerOcclusion.Physics)
            {
                marker.IsOccluded = false;
                marker.OcclusionAlpha = 1f;
            }
            else
            {
                marker.OcclusionAlpha = Math.Clamp(marker.OcclusionAlpha + (marker.IsOccluded ? -fadeStep : fadeStep), 0f, 1f);
            }

            var inRange = marker.MaxDistance <= 0f || distance <= marker.MaxDistance;
            var fade = marker.MaxDistance > 0f && marker.FadeDistance > 0f
                ? Math.Clamp((marker.MaxDistance - distance) / marker.FadeDistance, 0f, 1f)
                : 1f;
            var scale = marker.ScaleWithDistance && distance > marker.ReferenceDistance
                ? Math.Clamp(marker.ReferenceDistance / distance, marker.MinScale, 1f)
                : 1f;

            var behind = Vector3.Dot(forward, point - cameraPosition) < camera.Near;
            var pixel = Camera3D.Project(viewProjection, point, viewportSize) * toLayer + marker.ScreenOffset * dpRatio;
            var onScreen = !behind && pixel.X >= 0 && pixel.Y >= 0 && pixel.X <= layerSize.X && pixel.Y <= layerSize.Y;
            var edge = false;
            var direction = pixel - center;
            if (behind)
                direction = -direction; // the projection mirrors points behind the camera through the centre
            if (direction.LengthSquared() < 1e-6f)
                direction = new Vector2(0f, 1f);
            marker.EdgeAngle = MathF.Atan2(direction.Y, direction.X) * (180f / MathF.PI);
            if (!onScreen && marker.Offscreen == MarkerOffscreen.ClampToEdge)
            {
                var margin = marker.EdgeMargin * dpRatio;
                var half = Vector2.Max(center - new Vector2(margin), Vector2.One);
                var t = MathF.Min(half.X / MathF.Max(MathF.Abs(direction.X), 1e-6f), half.Y / MathF.Max(MathF.Abs(direction.Y), 1e-6f));
                pixel = center + direction * t;
                edge = true;
            }

            marker.IsOnScreen = onScreen;
            marker.ScreenPosition = pixel;
            var visible = inRange && (onScreen || edge) && marker.OcclusionAlpha > 0f;
            marker.SetShown(visible && !marker.IsOccluded);

            marker.Row = new MarkerRow(pixel.X, pixel.Y, scale, fade * marker.OcclusionAlpha, visible, edge, marker.EdgeAngle,
                distance, marker.Kind, marker.ShownText, marker.ShownSubtext, marker.Value, marker.Icon);
            if (marker.HasTarget)
                MoveTarget(marker, pixel, scale, visible ? fade * marker.OcclusionAlpha : 0f);
        }

        if (_sinceRefresh >= ContentRefreshSeconds || ContentChanged() || !MoveRowElements())
            Rebuild();
    }

    // Rows in order (farthest first), the model dirtied: RmlUi re-evaluates every row's views on its next update.
    private void Rebuild()
    {
        Rebuilds++;
        _sinceRefresh = 0f;
        _rows.Clear();
        _rowMarkers.Clear();
        for (var i = 0; i < _markers.Count; i++)
            if (!_markers[i].HasTarget)
                _rowMarkers.Add(_markers[i]);
        if (SortByDistance && _rowMarkers.Count > 1)
            NoAllocSort.Sort(_rowMarkers, default(FarthestFirst));
        for (var r = 0; r < _rowMarkers.Count; r++)
            _rows.Add(_rowMarkers[r].Row);
        _model!.Dirty(ModelName);
    }

    // True when the rows' markers or anything shown other than position, scale and opacity changed since the last rebuild.
    private bool ContentChanged()
    {
        var count = 0;
        for (var i = 0; i < _markers.Count; i++)
            if (!_markers[i].HasTarget)
                count++;
        if (count != _rowMarkers.Count)
            return true;
        for (var r = 0; r < _rowMarkers.Count; r++)
        {
            var marker = _rowMarkers[r];
            if (marker.HostStamp != _stamp || marker.HasTarget)
                return true;
            var now = marker.Row;
            var bound = _rows[r];
            if (now.Visible != bound.Visible || now.Edge != bound.Edge || now.Value != bound.Value
                || !ReferenceEquals(now.Kind, bound.Kind) || !ReferenceEquals(now.Text, bound.Text)
                || !ReferenceEquals(now.Subtext, bound.Subtext) || !ReferenceEquals(now.Icon, bound.Icon)
                || (now.Edge && MathF.Abs(now.Angle - bound.Angle) > 2f))
                return true;
        }

        return false;
    }

    // Moves the generated row elements to this frame's positions; false when the document's rows are not as expected.
    private bool MoveRowElements()
    {
        if (_rowMarkers.Count == 0)
            return true;
        if (!IsLoaded)
            return false;
        var first = Document.QuerySelector(".marker");
        if (first.IsNull)
            return false;
        var parent = first.Parent;
        // RmlUi keeps the data-for template (hidden) after the rows it generated.
        var childCount = parent.ChildCount;
        if (childCount < _rowMarkers.Count + 1)
            return false;
        Span<char> buffer = stackalloc char[96];
        for (var r = 0; r < _rowMarkers.Count; r++)
        {
            var element = parent.GetChild(r);
            if (element.IsNull)
                return false;
            var now = _rowMarkers[r].Row;
            var written = 0;
            Append(buffer, ref written, "translate(");
            Append(buffer, ref written, now.X);
            Append(buffer, ref written, "px, ");
            Append(buffer, ref written, now.Y);
            Append(buffer, ref written, "px) scale(");
            Append(buffer, ref written, now.Scale);
            Append(buffer, ref written, ")");
            element.SetProperty("transform", buffer[..written]);
            written = 0;
            Append(buffer, ref written, now.Opacity);
            element.SetProperty("opacity", buffer[..written]);
            _rows[r] = now; // the model reads current values when it is next dirtied
        }

        return true;
    }

    // Unclaimed markers belong to the first host whose camera shares their viewport.
    private bool Owns(UiMarker3D marker, Camera3D? camera)
    {
        if (!marker.Host.IsEmpty)
            return ReferenceEquals(marker.GetNodeOrNull(marker.Host), this);
        var viewport = camera?.GetViewport() ?? GetViewport();
        if (!ReferenceEquals(marker.GetViewport(), viewport))
            return false;
        var hosts = Tree!.GetNodesInGroup(HostGroup);
        for (var i = 0; i < hosts.Count; i++)
            if (hosts[i] is UiMarkerHost host)
            {
                var hostViewport = (host.Camera.IsEmpty ? null : host.GetNodeOrNull<Camera3D>(host.Camera)?.GetViewport()) ?? host.GetViewport();
                if (ReferenceEquals(hostViewport, viewport))
                    return ReferenceEquals(host, this);
            }

        return false;
    }

    private static CollisionObject3D? NearestBody(Node node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n is CollisionObject3D body)
                return body;
        return null;
    }

    // Element mode: translate the target element, formatted on the stack.
    private static void MoveTarget(UiMarker3D marker, Vector2 pixel, float scale, float opacity)
    {
        if (marker.GetNodeOrNull<UiDocument>(marker.TargetDocument) is not { IsLoaded: true } document)
            return;
        var element = document.FindLive(marker.TargetElementId);
        if (element.IsNull)
            return;
        Span<char> buffer = stackalloc char[96];
        var written = 0;
        Append(buffer, ref written, "translate(");
        Append(buffer, ref written, pixel.X);
        Append(buffer, ref written, "px, ");
        Append(buffer, ref written, pixel.Y);
        Append(buffer, ref written, "px) scale(");
        Append(buffer, ref written, scale);
        Append(buffer, ref written, ")");
        element.SetProperty("transform", buffer[..written]);
        written = 0;
        Append(buffer, ref written, opacity);
        element.SetProperty("opacity", buffer[..written]);
    }

    private static void Append(Span<char> buffer, ref int written, ReadOnlySpan<char> text)
    {
        text.CopyTo(buffer[written..]);
        written += text.Length;
    }

    private static void Append(Span<char> buffer, ref int written, float value)
    {
        value.TryFormat(buffer[written..], out var n, "0.##", System.Globalization.CultureInfo.InvariantCulture);
        written += n;
    }
}

/// <summary>One row of <see cref="UiMarkerHost"/>'s <c>markers</c> model.</summary>
internal readonly record struct MarkerRow(float X, float Y, float Scale, float Opacity, bool Visible, bool Edge, float Angle,
    float Distance, string Kind, string Text, string Subtext, float Value, string Icon);

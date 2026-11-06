using System.Numerics;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The document that draws <see cref="UiMarker3D"/>s on a screen <see cref="UiLayer"/> (G11.4, ADR 0250): every marker of
/// its camera's viewport is one row of the <c>markers</c> data model (<c>data-for="m : markers"</c>), so a hundred
/// nameplates are one document with no per-marker documents, model names or created elements. Each frame, just before
/// the layer's context updates, it projects the markers, applies distance fade and scale, clamps waypoints to the
/// edge, casts up to <see cref="OcclusionRaysPerFrame"/> occlusion rays round robin, sorts the rows (nearer on top)
/// and dirties the model once. Rows hold the markers' own strings, so a steady frame allocates nothing.
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
    private readonly struct FarthestFirst : IComparer<MarkerRow>
    {
        public int Compare(MarkerRow a, MarkerRow b) => b.Distance.CompareTo(a.Distance);
    }

    private readonly List<MarkerRow> _rows = [];
    private readonly List<UiMarker3D> _markers = [];
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

    /// <summary>The markers drawn as rows in the last frame (element-mode markers included).</summary>
    public IReadOnlyList<UiMarker3D> Markers => _markers;

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
        _rows.Clear();
        _markers.Clear();
        var all = tree.GetNodesInGroup(UiMarker3D.Group);
        if (camera is null || !camera.IsInsideTree)
        {
            for (var i = 0; i < all.Count; i++)
                if (all[i] is UiMarker3D hidden && Owns(hidden, camera))
                    hidden.SetShown(false);
            _model!.Dirty(ModelName);
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
                _markers.Add(marker);

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

            if (marker.HasTarget)
            {
                MoveTarget(marker, pixel, scale, visible ? fade * marker.OcclusionAlpha : 0f);
                continue;
            }

            _rows.Add(new MarkerRow(pixel.X, pixel.Y, scale, fade * marker.OcclusionAlpha, visible, edge, marker.EdgeAngle,
                distance, marker.Kind, marker.ShownText, marker.ShownSubtext, marker.Value, marker.Icon));
        }

        if (SortByDistance && _rows.Count > 1)
            NoAllocSort.Sort(_rows, default(FarthestFirst));
        _model!.Dirty(ModelName);
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

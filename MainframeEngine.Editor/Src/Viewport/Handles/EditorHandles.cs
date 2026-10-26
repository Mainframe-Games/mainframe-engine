using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>How a handle is drawn: a solid-looking square, or a hollow one (a value that is not set yet, such as an unlimited distance).</summary>
public enum HandleStyle
{
    Square,
    Hollow,
}

/// <summary>
/// One draggable point of a selected node (editor-viewport-tools G7.3): world space in 3D; in 2D x, y in pixels and z = 0.
/// <paramref name="DragAxis"/> is the line the drag follows (unit; fixed when the drag starts).
/// </summary>
public readonly record struct EditorHandle(int Id, Vector3 Position, Vector3 DragAxis, HandleStyle Style = HandleStyle.Square);

/// <summary>
/// What a drag tells <see cref="IHandleProvider.Drag"/>: where the node and the handle were when the drag started, the
/// drag axis, how far along it the mouse is now, and the mouse ray (for providers that intersect their own planes, such
/// as angle handles). Values snap with <see cref="SnapDistance"/> and <see cref="SnapAngle"/> when the toolbar's Snap is
/// on.
/// </summary>
public readonly struct HandleDrag
{
    /// <summary>The node's global position at drag start (2D: x, y, 0).</summary>
    public Vector3 NodeOrigin { get; init; }

    /// <summary>The handle's position at drag start.</summary>
    public Vector3 HandleStart { get; init; }

    /// <summary>The handle's <see cref="EditorHandle.DragAxis"/> at drag start (unit).</summary>
    public Vector3 Axis { get; init; }

    /// <summary>
    /// The signed distance from <see cref="NodeOrigin"/> along <see cref="Axis"/> of the dragged point: the handle's own
    /// distance at drag start plus how far the mouse moved along the axis since (no jump when grabbed off-centre).
    /// </summary>
    public float Along { get; init; }

    /// <summary>The dragged point on the axis line (world): <see cref="NodeOrigin"/> + <see cref="Axis"/> × <see cref="Along"/> plus the handle's offset across the axis.</summary>
    public Vector3 Point { get; init; }

    /// <summary>The mouse ray (world).</summary>
    public Vector3 RayOrigin { get; init; }

    /// <summary>The mouse ray's direction (unit).</summary>
    public Vector3 RayDirection { get; init; }

    /// <summary>The toolbar's Snap is on.</summary>
    public bool Snap { get; init; }

    /// <summary>The move step (metres in 3D; in 2D the move step in pixels).</summary>
    public float SnapStep { get; init; }

    /// <summary>The rotate step in degrees.</summary>
    public float AngleStep { get; init; }

    /// <summary>The view's editor camera.</summary>
    public EditorCamera Camera { get; init; }

    /// <summary>The drag happens in the 2D view.</summary>
    public bool Is2D { get; init; }

    /// <summary><paramref name="distance"/> snapped to the move step when Snap is on.</summary>
    public float SnapDistance(float distance) => Snap ? TransformGizmo.SnapTo(distance, SnapStep) : distance;

    /// <summary><paramref name="degrees"/> snapped to the rotate step when Snap is on.</summary>
    public float SnapAngle(float degrees) => Snap ? TransformGizmo.SnapTo(degrees, AngleStep) : degrees;
}

/// <summary>
/// Declares a node type's viewport handles (G7.3; the handle half of Godot's <c>EditorNode3DGizmoPlugin</c>:
/// <c>_get_handle_name</c>, <c>_get_handle_value</c>, <c>_set_handle</c>, <c>_commit_handle</c>). The editor draws the
/// handles of every selected node, hit-tests them before the transform gizmo, captures the <see cref="Properties"/> of
/// the grabbed handle, lets <see cref="Drag"/> apply the drag live and commits one undo entry on release (Esc or a right
/// click restores the values). Register with <see cref="HandleProviderAttribute"/>: the editor finds providers in every
/// loaded assembly that references it, game assemblies included, and the nearest base type's provider wins.
/// </summary>
public interface IHandleProvider
{
    /// <summary>Adds the handles of <paramref name="node"/> in this view. Called each frame per selected node: must not allocate.</summary>
    void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles);

    /// <summary>The exported properties a drag of handle <paramref name="id"/> may change (captured for undo and cancel).</summary>
    ReadOnlySpan<string> Properties(Node node, int id);

    /// <summary>The label next to the mouse, e.g. "Range 4.20 m" (only called while the handle is hovered or dragged).</summary>
    string Describe(Node node, int id);

    /// <summary>Applies a drag live through the node's typed setters (the editor commits or cancels).</summary>
    void Drag(Node node, int id, in HandleDrag drag);

    /// <summary>
    /// Draws what the handles edit (a range circle, a limit arc) into the view's overlay lines while the node is selected.
    /// Called each frame: must not allocate. Default: nothing (the editor already draws light and audio ranges).
    /// </summary>
    void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
    }
}

/// <summary>Registers an <see cref="IHandleProvider"/> (public parameterless constructor) for <see cref="NodeType"/> and its subclasses.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HandleProviderAttribute(Type nodeType) : Attribute
{
    public Type NodeType { get; } = nodeType ?? throw new ArgumentNullException(nameof(nodeType));
}

/// <summary>
/// Finds the <see cref="IHandleProvider"/> of a node type: the nearest base type's <see cref="HandleProviderAttribute"/>
/// class (editor built-ins, editor plugins and game assemblies that reference the editor), else the
/// <see cref="HintHandleProvider"/> when the type has <c>[Export(Handle = …)]</c> floats, else none. Results are cached per
/// type and forgotten on <see cref="Reset"/> (code reload; game types unload).
/// </summary>
public static class HandleProviders
{
    private static readonly Lock Gate = new();
    private static Dictionary<Type, Type>? _byNode;
    private static readonly Dictionary<Type, IHandleProvider?> Cache = [];

    /// <summary>The provider for <paramref name="type"/>, or null.</summary>
    public static IHandleProvider? Find(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out var cached))
                return cached;
            _byNode ??= Scan();
            IHandleProvider? found = null;
            for (var t = type; t is not null && found is null; t = t.BaseType)
                if (_byNode.TryGetValue(t, out var providerType))
                    found = (IHandleProvider?)Activator.CreateInstance(providerType);
            found ??= HintHandleProvider.HasHints(type) ? HintHandleProvider.Instance : null;
            Cache[type] = found;
            return found;
        }
    }

    /// <summary>Forgets what was scanned (after loading or unloading a game assembly).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _byNode = null;
            Cache.Clear();
            HintHandleProvider.Reset();
        }
    }

    private static Dictionary<Type, Type> Scan()
    {
        var result = new Dictionary<Type, Type>();
        foreach (var type in EditorPluginTypes.All())
            if (typeof(IHandleProvider).IsAssignableFrom(type) && !type.IsAbstract &&
                type.GetCustomAttribute<HandleProviderAttribute>() is { } attribute)
                result[attribute.NodeType] = type;
        return result;
    }

    /// <summary>
    /// <paramref name="value"/> clamped to the <c>[Export(Range = "min,max")]</c> of <paramref name="property"/> on
    /// <paramref name="node"/>'s type (unchanged without a range).
    /// </summary>
    public static float Clamp(Node node, string property, float value)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (TypeRegistry.GetNearest(node.GetType())?.FindProperty(property) is not { } info)
            return value;
        // The hint is a string: parsed once per property, since drags clamp on every mouse move.
        var range = Ranges.GetValue(info, static p => p.Hints.TryGetRange(out var min, out var max, out _)
            ? new StrongBox<(float Min, float Max)>(((float)min, (float)max))
            : NoRange);
        return ReferenceEquals(range, NoRange) ? value : Math.Clamp(value, range.Value.Min, range.Value.Max);
    }

    private static readonly StrongBox<(float Min, float Max)> NoRange = new();

    // Weak keys: game types' property infos go away with a code reload.
    private static readonly ConditionalWeakTable<ExportPropertyInfo, StrongBox<(float Min, float Max)>> Ranges = new();
}

/// <summary>The types of the loaded assemblies that can hold editor plugins: the editor's and those that reference it.</summary>
internal static class EditorPluginTypes
{
    public static IEnumerable<Type> All()
    {
        var editor = typeof(IHandleProvider).Assembly;
        var editorName = editor.GetName().Name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || (assembly != editor && !assembly.GetReferencedAssemblies().Any(a => a.Name == editorName)))
                continue;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = [.. e.Types.OfType<Type>()];
            }

            foreach (var type in types)
                yield return type;
        }
    }
}

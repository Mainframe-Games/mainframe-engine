using System.Globalization;
using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The handles of <c>[Export(Handle = …)]</c> floats (G7.3), for game nodes, which do not reference the editor: a
/// <see cref="HandleHint.Radius"/> is a circle around the node's origin with a handle on the camera's right (3D) or +X
/// (2D); a <see cref="HandleHint.Length"/> a handle along the node's forward axis (−Z in 3D, +X in 2D). Values clamp to
/// the member's <c>Range</c> and snap to the move step. Explicit <see cref="IHandleProvider"/>s win over hints.
/// </summary>
public sealed class HintHandleProvider : IHandleProvider
{
    private static readonly Vector4 HintColor = new(0.55f, 0.85f, 1f, 0.6f);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Type, Hinted[]> ByType = [];

    /// <summary>The shared instance (stateless).</summary>
    public static HintHandleProvider Instance { get; } = new();

    private readonly record struct Hinted(ExportPropertyInfo Property, HandleHint Hint, string[] Names);

    /// <summary>True when <paramref name="type"/> has a float export with a handle hint.</summary>
    public static bool HasHints(Type type) => For(type).Length > 0;

    internal static void Reset()
    {
        lock (Gate)
            ByType.Clear();
    }

    private static Hinted[] For(Type type)
    {
        lock (Gate)
        {
            if (ByType.TryGetValue(type, out var cached))
                return cached;
            var list = new List<Hinted>();
            if (TypeRegistry.GetNearest(type) is { } info)
                foreach (var property in info.Properties)
                    if (property.Hints.Handle != HandleHint.None && property.ValueType == typeof(float))
                        list.Add(new Hinted(property, property.Hints.Handle, [property.Name]));
            var result = list.ToArray();
            ByType[type] = result;
            return result;
        }
    }

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(handles);
        var hinted = For(node.GetType());
        for (var i = 0; i < hinted.Length; i++)
        {
            if (!hinted[i].Property.TryGetFloat(node, out var value) || !TryFrame(node, camera, hinted[i].Hint, out var origin, out var axis))
                continue;
            handles.Add(new EditorHandle(i, origin + axis * value, axis));
        }
    }

    public ReadOnlySpan<string> Properties(Node node, int id)
    {
        ArgumentNullException.ThrowIfNull(node);
        var hinted = For(node.GetType());
        return (uint)id < (uint)hinted.Length ? hinted[id].Names : [];
    }

    public string Describe(Node node, int id)
    {
        ArgumentNullException.ThrowIfNull(node);
        var hinted = For(node.GetType());
        if ((uint)id >= (uint)hinted.Length || !hinted[id].Property.TryGetFloat(node, out var value))
            return "";
        var unit = node is Node2D ? " px" : " m";
        return $"{InspectorProperty.Humanize(hinted[id].Property.Name)} {value.ToString("0.00", CultureInfo.InvariantCulture)}{unit}";
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        ArgumentNullException.ThrowIfNull(node);
        var hinted = For(node.GetType());
        if ((uint)id >= (uint)hinted.Length)
            return;
        var property = hinted[id].Property;
        var value = drag.SnapDistance(MathF.Max(0f, drag.Along));
        property.TrySetFloat(node, HandleProviders.Clamp(node, property.Name, value));
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(lines);
        var hinted = For(node.GetType());
        for (var i = 0; i < hinted.Length; i++)
        {
            if (!hinted[i].Property.TryGetFloat(node, out var value) || !TryFrame(node, camera, hinted[i].Hint, out var origin, out var axis))
                continue;
            if (hinted[i].Hint == HandleHint.Radius)
                HandleDrawing.Circle(lines, origin, camera.Right, camera.Up, value, HintColor);
            else
                lines.AddLine(origin, origin + axis * value, HintColor);
        }
    }

    // The handle's origin and axis: the camera's right (3D) or +X (2D) for a radius, the node's forward for a length.
    private static bool TryFrame(Node node, EditorCamera camera, HandleHint hint, out Vector3 origin, out Vector3 axis)
    {
        switch (node)
        {
            case Node3D n3:
                origin = n3.GlobalPosition;
                axis = hint == HandleHint.Length ? n3.GlobalForward : camera.Right;
                return true;
            case Node2D n2:
                var global = n2.GlobalTransform;
                origin = new Vector3(global.Origin, 0f);
                var x = global.X.LengthSquared() > 1e-12f ? Vector2.Normalize(global.X) : Vector2.UnitX;
                axis = hint == HandleHint.Length ? new Vector3(x, 0f) : Vector3.UnitX;
                return true;
            default:
                origin = axis = Vector3.Zero;
                return false;
        }
    }
}

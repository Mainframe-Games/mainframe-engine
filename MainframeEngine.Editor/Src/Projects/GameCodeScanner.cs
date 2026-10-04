using System.Reflection;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Finds out whether an open scene depends on game code, so a code reload re-creates only the scenes that must be
/// re-created (and keeps the undo history of the others): a node or a resource (exported values, nested resources) of a
/// type from the game assembly, or a <see cref="MissingNode"/>/<see cref="MissingResource"/> the new build may
/// provide.
/// </summary>
public static class GameCodeScanner
{
    /// <summary>
    /// True when <paramref name="root"/>'s tree (every node, instanced sub-scenes included) holds anything of
    /// <paramref name="assembly"/> (null: only missing types count).
    /// </summary>
    public static bool Uses(Node root, Assembly? assembly)
    {
        ArgumentNullException.ThrowIfNull(root);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is MissingNode || IsFrom(node.GetType(), assembly) || UsesResources(node, assembly, seen, 0))
                return true;
            for (var i = node.ChildCount - 1; i >= 0; i--)
                stack.Push(node.GetChild(i));
        }

        return false;
    }

    private static bool IsFrom(Type type, Assembly? assembly) => assembly is not null && type.Assembly == assembly;

    private static bool UsesResources(object owner, Assembly? assembly, HashSet<object> seen, int depth)
    {
        if (depth > 16 || TypeRegistry.GetNearest(owner.GetType()) is not { } info)
            return false;
        foreach (var property in info.Properties)
        {
            if (!typeof(Resource).IsAssignableFrom(Nullable.GetUnderlyingType(property.ValueType) ?? property.ValueType) &&
                !property.ValueType.IsArray && !property.ValueType.IsGenericType)
                continue;
            object? value;
            try
            {
                value = property.GetValue(owner);
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                continue;
            }

            if (value is Resource resource)
            {
                if (Uses(resource, assembly, seen, depth))
                    return true;
            }
            else if (value is System.Collections.IEnumerable items and not string)
            {
                foreach (var item in items)
                    if (item is Resource element && Uses(element, assembly, seen, depth))
                        return true;
            }
        }

        return false;
    }

    private static bool Uses(Resource resource, Assembly? assembly, HashSet<object> seen, int depth)
    {
        if (!seen.Add(resource))
            return false;
        if (resource is MissingResource || IsFrom(resource.GetType(), assembly))
            return true;
        return UsesResources(resource, assembly, seen, depth + 1);
    }
}

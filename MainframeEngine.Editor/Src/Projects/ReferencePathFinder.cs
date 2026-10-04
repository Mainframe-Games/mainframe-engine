using System.Reflection;

namespace MainframeEngine.Editor;

/// <summary>
/// Code-reload leak diagnostics: walks the object graph by reflection (breadth first, so the path is short) from the
/// given roots and the static fields of the engine and editor assemblies, and returns the reference path to the first
/// object of an assembly (an instance of one of its types, a delegate into it, or its type objects). Slow (seconds on a
/// large editor): the editor runs it only when an unloaded game assembly stayed alive, to name the culprit.
/// </summary>
public static class ReferencePathFinder
{
    public static string? PathTo(Assembly target, params object[] roots)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, string Path)>();
        foreach (var root in roots)
            queue.Enqueue((root, root.GetType().Name));
        foreach (var assembly in new[] { typeof(Node).Assembly, typeof(EditorWorkspace).Assembly })
            foreach (var type in SafeTypes(assembly))
            {
                if (type.ContainsGenericParameters)
                    continue;
                FieldInfo[] statics;
                try
                {
                    statics = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue; // a type whose field types cannot load (optional dependencies)
                }

                foreach (var field in statics)
                {
                    object? value;
                    try
                    {
                        if (field.IsLiteral || field.FieldType.IsPrimitive)
                            continue;
                        value = field.GetValue(null);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (value is not null)
                        queue.Enqueue((value, $"{type.Name}.{field.Name}"));
                }
            }

        while (queue.Count > 0)
        {
            var (value, path) = queue.Dequeue();
            if (value is Pointer || !seen.Add(value) || path.Length > 2000)
                continue;
            var type = value.GetType();
            if (type.Assembly == target || (value is Type t && t.Assembly == target) || ReferenceEquals(value, target) ||
                (value is MemberInfo m && m.Module.Assembly == target) || (value is Delegate d && d.Method.Module.Assembly == target))
                return path + $" [{type.FullName}]";
            if (type.IsPrimitive || value is string || value is Type || value is Assembly || value is Module || value is MemberInfo)
                continue;
            if (value is Array array && !type.GetElementType()!.IsPrimitive)
            {
                var i = 0;
                foreach (var item in array)
                {
                    if (item is not null)
                        queue.Enqueue((item, $"{path}[{i}]"));
                    i++;
                }

                continue;
            }

            for (var t2 = type; t2 is not null && t2 != typeof(object); t2 = t2.BaseType)
                foreach (var field in Fields(t2))
                {
                    object? child;
                    try
                    {
                        if (field.FieldType.IsPrimitive || field.FieldType.IsPointer || field.FieldType.IsByRefLike)
                            continue;
                        child = field.GetValue(value);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (child is not null)
                        queue.Enqueue((child, $"{path}.{field.Name}"));
                }
        }

        return null;
    }

    private static FieldInfo[] Fields(Type type)
    {
        try
        {
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return [.. e.Types.OfType<Type>()];
        }
    }
}

namespace MainframeEngine.Serialization;

/// <summary>
/// Equality for resource-valued properties when diffing (defaults, nested-instance overrides): external
/// resources are equal when they are the same file (UID); inline resources are equal when they have the same
/// type and equal exported values, so a reloaded sub-scene's inline resources are not mistaken for overrides.
/// </summary>
internal static class ResourceValueEquality
{
    private const int MaxDepth = 16;

    [ThreadStatic]
    private static int _depth;

    public static bool AreEqual(Resource? a, Resource? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.GetType() != b.GetType())
            return false;
        if (a.IsExternal || b.IsExternal)
            return a.Uid is not null && string.Equals(a.Uid, b.Uid, StringComparison.Ordinal);
        if (_depth >= MaxDepth || TypeRegistry.Get(a.GetType()) is not { } info)
            return false; // cyclic or unregistered: only identical instances are equal

        _depth++;
        try
        {
            foreach (var property in info.Properties)
                if (!property.ValueEquals(a, b))
                    return false;
            return true;
        }
        finally
        {
            _depth--;
        }
    }
}

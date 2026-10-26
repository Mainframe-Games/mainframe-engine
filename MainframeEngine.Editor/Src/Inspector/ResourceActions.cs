using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The rules of the inspector's resource More menu (editor-viewport-tools G7.2): which of Save as .mres, Make Unique
/// (shallow, deep), Open and Show in FileSystem apply to the resource in a slot, and why one does not.
/// </summary>
public static class ResourceActions
{
    /// <summary>An external resource whose data lives in its source file (a texture, a model, a sound, a LUT), not a <c>.mres</c>.</summary>
    public static bool IsImported(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.ResourcePath is { } path && !path.EndsWith(".mres", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A <c>.mres</c> file resource (Open edits it on its own).</summary>
    public static bool IsResourceFile(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.ResourcePath is { } path && path.EndsWith(".mres", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Save as .mres applies: an inline resource of a type a <c>.mres</c> can hold.</summary>
    public static bool CanSaveAsFile(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return !resource.IsExternal && resource is not (PackedScene or MissingResource);
    }

    /// <summary>
    /// Make Unique applies: a <c>.mres</c> file resource, or an inline one the scene uses in more than one slot
    /// (<paramref name="inlineUses"/>). <paramref name="reason"/> says why not (the menu item's tooltip).
    /// </summary>
    public static bool CanMakeUnique(Resource resource, int inlineUses, out string reason)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is PackedScene or MissingResource)
        {
            reason = $"A {resource.GetType().Name} cannot be copied here.";
            return false;
        }

        if (IsImported(resource))
        {
            reason = $"{Path.GetFileName(resource.ResourcePath)} is imported: its data lives in the source file. Make Unique copies .mres files only.";
            return false;
        }

        if (!resource.IsExternal && inlineUses <= 1)
        {
            reason = "This inline resource is used by this slot only: it is already unique.";
            return false;
        }

        reason = resource.IsExternal
            ? $"Make Unique — an inline copy of {Path.GetFileName(resource.ResourcePath)} for this slot; the file and its other users stay"
            : $"Make Unique — an inline copy for this slot; the other {inlineUses - 1} slot(s) keep sharing the original";
        return true;
    }

    /// <summary>True when <paramref name="resource"/> holds inline sub-resources, directly or in a list or array (Make Unique (Deep) copies them too).</summary>
    public static bool HasInlineSubResources(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (TypeRegistry.GetNearest(resource.GetType()) is not { } info)
            return false;
        foreach (var property in info.Properties)
        {
            switch (property.GetValue(resource))
            {
                case Resource { IsExternal: false }:
                    return true;
                case System.Collections.IList list:
                    foreach (var element in list)
                        if (element is Resource { IsExternal: false })
                            return true;
                    break;
            }
        }

        return false;
    }
}

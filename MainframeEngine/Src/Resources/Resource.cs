using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// Shared data referenced by nodes and other resources (Godot's <c>Resource</c>): skies now; meshes, materials,
/// physics shapes, audio streams and UI themes in later milestones. Resources are either <i>inline</i>
/// (stored inside the scene or resource file that uses them) or <i>external</i> (their own <c>.mres</c> file,
/// referenced by UID and loaded once through <see cref="ResourceLoader"/>).
/// </summary>
/// <remarks>
/// Serialized members are marked <see cref="ExportAttribute"/>, exactly as on nodes. Like nodes, constructors
/// must be cheap: the registry creates a pristine instance of each type to learn its defaults.
/// <see cref="ResourceLoader"/> reference-counts external resources (<see cref="Release"/>) and calls
/// <see cref="OnUnloaded"/> when the last reference goes; GPU-backed resources release their objects there.
/// </remarks>
public abstract class Resource
{
    private int _referenceCount;

    /// <summary>Optional display name (shown by the editor).</summary>
    [Export]
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>The file this resource was loaded from or saved to; null for inline resources.</summary>
    public string? ResourcePath { get; internal set; }

    /// <summary>Stable id of the resource file (<c>res_xxxxxxxx</c>); null for inline resources.</summary>
    public string? Uid { get; internal set; }

    /// <summary>True when the resource lives in its own file (referenced by UID from scenes).</summary>
    public bool IsExternal => ResourcePath is not null;

    /// <summary>Outstanding <see cref="ResourceLoader.Load{T}"/> references.</summary>
    public int ReferenceCount => _referenceCount;

    /// <summary>Raised by <see cref="EmitChanged"/> when a property that users of the resource care about changes.</summary>
    public event Action? Changed;

    protected void EmitChanged() => Changed?.Invoke();

    /// <summary>
    /// Drops one reference obtained from <see cref="ResourceLoader.Load{T}"/>. At zero the resource leaves the
    /// loader's cache and <see cref="OnUnloaded"/> runs.
    /// </summary>
    public void Release()
    {
        if (_referenceCount <= 0)
            throw new InvalidOperationException($"'{ResourcePath ?? GetType().Name}' has no references to release.");
        if (--_referenceCount == 0)
            ResourceLoader.OnReleased(this);
    }

    internal void AddReference() => _referenceCount++;

    /// <summary>The file's resource table (external references taken while loading), released on unload.</summary>
    internal Serialization.ResourceTable? Dependencies { get; set; }

    internal void ClearReferences() => _referenceCount = 0;

    /// <summary>Set once the loader has unloaded the resource (<see cref="OnUnloaded"/> runs once).</summary>
    internal bool Unloaded { get; set; }

    /// <summary>Called when the loader drops the resource (last <see cref="Release"/> or cache clear).</summary>
    protected internal virtual void OnUnloaded()
    {
    }

    /// <summary>
    /// A new, unsaved copy with the same exported values. Nested resources are shared unless
    /// <paramref name="deep"/>, which duplicates inline sub-resources too (external ones stay shared).
    /// </summary>
    public Resource Duplicate(bool deep = false)
    {
        var info = TypeRegistry.Get(GetType())
                   ?? throw new InvalidOperationException($"{GetType().FullName} is not registered; it cannot be duplicated.");
        var copy = (Resource)info.CreateInstance();
        foreach (var property in info.Properties)
        {
            property.CopyValue(this, copy);
            if (deep && property.GetValue(copy) is Resource { IsExternal: false } nested)
                property.SetValue(copy, nested.Duplicate(deep: true));
        }

        return copy;
    }

    public override string ToString() => ResourcePath is null ? GetType().Name : $"{GetType().Name} ({ResourcePath})";
}

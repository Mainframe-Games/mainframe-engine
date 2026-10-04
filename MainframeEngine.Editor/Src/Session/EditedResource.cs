using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Where the inspector's edits go: the scene a node belongs to (<see cref="EditedScene"/>) or a resource file opened on
/// its own (<see cref="EditedResource"/>). Custom inspectors act through it so their edits can be undone.
/// </summary>
public interface IInspectorContext
{
    UndoRedo History { get; }

    /// <summary>Sets an exported property through <see cref="History"/>; <paramref name="mergeKey"/> merges a continuous edit.</summary>
    void SetProperty(object target, ExportPropertyInfo property, object? value, string? mergeKey = null);
}

/// <summary>
/// A resource file (<c>.mres</c>) opened in the inspector on its own — double-click in the FileSystem panel, or the
/// Project Settings' bus layout "Edit": its own undo history and Save (<see cref="ResourceSaver"/>, keeping its UID).
/// </summary>
public sealed class EditedResource : IInspectorContext, IDisposable
{
    private bool _disposed;

    public EditedResource(Resource resource, string filePath)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public Resource Resource { get; }

    public string FilePath { get; }

    public UndoRedo History { get; } = new();

    public bool IsDirty => History.IsDirty;

    public void SetProperty(object target, ExportPropertyInfo property, object? value, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        var old = property.GetValue(target);
        if (Equals(old, value))
            return;
        History.Commit(new SetPropertyAction(target, property, old, value), mergeKey: mergeKey);
    }

    /// <summary>Writes the resource back to <see cref="FilePath"/> (atomic; its UID is kept).</summary>
    public void Save()
    {
        ResourceSaver.Save(Resource, FilePath);
        History.MarkSaved();
    }

    /// <summary>Drops the loader reference taken when it was opened.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        History.Clear();
        if (Resource.ReferenceCount > 0)
            Resource.Release();
    }
}

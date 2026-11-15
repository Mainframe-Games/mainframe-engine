using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's live loop for files changed outside it (G1.18, docs/design/editor.md#files-changed-on-disk): a baked clip
/// written by <c>mfe anim bake</c>, a library edited by hand, a model's <c>.mfmeta</c> that now links a library. Fed the
/// changed project paths on the main thread (<see cref="ProjectFileSystem.TakeChangedFiles"/>):
/// <list type="bullet">
/// <item>a loaded <c>.mfres</c> reloads in place (<see cref="ResourceLoader.ReloadInPlace"/>), so open scenes see it, and
/// raises <see cref="Reloaded"/>; with unsaved edits in the editor (the inspected resource, or one a dirty scene edited in
/// place) it asks first, "Keep mine" or "Load from disk";</item>
/// <item>a loaded model whose sidecar changed is evicted and the clean scenes that instance it reopen (as code reload does);
/// a scene with unsaved changes gets a message instead.</item>
/// </list>
/// </summary>
public sealed class ResourceReloader
{
    private readonly EditorSession _session;
    private readonly Func<EditedResource?> _inspected;
    private readonly Action<MessageRequest> _ask;
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);

    /// <param name="session">The open scenes.</param>
    /// <param name="inspected">The resource open in the inspector on its own, if any.</param>
    /// <param name="ask">Shows a message (the workspace's <see cref="MessageDialog"/>).</param>
    public ResourceReloader(EditorSession session, Func<EditedResource?> inspected, Action<MessageRequest> ask)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inspected);
        ArgumentNullException.ThrowIfNull(ask);
        _session = session;
        _inspected = inspected;
        _ask = ask;
    }

    /// <summary>A loaded resource now holds what its file says (the same instance).</summary>
    public event Action<Resource>? Reloaded;

    /// <summary>Main thread: handles the project paths (<c>Assets/…</c>) of files that changed on disk.</summary>
    public void FilesChanged(IReadOnlyList<string> projectPaths)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        var reopen = new List<string>();
        foreach (var path in projectPaths)
        {
            if (path.EndsWith(SceneFormat.ResourceExtension, StringComparison.OrdinalIgnoreCase))
                ResourceChanged(path);
            else if (path.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase) && ResourceLoader.Evict(path[..^AssetDatabase.MetaExtension.Length]))
                reopen.Add(path[..^AssetDatabase.MetaExtension.Length]);
        }

        if (reopen.Count > 0)
            ReopenScenesUsing(reopen);
    }

    // ── Resource files ──────────────────────────────────────────────────────────────────────────────────────────

    private void ResourceChanged(string path)
    {
        if (HasUnsavedEdits(path, out var holder))
        {
            if (!_asking.Add(path))
                return; // already asking about this file
            _ask(new MessageRequest
            {
                Title = "File changed on disk",
                Icon = "ti ti-file-alert",
                Message = $"{path} changed on disk, but {holder} has unsaved changes to it.\n" +
                          "Keep your version (saving overwrites the file), or load the file and lose your changes?",
                Buttons = ["Keep mine", "Load from disk"],
                DefaultButton = 0,
                CancelButton = 0,
                Callback = (button, _) =>
                {
                    _asking.Remove(path);
                    if (button == 1)
                        LoadFromDisk(path);
                },
            });
            return;
        }

        Reload(path);
    }

    private void Reload(string path)
    {
        var result = ResourceLoader.ReloadInPlace(path);
        switch (result.Status)
        {
            case ResourceReloadStatus.Reloaded:
                Log.Info($"[Editor] Reloaded {path} (changed on disk).");
                Reloaded?.Invoke(result.Resource!);
                break;
            case ResourceReloadStatus.Failed:
                Log.Warning($"[Editor] {path} changed on disk but could not be read: {result.Error}");
                break;
        }
    }

    // "Load from disk": the file wins; the edits that held it are dropped.
    private void LoadFromDisk(string path)
    {
        if (_inspected() is { } inspected && SamePath(inspected.FilePath, path))
        {
            // Its undo steps point at values the file replaces; what is shown now is the file.
            inspected.History.Clear();
            inspected.History.MarkSaved();
        }
        foreach (var scene in _session.Scenes)
            if (scene.EditedResourceFiles.Any(r => SamePath(r.ResourcePath, path)))
                scene.ForgetEditedResourceFile(path);
        Reload(path);
    }

    private bool HasUnsavedEdits(string path, out string holder)
    {
        if (_inspected() is { IsDirty: true } inspected && SamePath(inspected.FilePath, path))
        {
            holder = "the inspector";
            return true;
        }

        foreach (var scene in _session.Scenes)
        {
            if (scene.IsDirty && scene.EditedResourceFiles.Any(r => SamePath(r.ResourcePath, path)))
            {
                holder = scene.DisplayName;
                return true;
            }
        }

        holder = "";
        return false;
    }

    private static bool SamePath(string? a, string b)
    {
        if (a is null)
            return false;
        var assets = AssetDatabase.Current;
        return string.Equals(assets.ToProjectPath(assets.ToAbsolutePath(a)), assets.ToProjectPath(assets.ToAbsolutePath(b)), StringComparison.Ordinal);
    }

    // ── Models ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void ReopenScenesUsing(List<string> models)
    {
        var active = _session.Active;
        var scenes = _session.Scenes.Where(s => Instances(s.Root, models)).ToList();
        var dirty = scenes.Where(s => s.IsDirty).ToList();
        if (dirty.Count > 0)
            _ask(new MessageRequest
            {
                Title = "Model changed on disk",
                Icon = "ti ti-refresh-alert",
                Message = $"{string.Join(", ", models)} changed on disk (import settings or linked animation libraries).\n" +
                          $"Save and reopen {string.Join(", ", dirty.Select(s => s.DisplayName))} to see the change.",
                Buttons = ["OK"],
            });

        // Last tab first, so recorded positions stay valid; resumed in ascending order, each goes back where it was.
        var clean = scenes.Where(s => !s.IsDirty).OrderByDescending(_session.IndexOf).ToList();
        var activeIndex = active is not null && clean.Contains(active) ? _session.IndexOf(active) : -1;
        var snapshots = new List<SceneSnapshot>();
        foreach (var scene in clean)
            snapshots.Add(_session.Suspend(scene));
        snapshots.Reverse();
        EditedScene? reactivate = null;
        foreach (var snapshot in snapshots)
        {
            try
            {
                var scene = _session.Resume(snapshot);
                if (snapshot.Index == activeIndex)
                    reactivate = scene;
                Log.Info($"[Editor] Reopened {scene.DisplayName}: {string.Join(", ", models)} changed on disk.");
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Log.Error($"[Editor] Could not reopen {snapshot.FilePath ?? "an untitled scene"} after its model changed: {e.Message}");
            }
        }

        if (reactivate is not null)
            _session.Activate(reactivate);
    }

    private static bool Instances(Node node, List<string> models)
    {
        foreach (var child in node.Children)
        {
            if (child.SceneFilePath is { } file && models.Any(m => SamePath(ResolveUid(file), m)))
                return true;
            if (Instances(child, models))
                return true;
        }

        return false;
    }

    private static string ResolveUid(string path) =>
        AssetUid.IsUid(path) && AssetDatabase.Current.GetPath(path) is { } resolved ? resolved : path;
}

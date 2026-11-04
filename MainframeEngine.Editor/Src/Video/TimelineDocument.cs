using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// An open <c>.mftimeline</c>: the current <see cref="Timeline"/> snapshot, its file, and its undo history in the
/// editor's <see cref="UndoRedo"/>. Every edit is a pure <see cref="TimelineEdits"/> function applied through
/// <see cref="Apply"/>: an edit that returns the same instance (nothing changed, or refused by a lock) records nothing.
/// Gestures re-apply their edit to the timeline as it was when they began (<see cref="ApplyFrom"/>) under one merge key,
/// so a drag is one undo step and passing over other clips on the way does not overwrite them.
/// </summary>
public sealed class TimelineDocument
{
    private readonly Dictionary<MediaRef, string> _resolved = [];
    private AssetDatabase? _resolvedWith;
    private bool _wasDirty;

    public TimelineDocument(Timeline timeline, string filePath, string projectRoot, int historyLimit = 256)
    {
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        FilePath = Path.GetFullPath(filePath);
        ProjectRoot = Path.GetFullPath(projectRoot);
        History = new UndoRedo(historyLimit);
        History.Changed += OnHistoryChanged;
    }

    public Timeline Timeline { get; private set; }

    public string FilePath { get; private set; }

    /// <summary>The game project the timeline belongs to: media paths are relative to it.</summary>
    public string ProjectRoot { get; }

    public UndoRedo History { get; }

    public bool IsDirty => History.IsDirty;

    /// <summary>Bumped on every change (edit, undo, redo): the preview and the canvases follow it.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>Raised when <see cref="IsDirty"/> flips.</summary>
    public event Action? DirtyChanged;

    /// <summary>Loads <paramref name="path"/> (throws <see cref="TimelineFormatException"/> for a file it cannot read).</summary>
    public static TimelineDocument Load(string path, string projectRoot, int historyLimit = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var timeline = TimelineSerializer.Deserialize(File.ReadAllText(path)); // disk-io: the editor opens a project file
        return new TimelineDocument(timeline, path, projectRoot, historyLimit);
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the current timeline as one undoable step named <paramref name="name"/>.
    /// Consecutive applies with the same <paramref name="mergeKey"/> merge until <see cref="UndoRedo.EndMerge"/>. False
    /// when the edit changed nothing.
    /// </summary>
    public bool Apply(string name, Func<Timeline, Timeline> edit, string? mergeKey = null) => ApplyFrom(Timeline, name, edit, mergeKey);

    /// <summary>
    /// Like <see cref="Apply"/>, but <paramref name="edit"/> is applied to <paramref name="from"/> (a gesture's starting
    /// timeline): each step of a drag replaces the previous one instead of adding to it.
    /// </summary>
    public bool ApplyFrom(Timeline from, string name, Func<Timeline, Timeline> edit, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(edit);
        var next = edit(from);
        if (ReferenceEquals(next, Timeline))
            return false;
        History.Commit(new TimelineEdit(this, name, Timeline, next), mergeKey: mergeKey);
        return true;
    }

    /// <summary>Writes the timeline to <see cref="FilePath"/> atomically and marks the history saved.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); // disk-io: the editor writes into the project
        TimelineSerializer.SaveAsync(Timeline, FilePath, CancellationToken.None).GetAwaiter().GetResult();
        AssetDatabase.Current.Register(Timeline.Uid, FilePath);
        History.MarkSaved();
    }

    /// <summary>The file moved (FileSystem panel, Save As): follow it.</summary>
    public void Rename(string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        FilePath = Path.GetFullPath(newPath);
    }

    /// <summary>
    /// The file ffmpeg reads for <paramref name="media"/>: its asset UID's current path when the project knows it (moved
    /// in the media pool), else its stored path, relative to the project.
    /// </summary>
    /// <remarks>
    /// Cached per media (the canvases ask every frame), until the next change, a new asset database or
    /// <see cref="ForgetResolvedPaths"/> (the media pool saw files move). Any thread: the preview's decoders ask from the pool.
    /// </remarks>
    public string Resolve(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var database = AssetDatabase.Current;
        lock (_resolved)
        {
            if (!ReferenceEquals(_resolvedWith, database))
            {
                _resolved.Clear();
                _resolvedWith = database;
            }

            if (_resolved.TryGetValue(media, out var known))
                return known;
            var path = media.Uid.Length > 0 && database.GetPath(media.Uid) is { } moved ? moved : media.Path;
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ProjectRoot, path));
            _resolved[media] = full;
            return full;
        }
    }

    /// <summary>Files moved in the project: media paths are looked up again.</summary>
    public void ForgetResolvedPaths()
    {
        lock (_resolved)
            _resolved.Clear();
    }

    internal void Set(Timeline timeline)
    {
        ForgetResolvedPaths();
        Timeline = timeline;
        Version++;
        Changed?.Invoke();
    }

    private void OnHistoryChanged()
    {
        if (_wasDirty == IsDirty)
            return;
        _wasDirty = IsDirty;
        DirtyChanged?.Invoke();
    }

    /// <summary>One edit: the timeline before and after. Merging keeps the first before and takes the latest after.</summary>
    private sealed class TimelineEdit(TimelineDocument document, string name, Timeline before, Timeline after) : IEditorAction
    {
        private Timeline _after = after;

        public string Name { get; } = name;

        public void Do() => document.Set(_after);

        public void Undo() => document.Set(before);

        public bool TryMerge(IEditorAction next)
        {
            if (next is not TimelineEdit edit || !ReferenceEquals(edit.Owner, document))
                return false;
            _after = edit._after;
            return true;
        }

        private TimelineDocument Owner => document;
    }
}

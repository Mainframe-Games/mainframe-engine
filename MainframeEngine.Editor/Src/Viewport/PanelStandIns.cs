namespace MainframeEngine.Editor;

/// <summary>
/// G11.7: the UI preview's stand-in data for the <see cref="UiPanel3D"/>s of an edited scene, so a terminal shows its
/// layout in the scene view. Game code creates a panel's data models, and it does not run in edit mode; so for each panel
/// (once its context exists) the documents' bindings are read (<see cref="RmlBindings.ParseFile"/>), stand-in models are
/// bound in the panel's context (<see cref="UiPreviewData"/>: text shows each variable's name, lists have three rows), and
/// the documents reload, which renders the panel once more. A panel binds again when its context or its documents'
/// sources change; panels that left are dropped.
/// </summary>
/// <remarks>Main thread. Holds engine nodes only between <see cref="Update"/> calls and until <see cref="Clear"/> (code reload).</remarks>
public sealed class PanelStandIns : IDisposable
{
    private sealed class Entry
    {
        public UI.Rml.RmlContext? Context;
        public string Sources = "";
        public readonly List<UiPreviewData> Data = [];
        public bool Seen;
    }

    private readonly Dictionary<UiPanel3D, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<UiPanel3D> _gone = [];

    /// <summary>Panels with stand-ins bound.</summary>
    public int Count => _entries.Count;

    /// <summary>The stand-in models bound in <paramref name="panel"/>'s context (empty when none).</summary>
    public IEnumerable<UI.Rml.RmlDataModel> ModelsOf(UiPanel3D panel) =>
        _entries.TryGetValue(panel, out var entry) ? entry.Data.SelectMany(d => d.Models) : [];

    /// <summary>Binds stand-ins for <paramref name="panels"/> that need them and drops entries for panels not listed.</summary>
    public void Update(IReadOnlyList<UiPanel3D> panels)
    {
        foreach (var entry in _entries.Values)
            entry.Seen = false;
        for (var i = 0; i < panels.Count; i++)
        {
            var panel = panels[i];
            if (panel.Context is not { IsDisposed: false } context || panel.Server is not { } server || panel.Documents.Count == 0)
                continue;
            if (!_entries.TryGetValue(panel, out var entry))
                _entries[panel] = entry = new Entry();
            entry.Seen = true;
            var sources = Sources(panel);
            if (ReferenceEquals(entry.Context, context) && entry.Sources == sources)
                continue;
            Bind(entry, panel, context, server, sources);
        }

        foreach (var (panel, entry) in _entries)
            if (!entry.Seen)
                _gone.Add(panel);
        foreach (var panel in _gone)
        {
            Release(_entries[panel]);
            _entries.Remove(panel);
        }

        _gone.Clear();
    }

    /// <summary>Forgets every panel (before a code reload, or when the scene changes).</summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values)
            Release(entry);
        _entries.Clear();
    }

    public void Dispose() => Clear();

    private static void Bind(Entry entry, UiPanel3D panel, UI.Rml.RmlContext context, UiServer server, string sources)
    {
        Release(entry);
        entry.Context = context;
        entry.Sources = sources;
        foreach (var document in panel.Documents)
        {
            if (string.IsNullOrEmpty(document.Source))
                continue;
            var path = ContentPaths.Resolve(document.Source);
            if (!ContentFiles.Exists(path))
                continue;
            var data = new UiPreviewData();
            data.Bind(context, RmlBindings.ParseFile(path)); // models another document already stood in for are kept
            entry.Data.Add(data);
        }

        // Documents that loaded before the stand-ins existed reload against them (and the panel renders again).
        foreach (var document in panel.Documents)
            if (document.IsLoaded)
                server.Reload(document, UiReloadKind.Documents);
    }

    private static void Release(Entry entry)
    {
        foreach (var data in entry.Data)
            data.Dispose(); // a model of a context already gone is invalid, and disposing it does nothing
        entry.Data.Clear();
        entry.Context = null;
        entry.Sources = "";
    }

    private static string Sources(UiPanel3D panel)
    {
        var documents = panel.Documents;
        if (documents.Count == 1)
            return documents[0].Source ?? "";
        return string.Join('|', documents.Select(d => d.Source ?? ""));
    }
}

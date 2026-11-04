using System.Globalization;
using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Transcript;
using Clip = MainframeEngine.Video.Timelines.Clip;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The Inspector's Transcript tab (G20d): the selected clip's words inside its window, from its media's transcript, as
/// flowing text, one span per word, struck where the clip's cuts remove them. A click cuts or restores a word in the clip
/// and its linked partner (one undo step), a drag takes a run, Cmd/Ctrl+click plays from the word and a right-click opens
/// a menu. Ported from the old mode's panel (Cutback's <c>TranscriptControl</c>); the behaviour of
/// <c>#video-transcript-words</c> inside <see cref="VideoPanel"/>'s document.
/// <para>
/// Cost model: the markup is rebuilt only when the words shown change (another clip, window or transcript). A cut, an undo
/// or a boundary drag only flips the <c>cut</c> class of the spans whose state changed, nothing is touched during a cut
/// editor drag, the current word's class moves between two spans, and a steady frame allocates nothing.
/// </para>
/// </summary>
public sealed class TranscriptPanel
{
    private const string NoClip = "<div class=\"transcript-empty hint\">Select a clip to see its words.</div>";
    private const string NoTranscript = "<div class=\"transcript-empty hint\">No transcript yet. Transcribe (the toolbar or the clip's menu) to edit by text.</div>";
    private const string NoWords = "<div class=\"transcript-empty hint\">No words in this clip.</div>";

    private readonly EditorWorkspace _workspace;
    private readonly RmlEventListener?[] _listeners = new RmlEventListener?[3];
    private RmlElement _words;
    private TimelineTab? _tab;

    // What the markup shows: the clip, its window, the transcript it came from (and the store's version), the words.
    private string? _builtClip;
    private (double In, double Out) _builtWindow;
    private IReadOnlyList<Word>? _builtSource;
    private int _builtStore = -1;
    private int _builtVersion = -1;
    private IReadOnlyList<Word> _shown = [];
    private bool[] _states = [];
    private int _current = -1;

    private RmlElement[] _spans = [];
    private int _spanCount;

    private int _press = -1;
    private int _end = -1;
    private int _markedFirst = -1;
    private int _markedLast = -1;

    public TranscriptPanel(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>How many times the markup was built (tests: an edit must not rebuild).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>The words shown, in order (the clip's window of its media's transcript).</summary>
    internal IReadOnlyList<Word> Words => _shown;

    /// <summary>Whether each shown word is cut.</summary>
    internal IReadOnlyList<bool> CutStates => _states;

    /// <summary>The shown word under the playhead, or -1.</summary>
    internal int Current => _current;

    /// <summary>After every load of the video document: the elements are new, so everything is built again.</summary>
    public void Attach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        _words = default;
        _spanCount = 0;
        Invalidate();
        var transcript = document.GetElementById("video-transcript");
        _words = document.GetElementById("video-transcript-words");
        if (transcript.IsNull || _words.IsNull)
            return;
        _listeners[0] = transcript.AddEventListener("mousedown", OnMouseDown);
        _listeners[1] = transcript.AddEventListener("mousemove", OnMouseMove);
        _listeners[2] = document.AsElement().AddEventListener("mouseup", OnMouseUp); // released outside the panel too
    }

    private void Invalidate()
    {
        _builtClip = null;
        _builtSource = null;
        _builtStore = -1;
        _builtVersion = -1;
        _current = -1;
        ResetRun();
    }

    /// <summary>Every frame while video mode shows a tab: follows its selected clip, its cuts and the playhead.</summary>
    public void Update(TimelineTab? tab, ClipTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            Invalidate();
        }

        if (_words.IsNull || tab is null)
            return;
        if (tab.Cut.IsDragging)
            return; // a boundary drag changes the cuts on every move; the first update after it catches up
        var clip = tab.Primary;
        var source = clip is null ? null : tools.WordsOf(tab, clip);
        if (clip?.Id != _builtClip || source != _builtSource || tools.Transcripts.Version != _builtStore
            || (clip is not null && (clip.SourceIn, clip.SourceOut) != _builtWindow))
            Rebuild(clip, source, tools.Transcripts.Version, tab.Document.Version);
        else if (clip is not null && tab.Document.Version != _builtVersion)
            RefreshCuts(clip, tab.Document.Version);
        MoveCurrent(tab);
    }

    private void Rebuild(Clip? clip, IReadOnlyList<Word>? source, int store, int version)
    {
        _builtClip = clip?.Id;
        _builtSource = source;
        _builtStore = store;
        _builtVersion = version;
        _builtWindow = clip is null ? default : (clip.SourceIn, clip.SourceOut);
        _shown = clip is null || source is null ? [] : ClipDetection.WordsInWindow(source, clip.SourceIn, clip.SourceOut);
        _states = clip is null ? [] : TranscriptView.CutStates(_shown, clip.Segments);
        _words.SetInnerRml(clip is null ? NoClip : source is null ? NoTranscript : _shown.Count == 0 ? NoWords : TranscriptMarkup.Build(_shown, _states));
        CacheSpans(_shown.Count);
        _current = -1;
        ResetRun();
        Rebuilds++;
    }

    // The cuts changed under the same words: flip the cut class of the spans whose state changed, in place.
    private void RefreshCuts(Clip clip, int version)
    {
        var segments = clip.Segments;
        for (var i = 0; i < _shown.Count; i++)
        {
            var cut = TranscriptView.IsCut(_shown[i], segments);
            if (cut == _states[i])
                continue;
            _states[i] = cut;
            Span(i).SetClass("cut", cut);
        }

        _builtVersion = version;
    }

    private void MoveCurrent(TimelineTab tab)
    {
        var sourceTime = tab.CutState.Playhead; // the playhead in the selected clip's source time, or -1
        var index = sourceTime < 0 || _shown.Count == 0 ? -1 : TranscriptView.IndexAtTime(_shown, sourceTime);
        if ((uint)index >= (uint)_states.Length)
            index = -1;
        if (index == _current)
            return;
        if (_current >= 0)
            Span(_current).SetClass("cur", false);
        _current = index;
        if (index < 0)
            return;
        var span = Span(index);
        span.SetClass("cur", true);
        if (!tab.Preview.IsPlaying)
            return;
        var bounds = span.Bounds;
        var view = _words.Bounds;
        if (bounds.Y < view.Y || bounds.Y + bounds.Height > view.Y + view.Height)
            span.ScrollIntoView(alignWithTop: false);
    }

    // Walks the paragraphs once, keeping each word's span (the spaces between them are text elements without the class).
    private void CacheSpans(int count)
    {
        if (_spans.Length < count)
            _spans = new RmlElement[count];
        var found = 0;
        var paragraphs = _words.ChildCount;
        for (var p = 0; p < paragraphs && found < count; p++)
        {
            var paragraph = _words.GetChild(p);
            var children = paragraph.ChildCount;
            for (var c = 0; c < children && found < count; c++)
            {
                var child = paragraph.GetChild(c);
                if (child.IsClassSet("w"))
                    _spans[found++] = child;
            }
        }

        _spanCount = found == count ? count : 0;
    }

    private RmlElement Span(int index)
    {
        if ((uint)index < (uint)_spanCount)
            return _spans[index];
        Span<char> id = stackalloc char[16]; // "w-" and an int, without allocating
        id[0] = 'w';
        id[1] = '-';
        index.TryFormat(id[2..], out var digits, default, CultureInfo.InvariantCulture);
        return _words.GetElementById(id[..(2 + digits)]);
    }

    // ── Gestures ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A press on word <paramref name="word"/>: with Cmd/Ctrl the playhead goes to it; otherwise a run starts there.</summary>
    internal void Press(int word, EditorModifiers modifiers)
    {
        if (_tab is not { } tab || (uint)word >= (uint)_shown.Count)
            return;
        if ((modifiers & EditorModifiers.Command) != 0)
        {
            SeekTo(tab, word);
            return;
        }

        _press = word;
        _end = word;
        Mark(word, word);
    }

    internal void Drag(int word)
    {
        if (_press < 0 || word == _end || (uint)word >= (uint)_shown.Count)
            return;
        _end = word;
        Mark(Math.Min(_press, word), Math.Max(_press, word));
    }

    /// <summary>The button went up: the run takes the opposite state of the word pressed, as one undo step.</summary>
    internal void Release()
    {
        if (_press < 0)
            return;
        var anchor = _press;
        var first = Math.Min(_press, _end);
        var last = Math.Max(_press, _end);
        Mark(-1, -1);
        _press = -1;
        _end = -1;
        CutWords(first, last, anchor);
    }

    /// <summary>
    /// Cuts (or restores) words <paramref name="first"/>..<paramref name="last"/>: the opposite state of word
    /// <paramref name="anchor"/>, edges snapped to the waveform, on the clip and its linked partner. False when nothing changed.
    /// </summary>
    internal bool CutWords(int first, int last, int anchor)
    {
        if (_tab is not { } tab || _shown.Count == 0 || tab.Timeline.FindClip(_builtClip ?? "") is not { } clip)
            return false;
        var lastIndex = _shown.Count - 1;
        var from = Math.Clamp(Math.Min(first, last), 0, lastIndex);
        var to = Math.Clamp(Math.Max(first, last), 0, lastIndex);
        var enabled = TranscriptView.IsCut(_shown[Math.Clamp(anchor, from, to)], clip.Segments);
        var waveform = tab.Cache?.WaveformOf(clip.Media.Hash, tab.Document.Resolve(clip.Media), clip.Media.Duration);
        var (start, end) = ClipTools.SnapWord(waveform, _shown[from].Start, _shown[to].End);
        return tab.Document.Apply(enabled ? "Restore Words" : "Cut Words",
            t => TimelineEdits.SetClipCuts(t, clip.Id, list => list.SetRange(start, end, enabled)));
    }

    private void SeekTo(TimelineTab tab, int word)
    {
        if (tab.Timeline.FindClip(_builtClip ?? "") is { } clip && TimelineLayout.TimelineTimeOf(clip, _shown[word].Start) is { } at)
            tab.Seek(at);
    }

    private void ResetRun()
    {
        _press = -1;
        _end = -1;
        _markedFirst = -1;
        _markedLast = -1;
    }

    private void Mark(int first, int last)
    {
        var oldFirst = _markedFirst;
        var oldLast = _markedLast;
        var from = oldFirst < 0 ? first : first < 0 ? oldFirst : Math.Min(first, oldFirst);
        var to = oldLast < 0 ? last : last < 0 ? oldLast : Math.Max(last, oldLast);
        _markedFirst = first;
        _markedLast = last;
        for (var i = from; i <= to && i >= 0; i++)
        {
            var was = i >= oldFirst && i <= oldLast;
            var now = i >= first && i <= last;
            if (was != now && !_words.IsNull && i < _states.Length)
                Span(i).SetClass("sel", now);
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────

    private void OnMouseDown(RmlEvent e)
    {
        var word = WordOf(e.Target);
        if (word < 0)
            return;
        var button = e.GetParameter("button", 0);
        if (button == 0)
        {
            var modifiers = ModifiersOf(e);
            Guard("Transcript edit failed", () => Press(word, modifiers));
        }
        else if (button == 1)
        {
            var scale = MathF.Max(0.01f, _workspace.Host.PixelScale);
            ShowMenu(word, (float)e.GetParameter("mouse_x", 0.0) / scale, (float)e.GetParameter("mouse_y", 0.0) / scale);
        }
    }

    private void OnMouseMove(RmlEvent e)
    {
        if (_press < 0)
            return;
        var word = WordOf(e.Target);
        if (word >= 0)
            Drag(word);
    }

    private void OnMouseUp(RmlEvent e)
    {
        if (_press >= 0 && e.GetParameter("button", 0) == 0)
            Guard("Transcript edit failed", Release);
    }

    private int WordOf(RmlElement target)
    {
        for (var element = target; !element.IsNull; element = element.Parent)
        {
            if (element == _words)
                break;
            if (element.GetAttribute("data-i") is { } index && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var word))
                return word;
        }

        return -1;
    }

    private EditorModifiers ModifiersOf(RmlEvent e)
    {
        var modifiers = EditorModifiers.None;
        if (e.GetParameter("ctrl_key", 0) != 0 || e.GetParameter("meta_key", 0) != 0)
            modifiers |= EditorModifiers.Command;
        return modifiers | (_workspace.Modifiers & EditorModifiers.Command);
    }

    internal void ShowMenu(int word, float x, float y)
    {
        if (_tab is not { } tab || (uint)word >= (uint)_shown.Count)
            return;
        var cut = word < _states.Length && _states[word];
        _workspace.Popup.Show(
        [
            new MenuItem("Play from here", "transcript.play", Icon: "player-play"),
            cut ? new MenuItem("Restore", "transcript.toggle", Icon: "arrow-back-up") : new MenuItem("Cut", "transcript.toggle", Icon: "trash"),
        ], x, y, onCommand: id =>
        {
            if (id == "transcript.play")
                Guard("Playback failed", () =>
                {
                    SeekTo(tab, word);
                    if (!tab.Preview.IsPlaying)
                        tab.TogglePlay();
                });
            else if (id == "transcript.toggle")
                Guard("Transcript edit failed", () => CutWords(word, word, word));
        });
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            _workspace.Commands.ReportError(what, e);
        }
    }
}

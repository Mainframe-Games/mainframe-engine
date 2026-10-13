using System.Globalization;
using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Edit by text: the behaviour of <c>#video-transcript-words</c> inside <see cref="VideoPanel"/>'s document (it is not a
/// document of its own). The transcript is flowing text, one span per word; a click cuts or restores a word, a drag takes a
/// run, Cmd/Ctrl+click plays from the word and a right-click opens a menu. Ported from Cutback's <c>TranscriptControl</c>.
/// <para>
/// Cost model: the markup is rebuilt only when the transcript itself changes (another list or another count). A cut, an
/// undo or a boundary drag changes only <see cref="VideoDocument.Version"/>, and then the cut states are recomputed in place
/// and the <c>cut</c> class flips on just the spans whose state changed. While a boundary is being dragged nothing is
/// touched; the first update after the drag catches up. The current word's class moves between two spans, and a steady frame
/// allocates nothing.
/// </para>
/// </summary>
public sealed class TranscriptPanel
{
    private const string Placeholder = "<div class=\"transcript-empty hint\">No transcript yet. Transcribe to edit by text.</div>";

    private readonly EditorWorkspace _workspace;
    private readonly RmlEventListener?[] _listeners = new RmlEventListener?[3];
    private RmlElement _words;
    private VideoTab? _tab;

    // What the markup shows: the list it was built from (by reference and count), the partition version of the last cut
    // states, and those states.
    private IReadOnlyList<Word>? _builtWords;
    private int _builtCount = -1;
    private int _builtVersion = -1;
    private bool[] _states = [];
    private int _current = -1;

    // The run being drawn by a press: the word pressed, the word the pointer is over, and the marked (sel) range.
    private int _press = -1;
    private int _end = -1;
    private int _markedFirst = -1;
    private int _markedLast = -1;

    public TranscriptPanel(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>How many times the whole markup was built (tests: an edit must not rebuild).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>The tab the panel follows (tests: a closed tab is let go of even while the panel is hidden).</summary>
    internal VideoTab? Tab => _tab;

    /// <summary>True while a press is drawing a run.</summary>
    internal bool IsPressed => _press >= 0;

    /// <summary>After every load and hot reload of the video document: the elements are new, so everything is built again.</summary>
    public void Attach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener?.Remove();
        _words = default;
        _builtWords = null;
        _builtCount = -1;
        _builtVersion = -1;
        _current = -1;
        ResetRun();
        var transcript = document.GetElementById("video-transcript");
        _words = document.GetElementById("video-transcript-words");
        if (transcript.IsNull || _words.IsNull)
            return;
        _listeners[0] = transcript.AddEventListener("mousedown", OnMouseDown);
        _listeners[1] = transcript.AddEventListener("mousemove", OnMouseMove);
        // On the body, so releasing the button outside the panel still ends the run.
        _listeners[2] = document.AsElement().AddEventListener("mouseup", OnMouseUp);
    }

    /// <summary>
    /// Every frame while the panel is hidden: only follows which tab is shown, building nothing, so a closed tab is not
    /// kept alive; the next <see cref="Update"/> rebuilds for it.
    /// </summary>
    public void Follow(VideoTab? tab)
    {
        if (ReferenceEquals(tab, _tab))
            return;
        _tab = tab;
        _builtWords = null;
        ResetRun();
    }

    /// <summary>Every frame while a video tab is shown: follows its transcript and cuts, and moves the current word.</summary>
    public void Update(VideoTab? tab)
    {
        if (_words.IsNull)
            return;
        Follow(tab);
        if (tab is null)
            return;
        var document = tab.Document;
        if (document.Editor.IsDragging)
            return; // a boundary drag changes the partition on every pointer move; the first update after it catches up
        var words = document.Transcript;
        if (!ReferenceEquals(words, _builtWords) || words.Count != _builtCount)
            Rebuild(document);
        else if (document.Version != _builtVersion)
            RefreshCuts(document);
        MoveCurrent(tab);
    }

    private void Rebuild(VideoDocument document)
    {
        var words = document.Transcript;
        _states = TranscriptView.CutStates(words, document.Segments.Segments);
        _words.SetInnerRml(words.Count == 0 ? Placeholder : TranscriptMarkup.Build(words, _states));
        _builtWords = words;
        _builtCount = words.Count;
        _builtVersion = document.Version;
        _current = -1; // the new spans carry no cur class
        ResetRun();
        Rebuilds++;
    }

    // The partition changed under the same words: flip the cut class of the spans whose state changed, in place.
    private void RefreshCuts(VideoDocument document)
    {
        var words = document.Transcript;
        var segments = document.Segments.Segments;
        for (var i = 0; i < words.Count; i++)
        {
            var cut = TranscriptView.IsCut(words[i], segments);
            if (cut == _states[i])
                continue;
            _states[i] = cut;
            Span(i).SetClass("cut", cut);
        }

        _builtVersion = document.Version;
    }

    private void MoveCurrent(VideoTab tab)
    {
        var index = tab.CurrentWord;
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

    private RmlElement Span(int index) => _words.GetElementById(TranscriptMarkup.SpanId(index));

    // ── Gestures ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on word <paramref name="word"/>: with Cmd/Ctrl the playhead goes to the word and nothing is cut; otherwise it
    /// starts a run anchored there (a single click is a run of one word).
    /// </summary>
    internal void Press(int word, EditorModifiers modifiers)
    {
        if (_tab is not { } tab || (uint)word >= (uint)tab.Document.Transcript.Count)
            return;
        if ((modifiers & EditorModifiers.Command) != 0)
        {
            tab.Seek(tab.Document.Transcript[word].Start);
            return;
        }

        _press = word;
        _end = word;
        Mark(word, word);
    }

    /// <summary>The pointer moved over <paramref name="word"/> while pressed: the run reaches it.</summary>
    internal void Drag(int word)
    {
        if (_press < 0 || word == _end || _tab is not { } tab || (uint)word >= (uint)tab.Document.Transcript.Count)
            return;
        _end = word;
        Mark(Math.Min(_press, word), Math.Max(_press, word));
    }

    /// <summary>
    /// The button went up: the run takes the opposite state of the word pressed (a kept word is cut, a struck word is
    /// restored), as one undo step.
    /// </summary>
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
        _tab?.Document.CutWords(first, last, anchor);
    }

    private void ResetRun()
    {
        _press = -1;
        _end = -1;
        _markedFirst = -1;
        _markedLast = -1; // the spans it marked are gone with the markup (or the tab)
    }

    // Moves the sel class from the old run to the new one, touching only the spans that change.
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

    // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────

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

    // The word index of the span the pointer is over (or inside one), else -1.
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

    // Built only when a right-click opens it. Cut / Restore act on this word alone, whatever run is drawn.
    internal void ShowMenu(int word, float x, float y)
    {
        if (_tab is not { } tab || (uint)word >= (uint)tab.Document.Transcript.Count)
            return;
        var cut = word < _states.Length && _states[word];
        _workspace.Popup.Show(
        [
            new MenuItem("Play from here", "transcript.play", Icon: "player-play"),
            cut
                ? new MenuItem("Restore", "transcript.toggle", Icon: "arrow-back-up")
                : new MenuItem("Cut", "transcript.toggle", Icon: "trash"),
        ], x, y, onCommand: id =>
        {
            if (id == "transcript.play")
                Guard("Playback failed", () => PlayFrom(tab, word));
            else if (id == "transcript.toggle")
                Guard("Transcript edit failed", () => tab.Document.CutWords(word, word, word));
        });
    }

    private static void PlayFrom(VideoTab tab, int word)
    {
        if ((uint)word >= (uint)tab.Document.Transcript.Count)
            return;
        tab.Seek(tab.Document.Transcript[word].Start);
        if (!tab.Preview.IsPlaying)
            tab.TogglePlay();
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

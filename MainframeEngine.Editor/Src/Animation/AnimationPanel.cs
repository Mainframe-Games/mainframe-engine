using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The bottom dock's Animation tab (G1.6, <c>animation.rml</c>), shown while an <see cref="AnimationPlayer"/> is selected
/// or pinned. Header: transport (play from start, play, pause, stop), the animation menu (pick, new, duplicate, rename,
/// delete), length, loop mode, snap, frames, zoom and pin, all icon buttons with tooltips. Left: one row per track (its
/// kind, node and property, and interpolation / loop wrap / enable / remove buttons). Right: the timeline canvas
/// (<see cref="AnimationTimelineCanvas"/>): click or drag on the ruler scrubs, click a key selects it (Shift or
/// Cmd/Ctrl adds), drag keys to move them (snapped), drag empty space for a rubber band, double-click a row inserts a
/// key, the wheel scrolls and Cmd/Ctrl+wheel zooms. Bottom: the selected key's time, value and easing. Keys come from
/// the inspector's key buttons and K. Every edit is one undo step (<see cref="AnimationEditor"/>).
/// </summary>
public sealed class AnimationPanel : EditorDocument
{
    public const string TextureName = "animation-timeline";

    private readonly TimelineTexture _texture = new(TextureName);
    private int _shownVersion = -1;
    private int _shownHistory = -1;
    private Gesture _gesture;
    private Vector2 _pressAt;
    private double _pressTime;
    private bool _moved;

    private enum Gesture
    {
        None,
        Scrub,
        Keys,
        Band,
    }

    public AnimationPanel(EditorWorkspace workspace)
        : base(workspace, "animation.rml")
    {
        Viewport = new SubViewport
        {
            Name = "AnimationTimeline",
            Disable3D = true,
            UpdateMode = SubViewportUpdateMode.Disabled,
            ClearColor = System.Drawing.Color.FromArgb(255, 27, 29, 35),
            Width = 16,
            Height = 16,
        };
        Canvas = new AnimationTimelineCanvas(workspace.Animations) { Name = "Canvas" };
        Viewport.AddChild(Canvas);
    }

    /// <summary>The timeline canvas's viewport (the workspace parents it).</summary>
    public SubViewport Viewport { get; }

    public AnimationTimelineCanvas Canvas { get; }

    private AnimationEditor Editor => Workspace.Animations;

    protected override void OnAttach(RmlDocument document)
    {
        base.OnAttach(document);
        var canvas = document.GetElementById("an-canvas");
        if (canvas.IsNull)
            return;
        canvas.AddEventListener("mousedown", OnMouseDown);
        document.AsElement().AddEventListener("mousemove", OnMouseMove);
        document.AsElement().AddEventListener("mouseup", OnMouseUp);
        canvas.AddEventListener("dblclick", OnDoubleClick);
        canvas.AddEventListener("mousescroll", OnWheel);
        document.AsElement().AddEventListener("change", OnChange);
        _shownVersion = -1;
    }

    /// <summary>Per frame while shown: places the canvas over its element and rebuilds the chrome when the model changed.</summary>
    public void Tick()
    {
        var shown = Visible && IsLoaded && Editor.IsActive;
        var element = IsLoaded ? Document.GetElementById("an-canvas") : default;
        if (shown && !element.IsNull)
        {
            var bounds = element.Bounds;
            var scale = Workspace.Host.PixelScale;
            TimelineHost.Place(Viewport, Canvas, new Vector2(MathF.Max(1, bounds.Width / scale), MathF.Max(1, bounds.Height / scale)), scale, show: true);
            _texture.Publish(Workspace.Tree?.Servers.Get<UiServer>(), Viewport);
            if (_texture.Registered is not null && element.GetAttribute("src") is null)
                element.SetAttribute("src", "engine://" + TextureName); // only once the texture exists (no load warning)
        }
        else if (Viewport.UpdateMode != SubViewportUpdateMode.Disabled)
        {
            Viewport.UpdateMode = SubViewportUpdateMode.Disabled;
        }

        var history = Workspace.Session.Active?.History.Position ?? -1;
        if (!IsLoaded || (_shownVersion == Editor.Version && _shownHistory == history))
            return;
        _shownVersion = Editor.Version;
        _shownHistory = history;
        Rebuild();
    }

    // ── Chrome ────────────────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        var tools = Document.GetElementById("an-tools");
        if (!tools.IsNull)
            tools.SetInnerRml(ToolsRml());
        var tracks = Document.GetElementById("an-tracks");
        if (!tracks.IsNull)
            tracks.SetInnerRml(TracksRml());
        var keyRow = Document.GetElementById("an-keyrow");
        if (!keyRow.IsNull)
            keyRow.SetInnerRml(KeyRowRml());
        // The canvas is as tall as the tracks, so the panel's scroll moves both.
        var canvas = Document.GetElementById("an-canvas");
        if (!canvas.IsNull)
        {
            var rows = Editor.Animation?.Tracks.Count ?? 0;
            var height = AnimationEditor.RulerHeight + Math.Max(rows, 3) * AnimationEditor.RowHeight;
            canvas.SetProperty("height", height.ToString(CultureInfo.InvariantCulture) + "dp");
        }
    }

    /// <summary>The header's tools (icon buttons with tooltips).</summary>
    public string ToolsRml()
    {
        var rml = new StringBuilder(2048);
        var editor = Editor;
        var animation = editor.Animation;
        Button(rml, "anim.play_start", "player-skip-back", false, "Play from Start — the preview from 0 s");
        Button(rml, editor.Playing ? "anim.pause" : "anim.play", editor.Playing ? "player-pause" : "player-play", false,
            editor.Playing ? "Pause — hold the preview here" : "Play — the preview from the playhead");
        Button(rml, "anim.stop", "player-stop", false, "Stop — back to 0 s; the scene's own values come back");
        rml.Append("<span class=\"caption mono\" data-tooltip=\"Playhead\">").Append(Seconds(editor.Playhead)).Append("</span>");
        rml.Append("<span class=\"sep\"></span>");
        rml.Append("<button class=\"chip\" data-command=\"anim.pick\" data-tooltip=\"Animation — pick the animation to edit\"><span class=\"icon icon-sm icon-movie\"></span><span>")
            .Append(RmlText.Escape(animation is null ? "No animation" : editor.AnimationName)).Append("</span></button>");
        Button(rml, "anim.new", "plus", false, "New Animation — an empty 1 s animation in the player's default library");
        if (animation is not null)
        {
            Button(rml, "anim.duplicate", "copy", false, "Duplicate Animation — a copy named …_copy");
            Button(rml, "anim.rename", "pencil", false, "Rename Animation");
            Button(rml, "anim.delete", "trash", false, "Delete Animation — remove it from its library");
            rml.Append("<span class=\"sep\"></span>");
            Button(rml, "anim.length.less", "minus", false, "Shorter — the length minus 0.5 s");
            rml.Append("<span class=\"caption mono\" data-tooltip=\"Length — the animation's duration\">").Append(Seconds(animation.Length)).Append("</span>");
            Button(rml, "anim.length.more", "plus", false, "Longer — the length plus 0.5 s");
            var (loopIcon, loopTip) = animation.LoopMode switch
            {
                AnimationLoopMode.Linear => ("repeat", "Loop: Linear — wraps to the start (click: ping-pong)"),
                AnimationLoopMode.PingPong => ("arrows-exchange", "Loop: Ping-Pong — plays back and forth (click: no loop)"),
                _ => ("repeat-off", "Loop: None — plays once (click: linear loop)"),
            };
            Button(rml, "anim.loop", loopIcon, animation.LoopMode != AnimationLoopMode.None, loopTip);
            Button(rml, "anim.snap", "magnet", editor.Snap, "Snap — keys and the playhead snap to the step (" + Seconds(animation.Step) + ")");
            Button(rml, "anim.frames", "timeline", editor.ShowFrames, "Frames — the ruler counts frames at the step");
            rml.Append("<span class=\"sep\"></span>");
            Button(rml, "anim.add_method", "code", false, "Add Method Track — calls the selected node's [AnimationMethod]s; double-click its row to key one");
            Button(rml, "anim.add_audio", "volume", false, "Add Audio Track — plays sounds on the selected audio player; double-click its row to key its stream");
        }

        rml.Append("<span class=\"sep\"></span>");
        Button(rml, "anim.zoom_out", "zoom-out", false, "Zoom Out — Cmd/Ctrl+wheel over the timeline zooms too");
        Button(rml, "anim.zoom_in", "zoom-in", false, "Zoom In");
        Button(rml, "anim.pin", "pin", editor.Pin, "Pin — keep editing this player while other nodes are selected (to key them)");
        return rml.ToString();
    }

    private string TracksRml()
    {
        var rml = new StringBuilder(2048);
        rml.Append("<div class=\"an-ruler-spacer\"></div>");
        if (Editor.Animation is not { } animation)
            return rml.Append("<div class=\"an-empty\">No animation: create one with +.</div>").ToString();
        if (animation.Tracks.Count == 0)
            rml.Append("<div class=\"an-empty\">No tracks: key a property with the inspector's key buttons, or K for the selected 3D nodes.</div>");
        for (var i = 0; i < animation.Tracks.Count; i++)
        {
            var track = animation.Tracks[i];
            var (icon, label) = Describe(track);
            rml.Append("<div class=\"an-track").Append(track.Enabled ? "" : " disabled").Append("\"><span class=\"icon icon-sm icon-").Append(icon)
                .Append("\"></span><span class=\"an-name\" data-tooltip=\"").Append(RmlText.Escape(label)).Append("\">").Append(RmlText.Escape(label)).Append("</span>");
            if (track is InterpolatedTrack it)
            {
                var (interpIcon, interpTip) = it.Interpolation switch
                {
                    AnimationInterpolation.Nearest => ("square", "Interpolation: Nearest — holds each key (click: linear)"),
                    AnimationInterpolation.Cubic => ("wave-sine", "Interpolation: Cubic — through the neighbouring keys (click: nearest)"),
                    _ => ("line", "Interpolation: Linear (click: cubic)"),
                };
                Button(rml, $"anim.track.interp:{i}", interpIcon, false, interpTip);
                Button(rml, $"anim.track.wrap:{i}", "repeat", it.LoopWrap, "Loop Wrap — on a looping animation, the last key blends into the first");
            }

            Button(rml, $"anim.track.enable:{i}", track.Enabled ? "eye" : "eye-off", false, track.Enabled ? "Enabled — click to mute the track" : "Disabled — click to apply the track again");
            Button(rml, $"anim.track.remove:{i}", "trash", false, "Remove Track");
            rml.Append("</div>");
        }

        return rml.ToString();
    }

    private static (string Icon, string Label) Describe(AnimationTrack track) => track switch
    {
        Position3DTrack p => ("arrows-move", $"{p.Path}{Bone(p.Bone)} · Position"),
        Rotation3DTrack r => ("rotate", $"{r.Path}{Bone(r.Bone)} · Rotation"),
        Scale3DTrack s => ("arrows-maximize", $"{s.Path}{Bone(s.Bone)} · Scale"),
        ValueTrack v => ("keyframe", $"{v.Path} · {v.Property}"),
        BezierTrack b => ("vector-bezier", $"{b.Path} · {b.Property} (bezier)"),
        MethodTrack m => ("code", $"{m.Path} · methods"),
        AudioTrack a => ("volume", $"{a.Path} · audio"),
        EventTrack => ("bolt", "events"),
        _ => ("help", track.GetType().Name),
    };

    private static string Bone(string bone) => bone.Length > 0 ? $" ({bone})" : "";

    private string KeyRowRml()
    {
        if (Editor.Animation is not { } animation || Editor.Selected.Count != 1 || Editor.Selected[0] is var key && key.Track >= animation.Tracks.Count)
            return "<span class=\"caption\">Select a key to edit its time, value and easing.</span>";
        var track = animation.Tracks[key.Track];
        if (key.Key >= track.Times.Length)
            return "";
        var rml = new StringBuilder(512);
        rml.Append("<span class=\"caption\">Key</span><span class=\"caption mono\">").Append(Seconds(track.Times[key.Key])).Append("</span>");
        var values = KeyValues(track, key.Key);
        string[] axes = ["x", "y", "z", "w"];
        for (var i = 0; i < values.Length && i < 4; i++)
            rml.Append("<span class=\"caption axis-").Append(axes[i]).Append("\">").Append(axes[i].ToUpperInvariant()).Append("</span>")
                .Append("<input type=\"text\" class=\"num\" data-key-value=\"").Append(i).Append("\" value=\"")
                .Append(values[i].ToString("0.###", CultureInfo.InvariantCulture)).Append("\"/>");
        if (track is InterpolatedTrack it)
        {
            var easing = key.Key < it.Transitions.Length ? it.Transitions[key.Key] : 1f;
            rml.Append("<span class=\"caption\" data-tooltip=\"Easing — 1 linear, below 1 ease out, above 1 ease in, negative in-out\">Easing</span>")
                .Append("<input type=\"text\" class=\"num\" data-key-easing=\"1\" value=\"").Append(easing.ToString("0.##", CultureInfo.InvariantCulture)).Append("\"/>");
        }

        if (track is MethodTrack m && key.Key < m.Methods.Length)
            rml.Append("<span class=\"caption mono\">").Append(RmlText.Escape(m.Methods[key.Key])).Append(' ')
                .Append(RmlText.Escape(key.Key < m.Arguments.Length ? m.Arguments[key.Key] : "[]")).Append("</span>");
        return rml.ToString();
    }

    private static float[] KeyValues(AnimationTrack track, int key) => track switch
    {
        Position3DTrack p when key < p.Values.Length => [p.Values[key].X, p.Values[key].Y, p.Values[key].Z],
        Scale3DTrack s when key < s.Values.Length => [s.Values[key].X, s.Values[key].Y, s.Values[key].Z],
        Rotation3DTrack r when key < r.Values.Length => EulerAngles.FromQuaternion(r.Values[key]) is var e ? [e.X, e.Y, e.Z] : [],
        ValueTrack v when (key + 1) * v.Components <= v.Values.Length => v.Values.AsSpan(key * v.Components, v.Components).ToArray(),
        BezierTrack b when key < b.Values.Length => [b.Values[key]],
        _ => [],
    };

    private static string Seconds(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static void Button(StringBuilder rml, string command, string icon, bool active, string tooltip) =>
        rml.Append("<button class=\"tool-button").Append(active ? " active" : "").Append("\" data-command=\"").Append(command)
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-").Append(icon).Append("\"></span></button>");

    // ── Commands ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs an <c>anim.*</c> command (the header's and the track rows' buttons, shortcuts).</summary>
    public bool RunCommand(string id)
    {
        var editor = Editor;
        if (id.StartsWith("anim.method_key:", StringComparison.Ordinal))
            return RunMethodKey(id);
        var colon = id.IndexOf(':');
        var index = colon > 0 && int.TryParse(id.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var name = colon > 0 ? id[..colon] : id;
        switch (name)
        {
            case "anim.play_start":
                editor.Scrub(0);
                editor.Play();
                return true;
            case "anim.play":
                editor.Play();
                return true;
            case "anim.pause":
                editor.Pause();
                return true;
            case "anim.toggle_play":
                if (editor.Playing)
                    editor.Pause();
                else
                    editor.Play();
                return true;
            case "anim.stop":
                editor.Stop();
                return true;
            case "anim.new":
                editor.NewAnimation();
                return true;
            case "anim.duplicate":
                editor.DuplicateAnimation();
                return true;
            case "anim.delete":
                editor.DeleteAnimation();
                return true;
            case "anim.rename" when editor.Animation is not null:
                Workspace.Message.Show(new MessageRequest
                {
                    Title = "Rename Animation",
                    Message = $"New name for {editor.AnimationName}:",
                    Input = editor.AnimationName.Split('/')[^1],
                    Buttons = ["Rename", "Cancel"],
                    DefaultButton = 0,
                    CancelButton = 1,
                    Callback = (button, text) =>
                    {
                        if (button == 0)
                            editor.RenameAnimation(text);
                    },
                });
                return true;
            case "anim.pick":
                ShowPicker();
                return true;
            case "anim.select" when colon > 0:
                editor.SelectAnimation(id[(colon + 1)..]);
                return true;
            case "anim.length.less" when editor.Animation is { } shorter:
                editor.SetLength(MathF.Max(0.1f, shorter.Length - 0.5f));
                return true;
            case "anim.length.more" when editor.Animation is { } longer:
                editor.SetLength(longer.Length + 0.5f);
                return true;
            case "anim.loop":
                editor.CycleLoop();
                return true;
            case "anim.snap":
                editor.Snap = !editor.Snap;
                Touch();
                return true;
            case "anim.frames":
                editor.ShowFrames = !editor.ShowFrames;
                Touch();
                return true;
            case "anim.zoom_in":
                editor.View.ZoomAround(0, 1.5);
                Touch();
                return true;
            case "anim.zoom_out":
                editor.View.ZoomAround(0, 1 / 1.5);
                Touch();
                return true;
            case "anim.pin":
                editor.Pin = !editor.Pin;
                return true;
            case "anim.delete_keys":
                editor.DeleteSelectedKeys();
                return true;
            case "anim.add_method" or "anim.add_audio":
                if (Workspace.Session.Active?.Selection.Primary is { } target and not AnimationPlayer)
                    editor.AddCallTrack(target, audio: name == "anim.add_audio");
                else
                    Workspace.Message.Show(new MessageRequest { Title = "Animation", Message = "Select the node the track should call (or play sounds on) first.", Buttons = ["OK"] });
                return true;
            case "anim.key_selected":
                if (Workspace.Session.Active is { } scene)
                    editor.KeyTransforms(scene.Selection.Nodes.OfType<Node3D>());
                return true;
            case "anim.track.interp":
                editor.CycleInterpolation(index);
                return true;
            case "anim.track.wrap":
                editor.ToggleLoopWrap(index);
                return true;
            case "anim.track.enable":
                editor.ToggleEnabled(index);
                return true;
            case "anim.track.remove":
                editor.RemoveTrack(index);
                return true;
            default:
                return false;
        }
    }

    private void ShowPicker()
    {
        var items = Editor.AnimationNames().Select(n => new MenuItem(n, "anim.select:" + n, null, true, Icon: "movie")).ToList();
        if (items.Count == 0)
            items.Add(new MenuItem("New Animation", "anim.new", null, true, Icon: "plus"));
        var element = Document.QuerySelector("[data-command=\"anim.pick\"]");
        var bounds = element.IsNull ? default : element.Bounds;
        var scale = Workspace.Host.PixelScale;
        Workspace.Popup.Show(items, bounds.X / scale, (bounds.Y + bounds.Height) / scale);
    }

    private void Touch() => _shownVersion = -1;

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-tab") is { } tab && int.TryParse(tab, CultureInfo.InvariantCulture, out var index))
            Workspace.ShowBottomTab((BottomDockTab)index);
    }

    // ── Timeline input ────────────────────────────────────────────────────────────────────────────────────────

    private Vector2 Local(RmlEvent e)
    {
        var canvas = Document.GetElementById("an-canvas");
        var bounds = canvas.IsNull ? default : canvas.Bounds;
        var scale = Workspace.Host.PixelScale;
        return new Vector2(((float)e.GetParameter("mouse_x", 0.0) - bounds.X) / scale, ((float)e.GetParameter("mouse_y", 0.0) - bounds.Y) / scale);
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (e.GetParameter("button", 0) != 0)
            return;
        var p = Local(e);
        Press(p, (Workspace.Modifiers & (EditorModifiers.Shift | EditorModifiers.Command)) != 0);
        e.StopPropagation();
    }

    /// <summary>A press at <paramref name="p"/> (dp in the timeline): scrub on the ruler, select and drag keys, or start a rubber band.</summary>
    public void Press(Vector2 p, bool add)
    {
        var editor = Editor;
        _pressAt = p;
        _moved = false;
        _pressTime = editor.View.TimeAt(p.X);
        if (p.Y < AnimationEditor.RulerHeight)
        {
            _gesture = Gesture.Scrub;
            editor.Scrub(_pressTime);
            return;
        }

        if (editor.HitKey(p.X, p.Y) is { } key)
        {
            if (add || !editor.Selected.Contains(key))
                editor.Select(key, add);
            _gesture = Gesture.Keys;
            editor.BeginKeyDrag();
            return;
        }

        _gesture = Gesture.Band;
        if (!add)
            editor.Selected.Clear();
    }

    private void OnMouseMove(RmlEvent e)
    {
        if (_gesture == Gesture.None)
            return;
        Drag(Local(e));
    }

    /// <summary>The mouse moved to <paramref name="p"/> during a press.</summary>
    public void Drag(Vector2 p)
    {
        var editor = Editor;
        _moved |= Vector2.Distance(p, _pressAt) > 3;
        switch (_gesture)
        {
            case Gesture.Scrub:
                editor.Scrub(editor.View.TimeAt(p.X));
                break;
            case Gesture.Keys when _moved:
                editor.MoveSelectedKeys(editor.View.TimeAt(p.X) - _pressTime);
                break;
            case Gesture.Band when _moved:
                Canvas.Band = new Vector4(_pressAt.X, _pressAt.Y, p.X, p.Y);
                break;
        }

        Canvas.QueueRedraw();
    }

    private void OnMouseUp(RmlEvent e)
    {
        if (_gesture == Gesture.None)
            return;
        Release(Local(e), (Workspace.Modifiers & (EditorModifiers.Shift | EditorModifiers.Command)) != 0);
    }

    /// <summary>The press ended at <paramref name="p"/>: commits a key drag, selects a rubber band.</summary>
    public void Release(Vector2 p, bool add)
    {
        var editor = Editor;
        switch (_gesture)
        {
            case Gesture.Keys:
                editor.EndGesture();
                break;
            case Gesture.Band:
                Canvas.Band = null;
                if (_moved)
                    editor.SelectRect(_pressAt.X, _pressAt.Y, p.X, p.Y, add);
                Touch();
                break;
        }

        _gesture = Gesture.None;
        Canvas.QueueRedraw();
    }

    /// <summary>Esc during a drag.</summary>
    public void CancelGesture()
    {
        if (_gesture == Gesture.Keys)
            Editor.CancelGesture();
        Canvas.Band = null;
        _gesture = Gesture.None;
    }

    private void OnDoubleClick(RmlEvent e)
    {
        var p = Local(e);
        var row = Editor.RowAt(p.Y);
        if (row >= 0 && Editor.HitKey(p.X, p.Y) is null)
        {
            var scale = Workspace.Host.PixelScale;
            DoubleClickRow(row, Editor.View.TimeAt(p.X), (float)e.GetParameter("mouse_x", 0.0) / scale, (float)e.GetParameter("mouse_y", 0.0) / scale);
        }

        e.StopPropagation();
    }

    /// <summary>
    /// A double-click on an empty spot of <paramref name="row"/> at <paramref name="time"/>: a value key from the current
    /// value, an audio key from the player's stream, or (a method track) a menu of its node's methods at the window point.
    /// </summary>
    public void DoubleClickRow(int row, double time, float windowX, float windowY)
    {
        switch (Editor.Animation?.Tracks.ElementAtOrDefault(row))
        {
            case MethodTrack:
                var methods = Editor.MethodsFor(row);
                if (methods.Count == 0)
                {
                    Workspace.Message.Show(new MessageRequest { Title = "Animation", Message = "This node has no [AnimationMethod] methods to call.", Buttons = ["OK"] });
                    return;
                }

                var at = time.ToString("R", CultureInfo.InvariantCulture);
                Workspace.Popup.Show([.. methods.Select(m => new MenuItem(m.Name, $"anim.method_key:{row}:{at}:{m.Name}", null, true, Icon: "code"))], windowX, windowY);
                break;
            case AudioTrack:
                if (!Editor.InsertAudioKey(row, time))
                    Workspace.Message.Show(new MessageRequest { Title = "Animation", Message = "The track's audio player has no Stream to key.", Buttons = ["OK"] });
                break;
            case not null:
                Editor.InsertKey(row, time);
                break;
        }
    }

    // anim.method_key:<track>:<time>:<method> from the method picker: a key calling it with default arguments.
    private bool RunMethodKey(string id)
    {
        var parts = id.Split(':', 4);
        if (parts.Length == 4 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var track) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            Editor.InsertMethodKey(track, time, parts[3]);
        return true;
    }

    private void OnWheel(RmlEvent e)
    {
        var p = Local(e);
        var delta = (float)e.GetParameter("wheel_delta_y", 0.0);
        if ((Workspace.Modifiers & EditorModifiers.Command) != 0)
            Editor.View.ZoomAround(p.X, delta < 0 ? 1.25 : 0.8);
        else
            Editor.View.ScrollBy(delta * 40f);
        Canvas.QueueRedraw();
        e.StopPropagation();
    }

    private void OnChange(RmlEvent e)
    {
        if (Editor.Selected.Count != 1 || e.Target.IsNull)
            return;
        var key = Editor.Selected[0];
        var text = e.Value;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return;
        if (e.Target.GetAttribute("data-key-easing") is not null)
        {
            Editor.SetKeyTransition(key, number);
            return;
        }

        if (e.Target.GetAttribute("data-key-value") is { } component && int.TryParse(component, CultureInfo.InvariantCulture, out var c) &&
            Editor.Animation?.Tracks.ElementAtOrDefault(key.Track) is { } track)
        {
            var values = KeyValues(track, key.Key);
            if (c < values.Length)
            {
                values[c] = number;
                Editor.SetKeyValue(key, values);
            }
        }
    }

    /// <summary>The panel's keys while it shows: Delete/Backspace remove the selected keys, Space plays or pauses, K keys the selection.</summary>
    public static string? ShortcutFor(Silk.NET.Input.Key key, EditorModifiers modifiers, bool hasSelectedKeys) =>
        modifiers != EditorModifiers.None ? null : key switch
        {
            Silk.NET.Input.Key.Delete or Silk.NET.Input.Key.Backspace when hasSelectedKeys => "anim.delete_keys",
            Silk.NET.Input.Key.K => "anim.key_selected",
            _ => null,
        };
}

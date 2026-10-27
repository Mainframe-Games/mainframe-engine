using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The SpriteFrames editor (ADR 0224) of a <see cref="SpriteFrames"/> resource file: the animation list and the frame
/// strip above the generated rows. See <see cref="SpriteFramesHeader"/>.
/// </summary>
[CustomInspector(typeof(SpriteFrames))]
public sealed class SpriteFramesInspector : ICustomInspector, IWorkspaceInspector
{
    private readonly SpriteFramesHeader _header = new();

    public EditorWorkspace? Workspace
    {
        get => _header.Workspace;
        set => _header.Workspace = value;
    }

    public string? GetHeaderRml(object target) => _header.Build((SpriteFrames)target, null);

    // The header edits the animations; the generated list stays for the per-animation rows it folds out.
    public bool ShowProperty(object target, ExportPropertyInfo property) => true;

    public void OnAction(object target, string action, IInspectorContext context) =>
        _header.Run((SpriteFrames)target, null, action, context);

    public void RefreshPreview(object target, RmlDocument document)
    {
    }
}

/// <summary>
/// The SpriteFrames editor on an <see cref="AnimatedSprite2D"/> (ADR 0224): the same header for its
/// <see cref="AnimatedSprite2D.SpriteFrames"/> (New when it has none), where picking an animation sets the sprite's
/// <see cref="AnimatedSprite2D.Animation"/>, Autoplay marks the one played on load and Play / Play Backwards preview it
/// in the view without editing the scene.
/// </summary>
[CustomInspector(typeof(AnimatedSprite2D))]
public sealed class AnimatedSprite2DInspector : ICustomInspector, IWorkspaceInspector
{
    private readonly SpriteFramesHeader _header = new();

    public EditorWorkspace? Workspace
    {
        get => _header.Workspace;
        set => _header.Workspace = value;
    }

    public string? GetHeaderRml(object target)
    {
        var sprite = (AnimatedSprite2D)target;
        return sprite.SpriteFrames is { } frames
            ? _header.Build(frames, sprite)
            : "<div class=\"sprite-frames\"><div class=\"sf-bar\"><span class=\"sf-label\">No SpriteFrames</span>" +
              "<button class=\"tool-button\" data-action=\"sf-new\" data-tooltip=\"New SpriteFrames — give the sprite an empty animation " +
              "set with a &quot;default&quot; animation\"><span class=\"icon icon-sm icon-library-photo\"></span></button></div></div>";
    }

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sprite = (AnimatedSprite2D)target;
        if (action == "sf-new")
        {
            if (sprite.SpriteFrames is not null)
                return;
            var frames = new SpriteFrames();
            frames.AddAnimation(SpriteFrames.DefaultAnimation);
            context.SetProperty(sprite, Property(nameof(AnimatedSprite2D.SpriteFrames)), frames);
            Workspace?.Inspector.Rebuild();
            return;
        }

        if (sprite.SpriteFrames is { } spriteFrames)
            _header.Run(spriteFrames, sprite, action, context);
    }

    public void RefreshPreview(object target, RmlDocument document) => Workspace?.Inspector.Rebuild();

    internal static ExportPropertyInfo Property(string name) =>
        TypeRegistry.GetRequired(typeof(AnimatedSprite2D)).FindProperty(name)
        ?? throw new InvalidOperationException($"AnimatedSprite2D has no exported {name}.");
}

/// <summary>
/// The SpriteFrames editor's markup and actions, shared by the resource and node inspectors (ADR 0224; Godot's
/// SpriteFrames panel). Animations: a list (name, frame count, fps, loop and autoplay marks) with Add, Duplicate,
/// Rename, Delete, Loop, Autoplay and a speed field. Frames: a strip of thumbnails (index, relative duration) with Play
/// Backwards / Play / Stop (on a sprite), Add Frames from an Image, Add Frames from a Sprite Sheet (the Select Frames
/// dialog), Copy, Paste, Move Left/Right, Delete and a duration field. Every edit is one undo entry
/// (<see cref="SpriteFramesEditing.Edit"/>); icon buttons with tooltips throughout.
/// </summary>
public sealed class SpriteFramesHeader
{
    private const float Thumb = 48f; // dp

    // What is selected in each resource's editor (the sprite's own Animation wins on a sprite).
    private readonly ConditionalWeakTable<SpriteFrames, Selection> _selection = [];

    // Copied frames (Copy / Paste, across animations and resources): the frames and the textures they index.
    private static (Texture2D?[] Textures, SpriteFrame[] Frames)? _clipboard;

    private sealed class Selection
    {
        public string Animation = SpriteFrames.DefaultAnimation;
        public int Frame;
    }

    public EditorWorkspace? Workspace { get; set; }

    /// <summary>The animation the strip shows: the sprite's current one, else the one picked last, else the first.</summary>
    public SpriteAnimation? Current(SpriteFrames frames, AnimatedSprite2D? sprite)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (sprite is not null && frames.GetAnimation(sprite.Animation) is { } playing)
            return playing;
        var state = _selection.GetOrCreateValue(frames);
        return frames.GetAnimation(state.Animation) ?? (frames.Animations.Count > 0 ? frames.Animations[0] : null);
    }

    /// <summary>The selected frame index of the strip (clamped to the current animation).</summary>
    public int SelectedFrame(SpriteFrames frames, AnimatedSprite2D? sprite)
    {
        var count = Current(frames, sprite)?.FrameCount ?? 0;
        return count == 0 ? -1 : Math.Clamp(_selection.GetOrCreateValue(frames).Frame, 0, count - 1);
    }

    // ── Markup ───────────────────────────────────────────────────────────────────────────────────────────────────

    public string Build(SpriteFrames frames, AnimatedSprite2D? sprite)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var current = Current(frames, sprite);
        var rml = new StringBuilder(4096);
        rml.Append("<div class=\"sprite-frames\">");

        // Animations.
        rml.Append("<div class=\"sf-bar\"><span class=\"sf-label\">Animations</span>");
        Button(rml, "sf-add", "plus", "Add Animation — a new empty animation");
        Button(rml, "sf-dup", "copy", "Duplicate Animation — a copy of the selected animation and its frames", current is not null);
        Button(rml, "sf-rename", "pencil", "Rename Animation — give the selected animation a new name", current is not null);
        Button(rml, "sf-delete", "trash", "Delete Animation — remove the selected animation", current is not null);
        Button(rml, "sf-loop", "repeat", "Loop — the selected animation starts again after its last frame", current is not null, current?.Loop == true);
        if (sprite is not null)
            Button(rml, "sf-autoplay", "player-skip-forward", "Autoplay on Load — the sprite plays the selected animation when the game starts",
                current is not null, current is not null && sprite.Autoplay == current.Name);
        rml.Append("</div><div class=\"sf-anims\">");
        for (var i = 0; i < frames.Animations.Count; i++)
        {
            if (frames.Animations[i] is not { } animation)
                continue;
            var selected = ReferenceEquals(animation, current);
            rml.Append("<div class=\"sf-anim").Append(selected ? " selected" : "").Append("\" data-action=\"sf-anim:")
                .Append(i.ToString(CultureInfo.InvariantCulture)).Append("\" data-tooltip=\"").Append(RmlText.Escape(animation.Name))
                .Append(sprite is null ? " — select to edit its frames" : " — select to edit its frames and show it on the sprite").Append("\">")
                .Append("<span class=\"icon icon-sm icon-movie\"></span><span class=\"sf-anim-name\">").Append(RmlText.Escape(animation.Name)).Append("</span>");
            if (sprite is not null && sprite.Autoplay == animation.Name)
                rml.Append("<span class=\"icon icon-sm icon-player-skip-forward sf-mark\"></span>");
            if (animation.Loop)
                rml.Append("<span class=\"icon icon-sm icon-repeat sf-mark\"></span>");
            rml.Append("<span class=\"sf-meta\">").Append(animation.FrameCount.ToString(CultureInfo.InvariantCulture)).Append(" · ")
                .Append(animation.Speed.ToString("0.##", CultureInfo.InvariantCulture)).Append(" fps</span></div>");
        }

        if (frames.Animations.Count == 0)
            rml.Append("<div class=\"sf-empty\">No animations: add one.</div>");
        rml.Append("</div>");
        if (current is null)
            return rml.Append("</div>").ToString();

        rml.Append("<div class=\"sf-field\" data-tooltip=\"Speed — frames per second at speed scale 1\"><span class=\"sf-label\">Speed (FPS)</span>")
            .Append("<input type=\"text\" class=\"text\" data-header-input=\"sf-speed\" value=\"")
            .Append(current.Speed.ToString("0.##", CultureInfo.InvariantCulture)).Append("\"/></div>");

        // Frames.
        var selectedFrame = SelectedFrame(frames, sprite);
        var hasFrame = selectedFrame >= 0;
        rml.Append("<div class=\"sf-bar\"><span class=\"sf-label\">Frames</span>");
        if (sprite is not null)
        {
            var preview = Workspace?.SpritePreview;
            var playing = preview?.IsPlaying(sprite) == true;
            Button(rml, playing && preview!.Backwards ? "sf-stop" : "sf-play-back", playing && preview!.Backwards ? "player-stop" : "player-track-prev",
                playing && preview!.Backwards ? "Stop — end the preview" : "Play Backwards — preview the animation on the sprite from its last frame",
                current.FrameCount > 0, playing && preview!.Backwards);
            Button(rml, playing && !preview!.Backwards ? "sf-stop" : "sf-play", playing && !preview!.Backwards ? "player-stop" : "player-play",
                playing && !preview!.Backwards ? "Stop — end the preview" : "Play — preview the animation on the sprite (the scene is not edited)",
                current.FrameCount > 0, playing && !preview!.Backwards);
            rml.Append("<span class=\"sf-sep\"></span>");
        }

        Button(rml, "sf-add-file", "file-plus", "Add Frame from Image — the whole of an image file as a new frame");
        Button(rml, "sf-add-sheet", "layout-grid", "Add Frames from Sprite Sheet — slice an image into a grid and pick frames (Select Frames)");
        rml.Append("<span class=\"sf-sep\"></span>");
        Button(rml, "sf-copy", "copy", "Copy Frame — the selected frame", hasFrame);
        Button(rml, "sf-paste", "clipboard", "Paste Frame — after the selected frame", _clipboard is not null);
        Button(rml, "sf-left", "arrow-left", "Move Frame Left — earlier in the animation", hasFrame && selectedFrame > 0);
        Button(rml, "sf-right", "arrow-right", "Move Frame Right — later in the animation", hasFrame && selectedFrame < current.FrameCount - 1);
        Button(rml, "sf-delete-frame", "trash", "Delete Frame — remove the selected frame", hasFrame);
        rml.Append("</div><div class=\"sf-strip\">");
        for (var i = 0; i < current.FrameCount; i++)
            AppendFrame(rml, current, i, i == selectedFrame);
        if (current.FrameCount == 0)
            rml.Append("<div class=\"sf-empty\">No frames: add them from an image or a sprite sheet.</div>");
        rml.Append("</div>");
        if (hasFrame)
            rml.Append("<div class=\"sf-field\" data-tooltip=\"Duration — how long the selected frame shows, relative to the others (2 = twice as long)\">")
                .Append("<span class=\"sf-label\">Frame ").Append(selectedFrame.ToString(CultureInfo.InvariantCulture)).Append(" duration</span>")
                .Append("<input type=\"text\" class=\"text\" data-header-input=\"sf-duration\" value=\"")
                .Append(current.Frames[selectedFrame].Duration.ToString("0.##", CultureInfo.InvariantCulture)).Append("\"/></div>");
        return rml.Append("</div>").ToString();
    }

    private static void AppendFrame(StringBuilder rml, SpriteAnimation animation, int index, bool selected)
    {
        var frame = animation.Frames[index];
        rml.Append("<div class=\"sf-frame").Append(selected ? " selected" : "").Append("\" data-action=\"sf-frame:")
            .Append(index.ToString(CultureInfo.InvariantCulture)).Append("\" data-tooltip=\"Frame ").Append(index.ToString(CultureInfo.InvariantCulture))
            .Append(" — duration ").Append(frame.Duration.ToString("0.##", CultureInfo.InvariantCulture)).Append("\"><div class=\"sf-thumb\">");
        if (animation.TryGetFrame(index, out var texture, out var region) && ImagePath(texture) is { } path && region.Size.X > 0 && region.Size.Y > 0)
        {
            var scale = MathF.Min(Thumb / region.Size.X, Thumb / region.Size.Y);
            rml.Append("<img src=\"").Append(RmlText.Escape(path)).Append("\" rect=\"")
                .Append(region.Position.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append(region.Position.Y.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append(region.Size.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append(region.Size.Y.ToString("0.##", CultureInfo.InvariantCulture)).Append("\" style=\"width: ")
                .Append(RmlText.Dp(region.Size.X * scale)).Append("; height: ").Append(RmlText.Dp(region.Size.Y * scale)).Append(";\"/>");
        }
        else
        {
            rml.Append("<span class=\"icon icon-photo icon-muted\"></span>");
        }

        rml.Append("</div><span class=\"sf-index\">").Append(index.ToString(CultureInfo.InvariantCulture));
        if (frame.Duration != 1f)
            rml.Append(" ×").Append(frame.Duration.ToString("0.##", CultureInfo.InvariantCulture));
        rml.Append("</span></div>");
    }

    // The image file a texture was imported from (absolute), or null for generated textures.
    private static string? ImagePath(Texture2D texture)
    {
        if (texture.ResourcePath is not { Length: > 0 } path)
            return null;
        try
        {
            var full = AssetDatabase.Current.ToAbsolutePath(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Button(StringBuilder rml, string action, string icon, string tooltip, bool enabled = true, bool active = false)
    {
        rml.Append("<button class=\"tool-button small").Append(active ? " active" : "").Append(enabled ? "" : " disabled").Append('"');
        if (enabled)
            rml.Append(" data-action=\"").Append(action).Append('"');
        rml.Append(" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span></button>");
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a header action (<c>sf-*</c>; inputs arrive as <c>sf-speed=12</c>) on <paramref name="frames"/>.</summary>
    public void Run(SpriteFrames frames, AnimatedSprite2D? sprite, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var state = _selection.GetOrCreateValue(frames);
        var current = Current(frames, sprite);
        var (name, argument) = Split(action);
        var frame = SelectedFrame(frames, sprite);
        switch (name)
        {
            case "sf-anim":
                if (Index(argument) is { } a && (uint)a < (uint)frames.Animations.Count && frames.Animations[a] is { } picked)
                    Select(frames, sprite, picked, context);
                break;
            case "sf-frame":
                if (Index(argument) is { } f)
                    state.Frame = f;
                break;
            case "sf-add":
                var added = SpriteFramesEditing.UniqueName(frames, frames.Animations.Count == 0 ? SpriteFrames.DefaultAnimation : "new");
                if (SpriteFramesEditing.Edit(context, frames, "Add Animation", f => f.AddAnimation(added)))
                    Select(frames, sprite, frames.GetAnimation(added)!, context);
                break;
            case "sf-dup" when current is not null:
                var copyName = SpriteFramesEditing.UniqueName(frames, current.Name);
                SpriteAnimation? copy = null;
                if (SpriteFramesEditing.Edit(context, frames, "Duplicate Animation", f => copy = SpriteFramesEditing.Duplicate(f, current, copyName)))
                    Select(frames, sprite, copy!, context);
                break;
            case "sf-rename" when current is not null:
                PromptRename(frames, sprite, current, context);
                return;
            case "sf-delete" when current is not null:
                SpriteFramesEditing.Edit(context, frames, "Delete Animation", f => f.RemoveAnimation(current.Name));
                if (sprite is not null && frames.Animations.Count > 0 && frames.Animations[0] is { } first)
                    Select(frames, sprite, first, context);
                break;
            case "sf-loop" when current is not null:
                SpriteFramesEditing.Edit(context, frames, current.Loop ? "Loop Off" : "Loop On", _ => current.Loop = !current.Loop);
                break;
            case "sf-autoplay" when current is not null && sprite is not null:
                context.SetProperty(sprite, AnimatedSprite2DInspector.Property(nameof(AnimatedSprite2D.Autoplay)),
                    sprite.Autoplay == current.Name ? "" : current.Name);
                break;
            case "sf-speed" when current is not null:
                if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && float.IsFinite(fps))
                    SpriteFramesEditing.Edit(context, frames, "Set Animation Speed", _ => current.Speed = Math.Clamp(fps, 0f, 120f));
                break;
            case "sf-duration" when current is not null && frame >= 0:
                if (float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                    SpriteFramesEditing.Edit(context, frames, "Set Frame Duration", _ => SpriteFramesEditing.SetDuration(current, frame, duration));
                break;
            case "sf-play" when sprite is not null:
                Workspace?.SpritePreview.Play(sprite);
                break;
            case "sf-play-back" when sprite is not null:
                Workspace?.SpritePreview.Play(sprite, backwards: true);
                break;
            case "sf-stop":
                Workspace?.SpritePreview.Stop();
                break;
            case "sf-add-file" when current is not null:
                PickImage("Add Frame from Image", texture =>
                {
                    SpriteFramesEditing.Edit(context, frames, "Add Frame", _ => current.AddFrame(texture));
                    state.Frame = current.FrameCount - 1;
                });
                return;
            case "sf-add-sheet" when current is not null:
                PickImage("Add Frames from Sprite Sheet", texture => SliceSheet(texture, (grid, cells) =>
                {
                    var before = current.FrameCount;
                    SpriteFramesEditing.Edit(context, frames, cells.Count == 1 ? "Add Frame" : "Add Frames",
                        _ => current.AddFrames(texture, grid, [.. cells]));
                    state.Frame = before;
                    Workspace?.Inspector.Rebuild();
                }));
                return;
            case "sf-copy" when current is not null && frame >= 0:
                _clipboard = (current.Textures, [current.Frames[frame]]);
                break;
            case "sf-paste" when current is not null && _clipboard is { } clip:
                var source = new SpriteAnimation { Textures = clip.Textures, Frames = clip.Frames };
                var at = frame < 0 ? current.FrameCount : frame + 1;
                if (SpriteFramesEditing.Edit(context, frames, "Paste Frame", _ => SpriteFramesEditing.InsertFrames(current, source, clip.Frames, at)))
                    state.Frame = at;
                break;
            case "sf-left" when current is not null && frame > 0:
                SpriteFramesEditing.Edit(context, frames, "Move Frame", _ => state.Frame = SpriteFramesEditing.MoveFrame(current, frame, -1));
                break;
            case "sf-right" when current is not null && frame >= 0:
                SpriteFramesEditing.Edit(context, frames, "Move Frame", _ => state.Frame = SpriteFramesEditing.MoveFrame(current, frame, 1));
                break;
            case "sf-delete-frame" when current is not null && frame >= 0:
                SpriteFramesEditing.Edit(context, frames, "Delete Frame", _ => SpriteFramesEditing.RemoveFrames(current, frame));
                break;
            default:
                return;
        }

        Workspace?.Inspector.Rebuild();
    }

    // Picking an animation shows its frames; on a sprite it also becomes the sprite's Animation (undoable, as Godot).
    private void Select(SpriteFrames frames, AnimatedSprite2D? sprite, SpriteAnimation animation, IInspectorContext context)
    {
        var state = _selection.GetOrCreateValue(frames);
        if (state.Animation != animation.Name)
            state.Frame = 0;
        state.Animation = animation.Name;
        if (sprite is not null && sprite.Animation != animation.Name)
        {
            Workspace?.SpritePreview.Stop();
            context.SetProperty(sprite, AnimatedSprite2DInspector.Property(nameof(AnimatedSprite2D.Animation)), animation.Name);
        }
    }

    private void PromptRename(SpriteFrames frames, AnimatedSprite2D? sprite, SpriteAnimation animation, IInspectorContext context)
    {
        if (Workspace is not { } workspace)
            return;
        var old = animation.Name;
        workspace.Message.Show(new MessageRequest
        {
            Title = "Rename Animation",
            Message = $"New name for the animation \"{old}\":",
            Input = old,
            Buttons = ["Rename", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, text) =>
            {
                var name = text.Trim();
                if (button != 0 || name.Length == 0 || name == old)
                    return;
                if (frames.HasAnimation(name))
                {
                    workspace.Message.Show(new MessageRequest { Title = "Rename Animation", Message = $"There is already an animation named \"{name}\"." });
                    return;
                }

                Rename(frames, sprite, old, name, context);
                workspace.Inspector.Rebuild();
            },
        });
    }

    /// <summary>Renames an animation (one undo entry); a sprite showing or autoplaying it follows the new name.</summary>
    public void Rename(SpriteFrames frames, AnimatedSprite2D? sprite, string from, string to, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(context);
        if (!frames.HasAnimation(from) || frames.HasAnimation(to) || string.IsNullOrEmpty(to))
            return;
        SpriteFramesEditing.Edit(context, frames, "Rename Animation", f => f.RenameAnimation(from, to));
        _selection.GetOrCreateValue(frames).Animation = to;
        if (sprite is null)
            return;
        if (sprite.Animation == from)
            context.SetProperty(sprite, AnimatedSprite2DInspector.Property(nameof(AnimatedSprite2D.Animation)), to);
        if (sprite.Autoplay == from)
            context.SetProperty(sprite, AnimatedSprite2DInspector.Property(nameof(AnimatedSprite2D.Autoplay)), to);
    }

    private void PickImage(string title, Action<Texture2D> then)
    {
        if (Workspace is not { } workspace)
            return;
        var start = workspace.Session.ProjectRoot is { } root && Directory.Exists(Path.Combine(root, "Assets"))
            ? Path.Combine(root, "Assets")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, start, ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tga", "*.svg"]);
        workspace.FilePicker.Show(model, title, "Open", path =>
        {
            try
            {
                then(ResourceLoader.Load<Texture2D>(AssetDatabase.Current.ToProjectPath(path)));
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                workspace.Commands.ReportError($"Could not load {Path.GetFileName(path)}", e);
            }

            workspace.Inspector.Rebuild();
        });
    }

    private void SliceSheet(Texture2D texture, Action<SpriteSheetGrid, IReadOnlyList<Vector2I>> onAdd)
    {
        if (Workspace is not { } workspace || texture.ResourcePath is not { } path)
            return;
        workspace.SheetSlicer.Show(path, new Vector2I(texture.Width, texture.Height), "Select Frames — " + Path.GetFileName(path), onAdd);
    }

    private static (string Name, string Argument) Split(string action)
    {
        var at = action.IndexOfAny([':', '=']);
        return at < 0 ? (action, "") : (action[..at], action[(at + 1)..]);
    }

    private static int? Index(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}

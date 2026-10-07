using System.Globalization;
using System.Text;
using MainframeEngine.Audio;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>A custom inspector that needs the editor (previews, dialogs, clipboard): the inspector panel binds it before each call.</summary>
internal interface IWorkspaceInspector
{
    EditorWorkspace? Workspace { get; set; }

    /// <summary>The preview started or ended: update the header's Play/Stop in place (no rebuild).</summary>
    void RefreshPreview(object target, RmlDocument document);
}

/// <summary>
/// The sound designer: the inspector of a <see cref="ZzfxStream"/> resource. Above the generated parameter sliders, a
/// header with Play/Stop and Auto-play (replays after every committed change, undo and redo), the length or load error,
/// a 96-column waveform, preset buttons (<see cref="ZzfxPresets"/>), Randomize, Mutate, Copy/Paste of a ZzFX line and
/// Export .wav. Presets, Randomize, Mutate and Paste set every parameter as one undo entry.
/// </summary>
[CustomInspector(typeof(ZzfxStream))]
public sealed class ZzfxStreamInspector : ICustomInspector, IWorkspaceInspector
{
    /// <summary>Waveform columns (min/max peaks each).</summary>
    public const int Columns = 96;

    private const float WaveHeight = 50f; // dp, inside .zzfx-wave's padding

    /// <summary>The stream's exported properties in ZzFX parameter order (randomness is <see cref="AudioStream.PitchRandomness"/>).</summary>
    private static readonly string[] PropertyNames =
    [
        nameof(ZzfxStream.Volume), nameof(AudioStream.PitchRandomness), nameof(ZzfxStream.Frequency), nameof(ZzfxStream.Attack),
        nameof(ZzfxStream.Sustain), nameof(ZzfxStream.ReleaseTime), nameof(ZzfxStream.Shape), nameof(ZzfxStream.ShapeCurve),
        nameof(ZzfxStream.Slide), nameof(ZzfxStream.DeltaSlide), nameof(ZzfxStream.PitchJump), nameof(ZzfxStream.PitchJumpTime),
        nameof(ZzfxStream.RepeatTime), nameof(ZzfxStream.Noise), nameof(ZzfxStream.Modulation), nameof(ZzfxStream.BitCrush),
        nameof(ZzfxStream.Delay), nameof(ZzfxStream.SustainVolume), nameof(ZzfxStream.Decay), nameof(ZzfxStream.Tremolo),
        nameof(ZzfxStream.Filter),
    ];

    private const int ShapeIndex = 6;

    /// <summary>The presets in button order: action suffix, icon, tooltip, generator.</summary>
    public static readonly IReadOnlyList<(string Name, string Icon, string Tooltip, Func<Random, ZzfxParameters> Make)> Presets =
    [
        ("pickup", "coin", "Pickup / Coin — replace every parameter", ZzfxPresets.Pickup),
        ("laser", "bolt", "Laser / Shoot — replace every parameter", ZzfxPresets.Laser),
        ("explosion", "flame", "Explosion — replace every parameter", ZzfxPresets.Explosion),
        ("hit", "target", "Hit / Hurt — replace every parameter", ZzfxPresets.Hit),
        ("jump", "arrow-up", "Jump — replace every parameter", ZzfxPresets.Jump),
        ("blip", "activity", "Blip / Select — replace every parameter", ZzfxPresets.Blip),
        ("powerup", "sparkles", "Power-up — replace every parameter", ZzfxPresets.PowerUp),
    ];

    private static ExportPropertyInfo[]? _properties;
    private readonly float[] _peaks = new float[Columns * 2];
    private ZzfxParameters? _peaksFor;
    private string? _peaksError;
    private double _peaksSeconds;

    public EditorWorkspace? Workspace { get; set; }

    private bool AutoPlay => Workspace?.Layout.Settings.SoundDesignerAutoPlay ?? false;

    // ── Header ───────────────────────────────────────────────────────────────────────────────────────────────────

    public string? GetHeaderRml(object target)
    {
        var stream = (ZzfxStream)target;
        var playing = Workspace?.AudioPreview.IsPlaying(stream) == true;
        var autoPlay = AutoPlay;
        var rml = new StringBuilder(8192);
        rml.Append("<div class=\"zzfx\"><div class=\"zzfx-top\">");
        AppendPlayButton(rml, playing);
        rml.Append("<button class=\"tool-button").Append(autoPlay ? " active" : "")
            .Append("\" data-action=\"zzfx-autoplay\" data-tooltip=\"Auto-play — replay the sound after every change, undo and redo (")
            .Append(autoPlay ? "on" : "off").Append(")\"><span class=\"icon icon-sm icon-repeat\"></span></button>");

        UpdatePeaks(stream.Parameters);
        var error = stream.LoadError ?? _peaksError;
        if (error is null)
            rml.Append("<span class=\"zzfx-info mono\">").Append(_peaksSeconds.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" s · 44.1 kHz mono</span></div>");
        else
            rml.Append("</div><div class=\"notice\">").Append(RmlText.Escape(error)).Append("</div>");

        if (error is null)
        {
            rml.Append("<div class=\"zzfx-wave\">");
            for (var i = 0; i < Columns; i++)
            {
                var min = _peaks[i * 2];
                var max = _peaks[i * 2 + 1];
                var top = (1f - max) * 0.5f * WaveHeight;
                var height = MathF.Max(1f, (max - min) * 0.5f * WaveHeight);
                rml.Append("<div class=\"zzfx-col\" style=\"margin-top: ").Append(RmlText.Dp(top)).Append("; height: ")
                    .Append(RmlText.Dp(height)).Append(";\"></div>");
            }

            rml.Append("</div>");
        }

        rml.Append("<div class=\"zzfx-row\"><span class=\"zzfx-label\">Presets</span>");
        foreach (var preset in Presets)
            AppendButton(rml, "zzfx-preset:" + preset.Name, preset.Icon, preset.Tooltip);
        rml.Append("</div><div class=\"zzfx-row\"><span class=\"zzfx-label\">Edit</span>");
        AppendButton(rml, "zzfx-randomize", "dice-5", "Randomize — a new random sound");
        AppendButton(rml, "zzfx-mutate", "wand", "Mutate — nudge every parameter a little (±10 %)");
        AppendButton(rml, "zzfx-copy", "copy", "Copy ZzFX — the sound as a zzfx(...) line");
        AppendButton(rml, "zzfx-paste", "clipboard", "Paste ZzFX — replace the sound with a zzfx(...) line from the clipboard");
        AppendButton(rml, "zzfx-export", "file-export", "Export .wav — write the sound as a 16-bit 44.1 kHz mono file");
        return rml.Append("</div></div>").ToString();
    }

    // The generated rows edit the parameters; File and LoadMode do not apply to a synthesised sound.
    public bool ShowProperty(object target, ExportPropertyInfo property) =>
        property.Name is not (nameof(AudioStream.File) or nameof(AudioStream.LoadMode));

    public void RefreshPreview(object target, RmlDocument document)
    {
        var button = document.GetElementById("zzfx-play");
        if (button.IsNull)
            return;
        var playing = Workspace?.AudioPreview.IsPlaying(target as AudioStream) == true;
        button.SetClass("active", playing);
        button.SetAttribute("data-tooltip", playing ? StopTip : PlayTip);
        button.SetInnerRml(playing ? "<span class=\"icon icon-player-stop\"></span>" : "<span class=\"icon icon-player-play\"></span>");
    }

    private const string PlayTip = "Play — preview this sound in the editor";
    private const string StopTip = "Stop — stop the preview";

    private static void AppendPlayButton(StringBuilder rml, bool playing) =>
        rml.Append("<button class=\"tool-button zzfx-play").Append(playing ? " active" : "")
            .Append("\" id=\"zzfx-play\" data-action=\"zzfx-play\" data-tooltip=\"").Append(playing ? StopTip : PlayTip)
            .Append("\"><span class=\"icon icon-").Append(playing ? "player-stop" : "player-play").Append("\"></span></button>");

    private static void AppendButton(StringBuilder rml, string action, string icon, string tooltip) =>
        rml.Append("<button class=\"tool-button\" data-action=\"").Append(action).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip))
            .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span></button>");

    // Min/max per column over the generated samples, normalised to the loudest peak (the shape, not the level).
    private void UpdatePeaks(in ZzfxParameters parameters)
    {
        if (_peaksFor == parameters)
            return;
        _peaksFor = parameters;
        _peaksError = null;
        Array.Clear(_peaks);
        _peaksSeconds = Zzfx.Duration(parameters);
        if (!(_peaksSeconds <= Zzfx.MaxSeconds))
        {
            _peaksError = $"The sound is {_peaksSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s long: longer than {Zzfx.MaxSeconds.ToString(CultureInfo.InvariantCulture)} s, so it stays silent.";
            return;
        }

        float[] samples;
        try
        {
            samples = Zzfx.Generate(parameters);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _peaksError = e.Message;
            return;
        }

        ComputePeaks(samples, _peaks);
    }

    /// <summary>Fills <paramref name="peaks"/> (min, max per column) from <paramref name="samples"/>, scaled so the loudest is ±1.</summary>
    internal static void ComputePeaks(ReadOnlySpan<float> samples, Span<float> peaks)
    {
        var columns = peaks.Length / 2;
        var loudest = 0f;
        foreach (var s in samples)
            loudest = MathF.Max(loudest, MathF.Abs(s));
        var scale = loudest > 0f ? 1f / loudest : 0f;
        for (var c = 0; c < columns; c++)
        {
            var start = (int)((long)samples.Length * c / columns);
            var end = (int)((long)samples.Length * (c + 1) / columns);
            float min = 0, max = 0;
            for (var i = start; i < end; i++)
            {
                min = MathF.Min(min, samples[i]);
                max = MathF.Max(max, samples[i]);
            }

            peaks[c * 2] = min * scale;
            peaks[c * 2 + 1] = max * scale;
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var stream = (ZzfxStream)target;
        switch (action)
        {
            case "zzfx-play":
                Workspace?.AudioPreview.Toggle(stream);
                return;
            case "zzfx-autoplay":
                if (Workspace is { } workspace)
                {
                    workspace.Layout.SetSoundDesignerAutoPlay(!AutoPlay);
                    workspace.Inspector.Rebuild();
                }

                return;
            case "zzfx-randomize":
                Randomize(stream, new Random(), context);
                return;
            case "zzfx-mutate":
                Mutate(stream, new Random(), context);
                return;
            case "zzfx-copy":
                if (Workspace?.PanelLayer.Server is { } copyServer)
                {
                    copyServer.ClipboardText = stream.ToLine();
                    Log.Info($"[Editor] Copied {stream.ToLine()}");
                }

                return;
            case "zzfx-paste":
                if (Workspace?.PanelLayer.Server is { } pasteServer && !Paste(stream, pasteServer.ClipboardText, context, out var error))
                    Workspace.Message.Show(new MessageRequest { Title = "Paste ZzFX", Message = error ?? "Not a ZzFX line." });
                return;
            case "zzfx-export":
                ShowExport(stream);
                return;
        }

        if (action.StartsWith("zzfx-preset:", StringComparison.Ordinal))
            ApplyPreset(stream, action["zzfx-preset:".Length..], new Random(), context);
    }

    /// <summary>Auto-play: replays the sound after every committed change (rows, actions, undo, redo).</summary>
    public void OnPropertyChanged(object target, ExportPropertyInfo? property, IInspectorContext context)
    {
        if (AutoPlay && target is ZzfxStream stream && Workspace is { } workspace)
            workspace.AudioPreview.Play(stream);
    }

    /// <summary>Replaces every parameter with preset <paramref name="name"/> (<see cref="Presets"/>) as one undo entry.</summary>
    public static bool ApplyPreset(ZzfxStream stream, string name, Random random, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(random);
        foreach (var preset in Presets)
            if (string.Equals(preset.Name, name, StringComparison.Ordinal))
                return SetParameters(stream, preset.Make(random), "Preset " + name, context);
        return false;
    }

    /// <summary>A new random sound (<see cref="ZzfxPresets.Randomize"/>) as one undo entry.</summary>
    public static bool Randomize(ZzfxStream stream, Random random, IInspectorContext context) =>
        SetParameters(stream, ZzfxPresets.Randomize(random), "Randomize Sound", context);

    /// <summary>Nudges the parameters (<see cref="ZzfxPresets.Mutate"/>) as one undo entry.</summary>
    public static bool Mutate(ZzfxStream stream, Random random, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return SetParameters(stream, ZzfxPresets.Mutate(stream.Parameters, random), "Mutate Sound", context);
    }

    /// <summary>Replaces the sound with a ZzFX line as one undo entry; false (with the parse error) when it does not parse.</summary>
    public static bool Paste(ZzfxStream stream, string? line, IInspectorContext context, out string? error)
    {
        if (!ZzfxParameters.TryParse(line, out var parameters, out error))
            return false;
        SetParameters(stream, parameters, "Paste ZzFX", context);
        return true;
    }

    /// <summary>Sets every parameter that differs as ONE history entry (undo restores them exactly). False when nothing changed.</summary>
    public static bool SetParameters(ZzfxStream stream, in ZzfxParameters next, string name, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        var properties = Properties();
        var current = stream.Parameters;
        var actions = new List<IEditorAction>(ZzfxParameters.Count);
        for (var i = 0; i < ZzfxParameters.Count; i++)
        {
            if (current[i].Equals(next[i]))
                continue;
            actions.Add(new SetPropertyAction(stream, properties[i], Box(i, current[i]), Box(i, next[i])));
        }

        if (actions.Count == 0)
            return false;
        context.History.Commit(new CompositeAction(name, [.. actions]));
        return true;
    }

    // The values as the properties take them (Shape is the enum).
    private static object Box(int index, float value) => index == ShapeIndex ? Enum.ToObject(typeof(ZzfxShape), (int)value) : value;

    private static ExportPropertyInfo[] Properties() => _properties ??= Array.ConvertAll(PropertyNames, name =>
        TypeRegistry.GetRequired(typeof(ZzfxStream)).FindProperty(name) ?? throw new InvalidOperationException($"ZzfxStream.{name} is not exported."));

    // ── Export .wav ──────────────────────────────────────────────────────────────────────────────────────────────

    private void ShowExport(ZzfxStream stream)
    {
        if (Workspace is not { } workspace)
            return;
        var folder = workspace.Inspector.InspectedResource is { } file
            ? Path.GetDirectoryName(file.FilePath)!
            : workspace.Session.ProjectRoot ?? Directory.GetCurrentDirectory();
        // <sound folder>/<name>.wav: the resource's name, else its file's.
        var stem = !string.IsNullOrWhiteSpace(stream.ResourceName) ? stream.ResourceName
            : workspace.Inspector.InspectedResource is { } inspected ? Path.GetFileNameWithoutExtension(inspected.FilePath) : "Sound";
        var name = stem + ".wav";
        var model = new FilePickerModel(FilePickerMode.Save, folder, ["*.wav"], name);
        workspace.FilePicker.Show(model, "Export .wav", "Export", path =>
        {
            try
            {
                ExportWav(stream, path);
                Log.Info($"[Editor] Exported {workspace.Session.DisplayPath(path)}");
                workspace.FileSystem.Rescan();
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e) || e is ArgumentException or InvalidOperationException)
            {
                workspace.Commands.ReportError($"Could not export {Path.GetFileName(path)}", e);
            }
        });
    }

    /// <summary>Writes the sound to <paramref name="path"/> (16-bit PCM, 44.1 kHz, mono); a partial file is deleted on failure.</summary>
    public static void ExportWav(ZzfxStream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var samples = Zzfx.Generate(stream.Parameters);
        try
        {
            WavWriter.Write(path, samples, 1, Zzfx.SampleRate, WavSampleFormat.Pcm16);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}

using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine;

/// <summary>
/// Baked adaptive music (<c>.mmusic</c>): the manifest the editor's Bake writes, compiled into a <see cref="MusicPlan"/>,
/// plus its stems. Play it with an <see cref="AdaptiveMusicPlayer"/> node (or an <see cref="AdaptiveMusicInstance"/>).
/// Loaded through <see cref="ResourceLoader"/> by <see cref="AdaptiveMusicImporter"/>, so an <c>[Export] AdaptiveMusic</c>
/// property references it by its <c>mus_</c> UID. See docs/design/future/adaptive-music.md.
/// </summary>
/// <remarks>
/// <para><b>Load modes.</b> Section bodies stream (a whole score decoded would not fit in memory); tails, stingers and
/// transition segments are decoded into memory, because they must start on an exact frame with no prefill.
/// <see cref="PreloadSections"/> memory-loads chosen sections too. Stems load at <see cref="Preload"/> (players call it
/// when they enter the tree) or at the first play.</para>
/// <para><b>Failures</b> never stop the music: a missing or undecodable stem logs one error naming its section and layer
/// and plays as silence; problems in the manifest are logged once and what cannot be used is left out.</para>
/// </remarks>
[EditorIcon("music")]
public sealed class AdaptiveMusic : Resource
{
    private MusicManifest _manifest = new();
    private MusicPlan? _plan;
    private MusicRuntime? _runtime;

    /// <summary>The manifest (do not modify it once the music has played).</summary>
    public MusicManifest Manifest => _manifest;

    /// <summary>The compiled logic shared by every instance.</summary>
    public MusicPlan Plan => _plan ??= Compile(_manifest);

    /// <summary>Section ids whose bodies load into memory instead of streaming (set before <see cref="Preload"/>).</summary>
    public List<string> PreloadSections { get; } = [];

    /// <summary>A note when the file was baked by a newer editor (read best-effort), else null.</summary>
    public string? LoadNotice { get; private set; }

    /// <summary>Stems that could not load at the last <see cref="Preload"/> (they play as silence).</summary>
    public int MissingStems { get; private set; }

    /// <summary>Reads a <c>.mmusic</c> file (project path or absolute). Throws for files that are not adaptive music.</summary>
    public static AdaptiveMusic Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = AssetDatabase.Current.ToAbsolutePath(path);
        var (manifest, notice) = MusicManifestFormat.Load(full);
        var music = FromManifest(manifest, Path.GetFileNameWithoutExtension(path));
        music.LoadNotice = notice;
        if (notice is not null)
            Log.Warning($"[Audio] {notice}");
        return music;
    }

    /// <summary>Wraps a manifest built in code (tests, the editor's audition). Stem paths are project paths, UIDs or absolute.</summary>
    public static AdaptiveMusic FromManifest(MusicManifest manifest, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var music = new AdaptiveMusic { _manifest = manifest, ResourceName = name ?? "Adaptive music" };
        if (AssetUid.IsUid(manifest.Uid))
            music.Uid = manifest.Uid;
        return music;
    }

    /// <summary>Loads every stem now (decodes the memory ones, probes the streamed ones); false when some are missing.</summary>
    public bool Preload()
    {
        GetRuntime();
        return MissingStems == 0;
    }

    /// <summary>Drops the loaded stems (the next play loads them again, e.g. after changing <see cref="PreloadSections"/>).</summary>
    public void Unload() => _runtime = null;

    internal MusicRuntime GetRuntime() => _runtime ??= BuildRuntime();

    private MusicPlan Compile(MusicManifest manifest)
    {
        var plan = MusicPlan.Compile(manifest);
        foreach (var problem in plan.Problems)
            Log.Warning($"[Audio] Adaptive music '{Label}': {problem}");
        return plan;
    }

    private string Label => ResourcePath ?? ResourceName ?? "?";

    private MusicRuntime BuildRuntime()
    {
        var plan = Plan;
        var byId = new Dictionary<string, MusicManifestSection>(StringComparer.Ordinal);
        foreach (var s in _manifest.Sections)
        {
            if (s is not null && !string.IsNullOrEmpty(s.Id))
                byId.TryAdd(s.Id, s);
        }

        var missing = 0;
        var sections = new MusicSectionStems[plan.Sections.Length];
        for (var i = 0; i < sections.Length; i++)
        {
            var section = plan.Sections[i];
            var stems = sections[i] = new MusicSectionStems(plan.Layers.Length);
            if (!byId.TryGetValue(section.Id, out var source))
                continue;
            if (section.Kind == MusicSectionKind.Stinger)
            {
                if (source.Stems.GetValueOrDefault(MusicManifestSection.MixStem) is { Body: { } mix })
                    stems.Mix = LoadStem(section.Id, MusicManifestSection.MixStem, mix, AudioLoadMode.Memory, ref missing) as AudioClipData;
                continue;
            }

            var bodyMode = section.Kind == MusicSectionKind.Section && !PreloadSections.Contains(section.Id) ? AudioLoadMode.Stream : AudioLoadMode.Memory;
            for (var l = 0; l < plan.Layers.Length; l++)
            {
                if (source.Stems.GetValueOrDefault(plan.Layers[l].Id) is not { } set)
                    continue;
                if (set.Body is { } body)
                    stems.Bodies[l] = LoadStem(section.Id, plan.Layers[l].Id, body, bodyMode, ref missing);
                if (set.Tail is { } tail)
                    stems.Tails[l] = LoadStem(section.Id, plan.Layers[l].Id, tail, AudioLoadMode.Memory, ref missing) as AudioClipData;
                stems.Streamed |= stems.Bodies[l] is AudioStreamSource;
            }

            LoadTailPoints(plan, section, source, stems, ref missing);
        }

        MissingStems = missing;
        return new MusicRuntime(plan, sections);
    }

    // Exit markers baked as tail points ("cut here + tail" per layer), in frame order.
    private void LoadTailPoints(MusicPlan plan, MusicPlanSection section, MusicManifestSection source, MusicSectionStems stems, ref int missing)
    {
        List<long>? frames = null;
        List<AudioClipData?[]>? tails = null;
        foreach (var marker in (source.Markers ?? []).Where(m => m?.Tails is not null).OrderBy(m => m.Frame))
        {
            var frame = Math.Clamp(marker.Frame, 0, section.LengthFrames);
            if (frame <= 0 || frame >= section.LengthFrames)
                continue; // at the start or the end: the section's own tails cover it
            var byLayer = new AudioClipData?[plan.Layers.Length];
            for (var l = 0; l < byLayer.Length; l++)
            {
                if (marker.Tails!.GetValueOrDefault(plan.Layers[l].Id) is { } tail)
                    byLayer[l] = LoadStem($"{section.Id}@{marker.Name}", plan.Layers[l].Id, tail, AudioLoadMode.Memory, ref missing) as AudioClipData;
            }

            (frames ??= []).Add(frame);
            (tails ??= []).Add(byLayer);
        }

        if (frames is null)
            return;
        stems.TailPointFrames = [.. frames];
        stems.TailPointTails = [.. tails!];
    }

    private AudioSource? LoadStem(string section, string layer, MusicStemRef stem, AudioLoadMode mode, ref int missing)
    {
        var assets = AssetDatabase.Current;
        var file = AssetUid.IsUid(stem.Uid) && assets.GetPath(stem.Uid) is { } byUid ? byUid : stem.Path;
        if (string.IsNullOrEmpty(file) || !File.Exists(assets.ToAbsolutePath(file)))
        {
            missing++;
            Log.Error($"[Audio] Adaptive music '{Label}': the {section}/{layer} stem '{(string.IsNullOrEmpty(file) ? stem.Uid : file)}' is missing; that lane is silent.");
            return null;
        }

        var source = new AudioStream { File = file, LoadMode = mode }.GetSource(); // a decode failure is logged by the stream
        if (source is null)
        {
            missing++;
            return null;
        }

        if (source.SampleRate != Plan.Grid.SampleRate)
        {
            missing++;
            Log.Error($"[Audio] Adaptive music '{Label}': the {section}/{layer} stem '{file}' is {source.SampleRate} Hz, not the music's " +
                      $"{Plan.Grid.SampleRate} Hz; that lane is silent (stems are baked at the music's rate so they stay aligned).");
            return null;
        }

        return source;
    }
}

/// <summary><c>.mmusic</c> → <see cref="AdaptiveMusic"/> (the UID is the one in the file, kept across bakes).</summary>
public sealed class AdaptiveMusicImporter : IAssetImporter
{
    public string Name => "music";

    public IReadOnlyList<string> Extensions { get; } = [MusicManifestFormat.Extension];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => AdaptiveMusic.Load(projectPath);
}

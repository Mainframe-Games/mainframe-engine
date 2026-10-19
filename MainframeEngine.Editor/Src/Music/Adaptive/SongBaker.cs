using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>How a bake runs; every member is optional.</summary>
public sealed class SongBakeOptions
{
    /// <summary>Encodes stems to Ogg Vorbis (the plugin helper); null: stems are written as 16-bit <c>.wav</c>.</summary>
    public IPluginHost? Encoder { get; init; }

    /// <summary>Instrument for a track (null: <see cref="SongSnapshotBuilder.DefaultInstrument"/>).</summary>
    public Func<SongTrack, int, IInstrument?>? InstrumentFactory { get; init; }

    /// <summary>Loads an audio clip's file (null: <see cref="SongSnapshotBuilder.LoadClip"/>).</summary>
    public Func<string, AudioStream?>? ClipLoader { get; init; }

    /// <summary>Creates the bake's plugin rack at a rate (offline helper); null: stems that need plugins fail the bake.</summary>
    public Func<int, PluginRack?>? Plugins { get; init; }

    /// <summary>Told after each stem, from the bake's worker threads (counts may arrive out of order).</summary>
    public IProgress<SongBakeProgress>? Progress { get; init; }

    /// <summary>Stems without plugins render in parallel on up to this many workers.</summary>
    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
}

/// <summary>Bake progress: <paramref name="Done"/> of <paramref name="Total"/> stems, the last one "Combat/Perc".</summary>
public readonly record struct SongBakeProgress(int Done, int Total, string Stem)
{
    public double Fraction => Total == 0 ? 1 : (double)Done / Total;
}

/// <summary>What a bake did.</summary>
/// <param name="ManifestPath">Project path of the <c>.mmusic</c>.</param>
/// <param name="Uid">Its <c>mus_</c> UID (kept across bakes).</param>
/// <param name="Rendered">Keys (<c>section/layer</c>, <c>stinger/mix</c>, <c>section@marker/layer</c>) rendered this time.</param>
/// <param name="Reused">Keys whose stems were reused (inputs unchanged).</param>
/// <param name="Streamed">Body stems games stream.</param>
/// <param name="MemoryLoaded">Stems games decode into memory (tails, stingers, transition segments).</param>
/// <param name="MemoryBytes">Their decoded size (32-bit float stereo).</param>
/// <param name="Deleted">Stale stems deleted (project paths).</param>
/// <param name="Encoded">Stems are Ogg Vorbis (false: 16-bit WAV).</param>
public sealed record SongBakeResult(string ManifestPath, string Uid, IReadOnlyList<string> Rendered, IReadOnlyList<string> Reused,
    int Streamed, int MemoryLoaded, long MemoryBytes, IReadOnlyList<string> Deleted, bool Encoded)
{
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{Rendered.Count} stem(s) rendered, {Reused.Count} reused; {Streamed} streamed, {MemoryLoaded} in memory ({MemoryBytes / 1048576.0:0.0} MB decoded)");
}

/// <summary>A bake that cannot run or failed (validation errors, missing plugins), with the details.</summary>
public sealed class SongBakeException : Exception
{
    public SongBakeException()
        : this("The bake failed.", [])
    {
    }

    public SongBakeException(string message)
        : this(message, [])
    {
    }

    public SongBakeException(string message, Exception inner)
        : base(message, inner)
    {
        Details = [];
    }

    public SongBakeException(string message, IReadOnlyList<string> details)
        : base(details.Count == 0 ? message : message + "\n  " + string.Join("\n  ", details))
    {
        Details = details;
    }

    public IReadOnlyList<string> Details { get; }
}

/// <summary>
/// Bakes an adaptive song (docs/design/future/adaptive-music.md, "The bake"): one body stem per layer per section (exactly
/// the section's <see cref="MusicGrid.LengthFrames"/>) plus its trimmed tail, one mixed stem per stinger, tails for tail
/// points, and the <c>.mmusic</c> manifest. Each stem is rendered in isolation (<see cref="RenderStem"/>), named by a hash
/// of its inputs and reused while they do not change. New stems are staged as <c>*.baking.tmp</c>, moved into place
/// once all rendered, then the manifest is written (<see cref="DurableFile"/>), then the stems only the previous manifest
/// listed are deleted: a failed or cancelled bake leaves the previous manifest and its stems as they were.
/// </summary>
public static partial class SongBaker
{
    /// <summary>Bumped when the bake renders differently, so every stem renders again.</summary>
    public const int BakeVersion = 1;

    /// <summary>Hex digits of the input hash in stem file names.</summary>
    public const int NameHashDigits = 8;

    public const string StemsSuffix = ".stems";

    private const string StageSuffix = ".baking.tmp";

    /// <summary>The manifest's project path: <c>adaptive.output</c>, else <c>Assets/Music/&lt;songName&gt;.mmusic</c>.</summary>
    public static string ManifestPathFor(Song song, string songName)
    {
        ArgumentNullException.ThrowIfNull(song);
        return ManifestPathFor(song.Adaptive?.Output, songName);
    }

    /// <inheritdoc cref="ManifestPathFor(Song, string)"/>
    public static string ManifestPathFor(string? output, string songName)
    {
        var path = string.IsNullOrWhiteSpace(output) ? $"{SongRenderer.DefaultFolder}/{songName}{MusicManifestFormat.Extension}" : output.Replace('\\', '/');
        return string.Equals(Path.GetExtension(path), MusicManifestFormat.Extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, MusicManifestFormat.Extension);
    }

    /// <summary>The generated stems folder next to a manifest: <c>Theme.mmusic</c> → <c>Theme.stems</c>.</summary>
    public static string StemsFolderFor(string manifestPath) => Path.ChangeExtension(manifestPath, null) + StemsSuffix;

    /// <summary>
    /// Bakes <paramref name="song"/> into the project of <paramref name="database"/> (on the calling thread; run it on a
    /// worker for the UI, see <see cref="SongBakeJob"/>). <paramref name="songName"/> names the default output.
    /// </summary>
    /// <exception cref="SongBakeException">Validation errors, missing plugins or a stem of the wrong length.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing changed.</exception>
    public static SongBakeResult Bake(Song song, string songName, AssetDatabase database, SongBakeOptions? options = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(database);
        options ??= new SongBakeOptions();
        if (song.Adaptive is not { } adaptive)
            throw new SongBakeException($"'{songName}' is not adaptive (it has no sections or layers to bake).");
        var problems = SongAdaptiveValidator.Validate(song);
        if (!SongAdaptiveValidator.CanBake(problems))
            throw new SongBakeException($"'{songName}' cannot be baked:",
                [.. problems.Where(p => p.Severity == AdaptiveProblemSeverity.Error).Select(p => p.Message)]);
        foreach (var p in problems)
            Log.Warning($"[Music] Bake '{songName}': {p.Message}");

        var rate = RateOf(song);
        var encode = options.Encoder is not null;
        if (!encode)
            Log.Warning("[Music] Encoding to .ogg needs the plugin helper (mfplughost), which is not available here; stems are written as 16-bit .wav.");
        var manifestPath = database.ToProjectPath(ManifestPathFor(song, songName));
        var manifestFull = database.ToAbsolutePath(manifestPath);
        var stemsFolder = StemsFolderFor(manifestPath);
        var old = ReadPrevious(manifestFull);

        var manifest = SongAdaptiveCompiler.ToManifest(song, rate);
        manifest.Uid = old?.Uid is { } uid && uid.StartsWith(AssetUid.MusicPrefix + "_", StringComparison.Ordinal) && AssetUid.IsUid(uid)
            ? uid
            : AssetUid.Generate(AssetUid.MusicPrefix);

        var context = new BakeContext(song, rate, encode, Math.Clamp(song.Render.Quality, 0, 10), stemsFolder, database, options, old);
        var jobs = Plan(context, adaptive, manifest);
        foreach (var job in jobs)
            job.Hash = HashOf(context, job);

        var staged = new ConcurrentBag<string>();
        var created = new List<string>();
        var progressDone = 0;
        void Report(StemJob job) => options.Progress?.Report(new SongBakeProgress(Interlocked.Increment(ref progressDone), jobs.Count, job.Label));

        try
        {
            var toRender = new List<StemJob>();
            foreach (var job in jobs)
            {
                if (TryReuse(context, job))
                    Report(job);
                else
                    toRender.Add(job);
            }

            var needPlugins = toRender.Where(j => j.UsesPlugins).ToList();
            if (needPlugins.Count > 0 && options.Plugins is null)
                throw new SongBakeException("These stems need plugins, but the plugin helper is not available:", [.. needPlugins.Select(j => j.Label)]);

            var encoderGate = new Lock();
            try
            {
                Parallel.ForEach(toRender.Where(j => !j.UsesPlugins), new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                    CancellationToken = cancellation,
                }, job =>
                {
                    RenderJob(context, job, null, staged, encoderGate, cancellation);
                    Report(job);
                });
            }
            catch (AggregateException e)
            {
                // The first worker's failure, as the sequential path would throw it.
                var first = e.InnerExceptions.FirstOrDefault(x => x is not OperationCanceledException) ?? e.InnerExceptions[0];
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }

            if (needPlugins.Count > 0)
            {
                // Plugin stems render one after another through one offline helper, as Render does.
                using var rack = options.Plugins!(rate);
                var failures = new List<string>();
                foreach (var job in needPlugins)
                {
                    var error = RenderJob(context, job, rack, staged, encoderGate, cancellation);
                    if (error is not null)
                        failures.Add(error);
                    Report(job);
                }

                if (failures.Count > 0)
                    throw new SongBakeException("Plugins could not play (nothing was changed):", failures);
            }

            cancellation.ThrowIfCancellationRequested();
            foreach (var job in toRender)
                Commit(context, job, created);
            Fill(manifest, jobs);
            var stale = StalePaths(context, manifest);
            manifest.Bake = BakeBlock(context, jobs, stale);
            cancellation.ThrowIfCancellationRequested();
            DurableFile.Write(manifestFull, MusicManifestFormat.Write(manifest));
            database.Register(manifest.Uid, manifestPath);
            var deleted = DeleteStale(database, stale);

            var result = BuildResult(manifestPath, manifest, jobs, deleted, encode);
            Log.Info($"[Music] Baked '{songName}' to {manifestPath}: {result.Summary}.");
            return result;
        }
        catch
        {
            // Nothing the previous manifest lists is touched: drop the staged files and the stems this bake added.
            foreach (var file in staged)
                TryDelete(file);
            foreach (var path in created)
            {
                TryDelete(database.ToAbsolutePath(path));
                TryDelete(database.ToAbsolutePath(path) + AssetDatabase.MetaExtension);
                if (database.GetUid(path) is { } stemUid)
                    database.Unregister(stemUid);
            }

            throw;
        }
    }

    // --- planning ---------------------------------------------------------------------------------

    private enum StemKind
    {
        Section,
        Stinger,
        TailPoint,
    }

    private sealed class StemJob
    {
        public required string Key;
        public required string Label;
        public required StemKind Kind;
        public required MusicManifestSection Section;
        public required SongSection Source;
        public required string Slot;
        public MusicManifestMarker? Marker;
        public SongMarker? SourceMarker;
        public required Song Isolated;

        /// <summary>Frames rendered before the cut (the body, or the run-up to a tail point).</summary>
        public required long CutFrames;

        public required bool UsesPlugins;
        public string Hash = "";
        public bool Reused;
        public MusicStemRef? Body;
        public MusicStemRef? Tail;
        public string? BodyStage;
        public string? TailStage;

        public string NamePrefix => Kind switch
        {
            StemKind.Stinger => SafeName(Source.Id),
            StemKind.TailPoint => $"{SafeName(Source.Id)}.at-{SafeName(SourceMarker!.Id)}.{SafeName(Slot)}",
            _ => $"{SafeName(Source.Id)}.{SafeName(Slot)}",
        };
    }

    private sealed record BakeContext(Song Song, int Rate, bool Encode, int Quality, string StemsFolder, AssetDatabase Database,
        SongBakeOptions Options, MusicManifest? Old)
    {
        public string Extension => Encode ? ".ogg" : ".wav";

        public long TailFrames { get; } = TailFramesOf(Song, Rate);

        /// <summary>Audio file fingerprints for this bake (project path → size, mtime, SHA-256).</summary>
        public Dictionary<string, FileFingerprint> Files { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct FileFingerprint(long Size, long ModifiedTicks, string Sha256);

    private static List<StemJob> Plan(BakeContext context, SongAdaptive adaptive, MusicManifest manifest)
    {
        var song = context.Song;
        var jobs = new List<StemJob>();
        for (var i = 0; i < adaptive.Sections.Count && i < manifest.Sections.Count; i++)
        {
            var source = adaptive.Sections[i];
            var section = manifest.Sections[i];
            if (section.LengthFrames <= 0)
                continue;
            if (source.Kind == MusicSectionKind.Stinger)
            {
                Add(jobs, StemKind.Stinger, source, section, MusicManifestSection.MixStem, "Mix", null, null,
                    Isolate(song, source.Start, source.End, static _ => true), section.LengthFrames);
                continue;
            }

            foreach (var layer in manifest.Layers)
            {
                bool InLayer(SongTrack t) => string.Equals(SongAdaptiveCompiler.LayerOf(song, t), layer.Id, StringComparison.Ordinal);
                Add(jobs, StemKind.Section, source, section, layer.Id, layer.Name, null, null, Isolate(song, source.Start, source.End, InLayer),
                    section.LengthFrames);
            }

            // Tail points: "cut at the marker + tail" per layer, for exit markers inside the section.
            var grid = SongAdaptiveCompiler.GridOf(song, context.Rate);
            var ticksPerBeat = SongAdaptiveCompiler.TicksPerBeat(song);
            foreach (var marker in adaptive.Markers)
            {
                if (!marker.Tail || !marker.Exit || marker.Tick <= source.Start || marker.Tick >= source.End)
                    continue;
                var frame = grid.FrameOfBeat((marker.Tick - source.Start) / ticksPerBeat);
                var target = section.Markers.Find(m => m.Frame == frame && string.Equals(m.Name, marker.Name, StringComparison.Ordinal));
                if (target is null)
                    continue;
                target.Tails ??= [];
                foreach (var layer in manifest.Layers)
                {
                    bool InLayer(SongTrack t) => string.Equals(SongAdaptiveCompiler.LayerOf(song, t), layer.Id, StringComparison.Ordinal);
                    Add(jobs, StemKind.TailPoint, source, section, layer.Id, layer.Name, target, marker,
                        Isolate(song, source.Start, marker.Tick, InLayer), frame);
                }
            }
        }

        return jobs;

        static void Add(List<StemJob> jobs, StemKind kind, SongSection source, MusicManifestSection section, string slot, string slotName,
            MusicManifestMarker? marker, SongMarker? sourceMarker, Song isolated, long cutFrames)
        {
            // A layer with nothing in the range bakes nothing: the manifest records null (silence).
            if (isolated.Tracks.Count == 0)
            {
                if (marker is not null)
                    marker.Tails![slot] = null;
                else
                    section.Stems[slot] = null;
                return;
            }

            var where = sourceMarker is null ? source.Id : $"{source.Id}@{sourceMarker.Id}";
            var label = sourceMarker is null ? $"{source.Name}/{slotName}" : $"{source.Name} @ {sourceMarker.Name}/{slotName}";
            jobs.Add(new StemJob
            {
                Key = $"{where}/{slot}",
                Label = label,
                Kind = kind,
                Section = section,
                Source = source,
                Slot = slot,
                Marker = marker,
                SourceMarker = sourceMarker,
                Isolated = isolated,
                CutFrames = cutFrames,
                UsesPlugins = isolated.Tracks.Exists(UsesPlugins),
            });
        }
    }

    private static string SafeName(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var c in id)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string StemPath(BakeContext context, StemJob job, bool tail) =>
        $"{context.StemsFolder}/{job.NamePrefix}.{job.Hash[..NameHashDigits]}{(tail ? ".tail" : "")}{context.Extension}";

    // --- input hashes -----------------------------------------------------------------------------

    /// <summary>
    /// SHA-256 over a canonical JSON of everything that changes a stem's samples: the bake version, rate and encoder, the
    /// section's range, kind and cut, the tail length, the isolated song (tempo, signature, the stem's tracks with their
    /// instruments, inserts and state, volume, pan, notes and clips, master volume), the audio files' contents and the
    /// versions of the plugins involved (from the scan catalog).
    /// </summary>
    private static string HashOf(BakeContext context, StemJob job)
    {
        var files = new JsonObject();
        var plugins = new JsonObject();
        foreach (var track in job.Isolated.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip is AudioClip audio && !files.ContainsKey(audio.File))
                    files[audio.File] = FingerprintOf(context, audio.File);
            }

            foreach (var descriptor in track.Inserts.Select(i => i.Plugin).Append(track.Instrument?.Plugin))
            {
                if (descriptor?.ClassId is { } classId && !plugins.ContainsKey(classId))
                    plugins[classId] = PluginCatalog.Current.Find(classId)?.Version ?? "";
            }
        }

        var root = new JsonObject
        {
            ["bake"] = BakeVersion,
            ["rate"] = context.Rate,
            ["encoder"] = context.Encode ? "vorbis-q" + context.Quality.ToString(CultureInfo.InvariantCulture) : "wav-pcm16",
            ["kind"] = job.Kind.ToString(),
            ["sectionKind"] = job.Source.Kind.ToString(),
            ["range"] = new JsonArray(job.Source.Start, job.Source.End),
            ["cut"] = job.SourceMarker?.Tick ?? job.Source.End,
            ["lengthFrames"] = job.Section.LengthFrames,
            ["tailFrames"] = context.TailFrames,
            ["song"] = JsonNode.Parse(SongFormat.Write(job.Isolated)),
            ["files"] = files,
            ["plugins"] = plugins,
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    // An audio file's content hash, recomputed only when its size or modification time changed since the last bake.
    private static string FingerprintOf(BakeContext context, string file)
    {
        var database = context.Database;
        var path = AssetUid.IsUid(file) ? database.GetPath(file) : file;
        if (path is null)
            return "missing:" + file;
        path = database.ToProjectPath(path);
        lock (context.Files)
        {
            if (context.Files.TryGetValue(path, out var known))
                return known.Sha256;
        }

        var full = database.ToAbsolutePath(path);
        if (!File.Exists(full))
            return "missing:" + path;
        var info = new FileInfo(full);
        var fingerprint = new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, "");
        if (context.Old?.Bake?["files"]?[path] is JsonObject previous &&
            previous["size"]?.GetValue<long>() == fingerprint.Size && previous["mtime"]?.GetValue<long>() == fingerprint.ModifiedTicks &&
            previous["sha256"]?.GetValue<string>() is { Length: > 0 } sha)
        {
            fingerprint = fingerprint with { Sha256 = sha };
        }
        else
        {
            using var stream = File.OpenRead(full);
            fingerprint = fingerprint with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
        }

        lock (context.Files)
            context.Files[path] = fingerprint;
        return fingerprint.Sha256;
    }

    // --- reuse ------------------------------------------------------------------------------------

    private static MusicManifest? ReadPrevious(string manifestFull)
    {
        if (!File.Exists(manifestFull))
            return null;
        try
        {
            return MusicManifestFormat.Load(manifestFull).Manifest;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] The previous bake '{manifestFull}' cannot be read ({e.Message}); every stem renders again and old stems stay.");
            return null;
        }
    }

    // The previous bake's stems for this job, when its inputs are unchanged and its files are still there.
    private static bool TryReuse(BakeContext context, StemJob job)
    {
        if (context.Old is not { } old || old.Bake?["inputs"]?[job.Key] is not JsonValue value || value.GetValue<string>() != job.Hash)
            return false;
        var section = old.Sections.Find(s => string.Equals(s.Id, job.Section.Id, StringComparison.Ordinal));
        if (section is null)
            return false;
        MusicStemRef? body = null, tail = null;
        switch (job.Kind)
        {
            case StemKind.Section or StemKind.Stinger:
                if (section.Stems.GetValueOrDefault(job.Slot) is not { } set || set.Body is null)
                    return false;
                (body, tail) = (set.Body, set.Tail);
                break;
            case StemKind.TailPoint:
                var marker = section.Markers.Find(m => m.Frame == job.Marker!.Frame && string.Equals(m.Name, job.Marker.Name, StringComparison.Ordinal));
                if (marker?.Tails is not { } tails || !tails.TryGetValue(job.Slot, out tail))
                    return false;
                break;
        }

        if (body is not null && !IsPlaced(context, body, StemPath(context, job, tail: false)))
            return false;
        if (tail is not null && !IsPlaced(context, tail, StemPath(context, job, tail: true)))
            return false;
        job.Body = Copy(body);
        job.Tail = Copy(tail);
        job.Reused = true;
        return true;

        static bool IsPlaced(BakeContext context, MusicStemRef stem, string expected) =>
            string.Equals(stem.Path, expected, StringComparison.Ordinal) && File.Exists(context.Database.ToAbsolutePath(stem.Path));

        static MusicStemRef? Copy(MusicStemRef? stem) => stem is null ? null : new MusicStemRef { Uid = stem.Uid, Path = stem.Path, Frames = stem.Frames };
    }

    // --- rendering --------------------------------------------------------------------------------

    // Renders one job and stages its files; returns a plugin failure for the job (or null). Other failures throw.
    private static string? RenderJob(BakeContext context, StemJob job, PluginRack? rack, ConcurrentBag<string> staged, Lock encoderGate,
        CancellationToken cancellation)
    {
        var samples = RenderRange(job.Isolated, context.Rate, job.CutFrames + context.TailFrames, context.Options, rack, cancellation,
            out var problems);
        if (problems.Count > 0)
            return $"{job.Label}: {string.Join("; ", problems)}";
        var audio = job.Kind switch
        {
            StemKind.Stinger => Split(samples, job.CutFrames, context.Rate, oneStem: true),
            _ => Split(samples, job.CutFrames, context.Rate, oneStem: false),
        };
        samples = [];

        if (job.Kind != StemKind.TailPoint)
        {
            var path = StemPath(context, job, tail: false);
            job.BodyStage = Stage(context, path, audio.Body, staged, encoderGate, cancellation);
            job.Body = new MusicStemRef { Path = path, Frames = job.Kind == StemKind.Stinger ? audio.BodyFrames : 0 };
        }

        if (audio.Tail is { } tail)
        {
            var path = StemPath(context, job, tail: true);
            job.TailStage = Stage(context, path, tail, staged, encoderGate, cancellation);
            job.Tail = new MusicStemRef { Path = path, Frames = audio.TailFrames };
        }

        return null;
    }

    // Writes a stem to its staging name (WAV, or a float WAV encoded to Vorbis), checking the encoded length.
    private static string Stage(BakeContext context, string projectPath, float[] samples, ConcurrentBag<string> staged, Lock encoderGate,
        CancellationToken cancellation)
    {
        var final = context.Database.ToAbsolutePath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var stage = final + StageSuffix;
        staged.Add(stage);
        cancellation.ThrowIfCancellationRequested();
        if (!context.Encode)
        {
            WavWriter.Write(stage, samples, 2, context.Rate, WavSampleFormat.Pcm16);
            return stage;
        }

        var wav = final + ".baking.wav.tmp";
        staged.Add(wav);
        staged.Add(stage + ".encoding.tmp");
        WavWriter.Write(wav, samples, 2, context.Rate, WavSampleFormat.IeeeFloat);
        PluginHostEncodeResult encoded;
        lock (encoderGate)
            encoded = context.Options.Encoder!.Encode(wav, stage, context.Quality, cancellation);
        TryDelete(wav);
        // Vorbis granule positions keep the decoded length exact; a stem of any other length would drift the layers apart.
        var frames = samples.Length / 2;
        if (encoded.Frames > 0 && encoded.Frames != frames)
            throw new SongBakeException($"'{projectPath}' encoded to {encoded.Frames} frames instead of {frames}; the stems would not stay aligned.");
        return stage;
    }

    // --- commit -----------------------------------------------------------------------------------

    // Moves a rendered job's staged files into place and gives them their .meta (keeping a UID already there).
    private static void Commit(BakeContext context, StemJob job, List<string> created)
    {
        if (job.BodyStage is not null)
            Place(context, job.BodyStage, job.Body!, job.Kind == StemKind.Section && job.Source.Kind == MusicSectionKind.Section, created);
        if (job.TailStage is not null)
            Place(context, job.TailStage, job.Tail!, stream: false, created);

        static void Place(BakeContext context, string stage, MusicStemRef stem, bool stream, List<string> created)
        {
            var database = context.Database;
            var final = database.ToAbsolutePath(stem.Path);
            var existed = File.Exists(final);
            File.Move(stage, final, overwrite: true);
            if (!existed)
                created.Add(stem.Path);
            AssetMeta? meta = null;
            try
            {
                meta = database.ReadOrCreateMeta(stem.Path, create: false);
            }
            catch (JsonException)
            {
            }

            meta ??= new AssetMeta();
            if (!AssetUid.IsUid(meta.Uid))
                meta.Uid = "";
            meta.Importer = "audio";
            var settings = meta.Settings ?? new Dictionary<string, JsonElement>();
            settings["loadMode"] = JsonSerializer.SerializeToElement(stream ? "Stream" : "Memory", MusicJsonContext.Default.String);
            meta.Settings = settings;
            database.WriteMeta(stem.Path, meta);
            stem.Uid = meta.Uid;
        }
    }

    private static void Fill(MusicManifest manifest, List<StemJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (job.Kind == StemKind.TailPoint)
                job.Marker!.Tails![job.Slot] = job.Tail;
            else
                job.Section.Stems[job.Slot] = new MusicStemSet { Body = job.Body, Tail = job.Tail };
        }
    }

    // Stems the previous manifest listed (or still had to delete) that this one does not, and that still exist.
    private static List<string> StalePaths(BakeContext context, MusicManifest manifest)
    {
        if (context.Old is not { } old)
            return [];
        var keep = new HashSet<string>(StemPaths(manifest), StringComparer.Ordinal);
        var candidates = StemPaths(old).ToList();
        if (old.Bake?["stale"] is JsonArray pending)
            candidates.AddRange(pending.Select(n => n?.GetValue<string>()).OfType<string>());
        return [.. candidates.Distinct(StringComparer.Ordinal)
            .Where(p => !keep.Contains(p) && File.Exists(context.Database.ToAbsolutePath(p)))];
    }

    private static IEnumerable<string> StemPaths(MusicManifest manifest)
    {
        foreach (var section in manifest.Sections)
        {
            foreach (var set in section.Stems.Values)
            {
                if (set?.Body?.Path is { Length: > 0 } body)
                    yield return body;
                if (set?.Tail?.Path is { Length: > 0 } tail)
                    yield return tail;
            }

            foreach (var marker in section.Markers)
            {
                if (marker.Tails is null)
                    continue;
                foreach (var tail in marker.Tails.Values)
                {
                    if (tail?.Path is { Length: > 0 } path)
                        yield return path;
                }
            }
        }
    }

    private static JsonObject BakeBlock(BakeContext context, List<StemJob> jobs, List<string> stale)
    {
        var inputs = new JsonObject();
        foreach (var job in jobs)
            inputs[job.Key] = job.Hash;
        var files = new JsonObject();
        foreach (var (path, f) in context.Files.OrderBy(e => e.Key, StringComparer.Ordinal))
            files[path] = new JsonObject { ["size"] = f.Size, ["mtime"] = f.ModifiedTicks, ["sha256"] = f.Sha256 };
        var block = new JsonObject
        {
            ["editor"] = EngineInfo.Version,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["inputs"] = inputs,
        };
        if (files.Count > 0)
            block["files"] = files;
        // Listed before the deletes, so a stem that is still open (a running game streams it) is retried next bake.
        if (stale.Count > 0)
            block["stale"] = new JsonArray([.. stale.Select(p => (JsonNode?)p)]);
        return block;
    }

    private static List<string> DeleteStale(AssetDatabase database, List<string> stale)
    {
        var deleted = new List<string>();
        foreach (var path in stale)
        {
            var full = database.ToAbsolutePath(path);
            try
            {
                File.Delete(full);
                if (File.Exists(full + AssetDatabase.MetaExtension))
                    File.Delete(full + AssetDatabase.MetaExtension);
                if (database.GetUid(path) is { } uid)
                    database.Unregister(uid);
                deleted.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Music] Old stem '{path}' cannot be deleted now ({e.Message}); the next bake retries.");
            }
        }

        return deleted;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] Cannot delete '{path}': {e.Message}");
        }
    }

    private static SongBakeResult BuildResult(string manifestPath, MusicManifest manifest, List<StemJob> jobs, List<string> deleted, bool encode)
    {
        int streamed = 0, memory = 0;
        long bytes = 0;
        foreach (var section in manifest.Sections)
        {
            foreach (var set in section.Stems.Values)
            {
                if (set?.Body is { } body)
                {
                    // Section bodies stream; transition segments and stingers start at an exact frame, so they are decoded.
                    if (section.Kind == MusicSectionKind.Section)
                    {
                        streamed++;
                    }
                    else
                    {
                        memory++;
                        bytes += (body.Frames > 0 ? body.Frames : section.LengthFrames) * 2 * sizeof(float);
                    }
                }

                if (set?.Tail is { } tail)
                {
                    memory++;
                    bytes += tail.Frames * 2 * sizeof(float);
                }
            }

            foreach (var marker in section.Markers)
            {
                foreach (var tail in marker.Tails?.Values ?? Enumerable.Empty<MusicStemRef?>())
                {
                    if (tail is null)
                        continue;
                    memory++;
                    bytes += tail.Frames * 2 * sizeof(float);
                }
            }
        }

        var rendered = jobs.Where(j => !j.Reused).Select(j => j.Key).ToList();
        var reused = jobs.Where(j => j.Reused).Select(j => j.Key).ToList();
        return new SongBakeResult(manifestPath, manifest.Uid, rendered, reused, streamed, memory, bytes, deleted, encode);
    }
}

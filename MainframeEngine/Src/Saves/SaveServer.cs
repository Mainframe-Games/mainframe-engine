using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MainframeEngine;

/// <summary>
/// Save games: named slots of a game-defined payload (<see cref="SaveSchema{T}"/>) in <c>.msave</c> files, written on a
/// worker so saving never hitches. <see cref="GameHost"/> and <see cref="HeadlessHost"/> register one over
/// <c>{user data}/saves</c> (<see cref="LocalSaveStorage"/>); nodes reach it with <c>Tree.Servers.Get&lt;SaveServer&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write path.</b> <see cref="SaveAsync{T}"/> serializes the payload on the calling (main) thread right away — the
/// bytes are the snapshot, so the game may change its state on the next line — then a single worker hashes it
/// (SHA-256), optionally gzips it, builds the header and replaces the file durably (<see cref="ISaveStorage.WriteDurable"/>:
/// temp file, flush to disk, <see cref="SaveOptions.Backups"/> rolling backups). Queued saves of the same slot that have
/// not started are coalesced: the newest wins and every caller gets its result. Results are queued for the main thread:
/// <see cref="Process"/> completes the task and raises <see cref="Saved"/>, so an <c>await</c> continues on the main thread.
/// </para>
/// <para>
/// <b>Load path.</b> A damaged slot (truncated, bad checksum, bad JSON, failed migration) is moved aside as
/// <c>.corrupt-{utc}</c> (never deleted) and the newest valid copy — the <c>.tmp</c> of an interrupted write, then
/// <c>.bak1</c> … — is restored and returned as <see cref="LoadStatus.RecoveredFromBackup"/>. A save from a newer build
/// is <see cref="LoadStatus.TooNew"/> and left untouched.
/// </para>
/// <para>
/// Call the methods from the main thread. Storage is touched by one thread at a time (a lock around every call).
/// <see cref="Process"/> allocates nothing while idle. <see cref="Dispose"/> (engine close) waits up to five seconds for
/// queued writes, so a quit right after <see cref="SaveAsync{T}"/> still saves. See docs/design/project-and-gamehost.md.
/// </para>
/// </remarks>
public sealed partial class SaveServer : IFrameServer
{
    /// <summary>The longest <see cref="SaveInfo.Description"/>, in characters.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>The save files' extension.</summary>
    public const string FileExtension = SaveFile.Extension;

    /// <summary>The longest slot name.</summary>
    public const int MaxSlotLength = 64;

    /// <summary>How long a save waits for its thumbnail frame before it is written without one.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly SceneTree? _tree;
    private readonly Lock _storageLock = new();
    private readonly object _queueLock = new(); // a monitor: Flush waits on it
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, SaveJob> _pendingSaves = new(StringComparer.Ordinal); // not started, by slot (_queueLock)
    private readonly HashSet<Job> _active = [];                                                // queued or running (_queueLock)
    private readonly ConcurrentQueue<Job> _completions = new();
    private readonly List<SaveJob> _awaitingThumbnail = [];                                    // main thread
    private readonly Action<FrameCapture> _onCaptured;
    private readonly ArrayBufferWriter<byte> _serializeBuffer = new(4096);                    // main thread
    private Utf8JsonWriter? _serializeWriter;
    private volatile IReadOnlyList<SaveSlotInfo>? _slots;
    private int _completed;
    private double _playTimeSeconds;
    private FrameCapture? _thumbnail;
    private bool _captureRequested;
    private long _captureRequestedAt;
    private bool _warnedNoCapture;
    private bool _disposed;

    /// <param name="storage">Where the files live.</param>
    /// <param name="options">Limits, backups, compression, thumbnails and header identity.</param>
    /// <param name="tree">The tree whose pause stops <see cref="PlayTime"/> and whose frames become thumbnails (null: always counts, no thumbnails).</param>
    public SaveServer(ISaveStorage storage, SaveOptions options, SceneTree? tree = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Backups);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Backups, DurableFile.MaxBackups);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThumbnailWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThumbnailHeight, 1);
        Storage = storage;
        Options = options;
        _tree = tree;
        _onCaptured = OnFrameCaptured;
        _ = Task.Run(RunWorkerAsync);
    }

    /// <summary>Where the files live.</summary>
    public ISaveStorage Storage { get; }

    /// <summary>The options the server was created with.</summary>
    public SaveOptions Options { get; }

    /// <summary>
    /// Unpaused time played: advanced in <see cref="Process"/> while the tree is not paused, set from the header by a
    /// successful load, written into every save.
    /// </summary>
    public TimeSpan PlayTime
    {
        get => TimeSpan.FromSeconds(_playTimeSeconds);
        set => _playTimeSeconds = Math.Max(0, value.TotalSeconds);
    }

    /// <summary>Raised on the main thread (from <see cref="Process"/>, or by <see cref="Save{T}"/>) for every save that ends.</summary>
    [Signal]
    public event Action<SaveResult>? Saved;

    /// <summary>True when <paramref name="slot"/> is a valid slot name: <c>[a-z0-9_-]{1,64}</c>.</summary>
    public static bool IsValidSlotName(string? slot)
    {
        if (string.IsNullOrEmpty(slot) || slot.Length > MaxSlotLength)
            return false;
        foreach (var c in slot)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
                return false;
        }

        return true;
    }

    private static void ValidateSlot(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!IsValidSlotName(slot))
            throw new ArgumentException($"'{slot}' is not a save slot name: use 1-{MaxSlotLength} of a-z, 0-9, '_' and '-'.", nameof(slot));
    }

    private static void ValidateInfo(in SaveInfo info)
    {
        if (info.Description is { Length: > MaxDescriptionLength })
            throw new ArgumentException($"The description is longer than {MaxDescriptionLength} characters.", nameof(info));
        if (!double.IsFinite(info.Progress))
            throw new ArgumentException("The progress must be a finite number.", nameof(info));
    }

    internal static string MainPath(string slot) => slot + SaveFile.Extension;

    internal static string ThumbnailFile(string slot) => slot + ".png";

    // ----------------------------------------------------------------------------------------------------
    // Listing
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every slot's header, newest first, read without touching the payloads (damaged files are listed with
    /// <see cref="SaveSlotInfo.IsReadable"/> false). Cached until the next write.
    /// </summary>
    public IReadOnlyList<SaveSlotInfo> ListSlots()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_slots is { } cached)
            return cached;
        lock (_storageLock)
        {
            if (_slots is { } again)
                return again;
            var result = new List<SaveSlotInfo>();
            foreach (var name in Storage.List(""))
            {
                if (!name.EndsWith(SaveFile.Extension, StringComparison.Ordinal))
                    continue;
                var slot = name[..^SaveFile.Extension.Length];
                if (IsValidSlotName(slot))
                    result.Add(ReadSlotInfo(slot));
            }

            result.Sort(static (a, b) => b.SavedUtc != a.SavedUtc ? b.SavedUtc.CompareTo(a.SavedUtc) : string.CompareOrdinal(a.Slot, b.Slot));
            var list = result.AsReadOnly();
            _slots = list;
            return list;
        }
    }

    /// <summary>True when <paramref name="slot"/> has a file (or a copy a load would recover).</summary>
    public bool Exists(string slot)
    {
        ValidateSlot(slot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var main = MainPath(slot);
        lock (_storageLock)
            return Storage.GetModifiedUtc(main) is not null || Storage.GetModifiedUtc(DurableFile.TempPath(main)) is not null
                   || (Options.Backups > 0 && Storage.GetModifiedUtc(DurableFile.BackupPath(main, 1)) is not null);
    }

    // Under _storageLock.
    private SaveSlotInfo ReadSlotInfo(string slot)
    {
        var path = MainPath(slot);
        try
        {
            using var stream = Storage.OpenRead(path);
            if (stream is not null)
            {
                var line = SaveFile.ReadHeaderLine(stream, out _);
                if (line is not null && SaveFile.TryParseHeaderLine(line, out var header, out _) && header.Kind == SaveFile.Kind)
                {
                    var size = stream.CanSeek ? stream.Length : line.Length + 1 + Math.Max(0, header.PayloadBytes);
                    if (header.Format <= SaveFile.CurrentFormat && header.Validate() is null)
                        return header.ToInfo(slot, ThumbnailPathOf(header), size);
                }

                return Unreadable(slot, stream.CanSeek ? stream.Length : 0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return Unreadable(slot, 0);
    }

    private SaveSlotInfo Unreadable(string slot, long size) =>
        new(slot, 0, 0, Storage.GetModifiedUtc(MainPath(slot)) ?? default, TimeSpan.Zero, null, 0, null, null, null, size, IsReadable: false);

    private string? ThumbnailPathOf(SaveHeader header)
    {
        if (header.Thumbnail is not { Length: > 0 } thumbnail || thumbnail.Contains('/', StringComparison.Ordinal))
            return null;
        try
        {
            return Storage is LocalSaveStorage local ? local.FullPath(thumbnail) : thumbnail;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Saving
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves <paramref name="data"/> to <paramref name="slot"/>: serializes it now (the snapshot) and writes it on the
    /// worker. The task completes on the main thread (in <see cref="Process"/>). Throws <see cref="ArgumentException"/>
    /// for an invalid slot name or info.
    /// </summary>
    public Task<SaveResult> SaveAsync<T>(string slot, T data, SaveSchema<T> schema, SaveInfo info = default)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(schema);
        ValidateInfo(info);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = Serialize(data, schema);
        var waiter = new TaskCompletionSource<SaveResult>();
        if (payload.Length > Options.MaxBytes)
        {
            PostCompletion(new FinishedJob(waiter, TooLarge(slot, payload.Length)));
            return waiter.Task;
        }

        var job = new SaveJob(slot, payload, schema.Version, info, _playTimeSeconds, DateTimeOffset.UtcNow);
        job.Waiters.Add(waiter);
        var waitsForFrame = TakeThumbnail(job, info.Thumbnail, canWait: true);
        job.AwaitingFrame = waitsForFrame;
        lock (_queueLock)
        {
            if (_pendingSaves.TryGetValue(slot, out var pending) && !pending.Started && (!waitsForFrame || pending.AwaitingFrame))
            {
                pending.Supersede(job); // the newest save of a slot wins; every caller gets its result
                return waiter.Task;
            }

            _pendingSaves[slot] = job;
            _active.Add(job);
        }

        if (waitsForFrame)
            _awaitingThumbnail.Add(job);
        else
            _jobs.Writer.TryWrite(job);

        return waiter.Task;
    }

    /// <summary>
    /// Saves synchronously on the calling thread (quit and pause paths): waits for queued work, writes, raises
    /// <see cref="Saved"/> and returns. A thumbnail is used only when one was already captured
    /// (<see cref="CaptureThumbnail"/>): a blocking call cannot wait for a frame.
    /// </summary>
    public SaveResult Save<T>(string slot, T data, SaveSchema<T> schema, SaveInfo info = default)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(schema);
        ValidateInfo(info);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = Serialize(data, schema);
        SaveResult result;
        if (payload.Length > Options.MaxBytes)
        {
            result = TooLarge(slot, payload.Length);
        }
        else
        {
            var job = new SaveJob(slot, payload, schema.Version, info, _playTimeSeconds, DateTimeOffset.UtcNow);
            TakeThumbnail(job, info.Thumbnail, canWait: false);
            Flush(Timeout.InfiniteTimeSpan); // queued saves of this slot are older: they go first
            DrainCompletions();
            result = WriteSave(job);
        }

        RaiseSaved(result);
        return result;
    }

    private SaveResult TooLarge(string slot, long bytes) =>
        new(slot, SaveStatus.TooLarge, $"The save is {bytes} bytes; the limit (saves.maxBytes) is {Options.MaxBytes}.", null);

    private byte[] Serialize<T>(T data, SaveSchema<T> schema)
    {
        _serializeBuffer.ResetWrittenCount();
        if (_serializeWriter is null)
            _serializeWriter = new Utf8JsonWriter(_serializeBuffer);
        else
            _serializeWriter.Reset(_serializeBuffer);
        JsonSerializer.Serialize(_serializeWriter, data, schema.TypeInfo);
        _serializeWriter.Flush();
        var bytes = _serializeBuffer.WrittenSpan.ToArray();
        if (_serializeBuffer.Capacity > 1 << 20)
            _serializeBuffer.Clear(); // keep a big save's buffer from pinning memory: the next call starts small again
        return bytes;
    }

    // Writes one save (the worker, or Save on the calling thread).
    private SaveResult WriteSave(SaveJob job)
    {
        var slot = job.Slot;
        try
        {
            var stored = Options.Compression == SaveCompression.Gzip ? SaveFile.Compress(job.Payload) : job.Payload;
            byte[]? png = null;
            if (job.Thumbnail is { } capture)
            {
                try
                {
                    png = SaveThumbnail.Encode(capture.Pixels, capture.Width, capture.Height, Options.ThumbnailWidth, Options.ThumbnailHeight);
                }
                catch (ArgumentException e)
                {
                    Log.Warning($"[Saves] No thumbnail for '{slot}': {e.Message}");
                }
            }

            var header = new SaveHeader
            {
                Kind = SaveFile.Kind,
                Format = SaveFile.CurrentFormat,
                Slot = slot,
                Version = job.Version,
                SavedUtc = job.SavedUtc,
                PlayTimeSeconds = job.PlayTimeSeconds,
                Description = job.Info.Description,
                Progress = job.Info.Progress,
                Device = Options.Device,
                Game = Options.GameName,
                GameVersion = Options.GameVersion,
                Engine = EngineInfo.Version,
                Compression = Options.Compression,
                PayloadBytes = stored.Length,
                Sha256 = SaveFile.Hash(stored),
            };

            byte[] file;
            lock (_storageLock)
            {
                // 1. The thumbnail first (a lost one is cosmetic), so the header never names a file that was not written.
                var thumbnailPath = ThumbnailFile(slot);
                if (png is not null)
                {
                    try
                    {
                        Storage.WriteDurable(thumbnailPath, png, 0);
                        header.Thumbnail = thumbnailPath;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning($"[Saves] Could not write the thumbnail of '{slot}': {e.Message}");
                        png = null;
                    }
                }
                else
                {
                    Storage.Delete(thumbnailPath); // the previous save's: this one has none
                }

                file = SaveFile.Build(header, stored);
                if (file.Length - stored.Length - 1 > SaveFile.MaxHeaderBytes)
                    return new SaveResult(slot, SaveStatus.TooLarge, $"The save header is over {SaveFile.MaxHeaderBytes} bytes.", null);

                // 2. Temp file, flush to disk, rotate backups, replace.
                Storage.WriteDurable(MainPath(slot), file, Options.Backups);
                _slots = null;
            }

            var cloudTooLarge = QueueUpload(slot, file, png, header.Sha256);
            return new SaveResult(slot, SaveStatus.Ok, null, header.ToInfo(slot, ThumbnailPathOf(header), file.Length), cloudTooLarge);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[Saves] Could not save '{slot}' to {Storage.Name} storage: {e.Message}");
            return new SaveResult(slot, SaveStatus.IoError, e.Message, null);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Loading
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads <paramref name="slot"/> synchronously (behind a loading screen, blocking is simpler), recovering a damaged
    /// slot from its newest valid copy. Waits for queued saves first. A successful load sets <see cref="PlayTime"/>.
    /// </summary>
    public SaveLoad<T> Load<T>(string slot, SaveSchema<T> schema)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(schema);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Flush(Timeout.InfiniteTimeSpan);
        DrainCompletions();
        var result = LoadCore(slot, schema);
        if (result.IsOk && result.Info is { } info)
            PlayTime = info.PlayTime;
        return result;
    }

    /// <summary><see cref="Load{T}"/> on the worker; the task completes on the main thread (in <see cref="Process"/>).</summary>
    public Task<SaveLoad<T>> LoadAsync<T>(string slot, SaveSchema<T> schema)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(schema);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseAwaitingThumbnails(); // a save waiting for its frame was asked for first: it must not run after this load
        var job = new LoadJob<T>(slot, schema);
        lock (_queueLock)
        {
            _pendingSaves.Remove(slot); // later saves must not merge into a save queued before this load
            _active.Add(job);
        }

        _jobs.Writer.TryWrite(job);
        return job.Waiter.Task;
    }

    private SaveLoad<T> LoadCore<T>(string slot, SaveSchema<T> schema)
    {
        var main = MainPath(slot);
        lock (_storageLock)
        {
            var first = TryLoadCopy(main, slot, schema);
            switch (first.Status)
            {
                case LoadStatus.Ok:
                    return new SaveLoad<T>(LoadStatus.Ok, first.Data, first.Info, null);
                case LoadStatus.TooNew:
                case LoadStatus.IoError:
                    return new SaveLoad<T>(first.Status, default, first.Info, first.Error);
            }

            // Damaged or missing: move the damaged file aside (never deleted), then the newest valid copy wins.
            var damage = first.Status == LoadStatus.Corrupt ? first.Error : null;
            if (damage is not null)
            {
                var quarantined = Quarantine(main);
                Log.Warning($"[Saves] Slot '{slot}' is damaged ({damage}){(quarantined is null ? "" : $"; kept as {quarantined}")}.");
                _slots = null;
            }

            var errors = damage;
            var candidates = new List<string>(Options.Backups + 1) { DurableFile.TempPath(main) };
            for (var i = 1; i <= Options.Backups; i++)
                candidates.Add(DurableFile.BackupPath(main, i));
            foreach (var candidate in candidates)
            {
                var copy = TryLoadCopy(candidate, slot, schema);
                if (copy.Status == LoadStatus.NotFound)
                    continue;
                if (copy.Status != LoadStatus.Ok)
                {
                    errors = errors is null ? $"{candidate}: {copy.Error}" : $"{errors}; {candidate}: {copy.Error}";
                    continue;
                }

                try
                {
                    Storage.WriteDurable(main, copy.File, 0);
                    _slots = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warning($"[Saves] Could not restore '{slot}' from {candidate}: {e.Message}");
                }

                Log.Info($"[Saves] Slot '{slot}' restored from {candidate} (saved {copy.Info!.SavedUtc:u}).");
                return new SaveLoad<T>(LoadStatus.RecoveredFromBackup, copy.Data, copy.Info,
                    damage ?? $"the slot file was missing; restored from {candidate}");
            }

            return errors is null
                ? new SaveLoad<T>(LoadStatus.NotFound, default, null, $"No save in slot '{slot}'.")
                : new SaveLoad<T>(LoadStatus.Corrupt, default, null, errors);
        }
    }

    private readonly record struct LoadAttempt<T>(LoadStatus Status, T? Data, SaveSlotInfo? Info, string? Error, byte[] File);

    // Under _storageLock. Reads, verifies and deserializes one copy of a slot.
    private LoadAttempt<T> TryLoadCopy<T>(string path, string slot, SaveSchema<T> schema)
    {
        byte[] file;
        try
        {
            using var stream = Storage.OpenRead(path);
            if (stream is null)
                return new LoadAttempt<T>(LoadStatus.NotFound, default, null, null, []);
            file = ReadAll(stream, SaveFile.MaxHeaderBytes + 1 + Options.MaxBytes);
        }
        catch (InvalidDataException e)
        {
            return Corrupt<T>(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LoadAttempt<T>(LoadStatus.IoError, default, null, e.Message, []);
        }

        if (!SaveFile.TryParseHeader(file, out var header, out var payloadOffset, out var headerError))
            return Corrupt<T>(headerError!);
        if (header.Kind != SaveFile.Kind)
            return Corrupt<T>($"not a save file (kind '{header.Kind}')");
        if (header.Format > SaveFile.CurrentFormat)
        {
            var info = header.ToInfo(slot, null, file.Length) with { IsReadable = false };
            return new LoadAttempt<T>(LoadStatus.TooNew, default, info,
                $"The save was written by a newer engine (format {header.Format}; this build reads up to {SaveFile.CurrentFormat}).", []);
        }

        if (header.Validate() is { } invalid)
            return Corrupt<T>(invalid);
        var slotInfo = header.ToInfo(slot, ThumbnailPathOf(header), file.Length);
        if (header.Version > schema.Version)
        {
            return new LoadAttempt<T>(LoadStatus.TooNew, default, slotInfo,
                $"The save has payload version {header.Version}; this build reads up to {schema.Version}.", []);
        }

        try
        {
            var payload = SaveFile.OpenPayload(file, payloadOffset, header, Options.MaxBytes);
            T? data;
            if (header.Version == schema.Version)
            {
                data = JsonSerializer.Deserialize(payload.Span, schema.TypeInfo);
            }
            else
            {
                if (JsonNode.Parse(payload.Span) is not JsonObject root)
                    throw new InvalidDataException("the payload is not a JSON object, so it cannot be migrated");
                JsonMigrations.Upgrade(root, header.Version, schema.Version, schema.Migrations, path, "save version");
                data = root.Deserialize(schema.TypeInfo);
            }

            return new LoadAttempt<T>(LoadStatus.Ok, data, slotInfo, null, file);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Bad JSON, a payload the type cannot hold, or a migration step that threw: all leave the copy unusable.
            return Corrupt<T>(e is JsonException ? $"the payload is not valid: {e.Message}" : e.Message);
        }
    }

    private static LoadAttempt<T> Corrupt<T>(string error) => new(LoadStatus.Corrupt, default, null, error, []);

    private static byte[] ReadAll(Stream stream, long limit)
    {
        if (stream.CanSeek)
        {
            var length = stream.Length;
            if (length > limit)
                throw new InvalidDataException($"the file is {length} bytes, over the {limit} byte limit");
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > limit)
                throw new InvalidDataException($"the file is over the {limit} byte limit");
            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }

    // Under _storageLock: moves a damaged file aside as path.corrupt-{utc}; returns the new path, or null.
    private string? Quarantine(string path) => MoveAside(path, "corrupt");

    private string? MoveAside(string path, string suffix)
    {
        var target = UniqueAside(path, suffix);
        try
        {
            return Storage.Move(path, target) ? target : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Saves] Could not move '{path}' aside: {e.Message}");
            return null;
        }
    }

    // Under _storageLock: path.{suffix}-{utc} that does not exist yet.
    private string UniqueAside(string path, string suffix)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var target = $"{path}.{suffix}-{stamp}";
        for (var i = 1; Storage.GetModifiedUtc(target) is not null; i++)
            target = $"{path}.{suffix}-{stamp}-{i.ToString(CultureInfo.InvariantCulture)}";
        return target;
    }

    // ----------------------------------------------------------------------------------------------------
    // Deleting
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes <paramref name="slot"/>: its file, temp file, backups and thumbnail (damaged and conflict copies are kept).
    /// Waits for queued saves first. False when there was nothing to delete.
    /// </summary>
    public bool Delete(string slot)
    {
        ValidateSlot(slot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Flush(Timeout.InfiniteTimeSpan);
        DrainCompletions();
        var main = MainPath(slot);
        var thumbnail = ThumbnailFile(slot);
        var deleted = false;
        lock (_storageLock)
        {
            foreach (var name in Storage.List(""))
            {
                if (name == main || name == DurableFile.TempPath(main) || IsBackupOf(name, main) || name == thumbnail || name == DurableFile.TempPath(thumbnail))
                    deleted |= Storage.Delete(name);
            }

            _slots = null;
        }

        return deleted;
    }

    private static bool IsBackupOf(string name, string main)
    {
        var prefix = main + ".bak";
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        for (var i = prefix.Length; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
                return false;
        }

        return true;
    }

    // ----------------------------------------------------------------------------------------------------
    // Thumbnails
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Requests a thumbnail now (e.g. as the pause menu opens, before it covers the game); the next save uses it. Needs
    /// frame capture (<c>saves.thumbnails</c> in <c>project.mfproj</c>); without it, warns once and does nothing.
    /// </summary>
    public void CaptureThumbnail()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanCapture())
            return;
        RequestCapture();
    }

    private bool CanCapture()
    {
        if (_tree is { CanCaptureFrame: true })
            return true;
        if (!_warnedNoCapture)
        {
            _warnedNoCapture = true;
            Log.Warning("[Saves] Save thumbnails need frame capture: set \"saves\": { \"thumbnails\": true } in project.mfproj. Thumbnail requests are ignored.");
        }

        return false;
    }

    private void RequestCapture()
    {
        if (_captureRequested)
            return;
        _captureRequested = true;
        _captureRequestedAt = Stopwatch.GetTimestamp();
        _tree!.CaptureFrame(_onCaptured);
    }

    // Gives the job a captured frame, or (canWait) requests one; true when the job must wait for it.
    private bool TakeThumbnail(SaveJob job, bool requested, bool canWait)
    {
        if (!requested && _thumbnail is null && !_captureRequested)
            return false;
        if (_thumbnail is { } ready)
        {
            _thumbnail = null;
            job.Thumbnail = ready;
            return false;
        }

        if (!canWait || !CanCapture())
            return false;
        RequestCapture();
        return true;
    }

    private void OnFrameCaptured(FrameCapture capture)
    {
        _captureRequested = false;
        if (_disposed)
            return;
        if (_awaitingThumbnail.Count == 0)
        {
            _thumbnail = capture; // CaptureThumbnail: kept for the next save
            return;
        }

        foreach (var job in _awaitingThumbnail)
            job.Thumbnail = capture;
        ReleaseAwaitingThumbnails();
    }

    // Main thread: hands the saves waiting for a frame to the worker (with the frame, if it came).
    private void ReleaseAwaitingThumbnails()
    {
        if (_awaitingThumbnail.Count == 0)
            return;
        foreach (var job in _awaitingThumbnail)
        {
            lock (_queueLock)
                job.AwaitingFrame = false;
            _jobs.Writer.TryWrite(job);
        }

        _awaitingThumbnail.Clear();
    }

    // ----------------------------------------------------------------------------------------------------
    // Frame, flush, dispose
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Advances <see cref="PlayTime"/> (unless the tree is paused) and delivers finished saves and loads. Allocation-free when idle.</summary>
    public void Process(in GameTime gameTime)
    {
        if (_tree is not { Paused: true })
            _playTimeSeconds += gameTime.DeltaTime;
        if (_awaitingThumbnail.Count > 0 && Stopwatch.GetElapsedTime(_captureRequestedAt) > CaptureTimeout)
        {
            Log.Warning("[Saves] No frame was captured for the save thumbnail; saving without one.");
            _captureRequested = false;
            ReleaseAwaitingThumbnails();
        }

        if (Volatile.Read(ref _completed) != 0)
            DrainCompletions();
    }

    /// <summary>
    /// Blocks until the queued writes and loads finish, at most <paramref name="timeout"/>
    /// (<see cref="Timeout.InfiniteTimeSpan"/> waits for ever); false on timeout. Saves waiting for a thumbnail frame are
    /// written without one. Mobile's <c>OnApplicationPause</c> and quit call it. Results are still delivered by the next
    /// <see cref="Process"/>.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        ReleaseAwaitingThumbnails();
        var infinite = timeout == Timeout.InfiniteTimeSpan;
        var deadline = infinite ? 0 : Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeout.TotalSeconds) * Stopwatch.Frequency);
        lock (_queueLock)
        {
            while (_active.Count > 0)
            {
                if (infinite)
                {
                    Monitor.Wait(_queueLock);
                    continue;
                }

                var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (remaining <= 0)
                    return false;
                Monitor.Wait(_queueLock, (int)Math.Min(remaining, int.MaxValue));
            }
        }

        return true;
    }

    /// <summary>Writes the queued saves (at most five seconds), delivers their results and stops the workers.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (!Flush(DisposeFlushTimeout))
            Log.Warning($"[Saves] Queued saves did not finish within {DisposeFlushTimeout.TotalSeconds:0} s of closing.");
        _disposed = true;
        _jobs.Writer.TryComplete();
        DisposeCloud();
        DrainCompletions();
        Job[] unfinished;
        lock (_queueLock)
            unfinished = [.. _active];
        foreach (var job in unfinished)
            job.Cancel();
    }

    // Main thread: completes finished jobs (tasks, Saved, PlayTime).
    private void DrainCompletions()
    {
        while (_completions.TryDequeue(out var job))
        {
            Interlocked.Decrement(ref _completed);
            try
            {
                job.Complete(this);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error($"[Saves] A save completion handler threw: {e}");
            }
        }
    }

    private void PostCompletion(Job job)
    {
        _completions.Enqueue(job);
        Interlocked.Increment(ref _completed);
    }

    private void RaiseSaved(in SaveResult result) => Saved?.Invoke(result);

    private async Task RunWorkerAsync()
    {
        var reader = _jobs.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                if (job is SaveJob save)
                {
                    lock (_queueLock)
                    {
                        save.Started = true;
                        if (_pendingSaves.TryGetValue(save.Slot, out var pending) && ReferenceEquals(pending, save))
                            _pendingSaves.Remove(save.Slot);
                    }
                }

                try
                {
                    job.Run(this);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Error($"[Saves] The save worker failed: {e}");
                    job.Fail(e);
                }

                PostCompletion(job);
                lock (_queueLock)
                {
                    _active.Remove(job);
                    Monitor.PulseAll(_queueLock);
                }
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Jobs
    // ----------------------------------------------------------------------------------------------------

    private abstract class Job
    {
        /// <summary>The worker's part.</summary>
        public abstract void Run(SaveServer server);

        /// <summary>The main thread's part (in <see cref="SaveServer.Process"/>).</summary>
        public abstract void Complete(SaveServer server);

        /// <summary><see cref="Run"/> threw unexpectedly.</summary>
        public abstract void Fail(Exception error);

        /// <summary>The server closed before the job finished.</summary>
        public abstract void Cancel();
    }

    private sealed class SaveJob(string slot, byte[] payload, int version, SaveInfo info, double playTimeSeconds, DateTimeOffset savedUtc) : Job
    {
        public string Slot { get; } = slot;
        public byte[] Payload { get; private set; } = payload;
        public int Version { get; private set; } = version;
        public SaveInfo Info { get; private set; } = info;
        public double PlayTimeSeconds { get; private set; } = playTimeSeconds;
        public DateTimeOffset SavedUtc { get; private set; } = savedUtc;
        public FrameCapture? Thumbnail { get; set; }
        public bool Started { get; set; }        // _queueLock
        public bool AwaitingFrame { get; set; }  // _queueLock
        public List<TaskCompletionSource<SaveResult>> Waiters { get; } = [];
        private SaveResult _result;

        // _queueLock, not started: takes a newer save of the same slot.
        public void Supersede(SaveJob newer)
        {
            Payload = newer.Payload;
            Version = newer.Version;
            Info = newer.Info;
            PlayTimeSeconds = newer.PlayTimeSeconds;
            SavedUtc = newer.SavedUtc;
            if (newer.Thumbnail is not null)
                Thumbnail = newer.Thumbnail;
            Waiters.AddRange(newer.Waiters);
        }

        public override void Run(SaveServer server) => _result = server.WriteSave(this);

        public override void Complete(SaveServer server)
        {
            foreach (var waiter in Waiters)
                waiter.TrySetResult(_result);
            server.RaiseSaved(_result);
        }

        public override void Fail(Exception error) => _result = new SaveResult(Slot, SaveStatus.IoError, error.Message, null);

        public override void Cancel()
        {
            var cancelled = new SaveResult(Slot, SaveStatus.Cancelled, "The save server closed before the save was written.", null);
            foreach (var waiter in Waiters)
                waiter.TrySetResult(cancelled);
        }
    }

    private sealed class LoadJob<T>(string slot, SaveSchema<T> schema) : Job
    {
        public TaskCompletionSource<SaveLoad<T>> Waiter { get; } = new();
        private SaveLoad<T> _result;

        public override void Run(SaveServer server) => _result = server.LoadCore(slot, schema);

        public override void Complete(SaveServer server)
        {
            if (_result.IsOk && _result.Info is { } info)
                server.PlayTime = info.PlayTime;
            Waiter.TrySetResult(_result);
        }

        public override void Fail(Exception error) => _result = new SaveLoad<T>(LoadStatus.IoError, default, null, error.Message);

        public override void Cancel() => Waiter.TrySetResult(new SaveLoad<T>(LoadStatus.IoError, default, null, "The save server closed before the load finished."));
    }

    // A save that ended before reaching the worker (TooLarge): delivered by Process like the others.
    private sealed class FinishedJob(TaskCompletionSource<SaveResult> waiter, SaveResult result) : Job
    {
        public override void Run(SaveServer server)
        {
        }

        public override void Complete(SaveServer server)
        {
            waiter.TrySetResult(result);
            server.RaiseSaved(result);
        }

        public override void Fail(Exception error)
        {
        }

        public override void Cancel() => waiter.TrySetResult(result);
    }

    // Runs an action on the main thread (cloud events and task completions).
    private sealed class MainThreadJob(Action action) : Job
    {
        public override void Run(SaveServer server)
        {
        }

        public override void Complete(SaveServer server) => action();

        public override void Fail(Exception error)
        {
        }

        public override void Cancel()
        {
        }
    }
}

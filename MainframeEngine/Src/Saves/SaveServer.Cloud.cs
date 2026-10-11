using System.Text.Json;
using System.Threading.Channels;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>The cloud seam (G4.7): uploads after local writes, <see cref="SyncAsync"/> and conflict resolution.</summary>
public sealed partial class SaveServer
{
    /// <summary>The file recording the last synced payload hash per slot.</summary>
    internal const string SyncStateFile = ".sync.json";

    private readonly Channel<CloudJob> _cloudJobs = Channel.CreateUnbounded<CloudJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cloudCancel = new();
    private readonly Lock _cloudLock = new();
    private readonly Dictionary<string, PendingUpload> _uploads = new(StringComparer.Ordinal); // _cloudLock
    private readonly Dictionary<string, PendingConflict> _conflicts = new(StringComparer.Ordinal); // cloud worker
    private bool _cloudWorkerStarted; // _cloudLock

    /// <summary>
    /// The cloud backend (M13: Steam Cloud, iCloud, Play Saved Games); null keeps saves local. Local storage stays the
    /// source of truth: every local write is then uploaded too (best effort, retried <see cref="SaveOptions.CloudRetries"/>
    /// times), unless the file is over the backend's <see cref="ICloudSaveBackend.MaxBytes"/>.
    /// </summary>
    public ICloudSaveBackend? Cloud { get; set; }

    /// <summary>Which copy <see cref="SyncAsync"/> keeps when a slot changed both locally and in the cloud since the last sync.</summary>
    public SaveConflictPolicy ConflictPolicy { get; set; } = SaveConflictPolicy.MostRecent;

    /// <summary>
    /// Raised on the main thread when <see cref="ConflictPolicy"/> is <see cref="SaveConflictPolicy.Manual"/> and a slot
    /// changed on both sides: (local, remote). Answer with <see cref="ResolveConflict"/>.
    /// </summary>
    [Signal]
    public event Action<SaveSlotInfo, SaveSlotInfo>? ConflictDetected;

    /// <summary>
    /// Reconciles local slots with <see cref="Cloud"/> (at start-up, on resume, from a "cloud" button): a slot changed on one
    /// side since the last sync (<c>saves/.sync.json</c>) is copied to the other; a slot changed on both is a conflict
    /// that <see cref="ConflictPolicy"/> settles, keeping the losing copy as <c>slot.msave.conflict-{utc}</c>. The task
    /// completes on the main thread (in <see cref="Process"/>); it fails when the backend's listing fails.
    /// </summary>
    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var waiter = new TaskCompletionSource();
        if (Cloud is null)
        {
            waiter.SetResult();
            return waiter.Task;
        }

        EnqueueCloud(new SyncJob(waiter, cancellationToken));
        return waiter.Task;
    }

    /// <summary>
    /// Settles a conflict raised by <see cref="ConflictDetected"/>: <paramref name="keepLocal"/> uploads the local copy
    /// and keeps the remote one as a conflict copy; otherwise the remote copy replaces the local one (kept as the
    /// conflict copy). The task completes on the main thread with false when the slot has no open conflict.
    /// </summary>
    public Task<bool> ResolveConflict(string slot, bool keepLocal)
    {
        ValidateSlot(slot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var waiter = new TaskCompletionSource<bool>();
        EnqueueCloud(new ResolveJob(slot, keepLocal, waiter));
        return waiter.Task;
    }

    // Worker, after a local write: queues the upload of the same bytes. True when the file is too large for the cloud.
    private bool QueueUpload(string slot, byte[] file, byte[]? thumbnail, string hash)
    {
        if (Cloud is not { } cloud || _disposed)
            return false;
        if (file.Length > cloud.MaxBytes)
        {
            Log.Warning($"[Saves] '{slot}' is {file.Length} bytes, over {cloud.Name}'s {cloud.MaxBytes} byte limit: kept local only.");
            return true;
        }

        bool queued;
        lock (_cloudLock)
        {
            queued = _uploads.ContainsKey(slot);
            _uploads[slot] = new PendingUpload(file, thumbnail ?? [], hash);
        }

        if (!queued)
            EnqueueCloud(new UploadJob(slot));
        return false;
    }

    private void EnqueueCloud(CloudJob job)
    {
        lock (_cloudLock)
        {
            if (!_cloudWorkerStarted)
            {
                _cloudWorkerStarted = true;
                _ = Task.Run(RunCloudWorkerAsync);
            }
        }

        _cloudJobs.Writer.TryWrite(job);
    }

    private void DisposeCloud()
    {
        _cloudCancel.Cancel();
        _cloudJobs.Writer.TryComplete();
    }

    private void PostToMainThread(Action action) => PostCompletion(new MainThreadJob(action));

    // Completes a cloud task on the main thread, or right away once the server is closed (Process no longer runs).
    private void CompleteOnMainThread(Action complete)
    {
        if (_disposed)
            complete();
        else
            PostToMainThread(complete);
    }

    private async Task RunCloudWorkerAsync()
    {
        var ct = _cloudCancel.Token;
        try
        {
            await foreach (var job in _cloudJobs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await job.RunAsync(this, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException e)
                {
                    job.Fail(this, e);
                    if (ct.IsCancellationRequested)
                        return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Warning($"[Saves] Cloud ({Cloud?.Name}): {e.Message}");
                    job.Fail(this, e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Uploads with retries; false when every attempt failed (the next sync tries again: .sync.json is not updated).
    private async Task<bool> UploadWithRetryAsync(ICloudSaveBackend cloud, string slot, ReadOnlyMemory<byte> file, ReadOnlyMemory<byte> thumbnail,
        CancellationToken ct)
    {
        var delay = Options.CloudRetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await cloud.UploadAsync(slot, file, thumbnail, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException && !(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (attempt >= Options.CloudRetries)
                {
                    Log.Warning($"[Saves] Could not upload '{slot}' to {cloud.Name} after {attempt + 1} attempts: {e.Message}");
                    return false;
                }
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay *= 2;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Sync state (.sync.json)
    // ----------------------------------------------------------------------------------------------------

    // Under _storageLock.
    private Dictionary<string, string> ReadSyncState()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = Storage.OpenRead(SyncStateFile);
            if (stream is null)
                return state;
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("slots", out var slots)
                && slots.ValueKind == JsonValueKind.Object)
            {
                foreach (var slot in slots.EnumerateObject())
                {
                    if (IsValidSlotName(slot.Name) && slot.Value.ValueKind == JsonValueKind.String)
                        state[slot.Name] = slot.Value.GetString()!;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Saves] {SyncStateFile} is unreadable ({e.Message}); every slot syncs as if new.");
        }

        return state;
    }

    // Under _storageLock.
    private void WriteSyncState(Dictionary<string, string> state)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, SceneFormat.WriteOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteStartObject("slots");
            var slots = state.Keys.ToArray();
            Array.Sort(slots, StringComparer.Ordinal);
            foreach (var slot in slots)
                w.WriteString(slot, state[slot]);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        Storage.WriteDurable(SyncStateFile, stream.GetBuffer().AsSpan(0, (int)stream.Length), 0);
    }

    private void RecordSynced(string slot, string hash)
    {
        try
        {
            lock (_storageLock)
            {
                var state = ReadSyncState();
                if (state.TryGetValue(slot, out var known) && known == hash)
                    return;
                state[slot] = hash;
                WriteSyncState(state);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Saves] Could not write {SyncStateFile}: {e.Message}");
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Sync
    // ----------------------------------------------------------------------------------------------------

    private readonly record struct LocalCopy(SaveHeader Header, SaveSlotInfo Info);

    // Under _storageLock: the readable local headers by slot.
    private Dictionary<string, LocalCopy> ReadLocalHeaders()
    {
        var result = new Dictionary<string, LocalCopy>(StringComparer.Ordinal);
        foreach (var name in Storage.List(""))
        {
            if (!name.EndsWith(SaveFile.Extension, StringComparison.Ordinal))
                continue;
            var slot = name[..^SaveFile.Extension.Length];
            if (IsValidSlotName(slot) && ReadLocalHeader(slot) is { } copy)
                result[slot] = copy;
        }

        return result;
    }

    // Under _storageLock.
    private LocalCopy? ReadLocalHeader(string slot)
    {
        try
        {
            using var stream = Storage.OpenRead(MainPath(slot));
            if (stream is null || SaveFile.ReadHeaderLine(stream, out _) is not { } line || !SaveFile.TryParseHeaderLine(line, out var header, out _)
                || header.Validate() is not null)
                return null;
            return new LocalCopy(header, header.ToInfo(slot, ThumbnailPathOf(header), stream.CanSeek ? stream.Length : line.Length + 1 + header.PayloadBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        if (Cloud is not { } cloud)
            return;
        var remote = new Dictionary<string, CloudSaveMeta>(StringComparer.Ordinal);
        foreach (var meta in await cloud.ListAsync(ct).ConfigureAwait(false))
        {
            if (IsValidSlotName(meta.Slot))
                remote[meta.Slot] = meta;
        }

        Dictionary<string, LocalCopy> local;
        Dictionary<string, string> synced;
        lock (_storageLock)
        {
            local = ReadLocalHeaders();
            synced = ReadSyncState();
        }

        var slots = new SortedSet<string>(local.Keys, StringComparer.Ordinal);
        slots.UnionWith(remote.Keys);
        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncSlotAsync(cloud, slot, local.TryGetValue(slot, out var l) ? l : null, remote.GetValueOrDefault(slot),
                    synced.GetValueOrDefault(slot), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException && e is not OperationCanceledException)
            {
                Log.Warning($"[Saves] Could not sync '{slot}' with {cloud.Name}: {e.Message}");
            }
        }
    }

    private async Task SyncSlotAsync(ICloudSaveBackend cloud, string slot, LocalCopy? local, CloudSaveMeta? remote, string? synced, CancellationToken ct)
    {
        var localHash = local?.Header.Sha256;
        var remoteHash = remote?.Sha256;
        if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            if (localHash is not null && localHash != synced)
                RecordSynced(slot, localHash);
            return;
        }

        if (local is not { } localCopy)
        {
            if (remoteHash == synced)
                return; // deleted here since the last sync: the cloud copy is not brought back
            await DownloadAsync(cloud, slot, expectedLocalHash: null, ct).ConfigureAwait(false);
            return;
        }

        var localChanged = localHash != synced;
        var remoteChanged = remoteHash != synced;
        if (remote is null || (localChanged && !remoteChanged))
        {
            await UploadLocalAsync(cloud, slot, ct).ConfigureAwait(false);
            return;
        }

        if (!localChanged)
        {
            await DownloadAsync(cloud, slot, localHash, ct).ConfigureAwait(false);
            return;
        }

        // Both changed since the last sync: a conflict.
        if (await DownloadValidAsync(cloud, slot, ct).ConfigureAwait(false) is not { } download)
            return;
        var remoteInfo = download.Header.ToInfo(slot, null, download.File.File.Length);
        var localHeader = localCopy.Header;
        switch (ConflictPolicy)
        {
            case SaveConflictPolicy.Manual:
                _conflicts[slot] = new PendingConflict(download, localHeader.Sha256);
                var localInfo = localCopy.Info;
                PostToMainThread(() => ConflictDetected?.Invoke(localInfo, remoteInfo));
                return;
            case SaveConflictPolicy.HighestProgress when localHeader.Progress != download.Header.Progress:
                await ResolveAsync(cloud, slot, localHeader.Progress > download.Header.Progress, download, localHeader.Sha256, ct).ConfigureAwait(false);
                return;
            default:
                await ResolveAsync(cloud, slot, localHeader.SavedUtc >= download.Header.SavedUtc, download, localHeader.Sha256, ct).ConfigureAwait(false);
                return;
        }
    }

    private readonly record struct ValidDownload(CloudSaveFile File, SaveHeader Header);

    private static async Task<ValidDownload?> DownloadValidAsync(ICloudSaveBackend cloud, string slot, CancellationToken ct)
    {
        if (await cloud.DownloadAsync(slot, ct).ConfigureAwait(false) is not { } file)
            return null;
        var bytes = file.File.ToArray();
        string? error = null;
        if (!SaveFile.TryParseHeader(bytes, out var header, out var offset, out error) || (error = header.Validate()) is not null)
        {
            Log.Warning($"[Saves] {cloud.Name}'s copy of '{slot}' is not a valid save ({error}); left alone.");
            return null;
        }

        if (header.Format <= SaveFile.CurrentFormat)
        {
            var stored = bytes.AsSpan(offset);
            if (stored.Length != header.PayloadBytes || !string.Equals(SaveFile.Hash(stored), header.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning($"[Saves] {cloud.Name}'s copy of '{slot}' is damaged (checksum); left alone.");
                return null;
            }
        }

        return new ValidDownload(file with { File = bytes }, header);
    }

    // The remote copy replaces the local one (no conflict); skipped when the local copy changed meanwhile.
    private async Task DownloadAsync(ICloudSaveBackend cloud, string slot, string? expectedLocalHash, CancellationToken ct)
    {
        if (await DownloadValidAsync(cloud, slot, ct).ConfigureAwait(false) is not { } download)
            return;
        lock (_storageLock)
        {
            if (ReadLocalHeader(slot)?.Header.Sha256 != expectedLocalHash)
                return; // a save landed while downloading: the next sync sees it
            WriteRemote(slot, download);
        }

        RecordSynced(slot, download.Header.Sha256);
    }

    // Under _storageLock: writes a downloaded copy over the local slot (the old one rotates into the backups).
    private void WriteRemote(string slot, ValidDownload download)
    {
        var thumbnail = ThumbnailFile(slot);
        if (download.File.Thumbnail.Length > 0)
            Storage.WriteDurable(thumbnail, download.File.Thumbnail.Span, 0);
        else
            Storage.Delete(thumbnail);
        Storage.WriteDurable(MainPath(slot), download.File.File.Span, Options.Backups);
        _slots = null;
    }

    private async Task UploadLocalAsync(ICloudSaveBackend cloud, string slot, CancellationToken ct)
    {
        byte[] file;
        byte[] thumbnail = [];
        string hash;
        lock (_storageLock)
        {
            if (ReadLocalHeader(slot) is not { } local)
                return;
            hash = local.Header.Sha256;
            using (var stream = Storage.OpenRead(MainPath(slot)))
            {
                if (stream is null)
                    return;
                file = ReadAll(stream, SaveFile.MaxHeaderBytes + 1 + Options.MaxBytes);
            }

            if (local.Header.Thumbnail is { } name && !name.Contains('/', StringComparison.Ordinal))
            {
                using var stream = Storage.OpenRead(name);
                if (stream is not null)
                    thumbnail = ReadAll(stream, 16 << 20);
            }
        }

        if (file.Length > cloud.MaxBytes)
        {
            Log.Warning($"[Saves] '{slot}' is over {cloud.Name}'s {cloud.MaxBytes} byte limit: kept local only.");
            return;
        }

        if (await UploadWithRetryAsync(cloud, slot, file, thumbnail, ct).ConfigureAwait(false))
            RecordSynced(slot, hash);
    }

    // Settles a conflict: the loser is kept as slot.msave.conflict-{utc}.
    private async Task ResolveAsync(ICloudSaveBackend? cloud, string slot, bool keepLocal, ValidDownload remote, string expectedLocalHash, CancellationToken ct)
    {
        var main = MainPath(slot);
        if (keepLocal)
        {
            lock (_storageLock)
                Storage.WriteDurable(UniqueAside(main, "conflict"), remote.File.File.Span, 0);
            Log.Info($"[Saves] Conflict on '{slot}': kept the local copy; the cloud copy is kept as a conflict copy.");
            if (cloud is not null)
                await UploadLocalAsync(cloud, slot, ct).ConfigureAwait(false);
            return;
        }

        lock (_storageLock)
        {
            if (ReadLocalHeader(slot)?.Header.Sha256 != expectedLocalHash)
            {
                Log.Info($"[Saves] Conflict on '{slot}': the local copy changed meanwhile; it is kept.");
                return;
            }

            using (var stream = Storage.OpenRead(main))
            {
                if (stream is not null)
                    Storage.WriteDurable(UniqueAside(main, "conflict"), ReadAll(stream, SaveFile.MaxHeaderBytes + 1 + Options.MaxBytes), 0);
            }

            WriteRemote(slot, remote);
        }

        Log.Info($"[Saves] Conflict on '{slot}': kept the cloud copy; the local copy is kept as a conflict copy.");
        RecordSynced(slot, remote.Header.Sha256);
    }

    // ----------------------------------------------------------------------------------------------------
    // Cloud jobs (run one at a time on the cloud worker)
    // ----------------------------------------------------------------------------------------------------

    private readonly record struct PendingUpload(byte[] File, byte[] Thumbnail, string Hash);

    private readonly record struct PendingConflict(ValidDownload Remote, string LocalHash);

    private abstract class CloudJob
    {
        public abstract Task RunAsync(SaveServer server, CancellationToken ct);

        public virtual void Fail(SaveServer server, Exception error)
        {
        }
    }

    private sealed class UploadJob(string slot) : CloudJob
    {
        public override async Task RunAsync(SaveServer server, CancellationToken ct)
        {
            PendingUpload upload;
            lock (server._cloudLock)
            {
                if (!server._uploads.Remove(slot, out upload))
                    return;
            }

            if (server.Cloud is { } cloud && await server.UploadWithRetryAsync(cloud, slot, upload.File, upload.Thumbnail, ct).ConfigureAwait(false))
                server.RecordSynced(slot, upload.Hash);
        }
    }

    private sealed class SyncJob(TaskCompletionSource waiter, CancellationToken cancellationToken) : CloudJob
    {
        public override async Task RunAsync(SaveServer server, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            await server.SyncAllAsync(linked.Token).ConfigureAwait(false);
            server.CompleteOnMainThread(() => waiter.TrySetResult());
        }

        public override void Fail(SaveServer server, Exception error) => server.CompleteOnMainThread(() =>
        {
            if (error is OperationCanceledException)
                waiter.TrySetCanceled();
            else
                waiter.TrySetException(error);
        });
    }

    private sealed class ResolveJob(string slot, bool keepLocal, TaskCompletionSource<bool> waiter) : CloudJob
    {
        public override async Task RunAsync(SaveServer server, CancellationToken ct)
        {
            if (!server._conflicts.Remove(slot, out var conflict))
            {
                server.CompleteOnMainThread(() => waiter.TrySetResult(false));
                return;
            }

            await server.ResolveAsync(server.Cloud, slot, keepLocal, conflict.Remote, conflict.LocalHash, ct).ConfigureAwait(false);
            server.CompleteOnMainThread(() => waiter.TrySetResult(true));
        }

        public override void Fail(SaveServer server, Exception error) => server.CompleteOnMainThread(() => waiter.TrySetException(error));
    }
}

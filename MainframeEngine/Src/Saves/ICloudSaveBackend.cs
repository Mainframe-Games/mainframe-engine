namespace MainframeEngine;

/// <summary>
/// A cloud store for save slots (<see cref="SaveServer.Cloud"/>): Steam Cloud, iCloud/CloudKit and Play Saved Games in
/// M13, a fake in tests. It stores whole <c>.msave</c> files (header line + payload, the same bytes as on disk) and their
/// thumbnails, so no backend needs its own format. Local storage stays the source of truth: the server uploads after
/// every local write (best effort, retried) and reconciles with <see cref="SaveServer.SyncAsync"/>.
/// </summary>
public interface ICloudSaveBackend
{
    /// <summary>A short name for logs.</summary>
    string Name { get; }

    /// <summary>The largest file one slot may hold; a larger save stays local (<see cref="SaveResult.CloudTooLarge"/>).</summary>
    long MaxBytes { get; }

    /// <summary>The slots the cloud holds, with their header metadata (<see cref="CloudSaveMeta.FromFile"/> builds it).</summary>
    Task<IReadOnlyList<CloudSaveMeta>> ListAsync(CancellationToken ct);

    /// <summary>Stores <paramref name="file"/> (and its thumbnail PNG, possibly empty) as <paramref name="slot"/>, replacing it.</summary>
    Task UploadAsync(string slot, ReadOnlyMemory<byte> file, ReadOnlyMemory<byte> thumbnail, CancellationToken ct);

    /// <summary>The slot's file and thumbnail, or null when the cloud does not hold it.</summary>
    Task<CloudSaveFile?> DownloadAsync(string slot, CancellationToken ct);
}

/// <summary>
/// A cloud slot's metadata: the header fields M13's <c>SaveMeta</c> needs (slot, description, play time, modified time,
/// device, progress) plus the payload hash the server compares against local copies and <c>saves/.sync.json</c>.
/// </summary>
/// <param name="Slot">The slot name.</param>
/// <param name="SavedUtc">The header's <c>savedUtc</c>.</param>
/// <param name="Progress">The header's <c>progress</c>.</param>
/// <param name="PlayTime">The header's <c>playTimeSeconds</c>.</param>
/// <param name="Description">The header's <c>description</c>.</param>
/// <param name="Device">The header's <c>device</c>.</param>
/// <param name="Sha256">The header's <c>sha256</c> (of the stored payload): two copies with the same hash hold the same save.</param>
/// <param name="SizeBytes">The file's size.</param>
public sealed record CloudSaveMeta(string Slot, DateTimeOffset SavedUtc, double Progress, TimeSpan PlayTime, string? Description,
    string? Device, string Sha256, long SizeBytes)
{
    /// <summary>
    /// The metadata of a <c>.msave</c> file (what a backend stores next to an upload); null when its header line is not a
    /// readable save header.
    /// </summary>
    public static CloudSaveMeta? FromFile(ReadOnlySpan<byte> file)
    {
        if (!SaveFile.TryParseHeader(file, out var header, out _, out _))
            return null;
        return new CloudSaveMeta(header.Slot, header.SavedUtc, header.Progress, TimeSpan.FromSeconds(header.PlayTimeSeconds),
            header.Description, header.Device, header.Sha256, file.Length);
    }
}

/// <summary>A downloaded cloud slot: the <c>.msave</c> file and its thumbnail PNG (empty when it has none).</summary>
public sealed record CloudSaveFile(string Slot, ReadOnlyMemory<byte> File, ReadOnlyMemory<byte> Thumbnail);

/// <summary>
/// Which copy wins when a slot changed both locally and in the cloud since the last sync. The losing copy is kept as
/// <c>slot.msave.conflict-{utc}</c>.
/// </summary>
public enum SaveConflictPolicy
{
    /// <summary>The newer <c>savedUtc</c> wins.</summary>
    MostRecent,

    /// <summary>The higher <c>progress</c> wins (ties: the newer).</summary>
    HighestProgress,

    /// <summary>
    /// <see cref="SaveServer.ConflictDetected"/> asks the game, which answers with <see cref="SaveServer.ResolveConflict"/>.
    /// </summary>
    Manual,
}

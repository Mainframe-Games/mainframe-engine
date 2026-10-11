using System.Text.Json.Serialization.Metadata;

namespace MainframeEngine;

/// <summary>
/// The game's payload type for <see cref="SaveServer"/>: its source-generated <see cref="JsonTypeInfo{T}"/> (no
/// reflection, trim- and AOT-safe), the payload version this build writes and the steps that upgrade older payloads.
/// </summary>
/// <example>
/// <code>
/// static readonly SaveSchema&lt;CrateSave&gt; Schema = new(DemoSaveJson.Default.CrateSave, version: 2,
///     new JsonMigration(1, static root => root["spawned"] ??= root["crates"]!.AsArray().Count));
/// </code>
/// </example>
public sealed class SaveSchema<T>
{
    /// <param name="typeInfo">The payload's source-generated type info (a <c>JsonSerializerContext</c> property).</param>
    /// <param name="version">The payload version this build writes (1 or more).</param>
    /// <param name="migrations">Upgrade steps from older versions, each editing the payload's JSON tree.</param>
    public SaveSchema(JsonTypeInfo<T> typeInfo, int version, params ReadOnlySpan<JsonMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        TypeInfo = typeInfo;
        Version = version;
        Migrations = migrations.ToArray();
    }

    /// <summary>Serializes and deserializes the payload.</summary>
    public JsonTypeInfo<T> TypeInfo { get; }

    /// <summary>The payload version this build writes; a save with a newer version loads as <see cref="LoadStatus.TooNew"/>.</summary>
    public int Version { get; }

    /// <summary>The upgrade steps, run by <see cref="JsonMigrations.Upgrade"/> when an older save loads.</summary>
    public IReadOnlyList<JsonMigration> Migrations { get; }
}

/// <summary>The optional metadata a save carries in its header besides the engine's own fields.</summary>
/// <param name="Description">The game's label for the slot ("Physics 3D — 14 crates"); at most <see cref="SaveServer.MaxDescriptionLength"/> characters.</param>
/// <param name="Progress">The game's progress measure (0..1 or any scale the game picks), used by <see cref="SaveConflictPolicy.HighestProgress"/>.</param>
/// <param name="Thumbnail">Capture a thumbnail of the current frame (<see cref="SaveServer.CaptureThumbnail"/>).</param>
public readonly record struct SaveInfo(string? Description = null, double Progress = 0, bool Thumbnail = false);

/// <summary>How a save ended.</summary>
public enum SaveStatus
{
    /// <summary>The save was written (and replaced the previous one, now <c>.bak1</c>).</summary>
    Ok,

    /// <summary>The payload is over <see cref="SaveOptions.MaxBytes"/>; nothing was written.</summary>
    TooLarge,

    /// <summary>The storage failed (<see cref="SaveResult.Error"/>); the previous save is unchanged.</summary>
    IoError,

    /// <summary>The server was disposed before the write finished.</summary>
    Cancelled,
}

/// <summary>How a load ended.</summary>
public enum LoadStatus
{
    /// <summary>The slot loaded.</summary>
    Ok,

    /// <summary>
    /// The slot was damaged (moved aside as <c>.corrupt-{utc}</c>); the newest valid copy (<c>.tmp</c>, then the backups)
    /// was restored and loaded. <see cref="SaveLoad{T}.Info"/> describes that copy, <see cref="SaveLoad{T}.Error"/> the damage.
    /// </summary>
    RecoveredFromBackup,

    /// <summary>The slot does not exist.</summary>
    NotFound,

    /// <summary>The slot and every copy of it are damaged (<see cref="SaveLoad{T}.Error"/>).</summary>
    Corrupt,

    /// <summary>The save was written by a newer build (container format or payload version); the file is left untouched.</summary>
    TooNew,

    /// <summary>The storage failed (<see cref="SaveLoad{T}.Error"/>).</summary>
    IoError,
}

/// <summary>A save slot's header (<see cref="SaveServer.ListSlots"/>), read without loading the payload.</summary>
/// <param name="Slot">The slot name (<c>[a-z0-9_-]{1,64}</c>).</param>
/// <param name="Format">The engine's container format.</param>
/// <param name="Version">The game's payload version.</param>
/// <param name="SavedUtc">When the save was taken.</param>
/// <param name="PlayTime">The unpaused play time at the save (<see cref="SaveServer.PlayTime"/>).</param>
/// <param name="Description">The game's label (<see cref="SaveInfo.Description"/>).</param>
/// <param name="Progress">The game's progress measure (<see cref="SaveInfo.Progress"/>).</param>
/// <param name="Device">The machine that wrote it (<see cref="SaveOptions.Device"/>).</param>
/// <param name="GameVersion">The game's version that wrote it (<see cref="ProjectSettings.Version"/>).</param>
/// <param name="ThumbnailPath">The thumbnail PNG (a file path for <see cref="LocalSaveStorage"/>), or null.</param>
/// <param name="SizeBytes">The file's size.</param>
/// <param name="IsReadable">False when the header is damaged or from a newer engine (the other fields are then partial).</param>
public sealed record SaveSlotInfo(string Slot, int Format, int Version, DateTimeOffset SavedUtc, TimeSpan PlayTime,
    string? Description, double Progress, string? Device, string? GameVersion, string? ThumbnailPath, long SizeBytes,
    bool IsReadable);

/// <summary>The outcome of <see cref="SaveServer.SaveAsync{T}"/> / <see cref="SaveServer.Save{T}"/>.</summary>
/// <param name="Slot">The slot written.</param>
/// <param name="Status">How the save ended.</param>
/// <param name="Error">What went wrong (null when <see cref="SaveStatus.Ok"/>).</param>
/// <param name="Info">The written header (null unless <see cref="SaveStatus.Ok"/>).</param>
/// <param name="CloudTooLarge">
/// The save is over the cloud backend's <see cref="ICloudSaveBackend.MaxBytes"/>: it was written locally and not uploaded.
/// </param>
public readonly record struct SaveResult(string Slot, SaveStatus Status, string? Error, SaveSlotInfo? Info, bool CloudTooLarge = false)
{
    /// <summary>True when the save was written.</summary>
    public bool IsOk => Status == SaveStatus.Ok;
}

/// <summary>The outcome of <see cref="SaveServer.Load{T}"/> / <see cref="SaveServer.LoadAsync{T}"/>.</summary>
/// <param name="Status">How the load ended.</param>
/// <param name="Data">The payload (default unless <see cref="LoadStatus.Ok"/> or <see cref="LoadStatus.RecoveredFromBackup"/>).</param>
/// <param name="Info">The header of the copy that loaded (or of the too-new file).</param>
/// <param name="Error">What went wrong; for <see cref="LoadStatus.RecoveredFromBackup"/>, what was wrong with the slot.</param>
public readonly record struct SaveLoad<T>(LoadStatus Status, T? Data, SaveSlotInfo? Info, string? Error)
{
    /// <summary>True when <see cref="Data"/> holds the payload (<see cref="LoadStatus.Ok"/> or <see cref="LoadStatus.RecoveredFromBackup"/>).</summary>
    public bool IsOk => Status is LoadStatus.Ok or LoadStatus.RecoveredFromBackup;
}

/// <summary>How a save's payload is stored (<c>saves.compression</c> in <c>project.mfproj</c>).</summary>
public enum SaveCompression
{
    /// <summary>Plain JSON (readable in any editor).</summary>
    None,

    /// <summary>Gzip (<see cref="System.IO.Compression.GZipStream"/>); the header stays plain JSON.</summary>
    Gzip,
}

/// <summary>
/// <see cref="SaveServer"/> settings: limits, backups, compression and thumbnails (from <c>project.mfproj</c>'s
/// <c>saves</c> section through <see cref="FromProject"/>), and the identity written into every header.
/// </summary>
public sealed record SaveOptions
{
    /// <summary>The largest payload (uncompressed bytes) a save may have, and a load accepts; default 16 MiB.</summary>
    public long MaxBytes { get; init; } = SavesProjectSettings.DefaultMaxBytes;

    /// <summary>Previous versions kept per slot (<c>.bak1</c> … <c>.bakN</c>, at most <see cref="DurableFile.MaxBackups"/>); default 2.</summary>
    public int Backups { get; init; } = SavesProjectSettings.DefaultBackups;

    /// <summary>How payloads are stored.</summary>
    public SaveCompression Compression { get; init; }

    /// <summary>Thumbnail width in pixels (default 320).</summary>
    public int ThumbnailWidth { get; init; } = SavesProjectSettings.DefaultThumbnailWidth;

    /// <summary>Thumbnail height in pixels (default 180).</summary>
    public int ThumbnailHeight { get; init; } = SavesProjectSettings.DefaultThumbnailHeight;

    /// <summary>The <c>game</c> header field (<see cref="ProjectSettings.Name"/>).</summary>
    public string? GameName { get; init; }

    /// <summary>The <c>gameVersion</c> header field (<see cref="ProjectSettings.Version"/>).</summary>
    public string? GameVersion { get; init; }

    /// <summary>The <c>device</c> header field: <see cref="Environment.MachineName"/> on desktop.</summary>
    public string? Device { get; init; } = SafeMachineName();

    /// <summary>Upload attempts after the first fails (<see cref="SaveServer.Cloud"/>); default 3.</summary>
    public int CloudRetries { get; init; } = 3;

    /// <summary>The wait before the first upload retry; doubles with every retry. Default 2 s.</summary>
    public TimeSpan CloudRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Options from <paramref name="settings"/>: its <see cref="ProjectSettings.Saves"/> section, name and version.</summary>
    public static SaveOptions FromProject(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var saves = settings.Saves;
        return new SaveOptions
        {
            MaxBytes = saves.MaxBytes,
            Backups = saves.Backups,
            Compression = saves.Compression,
            ThumbnailWidth = saves.ThumbnailWidth,
            ThumbnailHeight = saves.ThumbnailHeight,
            GameName = settings.Name,
            GameVersion = settings.Version.Length > 0 ? settings.Version : null,
        };
    }

    private static string? SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

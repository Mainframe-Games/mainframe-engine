using System.Text.Json.Nodes;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// Upgrades a parsed timeline document to <see cref="TimelineSerializer.CurrentVersion"/>, one version at a time, before it
/// is deserialised (the <c>.mfcut</c> migrator's pattern). Register a step keyed by the version it upgrades <em>from</em>;
/// steps edit the JSON in place and leave <c>version</c> to the migrator.
/// </summary>
public sealed class TimelineMigrator
{
    private readonly int _targetVersion;
    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _steps;

    /// <summary>The migrator <see cref="TimelineSerializer"/> uses. Version 1 is the first: no steps yet.</summary>
    public static TimelineMigrator Default { get; } = new(TimelineSerializer.CurrentVersion, new Dictionary<int, Action<JsonObject>>());

    public TimelineMigrator(int targetVersion, IReadOnlyDictionary<int, Action<JsonObject>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        _targetVersion = targetVersion;
        _steps = steps;
    }

    /// <exception cref="TimelineFormatException">The version is missing, invalid, newer than this build reads, or a step is missing.</exception>
    public void Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.TryGetPropertyValue("version", out var node) || node is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 1)
            throw new TimelineFormatException("The timeline file has no valid \"version\" field.");
        if (version > _targetVersion)
            throw new TimelineFormatException(
                $"This timeline was saved by a newer version of the editor (file version {version}, this build reads up to {_targetVersion}). Update the editor to open it.");
        while (version < _targetVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
                throw new TimelineFormatException($"No migration is registered from timeline version {version} to {version + 1}.");
            step(document);
            version++;
            document["version"] = version;
        }
    }
}

using System.Text.Json.Nodes;

namespace MainframeEngine.Video.Projects;

/// <summary>
/// Upgrades a parsed project document from an older schema version to the current one,
/// one version at a time, before it is deserialised into typed models.
/// </summary>
/// <remarks>
/// To add a schema change: bump <see cref="ProjectSerializer.CurrentVersion"/>, then register a step
/// keyed by the version it upgrades <em>from</em> in <see cref="Default"/>. Steps mutate the JSON in
/// place and do not touch the <c>version</c> field; the migrator bumps that itself. See <see cref="Default"/> for the registered steps.
/// </remarks>
public sealed class ProjectMigrator
{
    private readonly int _targetVersion;
    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _steps;

    /// <summary>The migrator used by <see cref="ProjectSerializer"/>.</summary>
    public static ProjectMigrator Default { get; } = new(
        ProjectSerializer.CurrentVersion,
        new Dictionary<int, Action<JsonObject>>
        {
            // 1 -> 2: the "filler" segment origin was added. No field changes; the bump exists so
            // an older build refuses a file it cannot read with its "newer version" message.
            [1] = static _ => { },

            // 2 -> 3: the "gameplay" segment origin was added. No field changes; same reason as 1 -> 2.
            [2] = static _ => { },
        });

    /// <param name="targetVersion">The version documents are migrated up to.</param>
    /// <param name="steps">Migration steps keyed by the version they upgrade from. Step <c>n</c> takes a version-<c>n</c> document and makes it version <c>n + 1</c>.</param>
    public ProjectMigrator(int targetVersion, IReadOnlyDictionary<int, Action<JsonObject>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        _targetVersion = targetVersion;
        _steps = steps;
    }

    /// <summary>Migrates <paramref name="document"/> in place to the target version.</summary>
    /// <exception cref="ProjectFormatException">The version is missing, invalid, newer than the target, or a step is missing.</exception>
    public void Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var version = ReadVersion(document);
        if (version > _targetVersion)
        {
            throw new ProjectFormatException(
                $"This project was saved by a newer version of the editor (file version {version}, this build reads up to {_targetVersion}). Update the editor to open it.");
        }

        while (version < _targetVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                throw new ProjectFormatException($"No migration is registered from project version {version} to {version + 1}.");
            }

            step(document);
            version++;
            document["version"] = version;
        }
    }

    private static int ReadVersion(JsonObject document)
    {
        if (!document.TryGetPropertyValue("version", out var node) || node is null)
        {
            throw new ProjectFormatException("The project file has no \"version\" field, so this is not a readable project.");
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var version))
        {
            throw new ProjectFormatException($"The project file's \"version\" field is not an integer: {node.ToJsonString()}.");
        }

        if (version < 1)
        {
            throw new ProjectFormatException($"Project version {version} is not valid; versions start at 1.");
        }

        return version;
    }
}

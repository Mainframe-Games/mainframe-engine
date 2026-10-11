using System.Text.Json.Nodes;

namespace MainframeEngine;

/// <summary>One upgrade step: rewrites a version-<see cref="From"/> document into version From+1 by editing the JSON tree.</summary>
public readonly record struct JsonMigration(int From, Action<JsonObject> Upgrade);

/// <summary>
/// The version upgrade loop shared by <c>project.mfproj</c>, save games and settings files. A document older than the
/// current version runs through the registered steps one at a time (each edits the JSON tree), and only then is the tree
/// read into the typed object. Steps are static lambdas over <see cref="JsonObject"/>: no reflection, trim- and AOT-safe.
/// </summary>
public static class JsonMigrations
{
    /// <summary>
    /// Runs the steps from <paramref name="version"/> to <paramref name="current"/> on <paramref name="root"/>. Throws
    /// <see cref="InvalidDataException"/> naming <paramref name="source"/> and <paramref name="what"/> ("save version",
    /// "settings format") when a step is missing. Does not write the version back: the caller owns that key.
    /// </summary>
    public static void Upgrade(JsonObject root, int version, int current, IReadOnlyList<JsonMigration> migrations, string source, string what)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(migrations);
        while (version < current)
        {
            var step = Find(migrations, version);
            if (step < 0)
                throw new InvalidDataException($"'{source}': no migration from {what} {version} to {version + 1}.");
            migrations[step].Upgrade(root);
            version++;
        }
    }

    /// <summary>
    /// Reads the integer <paramref name="key"/> of <paramref name="root"/> (a missing key means 1) and checks it against
    /// <paramref name="current"/>: newer and non-positive values throw <see cref="InvalidDataException"/>.
    /// </summary>
    public static int ReadVersion(JsonObject root, string key, int current, string source, string what)
    {
        ArgumentNullException.ThrowIfNull(root);
        var version = 1;
        if (root[key] is { } node)
        {
            if (node is not JsonValue value || !value.TryGetValue(out version))
            {
                if (node is JsonValue v && v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
                    version = (int)d;
                else
                    throw new InvalidDataException($"'{source}': '{key}' must be an integer.");
            }
        }

        if (version > current)
            throw new InvalidDataException($"'{source}' has {what} {version}; this build reads up to {current}.");
        if (version < 1)
            throw new InvalidDataException($"'{source}' has an invalid {what} {version}.");
        return version;
    }

    private static int Find(IReadOnlyList<JsonMigration> migrations, int from)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].From == from)
                return i;
        }

        return -1;
    }
}

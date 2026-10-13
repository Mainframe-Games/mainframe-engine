using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Projects;

/// <summary>Reads and writes <c>.mfcut</c> project files, and still reads Cutback's <c>.cutback</c> ones (same schema).</summary>
public static class ProjectSerializer
{
    /// <summary>Schema version written by this build. Bump alongside a new <see cref="ProjectMigrator"/> step.</summary>
    public const int CurrentVersion = 3;

    /// <summary>The extension projects are saved with.</summary>
    public const string FileExtension = ".mfcut";

    /// <summary>Cutback's extension: such a file opens, and is saved as <see cref="FileExtension"/>.</summary>
    public const string LegacyFileExtension = ".cutback";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>True when <paramref name="path"/> ends in <see cref="FileExtension"/> or <see cref="LegacyFileExtension"/>, ignoring case.</summary>
    public static bool IsProjectPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        return extension.Equals(FileExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LegacyFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static string Serialize(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var file = ToFile(project);
        return JsonSerializer.Serialize(file, ProjectJsonContext.Default.ProjectFile);
    }

    /// <exception cref="ProjectFormatException">The text is not a readable project.</exception>
    /// <exception cref="InvalidPartitionException">The segments in the file do not form a valid partition.</exception>
    public static VideoProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject
                ?? throw new ProjectFormatException("The project file does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("The project file is not valid JSON.", ex);
        }

        ProjectMigrator.Default.Migrate(root);

        ProjectFile file;
        try
        {
            file = root.Deserialize(ProjectJsonContext.Default.ProjectFile)
                ?? throw new ProjectFormatException("The project file deserialised to nothing.");
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException($"The project file has an unexpected shape: {ex.Message}", ex);
        }

        if (file.Source is null || file.Segments is null || file.Settings is null)
        {
            throw new ProjectFormatException("The project file is missing one of: source, segments, settings.");
        }

        return new VideoProject(file.Source, file.Segments, file.Transcript ?? [], file.Settings);
    }

    public static async Task SaveAsync(VideoProject project, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var json = Serialize(project);

        // Write to a sibling temp file and move it into place so a crash mid-write never leaves a
        // truncated project on disk.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <exception cref="ProjectFormatException">The file is not a readable project.</exception>
    /// <exception cref="InvalidPartitionException">The segments in the file do not form a valid partition.</exception>
    public static async Task<VideoProject> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    private static ProjectFile ToFile(VideoProject project) => new(
        CurrentVersion,
        project.Source,
        project.Segments.ToList(),
        project.Transcript.ToList(),
        project.Settings);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Video.Tests;

public sealed class ProjectSerializerTests
{
    private static readonly string[] SegmentFields = ["id", "start", "end", "enabled", "origin", "reason"];

    private static readonly SourceInfo Source = new(
        Path: "/abs/path/to/recording.mp4",
        Sha256: "0123abcd",
        DurationSeconds: 12.04,
        Width: 1920,
        Height: 1080,
        FrameRate: 30.0);

    private static VideoProject SampleProject()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 3.21, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.21, 4.86, enabled: false, SegmentOrigin.Auto, "silence 1.65s"),
            Segment.Create(4.86, 12.04, enabled: true, SegmentOrigin.Manual),
        };
        var transcript = new[] { new Word("hello", 0.5, 0.9, 0.97) };
        var settings = new DetectionSettings(PaddingMs: 80, MinSilenceMs: 500, SilenceThresholdDb: -30.0, MinKeepMs: 150);
        return new VideoProject(Source, segments, transcript, settings);
    }

    // ---- model --------------------------------------------------------------------------------

    [Fact]
    public void CreateNewHasOneEnabledSegmentAndDefaultSettings()
    {
        var project = VideoProject.CreateNew(Source);

        Assert.Equal(Source, project.Source);
        Assert.Single(project.Segments);
        Assert.Equal(0.0, project.Segments[0].Start);
        Assert.Equal(Source.DurationSeconds, project.Segments[0].End);
        Assert.True(project.Segments[0].Enabled);
        Assert.Empty(project.Transcript);
        Assert.Equal(DetectionSettings.Default, project.Settings);
    }

    [Fact]
    public void DefaultSettingsMatchTheDocumentedSchema()
    {
        Assert.Equal(new DetectionSettings(
            PaddingMs: 60,
            MinSilenceMs: 400,
            SilenceThresholdDb: -34.0,
            MinKeepMs: 120), DetectionSettings.Default);
    }

    [Fact]
    public void ConstructingAProjectWithAnInvalidPartitionThrows()
    {
        var bad = new[] { Segment.Create(0.0, 5.0, enabled: true, SegmentOrigin.Auto) }; // ends before 12.04

        var act = () => new VideoProject(Source, bad, [], DetectionSettings.Default);

        Assert.Throws<InvalidPartitionException>(act);
    }

    // ---- serialisation shape ------------------------------------------------------------------

    [Fact]
    public void SerializeWritesTheCurrentVersionAndCamelCaseKeys()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(ProjectSerializer.CurrentVersion, root["version"]!.GetValue<int>());
        Assert.True(root.ContainsKey("source"));
        Assert.True(root.ContainsKey("segments"));
        Assert.True(root.ContainsKey("transcript"));
        Assert.True(root.ContainsKey("settings"));
        Assert.True(root["source"]!.AsObject().ContainsKey("path"));
        Assert.True(root["source"]!.AsObject().ContainsKey("sha256"));
        Assert.True(root["source"]!.AsObject().ContainsKey("durationSeconds"));
        Assert.True(root["source"]!.AsObject().ContainsKey("width"));
        Assert.True(root["source"]!.AsObject().ContainsKey("height"));
        Assert.True(root["source"]!.AsObject().ContainsKey("frameRate"));
        Assert.True(root["settings"]!.AsObject().ContainsKey("paddingMs"));
        Assert.True(root["settings"]!.AsObject().ContainsKey("minSilenceMs"));
        Assert.True(root["settings"]!.AsObject().ContainsKey("silenceThresholdDb"));
        Assert.True(root["settings"]!.AsObject().ContainsKey("minKeepMs"));
    }

    [Fact]
    public void SerializeWritesOriginAsALowerCaseString()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var segments = JsonNode.Parse(json)!["segments"]!.AsArray();
        Assert.Equal("auto", segments[0]!["origin"]!.GetValue<string>());
        Assert.Equal("manual", segments[2]!["origin"]!.GetValue<string>());
    }

    [Fact]
    public void SerializeWritesANullReasonExplicitly()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var first = JsonNode.Parse(json)!["segments"]![0]!.AsObject();
        Assert.True(first.ContainsKey("reason"));
        Assert.Null(first["reason"]);
    }

    [Fact]
    public void SerializeWritesOnlySchemaFieldsForASegment()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        var first = JsonNode.Parse(json)!["segments"]![0]!.AsObject();
        Assert.Equivalent(SegmentFields, first.Select(kv => kv.Key));
    }

    [Fact]
    public void SerializeIsIndentedForDiffability()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        Assert.Contains("\n  \"version\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/r/a.mfcut", true)]
    [InlineData("/r/a.CUTBACK", true)]
    [InlineData("/r/a.mp4", false)]
    public void IsProjectPathAcceptsBothProjectExtensions(string path, bool expected) =>
        Assert.Equal(expected, ProjectSerializer.IsProjectPath(path));

    // ---- round trip ---------------------------------------------------------------------------

    [Fact]
    public void RoundTripPreservesEverything()
    {
        var original = SampleProject();

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));

        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.Segments, restored.Segments);
        Assert.Equal(original.Transcript, restored.Transcript);
        Assert.Equal(original.Settings, restored.Settings);
    }

    [Fact]
    public void RoundTripOfASingleSegmentProjectWorks()
    {
        var original = VideoProject.CreateNew(Source);

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));

        Assert.Equivalent(original.Segments, restored.Segments);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripThroughAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"video-test-{Guid.NewGuid():N}.mfcut");
        try
        {
            var original = SampleProject();

            await ProjectSerializer.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var restored = await ProjectSerializer.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original.Segments, restored.Segments);
            Assert.Equal(original.Source, restored.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeserializeAcceptsTheSchemaExampleFromCLAUDEMd()
    {
        const string json = """
            {
              "version": 1,
              "source": {
                "path": "/abs/path/to/recording.mp4",
                "sha256": "abc",
                "durationSeconds": 12.04,
                "width": 1920, "height": 1080, "frameRate": 30.0
              },
              "segments": [
                { "id": "a", "start": 0.0,  "end": 3.21,  "enabled": true,  "origin": "auto",   "reason": null },
                { "id": "b", "start": 3.21, "end": 4.86,  "enabled": false, "origin": "auto",   "reason": "silence 1.65s" },
                { "id": "c", "start": 4.86, "end": 12.04, "enabled": true,  "origin": "manual", "reason": null }
              ],
              "transcript": [],
              "settings": {
                "paddingMs": 60,
                "minSilenceMs": 400,
                "silenceThresholdDb": -34.0,
                "minKeepMs": 120
              }
            }
            """;

        var project = ProjectSerializer.Deserialize(json);

        Assert.Equal(3, project.Segments.Count);
        Assert.False(project.Segments[1].Enabled);
        Assert.Equal("silence 1.65s", project.Segments[1].Reason);
        Assert.Equal(SegmentOrigin.Manual, project.Segments[2].Origin);
        Assert.Equal(DetectionSettings.Default, project.Settings);
    }

    // ---- error handling -----------------------------------------------------------------------

    [Fact]
    public void DeserializeRejectsAFileFromANewerVersion()
    {
        var json = ProjectSerializer.Serialize(SampleProject())
            .Replace($"\"version\": {ProjectSerializer.CurrentVersion}", $"\"version\": {ProjectSerializer.CurrentVersion + 1}", StringComparison.Ordinal);

        var act = () => ProjectSerializer.Deserialize(json);

        var e = Assert.Throws<ProjectFormatException>(act);
        Assert.Contains("newer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRejectsAMissingVersion()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root.Remove("version");

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        var e = Assert.Throws<ProjectFormatException>(act);
        Assert.Contains("version", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRejectsNonObjectJson()
    {
        var act = () => ProjectSerializer.Deserialize("[1, 2, 3]");

        Assert.Throws<ProjectFormatException>(act);
    }

    [Fact]
    public void DeserializeWrapsMalformedJsonInAProjectFormatException()
    {
        var act = () => ProjectSerializer.Deserialize("{ not json");

        var e = Assert.Throws<ProjectFormatException>(act);
        Assert.IsAssignableFrom<JsonException>(e.InnerException);
    }

    [Fact]
    public void DeserializeSurfacesAnInvalidPartition()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root["segments"]!.AsArray().RemoveAt(1); // leaves a gap between 3.21 and 4.86

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.Throws<InvalidPartitionException>(act);
    }

    [Fact]
    public void DeserializeRejectsAnUnknownOrigin()
    {
        var root = JsonNode.Parse(ProjectSerializer.Serialize(SampleProject()))!.AsObject();
        root["segments"]![0]!["origin"] = "robot";

        var act = () => ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.Throws<ProjectFormatException>(act);
    }

    // ---- migration hook -----------------------------------------------------------------------

    [Fact]
    public void MigratorAppliesStepsInOrderAndBumpsTheVersion()
    {
        var log = new List<string>();
        var migrator = new ProjectMigrator(targetVersion: 3, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = doc => { log.Add("1->2"); doc["fromOne"] = true; },
            [2] = doc => { log.Add("2->3"); doc["fromTwo"] = true; },
        });
        var doc = new JsonObject { ["version"] = 1 };

        migrator.Migrate(doc);

        Assert.Equal(["1->2", "2->3"], log);
        Assert.Equal(3, doc["version"]!.GetValue<int>());
        Assert.True(doc["fromOne"]!.GetValue<bool>());
        Assert.True(doc["fromTwo"]!.GetValue<bool>());
    }

    [Fact]
    public void MigratorLeavesACurrentDocumentUntouched()
    {
        var called = false;
        var migrator = new ProjectMigrator(targetVersion: 2, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => called = true,
        });
        var doc = new JsonObject { ["version"] = 2 };

        migrator.Migrate(doc);

        Assert.False(called);
        Assert.Equal(2, doc["version"]!.GetValue<int>());
    }

    [Fact]
    public void MigratorThrowsWhenAStepIsMissing()
    {
        var migrator = new ProjectMigrator(targetVersion: 3, steps: new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => { },
            // no step for 2 -> 3
        });
        var doc = new JsonObject { ["version"] = 1 };

        var act = () => migrator.Migrate(doc);

        var e = Assert.Throws<ProjectFormatException>(act);
        Assert.Matches("2.*3", e.Message);
    }

    [Fact]
    public void MigratorRejectsAVersionBelowOne()
    {
        var migrator = new ProjectMigrator(targetVersion: 1, steps: new Dictionary<int, Action<JsonObject>>());
        var doc = new JsonObject { ["version"] = 0 };

        var act = () => migrator.Migrate(doc);

        Assert.Throws<ProjectFormatException>(act);
    }

    [Fact]
    public void DefaultMigratorAppliesNoStepsToACurrentVersionDocument()
    {
        var doc = new JsonObject { ["version"] = ProjectSerializer.CurrentVersion };

        ProjectMigrator.Default.Migrate(doc);

        Assert.Equal(ProjectSerializer.CurrentVersion, doc["version"]!.GetValue<int>());
    }

    // ---- word anchors -------------------------------------------------------------------------

    [Fact]
    public void AWordAnchorRoundTripsAndIsNullWhenAbsent()
    {
        var words = new[] { new Word("um", 1.0, 1.3, 0.9, Anchor: 1.12), new Word("so", 1.4, 1.6, 0.9) };
        var project = new VideoProject(Source, [Segment.Create(0.0, 12.04, enabled: true, SegmentOrigin.Auto)], words, DetectionSettings.Default);

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        Assert.Equal(1.12, JsonNode.Parse(json)!["transcript"]![0]!["anchor"]!.GetValue<double>());
        Assert.Equal(1.12, loaded.Transcript[0].Anchor);
        Assert.Null(loaded.Transcript[1].Anchor);
    }

    [Fact]
    public void ATranscriptWordWithoutAnAnchorFieldLoadsWithNoAnchor()
    {
        const string json = """
            {
              "version": 2,
              "source": { "path": "/abs/path/to/recording.mp4", "sha256": "0123abcd", "durationSeconds": 12.04, "width": 1920, "height": 1080, "frameRate": 30.0 },
              "segments": [ { "id": "a", "start": 0.0, "end": 12.04, "enabled": true, "origin": "auto", "reason": null } ],
              "transcript": [ { "text": "um", "start": 1.0, "end": 1.3, "confidence": 0.9 } ],
              "settings": { "paddingMs": 60, "minSilenceMs": 400, "silenceThresholdDb": -34.0, "minKeepMs": 120 }
            }
            """;

        var project = ProjectSerializer.Deserialize(json);

        Assert.Null(Assert.Single(project.Transcript).Anchor);
    }

    // ---- version 2: filler origin -------------------------------------------------------------

    [Fact]
    public void FillerOriginRoundTripsAsALowerCaseString()
    {
        var segments = new[]
        {
            Segment.Create(0.0, 3.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(3.0, 3.4, enabled: false, SegmentOrigin.Filler, "filler: um"),
            Segment.Create(3.4, 12.04, enabled: true, SegmentOrigin.Auto),
        };
        var project = new VideoProject(Source, segments, [], DetectionSettings.Default);

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        Assert.Equal("filler", JsonNode.Parse(json)!["segments"]![1]!["origin"]!.GetValue<string>());
        Assert.Equal(SegmentOrigin.Filler, loaded.Segments[1].Origin);
        Assert.Equal("filler: um", loaded.Segments[1].Reason);
    }

    [Fact]
    public void AVersion1FileIsMigratedToVersion2Unchanged()
    {
        const string v1 = """
            {
              "version": 1,
              "source": { "path": "/abs/path/to/recording.mp4", "sha256": "0123abcd", "durationSeconds": 12.04, "width": 1920, "height": 1080, "frameRate": 30.0 },
              "segments": [
                { "id": "a", "start": 0.0, "end": 12.04, "enabled": true, "origin": "auto", "reason": null }
              ],
              "transcript": [],
              "settings": { "paddingMs": 60, "minSilenceMs": 400, "silenceThresholdDb": -34.0, "minKeepMs": 120 }
            }
            """;

        var project = ProjectSerializer.Deserialize(v1);

        Assert.Equal("a", Assert.Single(project.Segments).Id);
        Assert.Equal(ProjectSerializer.CurrentVersion, JsonNode.Parse(ProjectSerializer.Serialize(project))!["version"]!.GetValue<int>());
    }

    // ---- version 3: gameplay origin -----------------------------------------------------------

    [Fact]
    public void CurrentVersionIs3()
    {
        Assert.Equal(3, ProjectSerializer.CurrentVersion);
    }

    [Fact]
    public void GameplayOriginRoundTripsAsLowerCaseString()
    {
        var source = new SourceInfo("/v.mp4", "abc", 10.0, 1920, 1080, 60.0);
        var segments = new[]
        {
            Segment.Create(0.0, 4.0, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4.0, 7.5, enabled: false, SegmentOrigin.Gameplay, "idle 3.5s"),
            Segment.Create(7.5, 10.0, enabled: true, SegmentOrigin.Auto),
        };
        var project = new VideoProject(source, segments, [], DetectionSettings.Default);

        var json = ProjectSerializer.Serialize(project);
        var back = ProjectSerializer.Deserialize(json);

        Assert.Equal("gameplay", JsonNode.Parse(json)!["segments"]![1]!["origin"]!.GetValue<string>());
        Assert.Equal(SegmentOrigin.Gameplay, back.Segments[1].Origin);
        Assert.Equal("idle 3.5s", back.Segments[1].Reason);
    }

    [Fact]
    public void AVersion2FileIsMigratedTo3()
    {
        var source = new SourceInfo("/v.mp4", "abc", 10.0, 1920, 1080, 60.0);
        var project = VideoProject.CreateNew(source);
        var v2 = ProjectSerializer.Serialize(project).Replace("\"version\": 3", "\"version\": 2", StringComparison.Ordinal);

        Assert.Contains("\"version\": 2", v2, StringComparison.Ordinal);
        var back = ProjectSerializer.Deserialize(v2);

        Assert.Single(back.Segments);
    }
}

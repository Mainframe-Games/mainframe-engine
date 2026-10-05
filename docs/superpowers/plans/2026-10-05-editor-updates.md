# Editor Updates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The editor checks GitHub Releases at start-up, shows a quiet badge when a newer release exists, and updates
itself in place with one click (download → verify → swap → relaunch) on macOS, Windows and Linux.

**Architecture:** Pure, unit-tested pieces in `MainframeEngine.Editor/Src/Updates/` (release feed parsing, version
rules, platform/asset naming, install location, downloader, applier) behind one seam, `IUpdateService`, which the
workspace's `UpdateController` drives from the main thread by polling tasks. The swap is done by the *downloaded*
editor itself, started with `--apply-update` before any window exists: it waits for the old process, renames the install
aside, copies itself in, records the result and relaunches.

**Tech Stack:** C# / .NET 10, `HttpClient`, `System.Text.Json` (`JsonDocument`, source-generated context),
`System.Formats.Tar`, `System.IO.Compression`, `System.Security.Cryptography`, RmlUi editor documents, xUnit v3.

**Spec:** `docs/design/future/editor-updates.md` (read it first; this plan argues from it).

## Global Constraints

- No new NuGet packages; versions stay central in `Directory.Packages.props` (nothing to add).
- Build has `TreatWarningsAsErrors` and `AnalysisLevel=latest-recommended`: use `StringComparison.Ordinal`,
  `CultureInfo.InvariantCulture`, `ConfigureAwait(false)` in library code (as `DotnetSdk`/`ProcessRunner` do).
- Tests use xUnit v3: pass `TestContext.Current.CancellationToken` to methods that take a token; editor UI tests use
  `[Collection(nameof(SerialEditor))]` and `HeadlessEditor`.
- All new types live in `namespace MainframeEngine.Editor;` (the editor uses one namespace across folders); tests in
  `namespace MainframeEngine.Editor.Tests.Updates;` (UI tests may live in `MainframeEngine.Editor.Tests`).
- Feed URL: `https://api.github.com/repos/Mainframe-Games/mainframe-engine/releases/latest`; headers
  `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: MainframeEngine-Editor/<version>`;
  10 s timeout; no token.
- Asset names: `MainframeEngine-X.Y.Z-osx-arm64.tar.gz`, `MainframeEngine-X.Y.Z-linux-x64.tar.gz`,
  `MainframeEngine-X.Y.Z-win-x64.zip`. Archive roots: `Mainframe Engine.app/` (macOS), `MainframeEngine-X.Y.Z-<rid>/`
  (Windows, Linux). Executable `MainframeEngine.Editor` (`.exe` on Windows; `Contents/MacOS/` in the bundle).
- Staging `~/.mainframe/updates/X.Y.Z/` (archive, then `app/`), `~/.mainframe/updates/result.json`,
  `~/.mainframe/updates/update.log`, backup `<root>.old`.
- Hand-off arguments: `--apply-update <install-root> --wait-pid <pid> --from <current version> [--project <folder>]`.
- Only strictly newer `vX.Y.Z` releases are offered; dev builds (`0.0.0-dev`, any suffix) never check or clean up.
- Editor runs created for tests, `--smoke`, `--qa-script` and `--hidden` have no update service (no network).
- Badges use the existing icon `arrow-bar-to-down`; View release uses `external-link`; the menu item uses `refresh`
  (no icon-atlas regeneration).
- Commits stay local (no pushes). Commit message prefix `Editor:` / `Docs:`; end with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Run `just format` before each commit.

## Review Focus

1. **Two editors open at once.** The second editor's start-up clean-up must not delete the first one's staged download
   of a newer version → clean-up keeps staging folders whose version is newer than the running editor
   (test in Task 6).
2. **Paths with spaces** (`/Applications/Mainframe Engine.app`, `C:\My Tools\…`, a project in `~/My Games/`) must reach
   the applier and the relaunch intact → all process arguments go through `ProcessStartInfo.ArgumentList`, never a
   joined string (tests in Tasks 6 and 7).
3. **Offline at start-up.** No badge, no dialog, one `Log.Info` line, the editor otherwise unaffected
   (test in Task 8).
4. **Impatient clicks.** Clicking Update again while downloading, or Check for Updates while the start-up check is still
   running, must not start a second download or a second request (tests in Task 8).
5. **Release notes with markup** (`<b>`, `&`, `<script>`) render as plain text and produce no RmlUi errors
   (test in Task 8).

---

## File Structure

Create (all in `MainframeEngine.Editor/Src/Updates/` unless noted):

| File | Responsibility |
|---|---|
| `ReleaseVersion.cs` | `X.Y.Z` parse/compare (no suffixes) |
| `ReleaseInfo.cs` | `ReleaseInfo`, `ReleaseAsset` records; `UpdateException` |
| `ReleaseFeed.cs` | GET `releases/latest`, `Parse(json)` |
| `UpdatePlatform.cs` | RID, asset name, staged root, executable path; `UpdatePaths` |
| `InstallLocation.cs` | install root from base directory, replaceability |
| `UpdateChecker.cs` | `UpdateCheckStatus`, `UpdateCheckResult`, `UpdateChecker` |
| `UpdateDownloader.cs` | `StagedUpdate`, download + SHA-256 + safe extract |
| `UpdateApplier.cs` | `ApplyUpdateRequest`, `UpdateResult` (+ JSON context), `UpdateApplier`, `UpdateCleanup` |
| `UpdateService.cs` | `IUpdateService`, `GitHubUpdateService` |
| `UpdateController.cs` | `UpdateState`, `UpdateController` (workspace side) |
| `MainframeEngine.Editor/Src/Files/OsShell.cs` | `Reveal(path)`, `OpenUrl(uri)` (moved out of `FileSystemPanel`) |
| `MainframeEngine.Editor/Src/UI/UpdateDialog.cs` | the dialog document |
| `MainframeEngine.Editor/Content/Editor/update.rml` | the dialog markup |
| `Tests/MainframeEngine.Editor.Tests/Updates/*.cs` | unit + headless UI tests, `ReleaseFixtures`, `StubHandler`, `FakeUpdateService` |

Modify: `EditorSettings.cs`, `EditorSettingsDialog.cs`, `editor_settings.rml`, `EditorWorkspace.cs` (options, quit),
`EditorWorkspace.Projects.cs` (wiring), `EditorCommandLine.cs`, `Program.cs`, `EditorCommands.cs`, `Panels.cs`
(toolbar), `toolbar.rml`, `ProjectManager.cs`, `project_manager.rml`, `FileSystemPanel.cs`, `Qa/EditorQaScript.cs`,
`Tests/QA/editor-walkthrough.qa`, docs (`release.md`, `editor.md`, spec move), `CLAUDE.md`.

---

### Task 1: `EditorSettings.CheckForUpdates` and its checkbox

**Files:**
- Modify: `MainframeEngine.Editor/Src/Settings/EditorSettings.cs`
- Modify: `MainframeEngine.Editor/Src/UI/EditorSettingsDialog.cs:41-48`
- Modify: `MainframeEngine.Editor/Content/Editor/editor_settings.rml`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/EditorSettingsUpdateTests.cs`

**Interfaces:**
- Produces: `bool EditorSettings.CheckForUpdates { get; set; }` (default `true`), persisted as `checkForUpdates`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace MainframeEngine.Editor.Tests.Updates;

public sealed class EditorSettingsUpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-settings").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void CheckForUpdatesDefaultsToOn() => Assert.True(new EditorSettings().CheckForUpdates);

    [Fact]
    public void CheckForUpdatesIsSavedLoadedAndCloned()
    {
        var path = Path.Combine(_directory, "editor_settings.json");
        new EditorSettings { CheckForUpdates = false }.Save(path);

        Assert.False(EditorSettings.Load(path).CheckForUpdates);
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(new EditorSettings { CheckForUpdates = false }.Clone().CheckForUpdates);
    }

    [Fact]
    public void SettingsFilesWithoutTheFieldKeepChecksOn()
    {
        var path = Path.Combine(_directory, "editor_settings.json");
        File.WriteAllText(path, """{ "format": 1, "accent": "#3b82f6" }""");
        Assert.True(EditorSettings.Load(path).CheckForUpdates);
    }
}

[Collection(nameof(SerialEditor))]
public sealed class EditorSettingsDialogUpdateTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    [Fact]
    public void TheDialogAppliesTheUpdateCheckSetting()
    {
        var w = _editor.Workspace;
        w.EditorSettingsDialog.Open();
        _editor.Tick();
        w.EditorSettingsDialog.Working.CheckForUpdates = false;
        w.EditorSettingsDialog.Apply();
        Assert.False(w.Settings.CheckForUpdates);
        Assert.Empty(_editor.RmlMessages);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.EditorSettings"`
Expected: build FAILS — `EditorSettings` has no `CheckForUpdates`.

- [ ] **Step 3: Implement**

In `EditorSettings.cs`, after `AutoReloadCode`:

```csharp
    /// <summary>Look for a newer editor on GitHub Releases at start-up (Help › Check for Updates… works either way).</summary>
    public bool CheckForUpdates { get; set; } = true;
```

In `Load`'s initializer add `CheckForUpdates = data.CheckForUpdates ?? true,`; in `Save`'s `EditorSettingsData`
initializer add `CheckForUpdates = CheckForUpdates,`; in `Clone()` add `CheckForUpdates = CheckForUpdates,`; in
`EditorSettingsData` add `public bool? CheckForUpdates { get; set; }`. Update the class summary's list of preferences to
end with "…, whether code reloads automatically after a build and whether the editor checks for updates."

In `EditorSettingsDialog.OnReady`, after the `auto_reload` binding:

```csharp
            .Bind("check_updates", this, static d => d._working.CheckForUpdates, static (d, v) => d._working.CheckForUpdates = v)
```

In `editor_settings.rml`: change `#dialog` to `height: 400dp; … margin-top: -200dp;` and add after the "Code reload"
row:

```xml
            <div class="dialog-row">
                <span class="caption">Updates</span>
                <label class="check" data-tooltip="Look for a newer Mainframe Engine release on GitHub when the editor starts">
                    <input type="checkbox" class="checkbox" id="es-check-updates" data-checked="check_updates"/><span>Check for updates at startup</span></label>
            </div>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.EditorSettings"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Check-for-updates setting

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Release versions and the release feed

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/ReleaseVersion.cs`, `ReleaseInfo.cs`, `ReleaseFeed.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/ReleaseFixtures.cs`, `ReleaseVersionTests.cs`, `ReleaseFeedTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>`;
    `static bool TryParse(string? text, out ReleaseVersion version)`; `ToString()` → `"X.Y.Z"`; `<`, `>`, `<=`, `>=`.
  - `sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256)` (lower-case hex or null).
  - `sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Notes, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets)`
    with `ReleaseAsset? FindAsset(string name)`.
  - `sealed class UpdateException : Exception` (user-facing message).
  - `sealed class ReleaseFeed(HttpClient http, string editorVersion)`: `static Uri LatestReleaseUri`,
    `Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)` (null when the tag is not `vX.Y.Z`; throws
    `UpdateException`), `static ReleaseInfo? Parse(ReadOnlyMemory<byte> json)` (throws `UpdateException` on bad JSON).
  - Test helpers: `ReleaseFixtures.Recorded`, `ReleaseFixtures.Latest(string tag = "v1.1.0", bool digests = true, params string[] rids)`,
    `StubHandler` (`Json(string)`, `Status(HttpStatusCode)`, `Throws(Exception)`, `Requests`).

- [ ] **Step 1: Write the test helpers**

`ReleaseFixtures.cs`:

```csharp
using System.Net;
using System.Text;

namespace MainframeEngine.Editor.Tests.Updates;

/// <summary>GitHub <c>releases/latest</c> responses: the real v1.0.0 answer (trimmed) and generated variants.</summary>
internal static class ReleaseFixtures
{
    public const string Recorded = """
        {
          "html_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/tag/v1.0.0",
          "tag_name": "v1.0.0",
          "name": "Mainframe Engine v1.0.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "MainframeEngine-1.0.0-linux-x64.tar.gz", "content_type": "application/x-gtar", "size": 56920893,
              "digest": "sha256:dfdf31c3d560f72aa9850e71af5511fe6e03f63e0e5fb052010fe2b16fa2f89e",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-linux-x64.tar.gz" },
            { "name": "MainframeEngine-1.0.0-osx-arm64.tar.gz", "content_type": "application/x-gtar", "size": 56772908,
              "digest": "sha256:857EF126FFC089AFCC78405195C60550DED974703A418EC5E03EAF5C61AFE6DA",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-osx-arm64.tar.gz" },
            { "name": "MainframeEngine-1.0.0-win-x64.zip", "content_type": "application/zip", "size": 55036179,
              "digest": "md5:0123",
              "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.0.0/MainframeEngine-1.0.0-win-x64.zip" }
          ],
          "body": "## What's Changed\n* M0–M10: engine foundation through editor"
        }
        """;

    public static string Latest(string tag = "v1.1.0", bool digests = true, params string[] rids)
    {
        rids = rids.Length > 0 ? rids : ["linux-x64", "osx-arm64", "win-x64"];
        var version = tag.TrimStart('v');
        var assets = string.Join(",\n", rids.Select(rid =>
        {
            var name = $"MainframeEngine-{version}-{rid}{(rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";
            var digest = digests ? $"\"digest\": \"sha256:{new string('a', 64)}\"," : "";
            return $$"""{ "name": "{{name}}", "size": 1000, {{digest}} "browser_download_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/download/{{tag}}/{{name}}" }""";
        }));
        return $$"""
            { "tag_name": "{{tag}}", "html_url": "https://github.com/Mainframe-Games/mainframe-engine/releases/tag/{{tag}}",
              "body": "Notes for {{tag}}", "assets": [ {{assets}} ] }
            """;
    }

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a function and records requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }

    public static StubHandler Json(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static StubHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

    public static StubHandler Throws(Exception exception) => new(_ => throw exception);
}
```

- [ ] **Step 2: Write the failing tests**

`ReleaseVersionTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Updates;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("v10.0.42", 10, 0, 42)]
    public void ParsesReleaseVersions(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
        Assert.Equal($"{major}.{minor}.{patch}", version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("0.0.0-dev")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3+build")]
    [InlineData("1.-2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("nightly")]
    public void RejectsAnythingElse(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void ComparesNumerically()
    {
        Assert.True(new ReleaseVersion(1, 10, 0) > new ReleaseVersion(1, 9, 9));
        Assert.True(new ReleaseVersion(2, 0, 0) > new ReleaseVersion(1, 99, 99));
        Assert.True(new ReleaseVersion(1, 0, 1) >= new ReleaseVersion(1, 0, 1));
        Assert.True(new ReleaseVersion(1, 0, 0) < new ReleaseVersion(1, 0, 1));
        Assert.Equal(0, new ReleaseVersion(1, 2, 3).CompareTo(new ReleaseVersion(1, 2, 3)));
    }
}
```

`ReleaseFeedTests.cs`:

```csharp
using System.Net;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class ReleaseFeedTests
{
    [Fact]
    public void ParsesTheRecordedRelease()
    {
        var release = ReleaseFeed.Parse(ReleaseFixtures.Bytes(ReleaseFixtures.Recorded))!;

        Assert.Equal(new ReleaseVersion(1, 0, 0), release.Version);
        Assert.Equal("v1.0.0", release.Tag);
        Assert.StartsWith("## What's Changed", release.Notes, StringComparison.Ordinal);
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/tag/v1.0.0", release.PageUrl.AbsoluteUri);
        Assert.Equal(3, release.Assets.Count);
        var linux = release.FindAsset("MainframeEngine-1.0.0-linux-x64.tar.gz")!;
        Assert.Equal(56920893, linux.Size);
        Assert.Equal("dfdf31c3d560f72aa9850e71af5511fe6e03f63e0e5fb052010fe2b16fa2f89e", linux.Sha256);
        // Upper-case hex is normalised; a non-SHA-256 digest is no digest.
        Assert.Equal("857ef126ffc089afcc78405195c60550ded974703a418ec5e03eaf5c61afe6da", release.FindAsset("MainframeEngine-1.0.0-osx-arm64.tar.gz")!.Sha256);
        Assert.Null(release.FindAsset("MainframeEngine-1.0.0-win-x64.zip")!.Sha256);
        Assert.Null(release.FindAsset("MainframeEngine-1.0.0-osx-x64.tar.gz"));
    }

    [Theory]
    [InlineData("v1.1.0-beta.1")]
    [InlineData("nightly")]
    [InlineData("1.1.0")] // release tags always start with v
    public void TagsThatAreNotReleaseVersionsGiveNoRelease(string tag) =>
        Assert.Null(ReleaseFeed.Parse(ReleaseFixtures.Bytes(ReleaseFixtures.Latest(tag))));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "v1.0.0" }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "html_url": "x", "assets": [] }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "html_url": "https://github.com/x", "assets": [ { "name": "a" } ] }""")]
    public void MalformedResponsesThrowUpdateException(string json) =>
        Assert.Throws<UpdateException>(() => ReleaseFeed.Parse(ReleaseFixtures.Bytes(json)));

    [Fact]
    public async Task GetLatestSendsGitHubsHeaders()
    {
        var handler = StubHandler.Json(ReleaseFixtures.Latest());
        using var http = new HttpClient(handler);

        var release = await new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ReleaseVersion(1, 1, 0), release!.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ReleaseFeed.LatestReleaseUri, request.RequestUri);
        Assert.Equal("MainframeEngine-Editor/1.0.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    public async Task ErrorStatusesThrowUpdateException(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(StubHandler.Status(status));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailuresThrowUpdateException()
    {
        using var http = new HttpClient(StubHandler.Throws(new HttpRequestException("No route to host")));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("No route to host", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutThrowsUpdateException()
    {
        using var http = new HttpClient(StubHandler.Throws(new TaskCanceledException("timed out")));
        var e = await Assert.ThrowsAsync<UpdateException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("did not answer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotAnUpdateError()
    {
        using var http = new HttpClient(StubHandler.Json(ReleaseFixtures.Latest()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseFeed(http, "1.0.0").GetLatestAsync(cancelled.Token));
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.Release"`
Expected: build FAILS — `ReleaseVersion`, `ReleaseFeed` not defined.

- [ ] **Step 4: Implement**

`ReleaseVersion.cs`:

```csharp
using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// A release version <c>X.Y.Z</c> (release tags are <c>vX.Y.Z</c>, docs/design/release.md). Pre-release and build
/// suffixes (<c>0.0.0-dev</c>) are not release versions: development builds never update.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>Parses <c>X.Y.Z</c> or <c>vX.Y.Z</c> (digits only, nothing else).</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
            return false;
        var span = text.AsSpan();
        if (span[0] is 'v' or 'V')
            span = span[1..];
        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3 ||
            !TryPart(span[parts[0]], out var major) || !TryPart(span[parts[1]], out var minor) || !TryPart(span[parts[2]], out var patch))
            return false;
        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    private static bool TryPart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        return part.Length is > 0 and <= 9 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
```

`ReleaseInfo.cs`:

```csharp
namespace MainframeEngine.Editor;

/// <summary>A file of a GitHub release: name, download URL, size and SHA-256 (lower-case hex; null when GitHub gave none).</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A published editor release (GitHub <c>releases/latest</c>).</summary>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Notes, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>An update step failed; the message is a sentence the editor shows as is.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`ReleaseFeed.cs`:

```csharp
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MainframeEngine.Editor;

/// <summary>
/// Reads the latest editor release from GitHub (<see cref="LatestReleaseUri"/>; public repository, no token; drafts and
/// pre-releases are never "latest"). Errors surface as <see cref="UpdateException"/>; cancellation by the caller does not.
/// </summary>
public sealed class ReleaseFeed(HttpClient http, string editorVersion)
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Mainframe-Games/mainframe-engine/releases/latest");

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>The latest release, or null when its tag is not <c>vX.Y.Z</c>.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MainframeEngine-Editor", editorVersion));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub's rate limit for update checks was reached; try again in an hour.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
            var json = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException($"GitHub did not answer within {Timeout.TotalSeconds:0} seconds.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Could not reach GitHub ({e.Message}).", e);
        }
    }

    /// <summary>Reads a <c>releases/latest</c> response; null when the tag is not <c>vX.Y.Z</c>.</summary>
    public static ReleaseInfo? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = Required(root, "tag_name");
            if (tag[0] is not 'v' || !ReleaseVersion.TryParse(tag, out var version))
                return null;
            var assets = new List<ReleaseAsset>();
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? Sha256Of(d.GetString()) : null;
                assets.Add(new ReleaseAsset(Required(asset, "name"), new Uri(Required(asset, "browser_download_url")),
                    asset.GetProperty("size").GetInt64(), digest));
            }

            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, notes, new Uri(Required(root, "html_url")), assets);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateException("GitHub's release information could not be read.", e);
        }
    }

    private static string Required(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new KeyNotFoundException(name);

    // "sha256:<64 hex>" → lower-case hex; anything else is no digest.
    private static string? Sha256Of(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) && !digest.AsSpan(7).ContainsAnyExcept(Hex)
            ? digest[7..].ToLowerInvariant()
            : null;
}
```

Note: `new Uri("x")` throws `UriFormatException` (a `FormatException`) — the `"html_url": "x"` test case relies on it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.Release"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
just format
git add MainframeEngine.Editor/Src/Updates Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Read the latest release from GitHub

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Platform naming, update paths and the install location

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdatePlatform.cs`, `InstallLocation.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/InstallLocationTests.cs`

**Interfaces:**
- Consumes: `ReleaseVersion` (Task 2).
- Produces:
  - `static class UpdatePlatform`: `const string ExecutableName = "MainframeEngine.Editor"`, `const string MacAppName = "Mainframe Engine.app"`,
    `string? CurrentRid`, `string? Rid(OSPlatform os, Architecture architecture)`, `bool IsMac(string rid)`, `bool IsWindows(string rid)`,
    `string AssetName(ReleaseVersion version, string rid)`, `string StagedRoot(string extracted, ReleaseVersion version, string rid)`,
    `string ExecutablePath(string root, string rid)`.
  - `static class UpdatePaths`: `string DefaultDirectory`, `string ResultFile(string updatesDirectory)`, `string LogFile(string updatesDirectory)`,
    `string BackupOf(string root)`.
  - `enum InstallKind { Replaceable, NotWritable, Translocated, NotBundled }`;
    `sealed record InstallLocation(string Root, InstallKind Kind)` with `bool CanReplace`, `string? Hint`,
    `static string? FindRoot(string baseDirectory, string rid)`, `static bool IsTranslocated(string path)`,
    `static InstallLocation Inspect(string baseDirectory, string rid, Func<string, bool>? canWrite = null)`,
    `static bool CanWriteTo(string directory)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Runtime.InteropServices;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class InstallLocationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-install").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void OnlyTheReleasedPlatformsHaveARid()
    {
        Assert.Equal("osx-arm64", UpdatePlatform.Rid(OSPlatform.OSX, Architecture.Arm64));
        Assert.Equal("win-x64", UpdatePlatform.Rid(OSPlatform.Windows, Architecture.X64));
        Assert.Equal("linux-x64", UpdatePlatform.Rid(OSPlatform.Linux, Architecture.X64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.OSX, Architecture.X64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.Windows, Architecture.Arm64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.Linux, Architecture.Arm64));
        Assert.Null(UpdatePlatform.Rid(OSPlatform.FreeBSD, Architecture.X64));
    }

    [Fact]
    public void AssetNamesMatchThePackagingScript()
    {
        var v = new ReleaseVersion(1, 2, 3);
        Assert.Equal("MainframeEngine-1.2.3-osx-arm64.tar.gz", UpdatePlatform.AssetName(v, "osx-arm64"));
        Assert.Equal("MainframeEngine-1.2.3-linux-x64.tar.gz", UpdatePlatform.AssetName(v, "linux-x64"));
        Assert.Equal("MainframeEngine-1.2.3-win-x64.zip", UpdatePlatform.AssetName(v, "win-x64"));
    }

    [Fact]
    public void StagedRootsAndExecutablesFollowTheArchiveLayout()
    {
        var v = new ReleaseVersion(1, 2, 3);
        var mac = UpdatePlatform.StagedRoot("x", v, "osx-arm64");
        Assert.Equal(Path.Combine("x", "Mainframe Engine.app"), mac);
        Assert.Equal(Path.Combine(mac, "Contents", "MacOS", "MainframeEngine.Editor"), UpdatePlatform.ExecutablePath(mac, "osx-arm64"));
        var win = UpdatePlatform.StagedRoot("x", v, "win-x64");
        Assert.Equal(Path.Combine("x", "MainframeEngine-1.2.3-win-x64"), win);
        Assert.Equal(Path.Combine(win, "MainframeEngine.Editor.exe"), UpdatePlatform.ExecutablePath(win, "win-x64"));
        var linux = UpdatePlatform.StagedRoot("x", v, "linux-x64");
        Assert.Equal(Path.Combine(linux, "MainframeEngine.Editor"), UpdatePlatform.ExecutablePath(linux, "linux-x64"));
    }

    [Fact]
    public void TheMacRootIsTheEnclosingBundle()
    {
        var app = Path.Combine(_directory, "Apps", "Mainframe Engine.app");
        var baseDirectory = Path.Combine(app, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.GetFullPath(app), InstallLocation.FindRoot(baseDirectory, "osx-arm64"));
    }

    [Fact]
    public void AMacExecutableOutsideABundleHasNoRoot()
    {
        Assert.Null(InstallLocation.FindRoot(Path.Combine(_directory, "bin", "Release") + Path.DirectorySeparatorChar, "osx-arm64"));
        Assert.Equal(InstallKind.NotBundled, InstallLocation.Inspect(Path.Combine(_directory, "bin"), "osx-arm64").Kind);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    public void OtherRootsAreTheEditorFolder(string rid)
    {
        var folder = Path.Combine(_directory, "My Tools", "MainframeEngine-1.0.0-" + rid);
        Assert.Equal(Path.GetFullPath(folder), InstallLocation.FindRoot(folder + Path.DirectorySeparatorChar, rid));
    }

    [Fact]
    public void TranslocatedAppsCannotBeReplaced()
    {
        var app = Path.Combine(_directory, "AppTranslocation", "1B2C", "d", "Mainframe Engine.app");
        var location = InstallLocation.Inspect(Path.Combine(app, "Contents", "MacOS"), "osx-arm64", _ => true);
        Assert.Equal(InstallKind.Translocated, location.Kind);
        Assert.False(location.CanReplace);
        Assert.Contains("Applications", location.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyFoldersCannotBeReplaced()
    {
        var folder = Path.Combine(_directory, "MainframeEngine-1.0.0-linux-x64");
        Assert.Equal(InstallKind.NotWritable, InstallLocation.Inspect(folder, "linux-x64", _ => false).Kind);
        // The root's parent must be writable too (the root is renamed inside it).
        var parent = Path.GetFullPath(_directory);
        Assert.Equal(InstallKind.NotWritable, InstallLocation.Inspect(folder, "linux-x64", d => !string.Equals(d, parent, StringComparison.Ordinal)).Kind);
        var replaceable = InstallLocation.Inspect(folder, "linux-x64", _ => true);
        Assert.True(replaceable.CanReplace);
        Assert.Null(replaceable.Hint);
    }

    [Fact]
    public void CanWriteToProbesWithoutLeavingFiles()
    {
        Assert.True(InstallLocation.CanWriteTo(_directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
        Assert.False(InstallLocation.CanWriteTo(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void UpdatePathsLiveUnderTheUsersMainframeFolder()
    {
        Assert.EndsWith(Path.Combine(".mainframe", "updates"), UpdatePaths.DefaultDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine("u", "result.json"), UpdatePaths.ResultFile("u"));
        Assert.Equal(Path.Combine("u", "update.log"), UpdatePaths.LogFile("u"));
        Assert.Equal(Path.Combine("a", "Mainframe Engine.app.old"), UpdatePaths.BackupOf(Path.Combine("a", "Mainframe Engine.app") + Path.DirectorySeparatorChar));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.InstallLocation"`
Expected: build FAILS — `UpdatePlatform`, `InstallLocation` not defined.

- [ ] **Step 3: Implement**

`UpdatePlatform.cs`:

```csharp
using System.Runtime.InteropServices;

namespace MainframeEngine.Editor;

/// <summary>
/// The platforms the release workflow builds (osx-arm64, win-x64, linux-x64) and the layout of their archives
/// (build/package-editor.sh): asset names, the install root inside an extracted archive, the editor executable.
/// </summary>
public static class UpdatePlatform
{
    public const string ExecutableName = "MainframeEngine.Editor";

    /// <summary>The macOS bundle name (docs/design/release.md, macOS app name).</summary>
    public const string MacAppName = "Mainframe Engine.app";

    /// <summary>This process's release RID, or null when no release is built for it.</summary>
    public static string? CurrentRid => Rid(
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.FreeBSD,
        RuntimeInformation.ProcessArchitecture);

    public static string? Rid(OSPlatform os, Architecture architecture) =>
        os == OSPlatform.OSX && architecture == Architecture.Arm64 ? "osx-arm64"
        : os == OSPlatform.Windows && architecture == Architecture.X64 ? "win-x64"
        : os == OSPlatform.Linux && architecture == Architecture.X64 ? "linux-x64"
        : null;

    public static bool IsMac(string rid) => rid.StartsWith("osx-", StringComparison.Ordinal);

    public static bool IsWindows(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    public static string AssetName(ReleaseVersion version, string rid) =>
        $"MainframeEngine-{version}-{rid}{(IsWindows(rid) ? ".zip" : ".tar.gz")}";

    /// <summary>The install root inside an extracted archive: the bundle on macOS, the top folder elsewhere.</summary>
    public static string StagedRoot(string extracted, ReleaseVersion version, string rid) =>
        IsMac(rid) ? Path.Combine(extracted, MacAppName) : Path.Combine(extracted, $"MainframeEngine-{version}-{rid}");

    public static string ExecutablePath(string root, string rid) =>
        IsMac(rid) ? Path.Combine(root, "Contents", "MacOS", ExecutableName)
        : Path.Combine(root, IsWindows(rid) ? ExecutableName + ".exe" : ExecutableName);
}

/// <summary>Where updates are staged and recorded (<c>~/.mainframe/updates</c>).</summary>
public static class UpdatePaths
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "updates");

    /// <summary>The applier's outcome, read (and deleted) by the next editor start.</summary>
    public static string ResultFile(string updatesDirectory) => Path.Combine(updatesDirectory, "result.json");

    public static string LogFile(string updatesDirectory) => Path.Combine(updatesDirectory, "update.log");

    /// <summary>Where the old install waits while the new one is copied in.</summary>
    public static string BackupOf(string root) => Path.TrimEndingDirectorySeparator(root) + ".old";
}
```

`InstallLocation.cs`:

```csharp
namespace MainframeEngine.Editor;

public enum InstallKind
{
    /// <summary>The install can be renamed and replaced.</summary>
    Replaceable,

    /// <summary>The install or its parent folder is read-only (Program Files, a read-only mount).</summary>
    NotWritable,

    /// <summary>macOS App Translocation: an unsigned app started where it was downloaded runs from a random read-only copy.</summary>
    Translocated,

    /// <summary>A macOS editor that does not run from a <c>.app</c> bundle (a bare build output).</summary>
    NotBundled,
}

/// <summary>The running editor's install (<see cref="Root"/>: the <c>.app</c> bundle on macOS, the editor folder elsewhere).</summary>
public sealed record InstallLocation(string Root, InstallKind Kind)
{
    public bool CanReplace => Kind == InstallKind.Replaceable;

    /// <summary>Why the update is only downloaded (shown in the update dialog); null when it can be installed.</summary>
    public string? Hint => Kind switch
    {
        InstallKind.Translocated => "Move Mainframe Engine to Applications to enable automatic updates.",
        InstallKind.NotWritable => $"The editor's folder ({Root}) is read-only, so the update is downloaded for you to install by hand.",
        InstallKind.NotBundled => "This editor does not run from Mainframe Engine.app, so the update is downloaded for you to install by hand.",
        _ => null,
    };

    /// <summary>The install root for an editor whose <see cref="AppContext.BaseDirectory"/> is <paramref name="baseDirectory"/>.</summary>
    public static string? FindRoot(string baseDirectory, string rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(rid);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!UpdatePlatform.IsMac(rid))
            return directory;
        // <X>.app/Contents/MacOS
        var contents = Path.GetDirectoryName(directory);
        var app = contents is null ? null : Path.GetDirectoryName(contents);
        return string.Equals(Path.GetFileName(directory), "MacOS", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal) &&
               app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? app
            : null;
    }

    public static bool IsTranslocated(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("AppTranslocation", StringComparer.Ordinal);

    /// <summary>Finds the root and whether it can be replaced (<paramref name="canWrite"/> defaults to <see cref="CanWriteTo"/>).</summary>
    public static InstallLocation Inspect(string baseDirectory, string rid, Func<string, bool>? canWrite = null)
    {
        var root = FindRoot(baseDirectory, rid);
        if (root is null)
            return new InstallLocation(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)), InstallKind.NotBundled);
        if (IsTranslocated(root))
            return new InstallLocation(root, InstallKind.Translocated);
        canWrite ??= CanWriteTo;
        var parent = Path.GetDirectoryName(root);
        return new InstallLocation(root, parent is not null && canWrite(parent) && canWrite(root) ? InstallKind.Replaceable : InstallKind.NotWritable);
    }

    /// <summary>True when a file can be created (and is deleted again) in <paramref name="directory"/>.</summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            using (File.Create(Path.Combine(directory, $".mf-write-test-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.InstallLocation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor/Src/Updates Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Update platforms and install location

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The update check

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdateChecker.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/UpdateCheckerTests.cs`

**Interfaces:**
- Consumes: `ReleaseFeed`, `ReleaseVersion`, `ReleaseInfo`, `UpdateException` (Task 2); `UpdatePlatform.AssetName` (Task 3).
- Produces:
  - `enum UpdateCheckStatus { UpdateAvailable, UpToDate, DevelopmentBuild, UnsupportedPlatform, Failed }`
  - `sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, ReleaseAsset? Asset = null, string? Error = null)`
    with `bool IsUpdate` and `string Describe(string currentVersion)`.
  - `sealed class UpdateChecker(ReleaseFeed feed, string currentVersion, string? rid)`:
    `Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)` (never throws except caller cancellation).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateCheckerTests
{
    private static async Task<(UpdateCheckResult Result, StubHandler Handler)> Check(StubHandler handler, string current = "1.0.0", string? rid = "linux-x64")
    {
        using var http = new HttpClient(handler);
        var result = await new UpdateChecker(new ReleaseFeed(http, current), current, rid).CheckAsync(TestContext.Current.CancellationToken);
        return (result, handler);
    }

    [Fact]
    public async Task ANewerReleaseWithThisPlatformsBuildIsAnUpdate()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0")));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.IsUpdate);
        Assert.Equal("MainframeEngine-1.1.0-linux-x64.tar.gz", result.Asset!.Name);
        Assert.Equal("Mainframe Engine v1.1.0 is available (you have v1.0.0).", result.Describe("1.0.0"));
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")] // never a downgrade
    public async Task TheSameOrAnOlderReleaseIsUpToDate(string current)
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0")), current);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.IsUpdate);
        Assert.Equal($"Mainframe Engine v{current} is the latest version.", result.Describe(current));
    }

    [Fact]
    public async Task ALatestReleaseWithoutAReleaseTagIsUpToDate()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("nightly")));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task DevelopmentBuildsNeverAskGitHub()
    {
        var (result, handler) = await Check(StubHandler.Json(ReleaseFixtures.Latest()), "0.0.0-dev");
        Assert.Equal(UpdateCheckStatus.DevelopmentBuild, result.Status);
        Assert.Empty(handler.Requests);
        Assert.Contains("development build", result.Describe("0.0.0-dev"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreleasedPlatformsNeverAskGitHub()
    {
        var (result, handler) = await Check(StubHandler.Json(ReleaseFixtures.Latest()), rid: null);
        Assert.Equal(UpdateCheckStatus.UnsupportedPlatform, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AReleaseWithoutThisPlatformsBuildIsUnsupported()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0", true, "osx-arm64")));
        Assert.Equal(UpdateCheckStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("The latest release has no editor build for this platform.", result.Describe("1.0.0"));
    }

    [Fact]
    public async Task AnAssetWithoutAChecksumIsRefused()
    {
        var (result, _) = await Check(StubHandler.Json(ReleaseFixtures.Latest("v1.1.0", digests: false)));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("checksum", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailuresBecomeFailedResults()
    {
        var (status, _) = await Check(StubHandler.Status(HttpStatusCode.TooManyRequests));
        Assert.Equal(UpdateCheckStatus.Failed, status.Status);
        Assert.Contains("rate limit", status.Describe("1.0.0"), StringComparison.Ordinal);

        var (network, _) = await Check(StubHandler.Throws(new HttpRequestException("offline")));
        Assert.Equal(UpdateCheckStatus.Failed, network.Status);
        Assert.Contains("offline", network.Error, StringComparison.Ordinal);

        var (garbage, _) = await Check(StubHandler.Json("<html>"));
        Assert.Equal(UpdateCheckStatus.Failed, garbage.Status);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateChecker"`
Expected: build FAILS — `UpdateChecker` not defined.

- [ ] **Step 3: Implement** `UpdateChecker.cs`:

```csharp
namespace MainframeEngine.Editor;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    DevelopmentBuild,
    UnsupportedPlatform,
    Failed,
}

/// <summary>The outcome of an update check; <see cref="Release"/> and <see cref="Asset"/> are set for an update.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, ReleaseAsset? Asset = null, string? Error = null)
{
    public bool IsUpdate => Status == UpdateCheckStatus.UpdateAvailable && Release is not null && Asset is not null;

    /// <summary>The sentence Help › Check for Updates… shows.</summary>
    public string Describe(string currentVersion) => Status switch
    {
        UpdateCheckStatus.UpdateAvailable => $"Mainframe Engine v{Release?.Version} is available (you have v{currentVersion}).",
        UpdateCheckStatus.UpToDate => $"Mainframe Engine v{currentVersion} is the latest version.",
        UpdateCheckStatus.DevelopmentBuild => $"This is a development build (v{currentVersion}); updates are offered to released builds only.",
        UpdateCheckStatus.UnsupportedPlatform => "The latest release has no editor build for this platform.",
        _ => Error ?? "The update check failed.",
    };
}

/// <summary>
/// Decides whether the latest release is an update for this editor: <paramref name="currentVersion"/> must be a release
/// version (development builds never check), <paramref name="rid"/> a released platform, and the release strictly newer
/// with a checksummed asset for that platform. Never throws, except when the caller cancels.
/// </summary>
public sealed class UpdateChecker(ReleaseFeed feed, string currentVersion, string? rid)
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            return new UpdateCheckResult(UpdateCheckStatus.DevelopmentBuild);
        if (rid is null)
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedPlatform);

        ReleaseInfo? release;
        try
        {
            release = await feed.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (UpdateException e)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: e.Message);
        }

        if (release is null || release.Version <= current)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release);
        var asset = release.FindAsset(UpdatePlatform.AssetName(release.Version, rid));
        if (asset is null)
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedPlatform, release);
        if (asset.Sha256 is null)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, release, asset,
                $"The release has no checksum for {asset.Name}, so it cannot be verified.");
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, asset);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateChecker"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor/Src/Updates Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Decide whether the latest release is an update

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Download, verify and extract

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdateDownloader.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/UpdateDownloaderTests.cs`

**Interfaces:**
- Consumes: `ReleaseInfo`, `ReleaseAsset`, `UpdateException` (Task 2); `UpdatePlatform` (Task 3).
- Produces:
  - `sealed record StagedUpdate(ReleaseVersion Version, string Folder, string Root, string Executable)`.
  - `sealed class UpdateDownloader(HttpClient http, string editorVersion)`:
    `Task<StagedUpdate> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string rid, string updatesDirectory, IProgress<double>? progress = null, CancellationToken ct = default)`;
    `static void Verify(string file, string sha256)`; `static void Extract(string archive, string destination)`.
  - Throws `UpdateException` for HTTP errors, checksum mismatch, bad archives, missing executable;
    `OperationCanceledException` on cancel. The version folder is deleted on any failure.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly ReleaseVersion Version = new(1, 1, 0);
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-download").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Updates => Path.Combine(_directory, "updates");

    private static byte[] TarGz(params (string Name, string Text, UnixFileMode Mode)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text, mode) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

        return output.ToArray();
    }

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static (ReleaseInfo Release, ReleaseAsset Asset) Release(string rid, byte[] archive, string? sha256 = null)
    {
        var name = UpdatePlatform.AssetName(Version, rid);
        var asset = new ReleaseAsset(name, new Uri("https://example.test/" + name), archive.Length, sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)));
        return (new ReleaseInfo(Version, "v1.1.0", "", new Uri("https://example.test/release"), [asset]), asset);
    }

    private static StubHandler Serving(byte[] archive) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });

    private async Task<StagedUpdate> Download(string rid, byte[] archive, string? sha256 = null, IProgress<double>? progress = null, CancellationToken? ct = null)
    {
        var (release, asset) = Release(rid, archive, sha256);
        using var http = new HttpClient(Serving(archive));
        return await new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, rid, Updates, progress, ct ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALinuxArchiveIsStagedWithItsExecuteBit()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable),
                            ("MainframeEngine-1.1.0-linux-x64/Content/a.txt", "a", Plain));
        var reports = new List<double>();

        var staged = await Download("linux-x64", archive, progress: new SyncProgress(reports));

        Assert.Equal(Version, staged.Version);
        Assert.Equal(Path.Combine(Updates, "1.1.0"), staged.Folder);
        Assert.Equal(Path.Combine(Updates, "1.1.0", "app", "MainframeEngine-1.1.0-linux-x64"), staged.Root);
        Assert.True(File.Exists(staged.Executable));
        Assert.True(File.Exists(Path.Combine(staged.Root, "Content", "a.txt")));
        Assert.False(File.Exists(Path.Combine(staged.Folder, "MainframeEngine-1.1.0-linux-x64.tar.gz"))); // the archive is gone
        Assert.Equal(1.0, reports[^1]);
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(staged.Executable).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task AMacArchiveIsStagedAsABundle()
    {
        var archive = TarGz(("Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor", "exe", Executable),
                            ("Mainframe Engine.app/Contents/Info.plist", "plist", Plain));
        var staged = await Download("osx-arm64", archive);
        Assert.EndsWith("Mainframe Engine.app", staged.Root, StringComparison.Ordinal);
        Assert.True(File.Exists(staged.Executable));
    }

    [Fact]
    public async Task AWindowsZipIsStaged()
    {
        var staged = await Download("win-x64", Zip(("MainframeEngine-1.1.0-win-x64/MainframeEngine.Editor.exe", "exe")));
        Assert.True(File.Exists(staged.Executable));
    }

    [Fact]
    public async Task AChecksumMismatchIsRefusedAndCleanedUp()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive, sha256: new string('0', 64)));
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task AnArchiveWithoutTheEditorIsRefused()
    {
        var archive = TarGz(("something-else/readme.txt", "hi", Plain));
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive));
        Assert.Contains("MainframeEngine.Editor", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task EntriesOutsideTheStagingFolderAreRefused()
    {
        var archive = TarGz(("../escaped.txt", "x", Plain), ("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        await Assert.ThrowsAsync<UpdateException>(() => Download("linux-x64", archive));
        Assert.False(File.Exists(Path.Combine(Updates, "1.1.0", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Updates, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task HttpErrorsAreUpdateExceptions()
    {
        var (release, asset) = Release("linux-x64", [1, 2, 3]);
        using var http = new HttpClient(StubHandler.Status(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(http, "1.0.0").DownloadAsync(release, asset, "linux-x64", Updates, null, TestContext.Current.CancellationToken));
        Assert.Contains("404", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingDeletesTheFolder()
    {
        var archive = TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download("linux-x64", archive, ct: cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
    }

    [Fact]
    public async Task AnEarlierStagingOfTheSameVersionIsReplaced()
    {
        Directory.CreateDirectory(Path.Combine(Updates, "1.1.0", "app", "stale"));
        var staged = await Download("linux-x64", TarGz(("MainframeEngine-1.1.0-linux-x64/MainframeEngine.Editor", "exe", Executable)));
        Assert.False(Directory.Exists(Path.Combine(staged.Folder, "app", "stale")));
    }

    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateDownloader"`
Expected: build FAILS — `UpdateDownloader` not defined.

- [ ] **Step 3: Implement** `UpdateDownloader.cs`:

```csharp
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MainframeEngine.Editor;

/// <summary>A downloaded, verified and extracted release: <see cref="Root"/> is what replaces the install.</summary>
public sealed record StagedUpdate(ReleaseVersion Version, string Folder, string Root, string Executable);

/// <summary>
/// Downloads a release archive into <c>&lt;updates&gt;/X.Y.Z/</c>, checks its SHA-256, extracts it to <c>app/</c> (entries
/// escaping the folder are refused; Unix modes are kept) and finds the editor executable. Any failure deletes the folder.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string editorVersion)
{
    private const int BufferSize = 81920;

    public async Task<StagedUpdate> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string rid, string updatesDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Sha256 is null)
            throw new UpdateException($"The release has no checksum for {asset.Name}, so it cannot be verified.");

        var folder = Path.Combine(updatesDirectory, release.Version.ToString());
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        try
        {
            var archive = Path.Combine(folder, asset.Name);
            await DownloadFileAsync(asset, archive, progress, ct).ConfigureAwait(false);
            Verify(archive, asset.Sha256);
            var extracted = Path.Combine(folder, "app");
            Extract(archive, extracted);
            File.Delete(archive);
            var root = UpdatePlatform.StagedRoot(extracted, release.Version, rid);
            var executable = UpdatePlatform.ExecutablePath(root, rid);
            if (!File.Exists(executable))
                throw new UpdateException($"The downloaded archive has no {Path.GetFileName(executable)}.");
            return new StagedUpdate(release.Version, folder, root, executable);
        }
        catch (Exception)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Editor] Could not delete {folder}: {e.Message}");
            }

            throw;
        }
    }

    private async Task DownloadFileAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MainframeEngine-Editor", editorVersion));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"The download failed ({e.Message}).", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if (total > 0)
                            progress?.Report(Math.Min(1.0, (double)done / total));
                    }
                }
            }
        }

        progress?.Report(1.0);
    }

    /// <summary>Throws <see cref="UpdateException"/> unless <paramref name="file"/>'s SHA-256 is <paramref name="sha256"/> (hex).</summary>
    public static void Verify(string file, string sha256)
    {
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The download is damaged (its checksum does not match the release). Try again.");
    }

    /// <summary>Extracts a <c>.zip</c> or <c>.tar.gz</c>; entries outside <paramref name="destination"/> are refused.</summary>
    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        try
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: false);
                return;
            }

            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The downloaded archive could not be unpacked ({e.Message}).", e);
        }
    }
}
```

If a test shows `TarFile.ExtractToDirectory` accepting `../escaped.txt` silently (it should throw `IOException`),
iterate entries with `TarReader` instead and reject any whose `Path.GetFullPath(Path.Combine(destination, name))` does
not start with `Path.GetFullPath(destination) + Path.DirectorySeparatorChar`, throwing `UpdateException`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateDownloader"`
Expected: PASS (on Windows the execute-bit assertion is skipped by the `if`).

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor/Src/Updates Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Download, verify and stage releases

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The applier (`--apply-update`) and start-up clean-up

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdateApplier.cs`
- Modify: `MainframeEngine.Editor/Program.cs` (before `EditorAppOptions options;`)
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/UpdateApplierTests.cs`

**Interfaces:**
- Consumes: `UpdatePlatform`, `UpdatePaths`, `InstallLocation.FindRoot` (Task 3); `ReleaseVersion` (Task 2); `AtomicFile.WriteAllBytes` (existing).
- Produces:
  - `sealed record ApplyUpdateRequest(string InstallRoot, int WaitPid, string From, string? Project)`:
    `const string Flag = "--apply-update"`, `static bool IsApplyUpdate(IReadOnlyList<string> args)`,
    `static ApplyUpdateRequest Parse(IReadOnlyList<string> args)` (throws `ArgumentException`),
    `IReadOnlyList<string> ToArguments()`.
  - `sealed record UpdateResult(string From, string To, bool Ok, string? Error)`: `void Save(string path)`, `static UpdateResult? Load(string path)`.
  - `sealed class UpdateApplier` (init properties `StagedRoot`, `Rid`, `ToVersion`, `UpdatesDirectory` required;
    `WaitForExit`, `Start`, `CopyDirectory`, `Log`, `ExitTimeout`, `RenameAttempts` optional): `int Apply(ApplyUpdateRequest request)`;
    `static ProcessStartInfo LaunchInfo(string root, string rid, string? project)`; `static int RunFromCommandLine(IReadOnlyList<string> args)`;
    `static bool DefaultWaitForExit(int pid, TimeSpan timeout)`; `static void CopyDirectoryRecursive(string from, string to)`.
  - `static class UpdateCleanup`: `UpdateResult? Run(string? installRoot, string updatesDirectory, ReleaseVersion current)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Diagnostics;

namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-apply").FullName;
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "old.txt"), "old");
        Directory.CreateDirectory(Path.Combine(Staged, "Content"));
        File.WriteAllText(Path.Combine(Staged, "MainframeEngine.Editor"), "new exe");
        File.WriteAllText(Path.Combine(Staged, "Content", "new.txt"), "new");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Root => Path.Combine(_directory, "My Tools", "MainframeEngine-1.0.0-linux-x64");
    private string Staged => Path.Combine(_directory, "updates", "1.1.0", "app", "MainframeEngine-1.1.0-linux-x64");
    private string Updates => Path.Combine(_directory, "updates");
    private string Backup => Root + ".old";

    private UpdateApplier Applier(Func<int, TimeSpan, bool>? wait = null, Action<string, string>? copy = null) => new()
    {
        StagedRoot = Staged,
        Rid = "linux-x64",
        ToVersion = "1.1.0",
        UpdatesDirectory = Updates,
        WaitForExit = wait ?? ((_, _) => true),
        Start = _started.Add,
        CopyDirectory = copy ?? UpdateApplier.CopyDirectoryRecursive,
        Log = _log.Add,
    };

    private ApplyUpdateRequest Request(string? project = null) => new(Root, 4242, "1.0.0", project);

    [Fact]
    public void ReplacesTheInstallRecordsAndRelaunches()
    {
        var project = Path.Combine(_directory, "My Games", "Space Game");
        Assert.Equal(0, Applier().Apply(Request(project)));

        Assert.Equal("new", File.ReadAllText(Path.Combine(Root, "Content", "new.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "old.txt"))); // the next start deletes it
        var result = UpdateResult.Load(UpdatePaths.ResultFile(Updates))!;
        Assert.Equal(new UpdateResult("1.0.0", "1.1.0", true, null), result);
        var start = Assert.Single(_started);
        Assert.Equal(Path.Combine(Root, "MainframeEngine.Editor"), start.FileName);
        Assert.Equal(["--project", project], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void AnOldEditorThatDoesNotExitChangesNothing()
    {
        Assert.Equal(1, Applier(wait: (_, _) => false).Apply(Request()));
        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(Directory.Exists(Backup));
        Assert.False(UpdateResult.Load(UpdatePaths.ResultFile(Updates))!.Ok);
        Assert.Equal(Path.Combine(Root, "MainframeEngine.Editor"), Assert.Single(_started).FileName); // the old editor again
    }

    [Fact]
    public void ACopyFailureRestoresThePreviousVersion()
    {
        void FailHalfway(string from, string to)
        {
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, "partial.txt"), "x");
            throw new IOException("Disk full");
        }

        Assert.Equal(1, Applier(copy: FailHalfway).Apply(Request()));

        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "partial.txt")));
        Assert.False(Directory.Exists(Backup));
        var result = UpdateResult.Load(UpdatePaths.ResultFile(Updates))!;
        Assert.False(result.Ok);
        Assert.Contains("Disk full", result.Error, StringComparison.Ordinal);
        Assert.Single(_started);
    }

    [Fact]
    public void ARelaunchFailureRestoresThePreviousVersion()
    {
        var applier = Applier();
        var attempts = 0;
        applier = new UpdateApplier
        {
            StagedRoot = applier.StagedRoot, Rid = applier.Rid, ToVersion = applier.ToVersion, UpdatesDirectory = applier.UpdatesDirectory,
            Start = info =>
            {
                if (attempts++ == 0)
                    throw new System.ComponentModel.Win32Exception("cannot start");
                _started.Add(info);
            },
            Log = _log.Add,
        };

        Assert.Equal(1, applier.Apply(Request()));
        Assert.True(File.Exists(Path.Combine(Root, "old.txt")));
        Assert.False(UpdateResult.Load(UpdatePaths.ResultFile(Updates))!.Ok); // the failure overwrote the success record
        Assert.Single(_started); // the old editor
    }

    [Fact]
    public void AStaleBackupFromAnEarlierRunIsReplaced()
    {
        Directory.CreateDirectory(Backup);
        File.WriteAllText(Path.Combine(Backup, "stale.txt"), "stale");
        Assert.Equal(0, Applier().Apply(Request()));
        Assert.False(File.Exists(Path.Combine(Backup, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "old.txt")));
    }

    [Fact]
    public void AProcessThatIsAlreadyGoneCountsAsExited()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(UpdateApplier.DefaultWaitForExit(process.Id, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void MacRelaunchesGoThroughOpen()
    {
        var info = UpdateApplier.LaunchInfo("/Applications/Mainframe Engine.app", "osx-arm64", "/Users/me/My Games/Space");
        Assert.Equal("open", info.FileName);
        Assert.Equal(["-n", "/Applications/Mainframe Engine.app", "--args", "--project", "/Users/me/My Games/Space"], info.ArgumentList);
        Assert.Equal(["-n", "/Applications/Mainframe Engine.app"], UpdateApplier.LaunchInfo("/Applications/Mainframe Engine.app", "osx-arm64", null).ArgumentList);
    }

    [Fact]
    public void RequestsRoundTripThroughArguments()
    {
        var request = new ApplyUpdateRequest(Path.GetFullPath(Root), 99, "1.0.0", "/p/My Game");
        var arguments = request.ToArguments();
        Assert.Equal(ApplyUpdateRequest.Flag, arguments[0]);
        Assert.True(ApplyUpdateRequest.IsApplyUpdate(arguments));
        Assert.Equal(request, ApplyUpdateRequest.Parse(arguments));
        Assert.Equal(request with { Project = null }, ApplyUpdateRequest.Parse(request with { Project = null }.ToArguments()));
    }

    [Theory]
    [InlineData("--apply-update")]
    [InlineData("--apply-update", "/x")]
    [InlineData("--apply-update", "/x", "--wait-pid", "abc", "--from", "1.0.0")]
    [InlineData("--apply-update", "/x", "--wait-pid", "1")]
    [InlineData("--apply-update", "/x", "--wait-pid", "1", "--from", "1.0.0", "--bogus")]
    public void BadArgumentsAreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => ApplyUpdateRequest.Parse(args));

    [Fact]
    public void TheNextStartReportsAndCleansUp()
    {
        Directory.CreateDirectory(Backup);
        Directory.CreateDirectory(Path.Combine(Updates, "1.1.0", "app"));
        Directory.CreateDirectory(Path.Combine(Updates, "junk"));
        File.WriteAllText(UpdatePaths.LogFile(Updates), "log");
        new UpdateResult("1.0.0", "1.1.0", true, null).Save(UpdatePaths.ResultFile(Updates));

        var result = UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0));

        Assert.Equal(new UpdateResult("1.0.0", "1.1.0", true, null), result);
        Assert.False(File.Exists(UpdatePaths.ResultFile(Updates)));
        Assert.False(Directory.Exists(Backup));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.1.0")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "junk")));
        Assert.True(File.Exists(UpdatePaths.LogFile(Updates)));
        Assert.Null(UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0))); // nothing to report the second time
    }

    [Fact]
    public void CleanUpKeepsAnotherEditorsDownloadOfANewerVersion()
    {
        Directory.CreateDirectory(Path.Combine(Updates, "1.2.0", "app"));
        Directory.CreateDirectory(Path.Combine(Updates, "1.0.0", "app"));
        UpdateCleanup.Run(Root, Updates, new ReleaseVersion(1, 1, 0));
        Assert.True(Directory.Exists(Path.Combine(Updates, "1.2.0")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "1.0.0")));
    }
}
```

`AProcessThatIsAlreadyGoneCountsAsExited` needs the `dotnet` host on PATH, which every test run has (CI and `just test`).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateApplier"`
Expected: build FAILS — `UpdateApplier` not defined.

- [ ] **Step 3: Implement** `UpdateApplier.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The hand-off from the running editor to the staged one:
/// <c>--apply-update &lt;install-root&gt; --wait-pid &lt;pid&gt; --from &lt;version&gt; [--project &lt;folder&gt;]</c>.
/// </summary>
public sealed record ApplyUpdateRequest(string InstallRoot, int WaitPid, string From, string? Project)
{
    public const string Flag = "--apply-update";

    public static bool IsApplyUpdate(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static ApplyUpdateRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsApplyUpdate(args) || args.Count < 2)
            throw new ArgumentException($"{Flag} needs the install folder.");
        string? from = null, project = null;
        int? pid = null;
        for (var i = 2; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--wait-pid":
                    pid = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        ? value
                        : throw new ArgumentException("--wait-pid needs a process id.");
                    break;
                case "--from":
                    from = Next();
                    break;
                case "--project":
                    project = Next();
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return new ApplyUpdateRequest(Path.GetFullPath(args[1]), pid ?? throw new ArgumentException("--wait-pid is required."),
            from ?? throw new ArgumentException("--from is required."), project);
    }

    public IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string> { Flag, InstallRoot, "--wait-pid", WaitPid.ToString(CultureInfo.InvariantCulture), "--from", From };
        if (Project is not null)
        {
            arguments.Add("--project");
            arguments.Add(Project);
        }

        return arguments;
    }
}

/// <summary>What the applier did (<c>~/.mainframe/updates/result.json</c>), reported by the next editor start.</summary>
public sealed record UpdateResult(string From, string To, bool Ok, string? Error)
{
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, UpdateJson.Default.UpdateResult));
    }

    /// <summary>The recorded result, or null when there is none or it cannot be read.</summary>
    public static UpdateResult? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateJson.Default.UpdateResult) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateResult))]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>
/// Installs the staged editor it runs from (docs/design/editor-updates.md, Applying): waits for the old editor, renames
/// the install to <c>&lt;root&gt;.old</c>, copies <see cref="StagedRoot"/> in, records the result, relaunches. Any failure
/// after the rename restores the old install and relaunches it. Runs before any window or engine exists.
/// </summary>
public sealed class UpdateApplier
{
    public required string StagedRoot { get; init; }
    public required string Rid { get; init; }
    public required string ToVersion { get; init; }
    public required string UpdatesDirectory { get; init; }
    public Func<int, TimeSpan, bool> WaitForExit { get; init; } = DefaultWaitForExit;
    public Action<ProcessStartInfo> Start { get; init; } = DefaultStart;
    public Action<string, string> CopyDirectory { get; init; } = CopyDirectoryRecursive;
    public Action<string> Log { get; init; } = static _ => { };
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Windows: antivirus and indexers hold files briefly after the old editor exits.</summary>
    public int RenameAttempts { get; init; } = OperatingSystem.IsWindows() ? 20 : 1;

    private string ResultPath => UpdatePaths.ResultFile(UpdatesDirectory);

    /// <summary>Applies the update; 0 when the new version was installed and started.</summary>
    public int Apply(ApplyUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.TrimEndingDirectorySeparator(request.InstallRoot);
        var backup = UpdatePaths.BackupOf(root);
        var launch = LaunchInfo(root, Rid, request.Project);
        Log($"Updating {root} from v{request.From} to v{ToVersion}.");

        if (!WaitForExit(request.WaitPid, ExitTimeout))
            return Fail(request, $"The old editor did not exit within {ExitTimeout.TotalSeconds:0} seconds; nothing was changed.", launch);
        try
        {
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
            RenameWithRetry(root, backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(request, $"The editor's folder could not be moved aside ({e.Message}); nothing was changed.", launch);
        }

        try
        {
            CopyDirectory(StagedRoot, root);
            new UpdateResult(request.From, ToVersion, true, null).Save(ResultPath);
            Start(launch);
            Log("Installed and started.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Log($"Installing failed ({e.Message}); restoring the previous version.");
            var restored = Restore(root, backup);
            return Fail(request, restored
                ? $"The update could not be installed ({e.Message}); the previous version was restored."
                : $"The update could not be installed ({e.Message}); the previous version is in {backup}.", launch);
        }
    }

    private bool Restore(string root, string backup)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            Directory.Move(backup, root);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not restore {root} from {backup}: {e.Message}");
            return false;
        }
    }

    private int Fail(ApplyUpdateRequest request, string error, ProcessStartInfo launch)
    {
        Log(error);
        try
        {
            new UpdateResult(request.From, ToVersion, false, error).Save(ResultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not record the result: {e.Message}");
        }

        try
        {
            Start(launch);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Log($"Could not start the editor: {e.Message}");
        }

        return 1;
    }

    private void RenameWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < RenameAttempts)
            {
                Log($"Moving the old version aside failed ({e.Message}); retrying.");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>How to start the editor installed at <paramref name="root"/> (macOS through <c>open</c>, so it starts as the app).</summary>
    public static ProcessStartInfo LaunchInfo(string root, string rid, string? project)
    {
        ProcessStartInfo info;
        if (UpdatePlatform.IsMac(rid))
        {
            info = new ProcessStartInfo("open") { ArgumentList = { "-n", root } };
            if (project is not null)
                info.ArgumentList.Add("--args");
        }
        else
        {
            info = new ProcessStartInfo(UpdatePlatform.ExecutablePath(root, rid)) { WorkingDirectory = root };
        }

        if (project is not null)
        {
            info.ArgumentList.Add("--project");
            info.ArgumentList.Add(project);
        }

        info.UseShellExecute = false;
        return info;
    }

    /// <summary>True when process <paramref name="pid"/> exited (or never existed) within <paramref name="timeout"/>.</summary>
    public static bool DefaultWaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static void DefaultStart(ProcessStartInfo info)
    {
        using var _ = Process.Start(info);
    }

    public static void CopyDirectoryRecursive(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
    }

    /// <summary><c>Program.cs</c>: the staged editor's whole run. Logs to <c>~/.mainframe/updates/update.log</c>.</summary>
    public static int RunFromCommandLine(IReadOnlyList<string> args)
    {
        var updates = UpdatePaths.DefaultDirectory;
        var logPath = UpdatePaths.LogFile(updates);
        void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(updates);
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(line);
            }
        }

        ApplyUpdateRequest request;
        try
        {
            request = ApplyUpdateRequest.Parse(args);
        }
        catch (ArgumentException e)
        {
            Write($"Bad arguments: {e.Message}");
            return 2;
        }

        var rid = UpdatePlatform.CurrentRid;
        var staged = rid is null ? null : InstallLocation.FindRoot(AppContext.BaseDirectory, rid);
        if (rid is null || staged is null)
        {
            Write($"{AppContext.BaseDirectory} is not a released editor layout; nothing was changed.");
            return 2;
        }

        return new UpdateApplier { StagedRoot = staged, Rid = rid, ToVersion = EngineInfo.Version, UpdatesDirectory = updates, Log = Write }.Apply(request);
    }
}

/// <summary>Start-up after an update: report the result, delete the backup and stale staging folders. Never throws.</summary>
public static class UpdateCleanup
{
    /// <summary>
    /// Reads and deletes <c>result.json</c>, deletes <c>&lt;installRoot&gt;.old</c> and every staging folder except those of
    /// versions newer than <paramref name="current"/> (another open editor may be about to install one).
    /// </summary>
    public static UpdateResult? Run(string? installRoot, string updatesDirectory, ReleaseVersion current)
    {
        var resultPath = UpdatePaths.ResultFile(updatesDirectory);
        var result = UpdateResult.Load(resultPath);
        TryDelete(resultPath, static p => File.Delete(p));
        if (installRoot is not null && Directory.Exists(UpdatePaths.BackupOf(installRoot)))
            TryDelete(UpdatePaths.BackupOf(installRoot), static p => Directory.Delete(p, recursive: true));
        if (Directory.Exists(updatesDirectory))
            foreach (var folder in Directory.EnumerateDirectories(updatesDirectory))
                if (!ReleaseVersion.TryParse(Path.GetFileName(folder), out var version) || version <= current)
                    TryDelete(folder, static p => Directory.Delete(p, recursive: true));
        return result;
    }

    private static void TryDelete(string path, Action<string> delete)
    {
        try
        {
            delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"[Editor] Could not delete {path} ({e.Message}); it is retried at the next start.");
        }
    }
}
```

`Program.cs`: insert before `EditorAppOptions options;`:

```csharp
// The staged editor of an update (docs/design/editor-updates.md): installs itself over the old editor and relaunches.
// No window, SDL or engine is created.
if (ApplyUpdateRequest.IsApplyUpdate(args))
    return UpdateApplier.RunFromCommandLine(args);
```

and add to the header comment: `//   --apply-update <root> --wait-pid <pid> --from <version> [--project <folder>]   (internal: editor updates)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateApplier"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests/Updates
git commit -m "Editor: Apply staged updates and clean up after them

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The update service seam, wiring and the quit hook

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdateService.cs`, `MainframeEngine.Editor/Src/Files/OsShell.cs`
- Modify: `MainframeEngine.Editor/Src/UI/FileSystemPanel.cs:537,710-726` (use `OsShell.Reveal`, delete the private `Reveal`)
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.cs` (`EditorWorkspaceOptions.Updates`; `RequestQuit(Func<bool>? beforeQuit = null)`)
- Modify: `MainframeEngine.Editor/Src/EditorCommandLine.cs` (normal options: `Updates = hidden ? null : new GitHubUpdateService()`)
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/UpdateServiceTests.cs`; add to `Tests/MainframeEngine.Editor.Tests/WorkspaceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–6.
- Produces:
  - `interface IUpdateService`: `string CurrentVersion { get; }`, `InstallLocation Install { get; }`,
    `Task<UpdateCheckResult> CheckAsync(CancellationToken ct)`,
    `Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct)`,
    `void StartApplier(StagedUpdate staged, string? project)` (throws `UpdateException`), `void Reveal(StagedUpdate staged)`,
    `Task<UpdateResult?> CleanUpAsync()`.
  - `sealed class GitHubUpdateService : IUpdateService` (`GitHubUpdateService(string? updatesDirectory = null)`),
    plus `ProcessStartInfo ApplierStartInfo(StagedUpdate staged, string? project)`.
  - `static class OsShell`: `void Reveal(string path)`, `void OpenUrl(Uri url)`.
  - `IUpdateService? EditorWorkspaceOptions.Updates { get; init; }`.
  - `void EditorWorkspace.RequestQuit(Func<bool>? beforeQuit = null)`.

- [ ] **Step 1: Write the failing tests**

`UpdateServiceTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Updates;

public sealed class UpdateServiceTests
{
    [Fact]
    public void TheApplierStartsFromTheStagedEditorWithEveryArgumentIntact()
    {
        var service = new GitHubUpdateService(Path.Combine(Path.GetTempPath(), "mf-unused-updates"));
        var staged = new StagedUpdate(new ReleaseVersion(1, 1, 0), "/u/1.1.0", "/u/1.1.0/app/Mainframe Engine.app",
            "/u/1.1.0/app/Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor");

        var info = service.ApplierStartInfo(staged, "/Users/me/My Games/Space Game");

        Assert.Equal(staged.Executable, info.FileName);
        Assert.False(info.UseShellExecute);
        var request = ApplyUpdateRequest.Parse(info.ArgumentList.ToArray());
        Assert.Equal(Environment.ProcessId, request.WaitPid);
        Assert.Equal(EngineInfo.Version, request.From);
        Assert.Equal("/Users/me/My Games/Space Game", request.Project);
        Assert.Equal(Path.GetFullPath(service.Install.Root), request.InstallRoot);
    }

    [Fact]
    public async Task DevelopmentBuildsDoNotCleanUp()
    {
        // Test builds are 0.0.0-dev: clean-up must not touch anything.
        var updates = Directory.CreateTempSubdirectory("mf-dev-updates").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(updates, "1.0.0"));
            Assert.Null(await new GitHubUpdateService(updates).CleanUpAsync());
            Assert.True(Directory.Exists(Path.Combine(updates, "1.0.0")));
        }
        finally
        {
            Directory.Delete(updates, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheEditorExecutableGetsAnUpdateService()
    {
        Assert.IsType<GitHubUpdateService>(EditorCommandLine.Parse([]).Workspace.Updates);
        Assert.Null(EditorCommandLine.Parse(["--hidden"]).Workspace.Updates);
        Assert.Null(EditorCommandLine.Parse(["--smoke", Path.GetTempPath()]).Workspace.Updates);
    }
}
```

Add to `WorkspaceTests` (next to `QuittingACleanSessionDoesNotAsk`):

```csharp
    [Fact]
    public void TheBeforeQuitStepRunsOnceQuittingIsConfirmed()
    {
        var calls = 0;
        W.RequestQuit(() => ++calls > 0);
        Assert.Equal(1, calls);
        Assert.True(_editor.Host.QuitRequested);
    }

    [Fact]
    public void ABeforeQuitStepCanKeepTheEditorOpen()
    {
        W.RequestQuit(() => false);
        Assert.False(_editor.Host.QuitRequested);
    }

    [Fact]
    public void CancellingTheUnsavedPromptSkipsTheBeforeQuitStep()
    {
        var calls = 0;
        AddChild("X");
        W.RequestQuit(() => ++calls > 0);
        W.Message.Answer(2); // cancel
        Assert.Equal(0, calls);
        Assert.False(_editor.Host.QuitRequested);

        W.RequestQuit(() => ++calls > 0);
        W.Message.Answer(1); // don't save
        Assert.Equal(1, calls);
        Assert.True(_editor.Host.QuitRequested);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~UpdateService|FullyQualifiedName~BeforeQuit"`
Expected: build FAILS — `GitHubUpdateService`, `EditorWorkspaceOptions.Updates`, `RequestQuit(Func<bool>)` missing.

- [ ] **Step 3: Implement**

`OsShell.cs` (move the body of `FileSystemPanel.Reveal` here unchanged, add `OpenUrl` from `ProjectManager.OpenDownloadPage`'s pattern):

```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>Hands things to the operating system: show a file in Finder / Explorer / the file manager, open a URL.</summary>
public static class OsShell
{
    public static void Reveal(string path)
    {
        try
        {
            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } }
                    : new ProcessStartInfo("xdg-open") { ArgumentList = { Directory.Exists(path) ? path : Path.GetDirectoryName(path)! } };
            start.UseShellExecute = false;
            using var _ = Process.Start(start);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Could not show {path}: {e.Message}");
        }
    }

    public static void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Open {url.AbsoluteUri} in a browser ({e.Message}).");
        }
    }
}
```

In `FileSystemPanel.cs` replace the call `Reveal(entry.FullPath);` with `OsShell.Reveal(entry.FullPath);` and delete the
private static `Reveal` method.

`UpdateService.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>
/// Everything the editor needs to update itself (docs/design/editor-updates.md). The editor executable passes
/// <see cref="GitHubUpdateService"/> through <see cref="EditorWorkspaceOptions.Updates"/>; tests and QA pass fakes.
/// </summary>
public interface IUpdateService
{
    /// <summary>The running editor's version (<see cref="EngineInfo.Version"/>).</summary>
    string CurrentVersion { get; }

    /// <summary>Where the editor is installed and whether it can be replaced in place.</summary>
    InstallLocation Install { get; }

    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    /// <summary>Downloads, verifies and stages <paramref name="update"/> (an <see cref="UpdateCheckResult.IsUpdate"/> result).</summary>
    Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Starts the staged editor with <c>--apply-update</c> for this process; throws <see cref="UpdateException"/>.</summary>
    void StartApplier(StagedUpdate staged, string? project);

    /// <summary>Shows the staged install in the file manager (installs that cannot be replaced).</summary>
    void Reveal(StagedUpdate staged);

    /// <summary>Start-up: the last update's result (null: none), after deleting the backup and stale staging folders.</summary>
    Task<UpdateResult?> CleanUpAsync();
}

/// <summary>Updates from GitHub Releases into <c>~/.mainframe/updates</c>.</summary>
public sealed class GitHubUpdateService : IUpdateService
{
    // One client for the process (like NetworkUtils); the feed applies its own 10 s timeout, downloads have none.
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly string _updatesDirectory;
    private readonly string? _rid = UpdatePlatform.CurrentRid;
    private readonly Lazy<InstallLocation> _install;

    public GitHubUpdateService(string? updatesDirectory = null)
    {
        _updatesDirectory = updatesDirectory ?? UpdatePaths.DefaultDirectory;
        _install = new Lazy<InstallLocation>(() => _rid is null
            ? new InstallLocation(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), InstallKind.NotBundled)
            : InstallLocation.Inspect(AppContext.BaseDirectory, _rid));
    }

    public string CurrentVersion => EngineInfo.Version;

    public InstallLocation Install => _install.Value;

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) =>
        new UpdateChecker(new ReleaseFeed(Http.Value, CurrentVersion), CurrentVersion, _rid).CheckAsync(ct);

    public Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.IsUpdate || _rid is null)
            throw new InvalidOperationException("Only an available update can be downloaded.");
        return new UpdateDownloader(Http.Value, CurrentVersion).DownloadAsync(update.Release!, update.Asset!, _rid, _updatesDirectory, progress, ct);
    }

    public ProcessStartInfo ApplierStartInfo(StagedUpdate staged, string? project)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var request = new ApplyUpdateRequest(Path.GetFullPath(Install.Root), Environment.ProcessId, CurrentVersion, project);
        var info = new ProcessStartInfo(staged.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(staged.Executable) ?? staged.Root,
        };
        foreach (var argument in request.ToArguments())
            info.ArgumentList.Add(argument);
        return info;
    }

    public void StartApplier(StagedUpdate staged, string? project)
    {
        try
        {
            using var process = Process.Start(ApplierStartInfo(staged, project))
                                ?? throw new UpdateException("The new version could not be started.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new UpdateException($"The new version could not be started ({e.Message}).", e);
        }
    }

    public void Reveal(StagedUpdate staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        OsShell.Reveal(staged.Root);
    }

    public Task<UpdateResult?> CleanUpAsync()
    {
        if (!ReleaseVersion.TryParse(CurrentVersion, out var current))
            return Task.FromResult<UpdateResult?>(null); // development builds never touch ~/.mainframe/updates
        return Task.Run(() => UpdateCleanup.Run(Install.Kind == InstallKind.NotBundled ? null : Install.Root, _updatesDirectory, current));
    }
}
```

`EditorWorkspaceOptions` (in `EditorWorkspace.cs`, after `StartCodeEditor`):

```csharp
    /// <summary>
    /// Editor updates from GitHub Releases (the editor executable passes <see cref="GitHubUpdateService"/>); null disables
    /// update checks (tests, <c>--smoke</c>, <c>--qa-script</c>, <c>--hidden</c>).
    /// </summary>
    public IUpdateService? Updates { get; init; }
```

`RequestQuit` becomes:

```csharp
    /// <summary>
    /// The window's close button or Cmd+Q: asks about unsaved scenes first, then quits. <paramref name="beforeQuit"/> runs
    /// once quitting is confirmed (after saving); returning false keeps the editor open (Update &amp; restart).
    /// </summary>
    public void RequestQuit(Func<bool>? beforeQuit = null)
    {
        void Quit()
        {
            if (beforeQuit is not null && !beforeQuit())
                return;
            SaveLayout();
            Host.Quit();
        }

        var dirty = Session.Scenes.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0)
        {
            Quit();
            return;
        }

        var names = string.Join(", ", dirty.Select(s => s.DisplayName));
        Message.Show(new MessageRequest
        {
            Title = "Unsaved changes",
            Message = $"Save the changes to {names} before closing?",
            Buttons = ["Save All", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 2)
                    return;
                if (button == 1)
                {
                    Quit();
                    return;
                }

                // Save All: untitled scenes get a Save As dialog each; a failure or cancel keeps the editor open.
                Commands.SaveAll(saved =>
                {
                    if (saved)
                        Quit();
                });
            },
        });
    }
```

`EditorCommandLine.Parse`, final (normal) `EditorWorkspaceOptions` initializer: add
`Updates = hidden ? null : new GitHubUpdateService(),`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`
Expected: PASS (whole editor suite: the `FileSystemPanel` move and `RequestQuit` change must not break existing tests).

- [ ] **Step 5: Commit**

```bash
just format
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Editor: Update service and the quit hook for Update & restart

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Controller, badges, update dialog and Help menu

**Files:**
- Create: `MainframeEngine.Editor/Src/Updates/UpdateController.cs`, `MainframeEngine.Editor/Src/UI/UpdateDialog.cs`,
  `MainframeEngine.Editor/Content/Editor/update.rml`
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.Projects.cs` (create, start, tick, dialog-open, change handler)
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.cs` (`Dispose`)
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs` (`help.check_updates`, `help.update`, Help menu)
- Modify: `MainframeEngine.Editor/Src/UI/Panels.cs` (`ToolbarPanel.RefreshUpdate`), `Content/Editor/toolbar.rml`
- Modify: `MainframeEngine.Editor/Src/UI/ProjectManager.cs` (`RefreshUpdate`), `Content/Editor/project_manager.rml`
- Test: `Tests/MainframeEngine.Editor.Tests/Updates/FakeUpdateService.cs`, `UpdateUiTests.cs`

**Interfaces:**
- Consumes: `IUpdateService`, `UpdateCheckResult`, `StagedUpdate`, `UpdateResult`, `UpdateException`, `OsShell` (Tasks 2–7);
  `EditorWorkspace.RequestQuit(Func<bool>?)`, `PlayController.Stop()`, `EditorSession.ProjectRoot`, `MessageDialog`.
- Produces:
  - `enum UpdateState { Idle, Downloading, Ready, Failed }`.
  - `sealed class UpdateController : IDisposable` — `bool IsEnabled`, `UpdateCheckResult? Available`, `UpdateState State`,
    `double Progress`, `string? Error`, `StagedUpdate? Staged`, `bool CanReplace`, `string? InstallHint`,
    `string CurrentVersion`, `event Action? Changed`, `void Start()`, `void CheckNow()`, `void Tick()`, `void Primary()`,
    `void Cancel()`, `void ShowPreview(UpdateCheckResult result)`.
  - `EditorWorkspace.Updates` (`UpdateController`), `EditorWorkspace.UpdateDialog` (`UpdateDialog`).
  - `UpdateDialog`: `void Open()`, `void Close()`, `void Refresh()`, `string PrimaryLabel`.
  - `ToolbarPanel.UpdateBadgeVisible`, `ProjectManager.UpdateBadgeVisible` (`bool`), `RefreshUpdate()` on both.
  - Commands `help.check_updates`, `help.update`.

- [ ] **Step 1: Write the fake service and the failing tests**

`FakeUpdateService.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Updates;

/// <summary>An <see cref="IUpdateService"/> the tests drive by hand: downloads finish when the test completes them.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public string CurrentVersion { get; set; } = "1.0.0";
    public InstallLocation Install { get; set; } = new("/Applications/Mainframe Engine.app", InstallKind.Replaceable);
    public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckStatus.UpToDate);
    public TaskCompletionSource<UpdateCheckResult>? PendingCheck { get; set; }
    public UpdateResult? LastResult { get; set; }
    public Exception? StartError { get; set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public TaskCompletionSource<StagedUpdate> Download { get; private set; } = new();
    public List<(StagedUpdate Staged, string? Project)> Started { get; } = [];
    public List<StagedUpdate> Revealed { get; } = [];

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        Checks++;
        return PendingCheck?.Task ?? Task.FromResult(CheckResult);
    }

    public Task<StagedUpdate> DownloadAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct)
    {
        Downloads++;
        Download = new TaskCompletionSource<StagedUpdate>();
        ct.Register(() => Download.TrySetCanceled(ct));
        progress?.Report(0.5);
        return Download.Task;
    }

    public void StartApplier(StagedUpdate staged, string? project)
    {
        if (StartError is not null)
            throw StartError;
        Started.Add((staged, project));
    }

    public void Reveal(StagedUpdate staged) => Revealed.Add(staged);

    public Task<UpdateResult?> CleanUpAsync() => Task.FromResult(LastResult);

    public static UpdateCheckResult Update(string version = "1.1.0", string notes = "Notes")
    {
        var json = ReleaseFixtures.Latest("v" + version).Replace($"Notes for v{version}", notes, StringComparison.Ordinal);
        var release = ReleaseFeed.Parse(ReleaseFixtures.Bytes(json))!;
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, release.Assets[0]);
    }

    public static StagedUpdate Staged() =>
        new(new ReleaseVersion(1, 1, 0), "/u/1.1.0", "/u/1.1.0/app/Mainframe Engine.app", "/u/1.1.0/app/Mainframe Engine.app/Contents/MacOS/MainframeEngine.Editor");
}
```

`UpdateUiTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Updates;

[Collection(nameof(SerialEditor))]
public sealed class UpdateUiTests : IDisposable
{
    private readonly FakeUpdateService _service = new();
    private HeadlessEditor? _editor;

    public void Dispose() => _editor?.Dispose();

    private HeadlessEditor Start(Func<EditorWorkspaceOptions, EditorWorkspaceOptions>? configure = null)
    {
        _editor = new HeadlessEditor(configure: o => (configure?.Invoke(o) ?? o) with { Updates = _service });
        _editor.Tick(2);
        return _editor;
    }

    private EditorWorkspace W => _editor!.Workspace;

    [Fact]
    public void WithoutAServiceThereIsNoBadgeAndNoCheck()
    {
        _editor = new HeadlessEditor();
        _editor.Tick(2);
        Assert.False(W.Updates.IsEnabled);
        Assert.False(W.Toolbar.UpdateBadgeVisible);
        W.Commands.Execute("help.check_updates");
        Assert.Equal("Updates are not available in this run.", W.Message.Current!.Message);
    }

    [Fact]
    public void AnAvailableUpdateShowsBothBadges()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        Assert.Equal(1, _service.Checks);
        Assert.True(W.Toolbar.UpdateBadgeVisible);
        Assert.True(W.ProjectManager.UpdateBadgeVisible);
        Assert.Null(W.Message.Current); // quiet: no dialog at start-up
        Assert.False(W.UpdateDialog.Visible);
    }

    [Fact]
    public void OfflineAtStartUpIsQuiet()
    {
        _service.CheckResult = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Could not reach GitHub (offline).");
        Start();
        Assert.False(W.Toolbar.UpdateBadgeVisible);
        Assert.Null(W.Message.Current);
        Assert.Contains(W.Output.Messages, m => m.Text.Contains("Could not reach GitHub", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingTurnsOffOnlyTheStartUpCheck()
    {
        var settings = Path.Combine(Directory.CreateTempSubdirectory("mf-update-settings").FullName, "editor_settings.json");
        new EditorSettings { CheckForUpdates = false }.Save(settings);
        _service.CheckResult = new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        Start(o => o with { EditorSettingsPath = settings });
        Assert.Equal(0, _service.Checks);

        W.Commands.Execute("help.check_updates");
        _editor!.Tick();
        Assert.Equal(1, _service.Checks);
        Assert.Equal("Mainframe Engine v1.0.0 is the latest version.", W.Message.Current!.Message);
    }

    [Fact]
    public void AManualCheckDuringTheStartUpCheckSendsOneRequest()
    {
        _service.PendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        Start();
        W.Commands.Execute("help.check_updates");
        _service.PendingCheck.SetResult(FakeUpdateService.Update());
        _editor!.Tick();
        Assert.Equal(1, _service.Checks);
        Assert.True(W.UpdateDialog.Visible); // the manual check opens the dialog for an update
    }

    [Fact]
    public void UpdateAndRestartDownloadsThenQuitsIntoTheApplier()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.Commands.Execute("help.update");
        _editor!.Tick();
        Assert.True(W.UpdateDialog.Visible);
        Assert.Equal("Update & restart", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        _editor.Tick();
        Assert.Equal(UpdateState.Downloading, W.Updates.State);
        Assert.Equal(0.5, W.Updates.Progress);
        W.Updates.Primary(); // a second click while downloading does nothing
        Assert.Equal(1, _service.Downloads);

        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor.Tick();
        var (staged, project) = Assert.Single(_service.Started);
        Assert.Equal(FakeUpdateService.Staged(), staged);
        Assert.Null(project);
        Assert.True(_editor.Host.QuitRequested);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void UnsavedScenesAreAskedAboutBeforeRestarting()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        _editor!.Scene.AddNode(new Node3D { Name = "X" }, _editor.Scene.Root);
        _editor.Tick();
        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor.Tick();

        Assert.Equal("Unsaved changes", W.Message.Current!.Title);
        W.Message.Answer(2); // cancel: no update, staged files kept for the next click
        Assert.Empty(_service.Started);
        Assert.Equal(UpdateState.Ready, W.Updates.State);
        Assert.Equal("Restart now", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        W.Message.Answer(1); // don't save
        Assert.Single(_service.Started);
        Assert.True(_editor.Host.QuitRequested);
    }

    [Fact]
    public void AnInstallThatCannotBeReplacedDownloadsAndReveals()
    {
        _service.CheckResult = FakeUpdateService.Update();
        _service.Install = new InstallLocation("/private/var/x/AppTranslocation/y/Mainframe Engine.app", InstallKind.Translocated);
        Start();
        W.UpdateDialog.Open();
        Assert.Equal("Download", W.UpdateDialog.PrimaryLabel);
        Assert.Contains("Applications", W.Updates.InstallHint, StringComparison.Ordinal);

        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor!.Tick();
        Assert.Single(_service.Revealed);
        Assert.Empty(_service.Started);
        Assert.False(_editor.Host.QuitRequested);
    }

    [Fact]
    public void AFailedDownloadOffersRetry()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.UpdateDialog.Open();
        W.Updates.Primary();
        _service.Download.SetException(new UpdateException("The download is damaged (its checksum does not match the release). Try again."));
        _editor!.Tick();
        Assert.Equal(UpdateState.Failed, W.Updates.State);
        Assert.StartsWith("The download is damaged", W.Updates.Error, StringComparison.Ordinal);
        Assert.Equal("Retry", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        Assert.Equal(UpdateState.Downloading, W.Updates.State);
        Assert.Null(W.Updates.Error);
        Assert.Equal(2, _service.Downloads);
    }

    [Fact]
    public void CancelStopsTheDownload()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.Updates.Primary();
        W.Updates.Cancel();
        _editor!.Tick();
        Assert.Equal(UpdateState.Idle, W.Updates.State);
        Assert.Null(W.Updates.Error);
    }

    [Fact]
    public void AnApplierThatCannotStartKeepsTheEditorOpen()
    {
        _service.CheckResult = FakeUpdateService.Update();
        _service.StartError = new UpdateException("The new version could not be started (denied).");
        Start();
        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor!.Tick();
        Assert.False(_editor.Host.QuitRequested);
        Assert.Equal(UpdateState.Failed, W.Updates.State);
        Assert.Contains("denied", W.Updates.Error, StringComparison.Ordinal);
        Assert.True(W.UpdateDialog.Visible);
    }

    [Fact]
    public void TheLastUpdateIsReportedInTheOutput()
    {
        _service.LastResult = new UpdateResult("1.0.0", "1.1.0", false, "Disk full");
        Start();
        Assert.Contains(W.Output.Messages, m => m.Level == OutputLevel.Error && m.Text.Contains("v1.1.0", StringComparison.Ordinal)
                                                 && m.Text.Contains("Disk full", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseNotesWithMarkupAreShownAsText()
    {
        _service.CheckResult = FakeUpdateService.Update(notes: "<b>bold</b> & <script>alert(1)</script> {{title}}");
        Start();
        W.UpdateDialog.Open();
        _editor!.Tick(2);
        Assert.Empty(_editor.RmlMessages);
        Assert.Contains("<b>bold</b>", W.Updates.Available!.Release!.Notes, StringComparison.Ordinal);
    }
}
```

If `OutputLevel`/`Node3D` need usings, add `using MainframeEngine;` as other editor tests do.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Updates.UpdateUi"`
Expected: build FAILS — `EditorWorkspace.Updates`, `UpdateDialog`, `UpdateState` missing.

- [ ] **Step 3: Implement the controller** `UpdateController.cs`:

```csharp
namespace MainframeEngine.Editor;

public enum UpdateState
{
    Idle,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// The editor side of updates (docs/design/editor-updates.md): the start-up check and the last update's report, the
/// badges' state, the update dialog's download and Update &amp; restart. Main thread only: background work runs as tasks
/// that <see cref="Tick"/> polls, like the Project Manager's SDK check.
/// </summary>
public sealed class UpdateController(EditorWorkspace workspace, IUpdateService? service) : IDisposable
{
    private readonly ProgressSink _progress = new();
    private Task<UpdateCheckResult>? _check;
    private bool _manualCheck;
    private Task<UpdateResult?>? _cleanUp;
    private Task<StagedUpdate>? _download;
    private CancellationTokenSource? _downloadCancel;
    private int _shownPercent = -1;
    private bool _preview;

    public bool IsEnabled => service is not null;

    /// <summary>The newer release (null: none known).</summary>
    public UpdateCheckResult? Available { get; private set; }

    public UpdateState State { get; private set; }

    /// <summary>Download progress 0–1.</summary>
    public double Progress => State switch
    {
        UpdateState.Downloading => _progress.Value,
        UpdateState.Ready => 1,
        _ => 0,
    };

    public string? Error { get; private set; }

    public StagedUpdate? Staged { get; private set; }

    public bool CanReplace => service?.Install.CanReplace ?? _preview;

    public string? InstallHint => service?.Install.Hint;

    public string CurrentVersion => service?.CurrentVersion ?? EngineInfo.Version;

    /// <summary>Raised when anything the badges or the dialog show changed.</summary>
    public event Action? Changed;

    /// <summary>Start-up: report the last update, then check unless Editor Settings turned checks off.</summary>
    public void Start()
    {
        if (service is null)
            return;
        _cleanUp = service.CleanUpAsync();
        if (workspace.Settings.CheckForUpdates)
            _check = service.CheckAsync(CancellationToken.None);
    }

    /// <summary>Help › Check for Updates…: reports every outcome (a running start-up check is reused).</summary>
    public void CheckNow()
    {
        if (service is null)
        {
            workspace.Message.Show(new MessageRequest { Title = "Check for Updates", Message = "Updates are not available in this run.", Icon = "refresh" });
            return;
        }

        _manualCheck = true;
        _check ??= service.CheckAsync(CancellationToken.None);
    }

    /// <summary>Main thread, every frame: finishes checks, clean-up and downloads.</summary>
    public void Tick()
    {
        if (_cleanUp is { IsCompleted: true } cleanUp)
        {
            _cleanUp = null;
            Report(cleanUp);
        }

        if (_check is { IsCompleted: true } check)
        {
            _check = null;
            FinishCheck(check);
        }

        if (_download is { IsCompleted: true } download)
        {
            _download = null;
            FinishDownload(download);
        }
        else if (_download is not null)
        {
            var percent = (int)(_progress.Value * 100);
            if (percent != _shownPercent)
            {
                _shownPercent = percent;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The dialog's main button: download (or retry); when staged, Update &amp; restart or show the files.</summary>
    public void Primary()
    {
        if (service is null || Available is not { IsUpdate: true } update)
            return;
        switch (State)
        {
            case UpdateState.Idle or UpdateState.Failed:
                BeginDownload(service, update);
                break;
            case UpdateState.Ready when Staged is { } staged:
                if (CanReplace)
                    Restart(service, staged);
                else
                    service.Reveal(staged);
                break;
        }
    }

    public void Cancel() => _downloadCancel?.Cancel();

    /// <summary>Shows <paramref name="result"/> as if a check found it and opens the dialog (QA captures; no network).</summary>
    public void ShowPreview(UpdateCheckResult result)
    {
        Available = result ?? throw new ArgumentNullException(nameof(result));
        _preview = true;
        State = UpdateState.Idle;
        Error = null;
        Changed?.Invoke();
        workspace.UpdateDialog.Open();
    }

    private void Report(Task<UpdateResult?> cleanUp)
    {
        if (!cleanUp.IsCompletedSuccessfully || cleanUp.Result is not { } result)
            return;
        if (result.Ok)
            Log.Info($"[Editor] Updated to v{result.To} (from v{result.From}).");
        else
            Log.Error($"[Editor] The update to v{result.To} failed: {result.Error} (details in ~/.mainframe/updates/update.log)");
    }

    private void FinishCheck(Task<UpdateCheckResult> check)
    {
        var result = check.IsCompletedSuccessfully
            ? check.Result
            : new UpdateCheckResult(UpdateCheckStatus.Failed, Error: check.Exception?.GetBaseException().Message);
        if (result.IsUpdate)
            Available = result;
        else if (result.Status == UpdateCheckStatus.Failed)
            Log.Info($"[Editor] Update check: {result.Error}");
        Changed?.Invoke();

        var manual = _manualCheck;
        _manualCheck = false;
        if (!manual)
            return;
        if (result.IsUpdate)
            workspace.UpdateDialog.Open();
        else
            workspace.Message.Show(new MessageRequest
            {
                Title = "Check for Updates",
                Message = result.Describe(CurrentVersion),
                Icon = result.Status == UpdateCheckStatus.Failed ? "alert-circle" : "refresh",
            });
    }

    private void BeginDownload(IUpdateService updates, UpdateCheckResult update)
    {
        Error = null;
        State = UpdateState.Downloading;
        _progress.Value = 0;
        _shownPercent = -1;
        _downloadCancel?.Dispose();
        _downloadCancel = new CancellationTokenSource();
        _download = updates.DownloadAsync(update, _progress, _downloadCancel.Token);
        Changed?.Invoke();
    }

    private void FinishDownload(Task<StagedUpdate> download)
    {
        _downloadCancel?.Dispose();
        _downloadCancel = null;
        if (download.IsCanceled || download.Exception?.GetBaseException() is OperationCanceledException)
        {
            State = UpdateState.Idle;
            Changed?.Invoke();
            return;
        }

        if (!download.IsCompletedSuccessfully)
        {
            var error = download.Exception!.GetBaseException();
            State = UpdateState.Failed;
            Error = error is UpdateException ? error.Message : $"The download failed ({error.Message}).";
            Changed?.Invoke();
            return;
        }

        Staged = download.Result;
        State = UpdateState.Ready;
        Changed?.Invoke();
        Primary(); // one click: Update & restart (or show the files)
    }

    private void Restart(IUpdateService updates, StagedUpdate staged)
    {
        workspace.UpdateDialog.Close();
        workspace.RequestQuit(() =>
        {
            workspace.Play.Stop();
            try
            {
                updates.StartApplier(staged, workspace.Session.ProjectRoot);
                return true;
            }
            catch (UpdateException e)
            {
                State = UpdateState.Failed;
                Error = e.Message;
                Staged = null;
                Changed?.Invoke();
                workspace.UpdateDialog.Open();
                return false;
            }
        });
    }

    public void Dispose()
    {
        _downloadCancel?.Cancel();
        _downloadCancel?.Dispose();
        _downloadCancel = null;
    }

    private sealed class ProgressSink : IProgress<double>
    {
        private double _value;

        public double Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }

        public void Report(double value) => Value = value;
    }
}
```

Note: after a failed applier start `Staged` is cleared and `State` is `Failed`, so the next click re-downloads (the staged
copy may be what failed to start).

- [ ] **Step 4: Implement the dialog**

`UpdateDialog.cs`:

```csharp
using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The update dialog (docs/design/editor-updates.md): the new version, its release notes (plain text), View release, and
/// Update &amp; restart — progress and Cancel while downloading, the error and Retry on failure; Download (then show the
/// files) when the install cannot be replaced.
/// </summary>
public sealed class UpdateDialog : EditorDocument
{
    private RmlDataModel? _model;

    public UpdateDialog(EditorWorkspace workspace)
        : base(workspace, "update.rml")
    {
        Visible = false;
        Modal = true;
    }

    private UpdateController Updates => Workspace.Updates;

    /// <summary>The main button's text for the current state.</summary>
    public string PrimaryLabel => Updates.State switch
    {
        UpdateState.Failed => "Retry",
        UpdateState.Ready when Updates.CanReplace => "Restart now",
        UpdateState.Ready => OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in Explorer" : "Show Files",
        _ => Updates.CanReplace ? "Update & restart" : "Download",
    };

    protected override void OnReady()
    {
        _model = CreateDataModel("update")
            .Bind("title", this, static d => d.Updates.Available?.Release is { } r ? $"Mainframe Engine v{r.Version}" : "Mainframe Engine")
            .Bind("current", this, static d => "You have v" + d.Updates.CurrentVersion)
            .Bind("notes", this, static d => d.Updates.Available?.Release?.Notes ?? "")
            .Bind("hint", this, static d => d.Updates.CanReplace ? "" : d.Updates.InstallHint ?? "")
            .Bind("downloading", this, static d => d.Updates.State == UpdateState.Downloading)
            .Bind("progress", this, static d => (Math.Clamp(d.Updates.Progress, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%")
            .Bind("error", this, static d => d.Updates.Error ?? "")
            .Bind("primary", this, static d => d.PrimaryLabel)
            .Event("primary", _ => Updates.Primary())
            .Event("cancel", _ => Updates.Cancel())
            .Event("view", _ => ViewRelease())
            .Event("later", _ => Close());
    }

    public void Open()
    {
        Visible = true;
        _model?.DirtyAll();
    }

    public void Close() => HideAndReleaseFocus();

    /// <summary>Re-reads the controller (its <see cref="UpdateController.Changed"/>).</summary>
    public void Refresh()
    {
        if (Visible)
            _model?.DirtyAll();
    }

    private void ViewRelease()
    {
        if (Updates.Available?.Release is { } release)
            OsShell.OpenUrl(release.PageUrl);
    }

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", e =>
    {
        if (Visible && (RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape && Updates.State != UpdateState.Downloading)
            Close();
    });
}
```

`update.rml`:

```xml
<rml>
<head>
    <title>Update</title>
    <link type="text/rcss" href="/Content/icons/icons.rcss"/>
    <link type="text/rcss" href="theme.rcss"/>
    <link type="text/rcss" href="dialogs.rcss"/>
    <style>
        #dialog { left: 50%; top: 50%; width: 520dp; height: 380dp; margin-left: -260dp; margin-top: -190dp; }
        #up-current { display: block; color: #8b93a5; font-size: 12dp; margin-bottom: 8dp; }
        #up-notes {
            display: block;
            flex: 1 1 auto;
            height: 160dp;
            overflow-y: auto;
            padding: 8dp;
            white-space: pre-wrap;
            background-color: #1b1d23;
            border: 1dp #2f333d;
            border-radius: 3dp;
            color: #b4bccc;
            font-size: 12dp;
        }
        #up-hint { display: block; margin-top: 8dp; color: #f59e0b; font-size: 12dp; }
        #up-bar { display: block; height: 6dp; margin-top: 10dp; background-color: #23262e; border-radius: 3dp; }
        #up-fill { display: block; height: 6dp; background-color: #3b82f6; border-radius: 3dp; }
        .dialog-buttons .spacer { flex: 1 1 auto; }
    </style>
</head>
<body class="backdrop" data-model="update">
    <div class="dialog" id="dialog">
        <div class="dialog-title"><span class="icon icon-sm icon-arrow-bar-to-down"></span> {{title}}</div>
        <div class="dialog-body">
            <span id="up-current">{{current}}</span>
            <div id="up-notes">{{notes}}</div>
            <div id="up-hint" data-if="hint != ''">{{hint}}</div>
            <div id="up-bar" data-if="downloading"><div id="up-fill" data-style-width="progress"></div></div>
            <div class="dialog-error">{{error}}</div>
        </div>
        <div class="dialog-buttons">
            <button id="up-view" data-event-click="view()" data-tooltip="View Release — open the release page on GitHub"><span class="icon icon-external-link"></span></button>
            <div class="spacer"></div>
            <button id="up-later" data-event-click="later()">Later</button>
            <button id="up-cancel" data-if="downloading" data-event-click="cancel()">Cancel</button>
            <button class="primary" id="up-primary" data-if="!downloading" data-event-click="primary()">{{primary}}</button>
        </div>
    </div>
</body>
</rml>
```

If `ReleaseNotesWithMarkupAreShownAsText` reports RmlUi errors (notes interpreted as markup), stop binding `{{notes}}`
and set the text from C# in `Refresh`/`Open` with
`Document.GetElementById("up-notes").SetInnerRml(RmlText.Escape(notes))` (as `MessageDialog` does), keeping
`white-space: pre-wrap`.

- [ ] **Step 5: Implement the badges**

`toolbar.rml` — style block additions:

```css
        #update { display: none; margin-right: 6dp; }
        #update.shown { display: inline-block; }
        #update .icon { image-color: #4ade80; }
```

and before `<span id="stats"…>`:

```xml
    <button id="update" class="tool-button" data-command="help.update" data-tooltip="Update available"><span class="icon icon-arrow-bar-to-down"></span></button>
```

`ToolbarPanel` (in `Panels.cs`): call `RefreshUpdate();` at the end of `OnAttach`, and add:

```csharp
    /// <summary>True while the update badge is shown (a newer release is available).</summary>
    public bool UpdateBadgeVisible { get; private set; }

    /// <summary>Shows or hides the update badge (on <see cref="UpdateController.Changed"/>, never per frame).</summary>
    public void RefreshUpdate()
    {
        var release = Workspace.Updates?.Available?.Release;
        UpdateBadgeVisible = release is not null;
        if (!IsLoaded)
            return;
        var badge = Document.GetElementById("update");
        badge.SetClass("shown", UpdateBadgeVisible);
        if (release is not null)
            badge.SetAttribute("data-tooltip", $"Mainframe Engine v{release.Version} is available — click for the release notes and Update and restart");
    }
```

`project_manager.rml` — style additions:

```css
        .pm-spacer { flex: 1 1 auto; }
        #pm-update { display: none; }
        #pm-update.shown { display: inline-block; }
        #pm-update .icon { image-color: #4ade80; }
```

and in `.pm-header`, after the title `<div>`:

```xml
        <div class="pm-spacer"></div>
        <button id="pm-update" class="tool-button" data-command="help.update" data-tooltip="Update available"><span class="icon icon-arrow-bar-to-down"></span></button>
```

`ProjectManager.cs`: add the same `UpdateBadgeVisible` property and a `RefreshUpdate()` identical to the toolbar's but
with element id `"pm-update"`; change `OnAttach` to a block body that keeps the existing keydown listener and then calls
`RefreshUpdate();`.

- [ ] **Step 6: Wire it into the workspace and commands**

`EditorWorkspace.Projects.cs`:

```csharp
    /// <summary>Editor updates (badges, dialog, Update &amp; restart).</summary>
    public UpdateController Updates { get; private set; } = null!;

    public UpdateDialog UpdateDialog { get; private set; } = null!;
```

In `CreateProjectUi`, before the `DialogLayer.MoveChild(FilePicker, …)` lines:

```csharp
        Updates = new UpdateController(this, Options.Updates);
        UpdateDialog = new UpdateDialog(this) { Name = "UpdateDialog" };
        DialogLayer.AddChild(UpdateDialog);
        Updates.Changed += OnUpdatesChanged;
```

Add the handler:

```csharp
    private void OnUpdatesChanged()
    {
        Toolbar.RefreshUpdate();
        ProjectManager.RefreshUpdate();
        UpdateDialog.Refresh();
    }
```

`ProjectDialogOpen` gains `|| UpdateDialog.Visible`. In `StartUp()`, first line: `Updates.Start();`. In
`ProcessProjects`, after `Play.Update();`: `Updates.Tick();`.

`EditorWorkspace.Dispose(bool)`: add `Updates?.Dispose();` with the other disposals.

`EditorCommands.Run`: next to `help.about`:

```csharp
            case "help.check_updates": _workspace.Updates.CheckNow(); return true;
            case "help.update": _workspace.UpdateDialog.Open(); return true;
```

Help menu (`"help" =>`), before About:

```csharp
                new MenuItem("Check for Updates…", "help.check_updates", null, _workspace.Updates.IsEnabled, Icon: "refresh"),
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`
Expected: PASS (all editor tests, including `UpdateUiTests` and the existing icon/menu tests).

- [ ] **Step 8: Commit**

```bash
just format
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests
git commit -m "Editor: Update badge, dialog and Update & restart

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: QA capture, docs, gates and the end-to-end check

**Files:**
- Modify: `MainframeEngine.Editor/Src/Qa/EditorQaScript.cs` (`update-preview` step; `cancel` closes the update dialog)
- Modify: `Tests/QA/editor-walkthrough.qa`
- Move: `docs/design/future/editor-updates.md` → `docs/design/editor-updates.md`
- Modify: `docs/design/release.md`, `docs/design/editor.md`, `CLAUDE.md`, `docs/design/editor.md` QA section if it lists steps

**Interfaces:**
- Consumes: `UpdateController.ShowPreview`, `UpdateDialog.Close`, `ReleaseInfo`, `ReleaseAsset`, `UpdateCheckResult`.

- [ ] **Step 1: Add the QA step**

In `EditorQaScript`'s step switch, before `default:`:

```csharp
            case "update-preview":
                workspace.Updates.ShowPreview(PreviewUpdate(step[1]));
                break;
```

and in `case "cancel":` add, before the final `else workspace.Popup.Close();`:

```csharp
                else if (workspace.UpdateDialog.Visible)
                    workspace.UpdateDialog.Close();
```

Add the helper to the class:

```csharp
    // update-preview X.Y.Z: the update dialog as a check would show it (QA runs never touch the network).
    private static UpdateCheckResult PreviewUpdate(string version)
    {
        if (!ReleaseVersion.TryParse(version, out var v))
            throw new FormatException($"update-preview needs X.Y.Z, not '{version}'.");
        var release = new ReleaseInfo(v, "v" + v, "## What's Changed\n* Example release notes for the QA capture.\n* A second line.",
            new Uri($"https://github.com/Mainframe-Games/mainframe-engine/releases/tag/v{v}"), []);
        var asset = new ReleaseAsset(UpdatePlatform.AssetName(v, "osx-arm64"), new Uri("https://example.invalid/"), 0, new string('0', 64));
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, asset);
    }
```

In `Tests/QA/editor-walkthrough.qa`, after the `13-quit-prompt` capture's `cancel` line:

```
# The update badge and dialog (a preview: QA runs never touch the network).
update-preview 9.9.9
wait 3
capture 14-update
cancel
```

Run: `just qa-editor` and open `artifacts/qa-editor/14-update.png`: the dialog shows "Mainframe Engine v9.9.9", the notes on
two lines, the green toolbar badge behind the backdrop, and the primary button "Update & restart". Check the earlier
captures still match their previous content (no layout shifts in the toolbar or Project Manager).

- [ ] **Step 2: Move and finish the docs**

```bash
git mv docs/design/future/editor-updates.md docs/design/editor-updates.md
```

In `docs/design/editor-updates.md`: title `# Editor updates — check GitHub Releases, update in place`; status line
"**Status:** implemented." (drop "Today an editor install never changes…" but keep the link to distribution via NuGet);
fix relative links (`../release.md` → `release.md`, `../editor.md` → `editor.md`, `distribution-nuget.md` →
`future/distribution-nuget.md`, `../../../build/…` → `../../build/…`, `../../../memory/…` → `../../memory/…`); replace the
"Docs when this ships" section with nothing (delete it).

`docs/design/release.md`: add before "## macOS app name":

```markdown
## Updates

Released editors check GitHub Releases at start-up and update themselves in place — badge, release notes, **Update &
restart**: download, SHA-256 check, swap by the downloaded editor (`--apply-update`), relaunch. Nothing in the release
workflow is specific to it: the archives' names and layout are the contract. See [Editor updates](editor-updates.md).

**End-to-end check** (manual, on each OS, before a release that changes the updater):

1. `just publish-local <rid> 0.9.0` and unpack `artifacts/release/MainframeEngine-0.9.0-<rid>.*` somewhere writable
   (macOS: `~/Applications`, then right-click → Open once).
2. Start it, open a project, click the badge, **Update & restart**.
3. The editor relaunches as the latest release with the project open; `~/.mainframe/updates/update.log` shows the run.
   When the latest release itself contains the updater, `<root>.old` and the staging folder are gone after it starts and
   the Output panel says "Updated to vX.Y.Z".
4. Repeat from a read-only location (macOS: straight from Downloads, i.e. translocated) to see **Download** and the hint.
```

and add `[Editor updates](editor-updates.md) ·` to its "Related docs" line.

`docs/design/editor.md`: in "## Editor settings" add a sentence: "Check for updates at startup (default on) — see
[Editor updates](editor-updates.md)."; add a short section before "## Files and safety":

```markdown
## Updates

Released builds check GitHub Releases at start-up (Editor Settings › Updates; Help › Check for Updates… always works). A
newer release shows a green badge in the toolbar and the Project Manager; it opens the update dialog (notes, View
release, **Update & restart**). Runs with `--hidden`, `--smoke`, `--qa-script` and tests have no update service; QA
captures the dialog with `update-preview X.Y.Z`. See [Editor updates](editor-updates.md).
```

If editor.md's "Testing and QA" section lists the QA script steps, add `update-preview X.Y.Z` there. Add
`[Editor updates](editor-updates.md)` to editor.md's "Related docs".

`CLAUDE.md`, the `MainframeEngine.Editor/` bullet: after "`Settings/` (editor settings, code editor)," insert
"`Updates/` (self-update from GitHub Releases: check, download, `--apply-update` swap),".

- [ ] **Step 3: Run every gate**

```bash
just build
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test
caffeinate -u -t 600 &
just test-render
just format-check
just shaders-check
```

Expected: all green; `just test-render`'s editor smoke goldens unchanged (the badge is hidden without an update). Fix
anything that fails before continuing.

- [ ] **Step 4: End-to-end on this Mac**

```bash
just publish-local osx-arm64 0.9.0
mkdir -p ~/Applications/mf-update-test && tar -xzf artifacts/release/MainframeEngine-0.9.0-osx-arm64.tar.gz -C ~/Applications/mf-update-test
open ~/Applications/mf-update-test/"Mainframe Engine.app"
```

Follow the release.md recipe steps 2–3 against the live `v1.0.0` release (it predates the updater, so `.old` and the
staging folder remain afterwards — expected; v1.0.0 has no clean-up). Confirm the About dialog / Project Manager show
v1.0.0 after the relaunch and `~/.mainframe/updates/update.log` ends with "Installed and started." Report the result to
the user; then delete `~/Applications/mf-update-test` and `~/.mainframe/updates` only after showing the user what is there.

- [ ] **Step 5: Commit**

```bash
just format
git add -A MainframeEngine.Editor Tests docs CLAUDE.md
git commit -m "Docs: Editor updates — QA capture, release recipe, design doc

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Do not push (commits stay local until the user asks).

# D4 — "Download Demo Project" Implementation Plan

**Status:** ✅ done

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Project Manager button that downloads the Demo matching the editor's version from the GitHub release,
unpacks it safely, points it at the engine checkout, adds it to Recent Projects and opens it.

**Architecture:** The release workflow zips `Examples/Demo` (LFS content included) as two assets. In the editor, pure
helpers build the URL (`DemoRelease`), extract and validate the zip (`DemoArchive`) and rewrite the engine path
(`EnginePathRewriter`); `DemoDownloader` streams the download with progress/cancel through an injected `HttpClient`;
`DownloadDemoDialog` polls it from `Tick()` like `NewProjectDialog` and finishes through `EditorCommands.OpenProject`.

**Tech Stack:** C# / .NET 10 BCL (`HttpClient`, `System.IO.Compression`, `System.Xml.Linq`), RmlUi editor UI, bash,
GitHub Actions. Reuses the editor self-update plumbing merged in #11 (`MainframeEngine.Editor/Src/Updates/`): its
shared `HttpClient` and the `StubHandler` test double (`Tests/MainframeEngine.Editor.Tests/Updates/ReleaseFixtures.cs`).

**Spec:** [`docs/design/future/demo-download.md`](../../design/future/demo-download.md)

**Plan series:** [D1](2026-10-06-d1-demo-project.md) → [D2](2026-10-06-d2-remove-imgui.md) →
[D3](2026-10-06-d3-project-icons.md) → D4 (this). Needs D1 (`Examples/Demo`). Branch `demo-and-polish`.

## Global Constraints

- No new NuGet packages. Tests never touch the network (`HttpMessageHandler` stub).
- Commit locally; do not push; never run `publish.yml` (manual, `main` only, actor `brogan89`).
- Release URLs: base `https://github.com/Mainframe-Games/mainframe-engine/releases`, one constant
  (`DemoRelease.ReleasesBaseUrl`); asset `MainframeEngine.Demo-v{version}.zip` and stable `MainframeEngine.Demo.zip`;
  development version `0.0.0-dev` → `…/releases/latest/download/MainframeEngine.Demo.zip`.
- Uncompressed size cap: 1 GiB. Symlink entries rejected. The destination is never partially written.
- Editor UI: icon button with `data-tooltip`; new glyphs via `icons.txt` + `just editor-icons-fetch` + `just editor-icons`.
- UI thread rule: background work never touches RmlUi; the dialog polls tasks in `Tick()`.
- Gates: `just build`, Release build, `just test`, `just test-render` (editor goldens), `just format-check`.

## Review Focus

- **Destination already exists / not empty:** refused before downloading, with the New Project wording. Pinned by
  Task 4's `ExistingNonEmptyDestinationIsRefusedUpFront`.
- **Cancel mid-download or mid-extract:** temp zip and temp folder removed; destination untouched. Pinned by Task 3's
  `CancellingCleansUp`.
- **No engine checkout (packaged editor without `MAINFRAME_ENGINE_PATH`):** the dialog explains and the Download button
  is disabled. Pinned by Task 4's `WithoutAnEngineCheckoutDownloadIsDisabled`.
- **A zip with LFS pointer files instead of content:** the release script fails rather than publishing it. Pinned by
  Task 1's pointer check (`build/package-demo.sh` exits non-zero on a pointer).
- **A failed download (500, network drop):** the message shows and Download stays enabled for a retry. Pinned by
  Task 4's `AFailedDownloadCanBeRetried`.
- **Slow/hanging server:** no UI freeze; cancel works while waiting for headers. Pinned by Task 3's
  `CancellingWhileWaitingForHeadersCleansUp`.

---

### Task 1: Release packaging

**Files:**
- Create: `build/package-demo.sh`
- Modify: `.github/workflows/publish.yml` (new `demo` job; `release` job downloads it), `justfile` (`publish-local`)
- Modify: `.github/workflows/ci.yml` (template job: package + validate the zip)

- [ ] **Step 1: The script**

```bash
#!/usr/bin/env bash
# Zips Examples/Demo for a release: MainframeEngine.Demo-v<version>.zip and MainframeEngine.Demo.zip in <out-dir>.
# Usage: build/package-demo.sh <version> <out-dir>. Needs the Demo's LFS content pulled (fails on pointer files).
set -euo pipefail
version="$1"; out="$(mkdir -p "$2" && cd "$2" && pwd)"
repo="$(cd "$(dirname "$0")/.." && pwd)"
stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
dest="$stage/MainframeEngine.Demo"
mkdir -p "$dest"
(cd "$repo/Examples/Demo" && git ls-files -z --cached --others --exclude-standard) |
  while IFS= read -r -d '' file; do
    case "$file" in */bin/*|*/obj/*|*.user|.vs/*) continue ;; esac
    mkdir -p "$dest/$(dirname "$file")"
    cp "$repo/Examples/Demo/$file" "$dest/$file"
  done
if grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >/dev/null; then
  echo "error: Git LFS pointer files in the Demo (run git lfs pull --include='Examples/Demo/**'):" >&2
  grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >&2
  exit 1
fi
(cd "$stage" && zip -qr -X "$out/MainframeEngine.Demo-v$version.zip" MainframeEngine.Demo)
cp "$out/MainframeEngine.Demo-v$version.zip" "$out/MainframeEngine.Demo.zip"
ls -la "$out"/MainframeEngine.Demo*.zip
```

`chmod +x build/package-demo.sh`. Run it locally: `build/package-demo.sh 0.0.0-local artifacts/release` — Expected:
two zips; `unzip -l` shows `MainframeEngine.Demo/project.mfproj`, `Demo.Launcher/…`, `Content/…`, no `bin/`.

- [ ] **Step 2: publish.yml**

Add a job (ubuntu, same checkout/LFS-cache pattern as `build`, but pulling only the Demo):

```yaml
  demo:
    needs: [guard, ci]
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
      - name: Fetch the Demo's LFS objects
        run: git lfs pull --include="Examples/Demo/**"
      - name: Package the Demo
        run: build/package-demo.sh "${{ needs.guard.outputs.version }}" artifacts/release
      - uses: actions/upload-artifact@v4
        with:
          name: demo
          path: artifacts/release/MainframeEngine.Demo*.zip
```

In `release`: `needs: [guard, build, demo]`, and change the download step's `pattern` to download both `editor-*` and
`demo` into `release/` (merge-multiple). `gh release create … release/*` then attaches the zips.

- [ ] **Step 3: justfile and CI validation**

`publish-local`: after `package-editor.sh`, add `build/package-demo.sh "{{version}}" "{{artifacts}}/release"`.
In `ci.yml`'s template job (which pulls `Examples/Demo/**` LFS since D1 Task 16), add after "Demo smoke":

```yaml
      - name: Demo release zip
        run: |
          build/package-demo.sh 0.0.0-ci "$RUNNER_TEMP/demo-zip"
          dotnet run --project MainframeEngine.Editor -c Release -- --validate-demo-zip "$RUNNER_TEMP/demo-zip/MainframeEngine.Demo.zip"
```

(`--validate-demo-zip` is added in Task 2, Step 5.)

- [ ] **Step 4: Commit**

```bash
git add build/package-demo.sh .github/workflows justfile
git commit -m "Release: package the Demo as MainframeEngine.Demo(-vX.Y.Z).zip"
```

---

### Task 2: URL, extraction, validation, engine-path rewrite (pure)

**Files:**
- Create: `MainframeEngine.Editor/Src/Projects/Demo/DemoRelease.cs`, `DemoArchive.cs`, `EnginePathRewriter.cs`, `DemoDownloadException.cs`
- Modify: `MainframeEngine.Editor/Src/Qa/…` argument handling (the place `--smoke`/`--qa-script` are parsed) for `--validate-demo-zip`
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/Demo/{DemoReleaseTests,DemoArchiveTests,EnginePathRewriterTests}.cs`,
  `Tests/MainframeEngine.Editor.Tests/Projects/Demo/DemoZips.cs` (test zip builder)

**Interfaces:**
- Produces:
  ```csharp
  public sealed class DemoDownloadException(string message, Exception? inner = null) : Exception(message, inner);
  public static class DemoRelease
  {
      public const string ReleasesBaseUrl = "https://github.com/Mainframe-Games/mainframe-engine/releases";
      public const string DevelopmentVersion = "0.0.0-dev";
      public static Uri AssetUrl(string editorVersion);
      public static bool IsDevelopment(string editorVersion);
  }
  public static class DemoArchive
  {
      public const long MaxUncompressedBytes = 1L << 30;
      public static string ExtractAndValidate(string zipPath, string workDirectory, long maxBytes = MaxUncompressedBytes); // returns the project root inside workDirectory
      public static void Validate(string projectRoot);
  }
  public static class EnginePathRewriter { public static void Rewrite(string directoryBuildProps, string enginePath); }
  ```

- [ ] **Step 1: Test zip builder**

```csharp
using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

/// <summary>Builds Demo-shaped zips in memory for tests.</summary>
internal static class DemoZips
{
    public const string Props = """
        <Project>
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <!-- The engine checkout this game builds against. -->
            <MainframeEnginePath>$([System.IO.Path]::GetFullPath($([System.IO.Path]::Combine('$(MSBuildThisFileDirectory)', '../..'))))</MainframeEnginePath>
          </PropertyGroup>
        </Project>
        """;

    public static Dictionary<string, string> ValidProject(string top = "MainframeEngine.Demo") => new()
    {
        [$"{top}/project.mfproj"] = """{ "format": 1, "name": "Mainframe Demo", "mainScene": "Content/Scenes/basic_3d.mscene" }""",
        [$"{top}/Directory.Build.props"] = Props,
        [$"{top}/Demo.Launcher/Demo.Launcher.csproj"] = "<Project />",
        [$"{top}/Content/Scenes/basic_3d.mscene"] = "{}",
    };

    public static string Write(string path, IReadOnlyDictionary<string, string> files, Action<ZipArchive>? extra = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        extra?.Invoke(zip);
        return path;
    }
}
```

- [ ] **Step 2: Failing tests**

`DemoReleaseTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoReleaseTests
{
    [Fact]
    public void ReleaseBuildsUseTheTaggedAsset() =>
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.4.2/MainframeEngine.Demo-v1.4.2.zip",
            DemoRelease.AssetUrl("1.4.2").ToString());

    [Fact]
    public void DevelopmentBuildsUseTheLatestRelease()
    {
        Assert.True(DemoRelease.IsDevelopment("0.0.0-dev"));
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/latest/download/MainframeEngine.Demo.zip",
            DemoRelease.AssetUrl("0.0.0-dev").ToString());
    }
}
```

`DemoArchiveTests.cs`:

```csharp
using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-demo-zip").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Work() => Directory.CreateTempSubdirectory(Path.Combine(_directory, "w")).FullName;

    [Fact]
    public void ValidZipExtractsToItsTopFolder()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "ok.zip"), DemoZips.ValidProject());
        var work = Work();
        var root = DemoArchive.ExtractAndValidate(zip, work);
        Assert.Equal(Path.Combine(work, "MainframeEngine.Demo"), root);
        Assert.True(File.Exists(Path.Combine(root, "project.mfproj")));
    }

    [Fact]
    public void EntriesEscapingTheFolderAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["MainframeEngine.Demo/../../evil.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "slip.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
        Assert.False(File.Exists(Path.Combine(_directory, "evil.txt")));
    }

    [Fact]
    public void AbsoluteEntriesAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["/tmp/evil.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "abs.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void SymlinkEntriesAreRefused()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "link.zip"), DemoZips.ValidProject(), z =>
        {
            var entry = z.CreateEntry("MainframeEngine.Demo/link");
            entry.ExternalAttributes = unchecked((int)(0xA1FF_0000u)); // S_IFLNK | 0777
            using var w = new StreamWriter(entry.Open());
            w.Write("/etc/passwd");
        });
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void OversizedArchivesAreRefused()
    {
        var zip = DemoZips.Write(Path.Combine(_directory, "big.zip"), DemoZips.ValidProject());
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work(), maxBytes: 10));
    }

    [Fact]
    public void TwoTopLevelFoldersAreRefused()
    {
        var files = DemoZips.ValidProject();
        files["Other/readme.txt"] = "x";
        var zip = DemoZips.Write(Path.Combine(_directory, "two.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Theory]
    [InlineData("MainframeEngine.Demo/project.mfproj")]
    [InlineData("MainframeEngine.Demo/Demo.Launcher/Demo.Launcher.csproj")]
    [InlineData("MainframeEngine.Demo/Content/Scenes/basic_3d.mscene")]
    public void MissingProjectPartsAreRefused(string missing)
    {
        var files = DemoZips.ValidProject();
        files.Remove(missing);
        var zip = DemoZips.Write(Path.Combine(_directory, "missing.zip"), files);
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(zip, Work()));
    }

    [Fact]
    public void CorruptZipIsADownloadError()
    {
        var path = Path.Combine(_directory, "corrupt.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<DemoDownloadException>(() => DemoArchive.ExtractAndValidate(path, Work()));
    }
}
```

`EnginePathRewriterTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class EnginePathRewriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-props").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void OnlyTheEnginePathChanges()
    {
        var props = Path.Combine(_directory, "Directory.Build.props");
        File.WriteAllText(props, DemoZips.Props);
        EnginePathRewriter.Rewrite(props, "/Users/me/My Engine");

        var text = File.ReadAllText(props);
        Assert.Contains("<MainframeEnginePath>/Users/me/My Engine</MainframeEnginePath>", text);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", text);
        Assert.Contains("<!-- The engine checkout this game builds against. -->", text);
    }

    [Fact]
    public void MissingPropertyIsAnError()
    {
        var props = Path.Combine(_directory, "Directory.Build.props");
        File.WriteAllText(props, "<Project><PropertyGroup /></Project>");
        Assert.Throws<DemoDownloadException>(() => EnginePathRewriter.Rewrite(props, "/x"));
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Projects.Demo"` — Expected: FAIL (types missing).

- [ ] **Step 3: Implement**

`DemoDownloadException.cs`:

```csharp
namespace MainframeEngine.Editor;

/// <summary>A user-facing Demo download failure (the message is shown in the dialog).</summary>
public sealed class DemoDownloadException(string message, Exception? inner = null) : Exception(message, inner);
```

`DemoRelease.cs`:

```csharp
namespace MainframeEngine.Editor;

/// <summary>Where the Demo zip for an editor version lives on GitHub Releases.</summary>
public static class DemoRelease
{
    public const string ReleasesBaseUrl = "https://github.com/Mainframe-Games/mainframe-engine/releases";
    public const string DevelopmentVersion = "0.0.0-dev";

    public static bool IsDevelopment(string editorVersion) =>
        string.IsNullOrWhiteSpace(editorVersion) || editorVersion == DevelopmentVersion || editorVersion.Contains('-');

    public static Uri AssetUrl(string editorVersion) => IsDevelopment(editorVersion)
        ? new Uri($"{ReleasesBaseUrl}/latest/download/MainframeEngine.Demo.zip")
        : new Uri($"{ReleasesBaseUrl}/download/v{editorVersion}/MainframeEngine.Demo-v{editorVersion}.zip");
}
```

`DemoArchive.cs`:

```csharp
using System.IO.Compression;

namespace MainframeEngine.Editor;

/// <summary>Extracts a downloaded Demo zip into a work folder (no escapes, no links, size-capped) and validates it.</summary>
public static class DemoArchive
{
    public const long MaxUncompressedBytes = 1L << 30;

    public static string ExtractAndValidate(string zipPath, string workDirectory, long maxBytes = MaxUncompressedBytes)
    {
        var work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory)) + Path.DirectorySeparatorChar;
        var tops = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || Path.IsPathRooted(name))
                    throw new DemoDownloadException($"The demo archive has an absolute path ({entry.FullName}).");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new DemoDownloadException($"The demo archive contains a link ({entry.FullName}).");
                var target = Path.GetFullPath(Path.Combine(work, name));
                if (!target.StartsWith(work, StringComparison.Ordinal))
                    throw new DemoDownloadException($"The demo archive has a path outside its folder ({entry.FullName}).");
                total += entry.Length;
                if (total > maxBytes)
                    throw new DemoDownloadException("The demo archive is larger than expected.");
                tops.Add(name.Split('/', 2)[0]);
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new DemoDownloadException("The demo archive could not be unpacked: " + e.Message, e);
        }

        if (tops.Count != 1)
            throw new DemoDownloadException("The demo archive must contain exactly one project folder.");
        var root = Path.Combine(work, tops.Single());
        Validate(root);
        return root;
    }

    public static void Validate(string projectRoot)
    {
        var projectFile = GameProjectLayout.ProjectFileOf(projectRoot);
        if (!File.Exists(projectFile))
            throw new DemoDownloadException($"The demo has no {ProjectSettings.FileName}.");
        try
        {
            ProjectSettings.Load(projectFile);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            throw new DemoDownloadException($"The demo's {ProjectSettings.FileName} could not be read: {e.Message}", e);
        }

        if (GameProjectLayout.LauncherProjectOf(projectRoot) is null)
            throw new DemoDownloadException("The demo has no *.Launcher project.");
        if (!Directory.Exists(Path.Combine(projectRoot, "Content", "Scenes")))
            throw new DemoDownloadException("The demo has no Content/Scenes folder.");
        if (!File.Exists(Path.Combine(projectRoot, "Directory.Build.props")))
            throw new DemoDownloadException("The demo has no Directory.Build.props.");
    }
}
```

`EnginePathRewriter.cs`:

```csharp
using System.Xml.Linq;

namespace MainframeEngine.Editor;

/// <summary>Points a game's <c>Directory.Build.props</c> at an engine checkout (same value New Project passes as --engine-path).</summary>
public static class EnginePathRewriter
{
    public static void Rewrite(string directoryBuildProps, string enginePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(directoryBuildProps, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            throw new DemoDownloadException("The demo's Directory.Build.props could not be read: " + e.Message, e);
        }

        var property = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "MainframeEnginePath")
            ?? throw new DemoDownloadException("The demo's Directory.Build.props has no MainframeEnginePath.");
        property.Value = enginePath;
        document.Save(directoryBuildProps, SaveOptions.DisableFormatting);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Projects.Demo"` — Expected: PASS.
(If `ZipFile` refuses to *write* the `../` or `/tmp` entry names in the test builder on some OS, build those entries
with a raw `ZipArchive.CreateEntry` — it accepts any name.)

- [ ] **Step 4: CLI validation hook**

In the editor's argument handling (next to `--smoke`/`--qa-script`), add `--validate-demo-zip <zip>`: extract to a temp
folder with `DemoArchive.ExtractAndValidate`, print `ok <root>` and exit 0, or print the `DemoDownloadException`
message and exit 1; delete the temp folder either way. Run:
`build/package-demo.sh 0.0.0-local artifacts/release && dotnet run --project MainframeEngine.Editor -- --validate-demo-zip artifacts/release/MainframeEngine.Demo.zip`
Expected: `ok …`, exit 0.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor/Src Tests/MainframeEngine.Editor.Tests/Projects/Demo
git commit -m "Editor: Demo release URL, safe zip extraction, engine-path rewrite"
```

---

### Task 3: `DemoDownloader`

**Files:**
- Create: `MainframeEngine.Editor/Src/Projects/Demo/DemoDownloader.cs`
- Create: `MainframeEngine.Editor/Src/EditorHttp.cs` (the shared client, moved out of `Updates/UpdateService.cs:37`)
- Modify: `MainframeEngine.Editor/Src/Updates/UpdateService.cs` (use `EditorHttp.Shared`),
  `Tests/MainframeEngine.Editor.Tests/Updates/ReleaseFixtures.cs` (`StubHandler` gains an async responder)
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/Demo/DemoDownloaderTests.cs`

**Interfaces:**
- Consumes: Task 2 types.
- Produces:
  ```csharp
  public sealed record DemoDownloadRequest(string ParentDirectory, string FolderName, string EnginePath)
  { public string Destination => Path.Combine(ParentDirectory, FolderName); }
  public sealed class DemoDownloader(HttpClient http, string editorVersion, string downloadsDirectory)
  {
      public static string DefaultDownloadsDirectory { get; } // ~/.mainframe/downloads
      public Task<string> DownloadAsync(DemoDownloadRequest request, IProgress<double>? progress = null, CancellationToken ct = default); // returns Destination
  }
  ```

- [ ] **Step 1: Shared client and an async-capable `StubHandler`**

`MainframeEngine.Editor/Src/EditorHttp.cs`:

```csharp
namespace MainframeEngine.Editor;

/// <summary>The editor's one <see cref="HttpClient"/> (update checks, update and demo downloads); callers set timeouts per request.</summary>
internal static class EditorHttp
{
    private static readonly Lazy<HttpClient> Client = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    public static HttpClient Shared => Client.Value;
}
```

In `UpdateService.cs` replace `private static readonly Lazy<HttpClient> Http = …` and its `Http.Value` uses with
`EditorHttp.Shared`. Run `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~Updates` — PASS.

Give `StubHandler` (in `ReleaseFixtures.cs`) an async responder so tests can hang until cancelled, keeping its
existing sync constructor and factories source-compatible:

```csharp
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return respond(request, cancellationToken);
    }

    public static StubHandler Bytes(byte[] body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    // (existing Json / Status / Throws factories unchanged)
}
```

- [ ] **Step 2: Failing tests**

```csharp
using System.Net;
using MainframeEngine.Editor.Tests.Updates;

namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoDownloaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-demo-dl").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private byte[] ValidZip() => File.ReadAllBytes(DemoZips.Write(Path.Combine(_directory, "demo.zip"), DemoZips.ValidProject()));

    private DemoDownloadRequest Request() => new(Path.Combine(_directory, "projects"), "MainframeEngine.Demo", "/engine");

    private string Downloads => Path.Combine(_directory, "downloads");

    [Fact]
    public async Task DownloadExtractsRewritesAndReportsProgress()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Bytes(ValidZip());
        var progress = new List<double>();
        var destination = await new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads)
            .DownloadAsync(Request(), new Progress<double>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Request().Destination, destination);
        Assert.True(File.Exists(Path.Combine(destination, "project.mfproj")));
        Assert.Contains("<MainframeEnginePath>/engine</MainframeEnginePath>", File.ReadAllText(Path.Combine(destination, "Directory.Build.props")));
        Assert.Equal(DemoRelease.AssetUrl("1.2.3"), handler.Requests.Single().RequestUri);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task NotFoundSaysThereIsNoDemoForThisVersion()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Status(HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.Contains("1.2.3", error.Message);
        Assert.False(Directory.Exists(Request().Destination));
    }

    [Fact]
    public async Task CancellingWhileWaitingForHeadersCleansUp()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: cancel.Token));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.True(!Directory.Exists(Downloads) || !Directory.EnumerateFileSystemEntries(Downloads).Any());
    }

    [Fact]
    public async Task CancellingCleansUp()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        using var cancel = new CancellationTokenSource();
        var body = new SlowStream(ValidZip(), afterBytes: 16, onPause: cancel.Cancel);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: cancel.Token));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task InvalidArchiveLeavesNothingBehind()
    {
        Directory.CreateDirectory(Request().ParentDirectory);
        var handler = StubHandler.Bytes("not a zip"u8.ToArray());
        await Assert.ThrowsAsync<DemoDownloadException>(() =>
            new DemoDownloader(new HttpClient(handler), "1.2.3", Downloads).DownloadAsync(Request(), ct: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Request().Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Downloads));
    }

    /// <summary>Returns <paramref name="afterBytes"/> bytes, calls <paramref name="onPause"/>, then honours cancellation.</summary>
    private sealed class SlowStream(byte[] data, int afterBytes, Action onPause) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= afterBytes)
            {
                onPause();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, afterBytes - Position)], cancellationToken);
        }
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter DemoDownloaderTests` — Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Net;

namespace MainframeEngine.Editor;

public sealed record DemoDownloadRequest(string ParentDirectory, string FolderName, string EnginePath)
{
    public string Destination => Path.Combine(ParentDirectory, FolderName);
}

/// <summary>
/// Downloads the Demo zip for <c>editorVersion</c>, extracts and validates it in a work folder under
/// <c>downloadsDirectory</c>, points it at the engine and moves it to the destination. Runs off the UI thread; failures
/// are <see cref="DemoDownloadException"/>s, cancellation is <see cref="OperationCanceledException"/>; either way the work
/// files are deleted and the destination is not created.
/// </summary>
public sealed class DemoDownloader(HttpClient http, string editorVersion, string downloadsDirectory)
{
    public static string DefaultDownloadsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "downloads");

    public async Task<string> DownloadAsync(DemoDownloadRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // off the caller's thread
        Directory.CreateDirectory(downloadsDirectory);
        var id = Guid.NewGuid().ToString("N");
        var zipPath = Path.Combine(downloadsDirectory, $"demo-{id}.zip");
        var work = Path.Combine(downloadsDirectory, $"demo-{id}");
        try
        {
            await DownloadZipAsync(zipPath, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var root = DemoArchive.ExtractAndValidate(zipPath, work);
            ct.ThrowIfCancellationRequested();
            EnginePathRewriter.Rewrite(Path.Combine(root, "Directory.Build.props"), GameProjectLayout.RealPath(request.EnginePath));
            if (Directory.Exists(request.Destination) && Directory.EnumerateFileSystemEntries(request.Destination).Any())
                throw new DemoDownloadException($"'{request.Destination}' already exists and is not empty.");
            if (Directory.Exists(request.Destination))
                Directory.Delete(request.Destination);
            Directory.Move(root, request.Destination);
            return request.Destination;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DemoDownloadException("The demo could not be saved: " + e.Message, e);
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(work);
        }
    }

    private async Task DownloadZipAsync(string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        var url = DemoRelease.AssetUrl(editorVersion);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.UserAgent.ParseAdd($"MainframeEngine-Editor/{editorVersion}");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new DemoDownloadException("Could not reach GitHub: " + e.Message, e);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DemoDownloadException($"There is no published demo for this editor version (v{editorVersion}).");
            if (!response.IsSuccessStatusCode)
                throw new DemoDownloadException($"The demo download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done > DemoArchive.MaxUncompressedBytes)
                    throw new DemoDownloadException("The demo download is larger than expected.");
                progress?.Report(total > 0 ? (double)done / total : -1);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
```

(`progress` of `-1` means "unknown size" — the dialog shows an indeterminate bar.) Run:
`dotnet test Tests/MainframeEngine.Editor.Tests --filter DemoDownloaderTests` — Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add MainframeEngine.Editor/Src Tests/MainframeEngine.Editor.Tests
git commit -m "Editor: DemoDownloader (streamed, cancellable, cleans up); shared EditorHttp client"
```

---

### Task 4: Dialog and Project Manager button

**Files:**
- Create: `MainframeEngine.Editor/Src/UI/DownloadDemoDialog.cs`, `MainframeEngine.Editor/Content/Editor/download_demo.rml`
- Modify: `MainframeEngine.Editor/Content/icons/icons.txt` (`download`), regenerate `icons-*.png` + `icons.rcss`
- Modify: `MainframeEngine.Editor/Src/UI/ProjectManager.cs` (event `download_demo`), `project_manager.rml` (button in `.pm-actions`)
- Modify: `MainframeEngine.Editor/Src/EditorWorkspace.Projects.cs` (create + tick the dialog; include it in `ProjectDialogOpen`)
- Modify: `EditorWorkspaceOptions` (new `Func<HttpMessageHandler>? DemoHttpHandler` and `string? DemoDownloadsDirectory` for tests)
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/DownloadDemoWorkflowTests.cs`

**Interfaces:**
- Consumes: `DemoDownloader`, `DemoDownloadRequest`, `NewProjectValidation.ValidateLocation`, `TemplateLocator.FindEngineCheckout`,
  `NewProjectDialog.DefaultLocation`, `Workspace.Commands.OpenProject`.
- Produces: `public sealed class DownloadDemoDialog : EditorDocument { public void Open(); public void Tick(); public bool Busy { get; } public string Error { get; } }`;
  workspace property `DownloadDemo`.

- [ ] **Step 1: Icon glyph**

Append `download` to `MainframeEngine.Editor/Content/icons/icons.txt`, then:

```bash
just editor-icons-fetch && just editor-icons
dotnet test Tests/MainframeEngine.Editor.Tests --filter IconTests
```

Expected: `icons.rcss` has `.icon-download`; IconTests PASS.

- [ ] **Step 2: Failing workflow tests**

```csharp
using MainframeEngine.Editor.Tests.Projects.Demo;
using MainframeEngine.Editor.Tests.Updates;

namespace MainframeEngine.Editor.Tests.Projects;

[Collection(nameof(SerialEditor))]
public sealed class DownloadDemoWorkflowTests : IDisposable
{
    private readonly string _directory = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-dl-demo").FullName);
    private HeadlessEditor? _editor;

    public void Dispose()
    {
        _editor?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private HeadlessEditor Editor(StubHandler handler) => _editor = new HeadlessEditor(configure: o => o with
    {
        ShowProjectManager = true,
        GameBuilder = new FakeGameBuilder(),        // the fake ProjectWorkflowTests uses
        DemoHttpHandler = () => handler,
        DemoDownloadsDirectory = Path.Combine(_directory, "downloads"),
    });

    [Fact]
    public void DownloadOpensTheDemoAndAddsItToRecentProjects()
    {
        var zip = File.ReadAllBytes(DemoZips.Write(Path.Combine(_directory, "demo.zip"), DemoZips.ValidProject()));
        var editor = Editor(StubHandler.Bytes(zip));
        var w = editor.Workspace;
        w.ProjectManager.Open();
        editor.Tick(3);

        w.DownloadDemo.Open();
        w.DownloadDemo.Location = _directory;
        editor.Tick(2);
        editor.Click("#dd-download");
        editor.TickUntil(() => w.Project.IsOpen, seconds: 30);

        var destination = Path.Combine(_directory, "MainframeEngine.Demo");
        Assert.Equal(destination, w.Project.Directory);
        Assert.Contains(w.RecentProjects.Items, p => p.Path == destination);
        Assert.Empty(editor.RmlMessages);
    }

    [Fact]
    public void ExistingNonEmptyDestinationIsRefusedUpFront()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "MainframeEngine.Demo", "x"));
        var handler = StubHandler.Bytes([]);
        var editor = Editor(handler);
        editor.Workspace.DownloadDemo.Open();
        editor.Workspace.DownloadDemo.Location = _directory;
        editor.Tick(3);
        Assert.NotEqual("", editor.Workspace.DownloadDemo.Error);
        editor.Click("#dd-download");
        editor.Tick(3);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AFailedDownloadCanBeRetried()
    {
        var calls = 0;
        var zip = File.ReadAllBytes(DemoZips.Write(Path.Combine(_directory, "demo.zip"), DemoZips.ValidProject()));
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var editor = Editor(handler);
        var w = editor.Workspace;
        w.DownloadDemo.Open();
        w.DownloadDemo.Location = _directory;
        editor.Tick(2);
        editor.Click("#dd-download");
        editor.TickUntil(() => !w.DownloadDemo.Busy, seconds: 10);
        Assert.Equal("", w.DownloadDemo.Error);           // no validation error: the button is enabled
        editor.Click("#dd-download");
        editor.TickUntil(() => w.Project.IsOpen, seconds: 30);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WithoutAnEngineCheckoutDownloadIsDisabled()
    {
        var editor = Editor(StubHandler.Bytes([]));
        editor.Workspace.DownloadDemo.EngineCheckoutOverride = ""; // simulate a packaged editor without a checkout
        editor.Workspace.DownloadDemo.Open();
        editor.Tick(3);
        Assert.Contains("engine", editor.Workspace.DownloadDemo.Error, StringComparison.OrdinalIgnoreCase);
    }
}
```

(Match `HeadlessEditor`'s click/tick helpers and the fake builder/launcher names to `ProjectWorkflowTests`;
`Location`/`EngineCheckoutOverride` are test-visible setters on the dialog.)

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter DownloadDemoWorkflowTests` — Expected: FAIL.

- [ ] **Step 3: Markup**

`MainframeEngine.Editor/Content/Editor/download_demo.rml`:

```xml
<rml>
<head>
    <title>Download Demo Project</title>
    <link type="text/rcss" href="/Content/icons/icons.rcss"/>
    <link type="text/rcss" href="theme.rcss"/>
    <link type="text/rcss" href="dialogs.rcss"/>
    <style>
        #dialog { left: 50%; top: 50%; width: 560dp; height: 300dp; margin-left: -280dp; margin-top: -150dp; }
        .dd-bar { height: 8dp; border-radius: 4dp; background-color: #ffffff1a; margin-top: 12dp; }
        .dd-fill { height: 8dp; border-radius: 4dp; background-color: #2563eb; }
    </style>
</head>
<body class="backdrop" data-model="download_demo">
    <div class="dialog" id="dialog">
        <div class="dialog-title"><span class="icon icon-download"></span><span>Download Demo Project</span></div>
        <div class="dialog-body">
            <div class="dialog-row">
                <span class="caption">Location</span>
                <input class="text" type="text" id="dd-location" data-value="location"/>
                <button id="dd-browse" data-event-click="browse()" data-tooltip="Browse — choose the folder the demo goes in"><span class="icon icon-sm icon-folder-open"></span></button>
            </div>
            <div class="dialog-row"><span class="caption">Folder</span><span>{{folder}}</span></div>
            <div class="dialog-row"><span class="caption">Engine</span><span>{{engine}}</span></div>
            <div class="dd-bar" data-if="busy"><div class="dd-fill" data-style-width="(progress * 100) + '%'"></div></div>
            <div class="caption" data-if="busy">{{status}}</div>
            <div class="dialog-error">{{error}}{{failure}}</div>
        </div>
        <div class="dialog-buttons">
            <button id="dd-cancel" data-event-click="cancel()">Cancel</button>
            <button class="primary" id="dd-download" data-class-disabled="busy || error != ''" data-event-click="download()">Download &amp; Open</button>
        </div>
    </div>
</body>
</rml>
```

Project Manager button (in `.pm-actions`, after Open Folder):

```xml
<button id="pm-download-demo" data-event-click="download_demo()" data-tooltip="Download Demo — get the demo project for this editor version"><span class="icon icon-download"></span><span>Demo…</span></button>
```

and in `ProjectManager.OnReady`: `.Event("download_demo", () => Workspace.DownloadDemo.Open())`.

- [ ] **Step 4: Dialog**

```csharp
namespace MainframeEngine.Editor;

/// <summary>Downloads the Demo for this editor version into a chosen folder, then opens it (New Project's flow).</summary>
public sealed class DownloadDemoDialog : EditorDocument
{
    public const string FolderName = "MainframeEngine.Demo";

    private RmlDataModel? _model;
    private Task<string>? _download;
    private CancellationTokenSource? _cancel;
    private ProgressSink _progress = new();
    private string _location = NewProjectDialog.DefaultLocation;
    private string _engine = "";
    private string _error = "";   // validation: blocks Download
    private string _failure = ""; // last download failure: shown, Download stays enabled (retry)

    public DownloadDemoDialog(EditorWorkspace workspace) : base(workspace, "download_demo.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>Tests: "" simulates no engine checkout; null uses <see cref="TemplateLocator.FindEngineCheckout"/>.</summary>
    public string? EngineCheckoutOverride { get; set; }

    public bool Busy => _download is not null;
    public string Error => _error;

    public string Location
    {
        get => _location;
        set
        {
            _location = value;
            CallDeferred(static s => ((DownloadDemoDialog)s!).Validate(), this);
        }
    }

    protected override void OnReady() =>
        _model = CreateDataModel("download_demo")
            .Bind("location", this, static d => d._location, static (d, v) => d.Location = v)
            .Bind("folder", this, static d => FolderName)
            .Bind("engine", this, static d => d._engine == "" ? "—" : d._engine)
            .Bind("busy", this, static d => d.Busy)
            .Bind("progress", this, static d => MathF.Max(0f, (float)d._progress.Value))
            .Bind("status", this, static d => d._progress.Value < 0 ? "Downloading…" : $"Downloading… {d._progress.Value:P0}")
            .Bind("error", this, static d => d._error)
            .Bind("failure", this, static d => d._failure)
            .Event("browse", Browse)
            .Event("download", Start)
            .Event("cancel", Cancel);

    public void Open()
    {
        _engine = EngineCheckoutOverride ?? TemplateLocator.FindEngineCheckout() ?? "";
        Visible = true;
        Validate();
    }

    public void Tick()
    {
        if (_download is null)
            return;
        _model?.Dirty("progress");
        _model?.Dirty("status");
        if (!_download.IsCompleted)
            return;
        var task = _download;
        _download = null;
        _cancel?.Dispose();
        _cancel = null;
        if (task.IsCompletedSuccessfully)
        {
            Close();
            Workspace.Commands.OpenProject(task.Result);
            return;
        }

        _failure = task.Exception?.InnerException is DemoDownloadException e ? e.Message
            : task.IsCanceled ? "" : "The demo download failed: " + task.Exception?.InnerException?.Message;
        _model?.DirtyAll();
    }

    private void Validate()
    {
        _error = _engine == ""
            ? "No engine checkout was found (set MAINFRAME_ENGINE_PATH); the demo builds against the engine sources."
            : !Directory.Exists(_location) ? "The location does not exist."
            : NewProjectValidation.ValidateLocation(_location, FolderName) ?? "";
        _model?.DirtyAll();
    }

    private void Start()
    {
        Validate();
        if (Busy || _error != "")
            return;
        _failure = "";
        _cancel = new CancellationTokenSource();
        _progress = new ProgressSink();
        var handler = Workspace.Options.DemoHttpHandler?.Invoke();
        var http = handler is null ? EditorHttp.Shared : new HttpClient(handler);
        var downloader = new DemoDownloader(http, EngineInfo.Version, Workspace.Options.DemoDownloadsDirectory ?? DemoDownloader.DefaultDownloadsDirectory);
        _download = downloader.DownloadAsync(new DemoDownloadRequest(_location, FolderName, _engine), _progress, _cancel.Token);
        _model?.DirtyAll();
    }

    private void Cancel()
    {
        if (Busy)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }

    private void Browse() =>
        Workspace.FilePicker.Open(new FilePickerModel(FilePickerMode.Folder, _location, []), path => Location = path);

    private void Close() => HideAndReleaseFocus();

    /// <summary>Progress written by the download thread, read by the UI thread.</summary>
    private sealed class ProgressSink : IProgress<double>
    {
        private double _value;
        public double Value => Volatile.Read(ref _value);
        public void Report(double value) => Volatile.Write(ref _value, value);
    }
}
```

(The `status` getter builds a string only when the model is dirtied by `Tick` during a download — acceptable in the
editor. Use the editor's real FilePicker API — `ProjectManager.OpenFolder` shows how — and `Workspace.Options` naming.)

Wire-up in `EditorWorkspace.Projects.cs` next to `NewProject`:

```csharp
DownloadDemo = new DownloadDemoDialog(this) { Name = "DownloadDemo" };
DialogLayer.AddChild(DownloadDemo);
```

then keep the existing "move FilePicker/ListPicker/Message/Popup to the top" lines after it, add
`|| DownloadDemo.Visible` to `ProjectDialogOpen`, and `if (DownloadDemo.Visible) DownloadDemo.Tick();` in
`ProcessProjects`. Add `DemoHttpHandler` and `DemoDownloadsDirectory` (both default null) to `EditorWorkspaceOptions`.

- [ ] **Step 5: Run tests; editor golden**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "DownloadDemoWorkflowTests|ProjectWorkflowTests"` — Expected: PASS.
Run: `caffeinate -u -t 1200 & dotnet test Tests/MainframeEngine.RenderTests --filter ProjectManagerMatchesGolden` — the
new button changes the Project Manager golden: inspect, re-record (`UPDATE_GOLDENS=1`, both drivers), inspect.

- [ ] **Step 6: Manual check against a real release (optional, needs network)**

`just editor` → Project Manager → Demo… → Download & Open (a development build fetches `latest`; this works once a
release with the Demo zip exists). Expected: progress bar, then the Demo opens and builds.

- [ ] **Step 7: Docs, commit**

`docs/design/editor.md#projects`: the Download Demo dialog (URL rules, safety, engine path, cancel).
`docs/design/release.md`: the two Demo assets, `build/package-demo.sh`, the `demo` job. `CLAUDE.md` Build & Run: no
change unless a `just` recipe was added. Mark D4 ✅ in `docs/milestones.md` and the spec status ✅; with D1–D4 done,
mark the "Demo & polish" milestone ✅.

```bash
git add MainframeEngine.Editor Tests docs CLAUDE.md
git commit -m "Editor: Download Demo Project in the Project Manager"
```

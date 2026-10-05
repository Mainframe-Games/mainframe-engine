# D3 — Project Icons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show each project's icon (`window.icon` from `project.mfproj`) in the editor's Project Manager, give new
projects a default icon, and make absolute image paths work in RmlUi on macOS/Linux.

**Architecture:** A C# `JoinPath` system callback (ported RmlUi default + "existing absolute file stays absolute")
fixes absolute `src` paths at the root. `ProjectIconResolver` reads and caches each recent project's icon path; the
Project Manager rows bind it to an `<img>` with the folder glyph as fallback. The `mfgame` template ships
`Content/icon.png` and sets `window.icon`.

**Tech Stack:** C# / .NET 10, RmlUi via the `mfrmlui` shim (function-pointer callbacks), xUnit v3, the editor's
headless test harness.

**Spec:** [`docs/design/future/project-icons.md`](../../design/future/project-icons.md)

**Plan series:** [D1](2026-10-06-d1-demo-project.md) → [D2](2026-10-06-d2-remove-imgui.md) → D3 (this) →
[D4 Demo download](2026-10-06-d4-demo-download.md). Branch `demo-and-polish`.

## Global Constraints

- Commit locally; do not push. No new NuGet packages; no native shim rebuild (the `join_path` slot already exists in
  `mfrmlui.h:215-216`; only C# assigns it).
- No `project.mfproj` format bump: `window.icon` is the project icon.
- Editor UI: icons over text, tooltips via `data-tooltip`.
- `[UnmanagedCallersOnly]` callbacks must never throw (catch, `Report`, fall back), matching `RmlCore.cs` callbacks.
- Gates: `just build`, Release build, `just test`, `just test-render` (editor goldens), `just format-check`,
  `just template-smoke`.

## Review Focus

- **Paths with spaces / non-ASCII (`~/My Games/Café`):** UTF-8 through the callback and RmlUi's texture cache. Pinned by
  Task 1's `JoinKeepsExistingAbsolutePathsVerbatim` (uses a space and `é`).
- **Content-root paths that also look absolute (`/Content/UI/x.png`):** must still resolve against the content root, not
  the filesystem root. Pinned by Task 1's `LeadingSlashContentPathsStayContentRelative`.
- **Icon pointing outside the project (`../../etc/x.png`) or not a PNG:** ignored, glyph shown. Pinned by Task 3 tests.
- **Icon replaced on disk while the Project Manager is open/reopened:** the new image shows (texture released). Pinned
  by Task 3's `ChangedIconIsReportedOnce`.
- **Malformed or newer-format `project.mfproj` in the recent list:** no crash, glyph shown. Pinned by Task 3's
  `MalformedProjectFileHasNoIcon`.

---

### Task 1: `JoinPath` — absolute image paths survive RmlUi

**Files:**
- Create: `MainframeEngine/Src/UI/Rml/RmlPaths.cs`
- Modify: `MainframeEngine/Src/UI/Rml/RmlCore.cs` (system callbacks initializer ~lines 133-145; add `SysJoinPath`
  next to the other `[UnmanagedCallersOnly]` callbacks ~357-430)
- Test: `Tests/MainframeEngine.Tests/UI/RmlPathsTests.cs`

**Interfaces:**
- Produces: `public static class RmlPaths { public static string Join(string documentPath, string path); }`

Behaviour (RmlUi's `SystemInterface::JoinPath`, `Native/RmlUi/external/RmlUi/Source/Core/SystemInterface.cpp:51-84`,
plus one rule):
1. `path` starts with `/` or `\` **and** `File.Exists(path)` → return `path` unchanged (new).
2. `path` starts with `/` → return `path[1..]` (content-root path; `UiFileInterface.ResolvePath` resolves it).
3. `path` has `:` before the first `/` or `\` (Windows drive, `engine://`, `file:`) → return `path` unchanged.
4. Otherwise: directory of `documentPath` (up to and including its last `/`) + `path` with `\` → `/`, then collapse
   `./` and `dir/../` segments.

- [ ] **Step 1: Failing tests**

```csharp
namespace MainframeEngine.Tests.UI;

public sealed class RmlPathsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf rml é").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void JoinKeepsExistingAbsolutePathsVerbatim()
    {
        var icon = Path.Combine(_directory, "icon.png");
        File.WriteAllBytes(icon, [1]);
        // Unix: rooted + exists (rule 1). Windows: drive path (rule 3).
        Assert.Equal(icon, RmlPaths.Join("Content/UI/doc.rml", icon));
    }

    [Fact]
    public void LeadingSlashContentPathsStayContentRelative() =>
        Assert.Equal("Content/Brand/logo-256.png", RmlPaths.Join("Content/Editor/project_manager.rml", "/Content/Brand/logo-256.png"));

    [Fact]
    public void MissingAbsolutePathsFallBackToRmlUisRule() =>
        Assert.Equal("nope/x.png", RmlPaths.Join("Content/UI/doc.rml", "/nope/x.png"));

    [Theory]
    [InlineData("engine://viewport")]
    [InlineData("C:/Games/icon.png")]
    [InlineData("file:/x.png")]
    public void SchemesAndDrivesPassThrough(string path) => Assert.Equal(path, RmlPaths.Join("Content/UI/doc.rml", path));

    [Theory]
    [InlineData("Content/UI/doc.rml", "img/a.png", "Content/UI/img/a.png")]
    [InlineData("Content/UI/doc.rml", "../Brand/a.png", "Content/Brand/a.png")]
    [InlineData("Content/UI/doc.rml", "./a.png", "Content/UI/a.png")]
    [InlineData("doc.rml", "a\\b.png", "a/b.png")]
    public void RelativePathsJoinTheDocumentFolder(string document, string path, string expected) =>
        Assert.Equal(expected, RmlPaths.Join(document, path));
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter RmlPathsTests` — Expected: FAIL (type missing).

- [ ] **Step 2: Implement `RmlPaths`**

```csharp
namespace MainframeEngine.UI.Rml;

/// <summary>
/// RmlUi's path joining (<c>SystemInterface::JoinPath</c>) with one change: an absolute path to an existing file stays
/// absolute (RmlUi strips the leading <c>/</c>, which breaks absolute image paths on macOS and Linux).
/// </summary>
public static class RmlPaths
{
    public static string Join(string documentPath, string path)
    {
        ArgumentNullException.ThrowIfNull(documentPath);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return path;
        if ((path[0] == '/' || path[0] == '\\') && File.Exists(path))
            return path;
        if (path[0] == '/')
            return path[1..];
        var colon = path.IndexOf(':');
        var slash = path.IndexOfAny(['/', '\\']);
        if (colon >= 0 && (slash < 0 || colon < slash))
            return path;

        var lastSlash = documentPath.Replace('\\', '/').LastIndexOf('/');
        var folder = lastSlash >= 0 ? documentPath[..(lastSlash + 1)].Replace('\\', '/') : "";
        return Normalize(folder + path.Replace('\\', '/'));
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "." or "")
                continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
                parts.RemoveAt(parts.Count - 1);
            else
                parts.Add(part);
        }

        return string.Join('/', parts);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter RmlPathsTests` — Expected: PASS. (Put the class in the namespace
the other `Rml` types use; add the `using` in the test.)

- [ ] **Step 3: Assign the callback**

In `RmlCore.cs`, in the `SystemCallbacks` initializer add `JoinPath = &SysJoinPath,` and:

```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
private static void SysJoinPath(nint user, byte* documentPath, byte* path, nint output)
{
    string? joined = null;
    try
    {
        joined = RmlPaths.Join(RmlUtf8.ToString(documentPath), RmlUtf8.ToString(path));
    }
    catch (Exception e)
    {
        Report(e, "JoinPath");
    }

    new RmlStringSink(output).Set((joined ?? RmlUtf8.ToString(path)).AsSpan());
}
```

(Use the same `RmlUtf8`/`RmlStringSink` helpers `SysGetClipboard`/`SysTranslate` use; the callback runs at document
and image load, never per frame.)

- [ ] **Step 4: End-to-end check — an absolute `<img>` reaches the texture loader intact**

Add to `Tests/MainframeEngine.Tests/UI/RmlBindingTests.cs` (or the test file that already drives documents through
`RecordingRenderInterface`):

```csharp
[Fact]
public void AbsoluteImageSourcesReachTheRendererUnchanged()
{
    var directory = Directory.CreateTempSubdirectory("mf-abs-img").FullName;
    try
    {
        var png = Path.Combine(directory, "icon.png");
        File.WriteAllBytes(png, TestPng.OnePixel); // a valid 1×1 PNG (use the helper the image tests use)
        using var host = new RmlTestHost();
        host.LoadDocument($"<rml><body><img src=\"{png.Replace('\\', '/')}\"/></body></rml>");
        host.Update();
        Assert.Contains(host.Render.LoadedTextures, t => t.Replace('\\', '/') == png.Replace('\\', '/'));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}
```

(Match `RmlTestHost`'s real API for loading a document and its `RecordingRenderInterface`.)

Run: `dotnet test Tests/MainframeEngine.Tests --filter AbsoluteImageSources` — Expected: PASS (it fails with the
callback commented out — check once).

- [ ] **Step 5: FileSystem thumbnails, commit**

Run the editor on a project with PNGs, switch the FileSystem panel to grid view: thumbnails now show on macOS.

```bash
git add MainframeEngine/Src/UI/Rml Tests/MainframeEngine.Tests/UI
git commit -m "UI: JoinPath keeps absolute paths to existing files (absolute <img> on macOS/Linux)"
```

---

### Task 2: Default icon in the `mfgame` template

**Files:**
- Create: `Templates/MainframeEngine.Templates/content/mfgame/Content/icon.png` (copy of `MainframeEngine/Content/Brand/logo-256.png`)
- Modify: `Templates/MainframeEngine.Templates/content/mfgame/project.mfproj` (`window.icon`)
- Modify: `MainframeEngine.Editor/Src/Projects/ProjectCreator.cs` (`Verify`, ~lines 219-237)
- Modify: `build/template-smoke.sh` (expected files list, ~lines 26-32)
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectCreatorTests.cs`

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public void VerifyRejectsAProjectWhoseIconIsMissing()
{
    var target = Path.Combine(_directory, "Game");
    Directory.CreateDirectory(Path.Combine(target, "Content", "Scenes"));
    File.WriteAllText(Path.Combine(target, "Content", "Scenes", "Main.mscene"), "{}");
    File.WriteAllText(Path.Combine(target, ProjectSettings.FileName),
        """{ "format": 1, "name": "Game", "window": { "icon": "Content/icon.png" } }""");

    Assert.Contains("icon", ProjectCreator.Verify(target), StringComparison.OrdinalIgnoreCase);
}
```

(`Verify` is `private static`; make it `internal static` — the editor test project already sees editor internals if
other tests use internal members; otherwise add `InternalsVisibleTo`.)

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter VerifyRejectsAProjectWhoseIconIsMissing` — Expected: FAIL.

- [ ] **Step 2: Implement**

In `Verify`, after `ProjectSettings.Load(projectFile)` succeeds (keep the loaded settings in a local):

```csharp
if (settings.Window.Icon is { Length: > 0 } icon && !File.Exists(Path.Combine(target, icon)))
    return $"The new project's icon '{icon}' is missing.";
```

Template `project.mfproj` window block:

```json
"window": { "width": 1280, "height": 720, "vsync": true, "icon": "Content/icon.png" },
```

`cp MainframeEngine/Content/Brand/logo-256.png Templates/MainframeEngine.Templates/content/mfgame/Content/icon.png`.
`build/template-smoke.sh`: add `Content/icon.png` to the list of files that must exist in the build output.

- [ ] **Step 3: Run tests and the smoke**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter ProjectCreatorTests && just template-smoke`
Expected: PASS; the smoke game window uses the logo (screenshot in `artifacts/template-smoke`).

- [ ] **Step 4: Commit**

```bash
git add Templates MainframeEngine.Editor/Src/Projects/ProjectCreator.cs build/template-smoke.sh Tests/MainframeEngine.Editor.Tests/Projects
git commit -m "Template: default project icon (window.icon = Content/icon.png)"
```

---

### Task 3: `ProjectIconResolver`

**Files:**
- Create: `MainframeEngine.Editor/Src/Projects/ProjectIconResolver.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectIconResolverTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct ProjectIcon(string? Path, bool Changed);
  public sealed class ProjectIconResolver
  {
      public ProjectIcon Resolve(string projectDirectory); // Path: absolute existing PNG inside the project, else null.
                                                           // Changed: the icon file is new or modified since the last call.
  }
  ```

- [ ] **Step 1: Failing tests**

```csharp
namespace MainframeEngine.Editor.Tests.Projects;

public sealed class ProjectIconResolverTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-icons").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Project(string? icon, bool writeIcon = true, string? json = null)
    {
        var root = Directory.CreateTempSubdirectory(Path.Combine(_directory, "p")).FullName;
        var window = icon is null ? "" : $", \"window\": {{ \"icon\": \"{icon}\" }}";
        File.WriteAllText(Path.Combine(root, ProjectSettings.FileName), json ?? $"{{ \"format\": 1, \"name\": \"P\"{window} }}");
        if (icon is not null && writeIcon && !icon.Contains(".."))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, icon))!);
            File.WriteAllBytes(Path.Combine(root, icon), [137, 80, 78, 71]);
        }

        return root;
    }

    [Fact]
    public void IconInsideTheProjectResolvesToAnAbsolutePath()
    {
        var root = Project("Content/icon.png");
        var icon = new ProjectIconResolver().Resolve(root);
        Assert.Equal(Path.Combine(root, "Content", "icon.png"), icon.Path);
    }

    [Fact]
    public void NoIconSettingMeansNoIcon() => Assert.Null(new ProjectIconResolver().Resolve(Project(null)).Path);

    [Fact]
    public void MissingIconFileMeansNoIcon() => Assert.Null(new ProjectIconResolver().Resolve(Project("Content/icon.png", writeIcon: false)).Path);

    [Fact]
    public void NonPngIconIsIgnored() => Assert.Null(new ProjectIconResolver().Resolve(Project("Content/icon.jpg")).Path);

    [Fact]
    public void IconOutsideTheProjectIsIgnored()
    {
        File.WriteAllBytes(Path.Combine(_directory, "outside.png"), [1]);
        Assert.Null(new ProjectIconResolver().Resolve(Project("../outside.png")).Path);
    }

    [Fact]
    public void MalformedProjectFileHasNoIcon() =>
        Assert.Null(new ProjectIconResolver().Resolve(Project(null, json: "{ not json")).Path);

    [Fact]
    public void ChangedIconIsReportedOnce()
    {
        var root = Project("Content/icon.png");
        var resolver = new ProjectIconResolver();
        Assert.True(resolver.Resolve(root).Changed);   // first sight
        Assert.False(resolver.Resolve(root).Changed);
        File.SetLastWriteTimeUtc(Path.Combine(root, "Content", "icon.png"), DateTime.UtcNow.AddMinutes(1));
        Assert.True(resolver.Resolve(root).Changed);
        Assert.False(resolver.Resolve(root).Changed);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter ProjectIconResolverTests` — Expected: FAIL.

- [ ] **Step 2: Implement**

```csharp
namespace MainframeEngine.Editor;

/// <summary>A project's Project Manager icon: an absolute PNG path (or null) and whether the file changed since last asked.</summary>
public readonly record struct ProjectIcon(string? Path, bool Changed);

/// <summary>
/// Resolves <c>window.icon</c> of a project (its icon, as in Godot) to an existing PNG inside the project folder, caching
/// by the modification times of <c>project.mfproj</c> and the icon. Never throws for a bad project.
/// </summary>
public sealed class ProjectIconResolver
{
    private readonly Dictionary<string, (DateTime ProjectWrite, string? Icon, DateTime IconWrite)> _cache = new(StringComparer.Ordinal);

    public ProjectIcon Resolve(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var projectFile = GameProjectLayout.ProjectFileOf(root);
        var projectWrite = File.Exists(projectFile) ? File.GetLastWriteTimeUtc(projectFile) : DateTime.MinValue;
        _cache.TryGetValue(root, out var cached);
        var icon = cached.ProjectWrite == projectWrite && cached.ProjectWrite != default ? cached.Icon : ReadIcon(root, projectFile);
        var iconWrite = icon is not null && File.Exists(icon) ? File.GetLastWriteTimeUtc(icon) : DateTime.MinValue;
        if (icon is not null && iconWrite == DateTime.MinValue)
            icon = null;
        var changed = icon is not null && (cached.Icon != icon || cached.IconWrite != iconWrite);
        _cache[root] = (projectWrite, icon, iconWrite);
        return new ProjectIcon(icon, changed);
    }

    private static string? ReadIcon(string root, string projectFile)
    {
        try
        {
            if (ProjectSettings.Load(projectFile).Window.Icon is not { Length: > 0 } relative)
                return null;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var inside = full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            return inside && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter ProjectIconResolverTests` — Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add MainframeEngine.Editor/Src/Projects/ProjectIconResolver.cs Tests/MainframeEngine.Editor.Tests/Projects/ProjectIconResolverTests.cs
git commit -m "Editor: ProjectIconResolver (window.icon, cached, safe)"
```

---

### Task 4: Icons in the Project Manager rows

**Files:**
- Modify: `MainframeEngine.Editor/Src/UI/ProjectManager.cs` (row model `:14-30`, `Rebuild()` `:120-148`, field for the resolver)
- Modify: `MainframeEngine.Editor/Content/Editor/project_manager.rml` (row template `:108-122`, row CSS ~`:48-55`)
- Test: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs` (Project Manager test `:133-163`)
- Golden: `ProjectManagerMatchesGolden` (`Tests/MainframeEngine.RenderTests/EditorRenderTests.cs:40-43`) if its fixture
  has projects with icons

**Interfaces:**
- Consumes: `ProjectIconResolver` (Task 3), `RmlPaths` fix (Task 1).
- Produces: row members `icon` (string, absolute path or "") and `has_icon` (bool); `ProjectManager.VisibleProjects`
  rows expose `Icon` for tests.

- [ ] **Step 1: Failing test**

In `ProjectWorkflowTests`, extend the Project Manager test (or add a sibling):

```csharp
[Fact]
public void ProjectManagerShowsTheProjectIcon()
{
    var icon = Path.Combine(_project.Root, "Content", "icon.png");
    Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
    File.Copy(Path.Combine(RenderTestPaths.RepositoryRoot, "MainframeEngine", "Content", "Brand", "logo-48.png"), icon);
    _project.Settings.Window.Icon = "Content/icon.png";
    _project.Settings.Save(_project.Root);

    _editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true, GameBuilder = _builder, GameLauncher = _launcher });
    var w = _editor.Workspace;
    w.RecentProjects.Touch(_project.Root, _project.Name);
    w.ProjectManager.Open();
    _editor.Tick(3);

    Assert.Equal(icon, w.ProjectManager.VisibleProjects.Single(p => p.Name == _project.Name).Icon);
    Assert.Empty(_editor.RmlMessages); // the image loaded (no "Image ... not found")
}
```

(Use the repository-root helper the editor tests already have, and `TestProject`'s real member names.)

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter ProjectManagerShowsTheProjectIcon` — Expected: FAIL.

- [ ] **Step 2: Row model + rebuild**

In `ProjectManager`:

```csharp
private readonly ProjectIconResolver _icons = new();

private sealed class Row
{
    // existing members…
    public string Icon { get; init; } = "";
}

private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
    .Member("name", static r => r.Project.Name)
    .Member("folder", static r => r.Folder)
    .Member("when", static r => r.When)
    .Member("valid", static r => r.Valid)
    .Member("index", static r => r.Index)
    .Member("selected", static r => r.Selected)
    .Member("icon", static r => r.Icon)
    .Member("has_icon", static r => r.Icon.Length > 0);
```

In `Rebuild()`, for each project: `var icon = valid ? _icons.Resolve(project.Path) : default;` set
`Icon = icon.Path ?? ""`, and track `anyChanged |= icon.Changed`. After the loop, before `_model?.DirtyAll()`:
`if (anyChanged) RmlCore.ReleaseTextures();` (RmlUi caches textures by source; releasing re-creates them on demand —
the Project Manager is the only document visible). Expose `Icon` through `VisibleProjects` (it returns row data or
`RecentProject` — add an `Icon` to what it returns).

- [ ] **Step 3: Markup**

Row template — replace the single glyph `<span>` with:

```xml
<img class="pm-icon" data-if="p.has_icon" data-attr-src="p.icon"/>
<span data-if="!p.has_icon" data-attr-class="p.valid ? 'icon icon-lg icon-folder-code' : 'icon icon-lg icon-folder-x'"></span>
```

CSS (in the document's `<style>`):

```css
.pm-row .pm-icon { width: 36dp; height: 36dp; margin-right: 12dp; border-radius: 6dp; }
.pm-row.missing .pm-icon { opacity: 0.4; }
```

- [ ] **Step 4: Tests and goldens**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "ProjectWorkflowTests|ProjectIconResolverTests"` — Expected: PASS.
Run: `caffeinate -u -t 1200 & dotnet test Tests/MainframeEngine.RenderTests --filter EditorRenderTests` — if
`ProjectManagerMatchesGolden` differs because its rows now show icons, inspect the output PNG, re-record with
`UPDATE_GOLDENS=1` (both drivers; lavapipe via `just render-tests-linux`), and inspect again.

- [ ] **Step 5: Project Settings preview**

In the Project Settings document, the Window → Icon row (a `SettingKind.File` setting defined in
`MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs:109-110`): add a 32 dp `<img>` after the value field,
bound to the icon's absolute path (`Path.Combine(ProjectDirectory, value)` when the file exists, else hidden). Follow
how that RML template renders other per-kind extras; the binding goes through the same row struct type, as a
`preview` member that is empty for non-PNG file settings.

- [ ] **Step 6: Docs, commit**

`docs/design/editor.md#projects`: rows show `window.icon`; template default; `ProjectIconResolver` rules.
`docs/design/game-ui.md`: absolute `src` paths are supported (JoinPath rule). Mark D3 ✅ in `docs/milestones.md` and the
spec status ✅.

```bash
git add MainframeEngine.Editor Tests/MainframeEngine.Editor.Tests Tests/MainframeEngine.RenderTests/Goldens docs
git commit -m "Editor: project icons in the Project Manager and a preview in Project Settings"
```

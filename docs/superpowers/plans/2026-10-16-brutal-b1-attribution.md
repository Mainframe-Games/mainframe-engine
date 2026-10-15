# BRUTAL B1: Plumbing and Attribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make BRUTAL a pinned dependency of the `MainframeEngine` package and make every game compliant with the BRUTAL licence by default: `BRUTAL-NOTICE.txt` next to the game, BRUTAL's copyright on a `Credits` page, and a locked `@brutal` card in a configurable splash sequence, plus the editor's splash, About and Project Settings → Splash page.
**Architecture:** Packages are pinned exactly in `Directory.Packages.props` and referenced by `MainframeEngine.csproj` without analyzers; the notice reaches games through the engine project's output items (checkout games) and `buildTransitive/MainframeEngine.targets` (package games). A static `Credits` registry is seeded from the engine's own entries and `THIRD_PARTY_NOTICES.md` (embedded at build); the `splash` section of `project.mfproj` (`SplashProjectSettings`, format 4) drives `SplashSequence`, an engine `UiLayer` whose timing is a pure `SplashTimeline` state machine, started by `GameHost` above the asynchronous start-scene load.
**Tech Stack:** C# / .NET 10, xUnit v3, RmlUi (RML/RCSS through `UiLayer`/`UiDocument`), StbImageSharp (`ImageInfo`), MSBuild/NuGet central package management, bash (`build/*.sh`), Astro Starlight (docs site).
**Spec:** docs/design/future/brutal-backend.md (B1) · ADR 0192

## Global Constraints

- Branch `brutal/b1-attribution` off `main`; `main` is PR-only, `ci-success` must pass, linear history (squash or rebase merges, never merge commits), no force pushes.
- Commits stay local until Task 16 (Actions minutes): exactly one push, then a PR with Auto-fix enabled.
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Exact pins in `Directory.Packages.props`: `Ahwoo.BRUTAL.Core` `[2026.10.1]`, `Ahwoo.BRUTAL.Interop` `[2026.10.1]`, `Ahwoo.BRUTAL.Logging` `[2026.10.2]`, `Ahwoo.BRUTAL.Memory` `[2026.10.1]`, `Ahwoo.BRUTAL.Numerics` `[2026.10.1]`, `Ahwoo.BRUTAL.Strings` `[2026.10.1]`, `Ahwoo.BRUTAL.Jolt` `[2026.10.2]`, `CommunityToolkit.HighPerformance` `[8.4.2]`, `Microsoft.Extensions.Logging` `[10.0.0]`, `Microsoft.Extensions.Logging.Console` `[10.0.0]`. Never put `Version` on a `PackageReference`.
- BRUTAL references use `ExcludeAssets="analyzers"` (their analyzers and Interop's generator stay off).
- 0 warnings: warnings are errors (`just build`; Release: `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`).
- Gates before committing anything substantial: `just build`, the Release build above, `just test`, `just test-render`, `just format-check`, `just shaders-check`; plus `just template-smoke` for packaging tasks.
- Per-frame code must not allocate in steady state (allocation gate) and must produce no validation warnings; the splash only touches the DOM when its phase, card or layout changes.
- `ProjectSettingsFormat.Current` goes from 3 to 4 with one new `ProjectMigration(3, …)`; shipped steps are never edited. If B2 merged first, B1's step is `From = 4` and `Current` becomes 5.
- `splash` defaults: `cards` `["@mainframe", "@brutal"]`, `background` `"#000000"`, `holdSeconds` `2.0`, `skippable` `true`, `nonProfit` `false`.
- `holdSeconds` is clamped to 1.5–10 (s). Fades are 0.3 s in and 0.3 s out. No card can be skipped before 1.5 s of its hold. A frame's delta counts at most 0.1 s in the splash clock.
- Every card's image fits a square box of 40 % of the shorter screen side, aspect kept, centred.
- `SplashSequence.Layer = int.MaxValue - 1` (below the dev overlay's `int.MaxValue`, above the loading screen's 10 000).
- BRUTAL logo: white (`LOGO_BRUTAL_WHT.png`) when the background's relative luminance (sRGB → linear) is below 0.5, black (`LOGO_BRUTAL_BLK.png`) otherwise; files copied byte-for-byte from BRUTAL's `docs/branding/`.
- BRUTAL copyright line, verbatim: `BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.`
- The splash does not run with `--headless`, `--editor-port`, `--scene`, `--max-frames` together with `--hidden`, or `--no-splash`; `--qa-splash` runs only the sequence.
- Load content with `ContentPaths.Resolve` (or the UI file interface, which falls back to it), never a working-directory-relative path.
- Editor UI: icon buttons with tooltips (name and what it does), text only for labels and inputs.
- Every feature goes on the README, the docs-site landing page and the docs; editor features with real screenshots (`just readme-screenshots`).
- Render tests: inspect every re-recorded golden PNG before committing it; moltenvk via `just golden-update`, lavapipe via `just render-tests-linux` (keep the display awake: `caffeinate -u -t 600 &`).

## Review Focus

1. **A game card whose image does not exist or is not a PNG** (`"Content/Brand/studio.png"` missing, a JPEG renamed): the card is skipped with one `[Splash]` warning naming it, every other card plays, the sequence still ends; the file loader rejects non-`Content/…/*.png` entries up front with an error naming `splash.cards[i]`. A missing built-in image logs an error (the engine's content is incomplete) — Task 9 `AMissingOrNonPngGameCardIsSkippedWithAWarning`, Task 7 `InvalidEntriesNameTheirIndex`.
2. **A window resize mid-splash**: the next frame re-fits the current card into 40 % of the new shorter side and re-centres it; later cards use the new size — Task 9 `AResizeMidSplashRefitsTheCard`.
3. **A main scene that loads in under a frame, or a frame hitch of several seconds**: a completed load shortens nothing (every card gets its fades and full hold); a hitch counts as at most 0.1 s, so no card (in particular `@brutal`) can be jumped over — Task 8 `ALoadThatFinishedBeforeTheFirstCardShortensNothing`, `AFrameHitchCannotJumpOverACard`, Task 9 `AFastLoadStillPlaysEveryCardThenTheSplashFreesItself`.
4. **Input held down from launch, and key auto-repeat**: a key, mouse button or gamepad button already down when the splash starts, or repeated key-down events, never skip; only a fresh press (down after up) during a hold that has run 1.5 s skips; every event is consumed (the game sees none) — Task 9 `AKeyHeldFromLaunchOrItsRepeatsNeverSkip`, `AKeyPressSkipsAHoldOnlyAfterOneAndAHalfSecondsAndTheGameNeverSeesIt`.
5. **`cards` containing `@brutal` twice (or any duplicate), or no `@brutal`**: the resolved list keeps the first of each entry; a missing `@brutal` is inserted after `@mainframe` (or first) unless `nonProfit`; `ProjectSettings.Load` logs one warning per problem per load, `Parse` (editor clones) logs none, the saver writes the user's list unchanged — Task 7 `DuplicateCardsShowOnceAndWarn`, `TheBrutalLockInsertsTheCardAndLoadWarnsOnce`.

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `Directory.Packages.props` | Modify | Exact pins of the BRUTAL packages and their third-party dependencies |
| `MainframeEngine/MainframeEngine.csproj` | Modify | BRUTAL references (no analyzers), `BRUTAL-NOTICE.txt` output item and pack entry, `THIRD_PARTY_NOTICES.md` embedded resource |
| `build/package/MainframeEngine.targets` | Modify | Package games: copy `mainframe/BRUTAL-NOTICE.txt` next to the game (build and publish) |
| `build/template-smoke.sh` | Modify | Checks `BRUTAL-NOTICE.txt` (build, publish, archives), no splash on hidden/headless runs, `--qa-splash-out` writes one PNG per card |
| `build/package-editor.sh` | Modify | Strip `libjolt.so` in Linux editor releases |
| `build/brand/brutal-logos.sha256` | Create | SHA-256 of the four BRUTAL logo copies (byte-for-byte guard) |
| `MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_WHT.png`, `LOGO_BRUTAL_BLK.png` | Create | BRUTAL's logos (engine content, in every game) |
| `docs/images/brand/brutal/LOGO_BRUTAL_WHT.png`, `LOGO_BRUTAL_BLK.png` | Create | The same files for the README and the docs site |
| `THIRD_PARTY_NOTICES.md` | Modify | Rows for BRUTAL, Jolt, CommunityToolkit.HighPerformance, Microsoft.Extensions.*; BRUTAL and Jolt sections |
| `Templates/MainframeEngine.Templates/content/mfgame/README.md` | Create | Tells new games what they inherit |
| `MainframeEngine/PACKAGE.md` | Modify | The package README's licence paragraph |
| `MainframeEngine/Src/Project/ThirdPartyNotices.cs` | Create | Parses the embedded notices table into rows |
| `MainframeEngine/Src/Project/Credits.cs` | Create | `Credits` registry and `CreditEntry` |
| `MainframeEngine/Src/UI/Settings/SettingsMenu.cs` | Modify | Credits tab |
| `MainframeEngine/Content/UI/settings/settings.rml`, `settings.rcss`, `settings.pot` | Modify | Credits tab markup, style, extracted strings |
| `MainframeEngine/Content/UI/credits.rml` | Modify | The static credits document gains BRUTAL and Jolt |
| `MainframeEngine/Src/Project/Splash/SplashProjectSettings.cs` | Create | The `splash` section: values, validation, the `@brutal` lock, warnings |
| `MainframeEngine/Src/Project/ProjectSettings.cs` | Modify | `Splash` property; `Load` logs the splash warnings |
| `MainframeEngine/Src/Project/ProjectSettingsFormat.cs` | Modify | Format 4, migration 3 → 4, read/write `splash` |
| `MainframeEngine/Src/Project/Splash/SplashTimeline.cs` | Create | Pure fade/hold/skip/wait state machine |
| `MainframeEngine/Src/Project/Splash/SplashSequence.cs` | Create | The splash `UiLayer` node: cards, layout, input, load, events |
| `MainframeEngine/Content/UI/splash/splash.rml`, `splash.rcss` | Create | The splash document |
| `MainframeEngine/Content/UI/widgets/widgets.rcss` | Modify | `.spinner` widget |
| `MainframeEngine/Src/Project/GameHostOptions.cs` | Modify | `--no-splash`, `--qa-splash`, `--qa-splash-out` |
| `MainframeEngine/Src/Project/GameHost.cs` | Modify | Splash decision, start ordering, QA captures |
| `Tests/MainframeEngine.RenderTests.Host/Scenes/GameHostRun.cs` | Modify | `--no-splash` (the allocation-gate GameHost) |
| `build/forest-record.sh`, `build/forest-screenshots.sh` | Modify | `--no-splash` so captures start at the game |
| `Tests/MainframeEngine.RenderTests.Host/Scenes/SplashScene.cs`, `Program.cs` | Create/Modify | Render-test scene `splash` |
| `Tests/MainframeEngine.RenderTests/UiRenderTests.cs` | Modify | `splash` goldens test |
| `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/splash_frame00{63,189,315}.png` | Create | Goldens |
| `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/ui-settings-menu_frame0030.png`, `editor-splash_frame0020.png` | Modify | Re-recorded (Credits tab, editor splash) |
| `MainframeEngine.Editor/Src/UI/SplashScreen.cs`, `Content/Editor/splash.rml` | Modify | BRUTAL logo beside the Mainframe logo, equal boxes |
| `MainframeEngine.Editor/Src/UI/MessageDialog.cs`, `Content/Editor/message.rml` | Modify | `MessageRequest.Scrollable` |
| `MainframeEngine.Editor/Src/EditorCommands.cs` | Modify | About lists the credits |
| `MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs` | Modify | Splash section, settings and card operations |
| `MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs`, `Content/Editor/project_settings.rml` | Modify | Splash page, nav ids, preview |
| `Tests/QA/editor-walkthrough.qa`, `Tests/QA/readme-screenshots.qa`, `justfile` | Modify | QA steps; README screenshot of the Splash page |
| Tests (new): `Tests/MainframeEngine.Tests/Project/{BrutalPackagesTests,BrutalNoticeTests,BrutalLogoTests,CreditsTests,SplashSettingsTests,SplashTimelineTests}.cs`, `Tests/MainframeEngine.Tests/UI/SplashSequenceTests.cs`, `Tests/MainframeEngine.Editor.Tests/{SplashScreenTests,AboutCreditsTests}.cs` | Create | Unit tests |
| Tests (modified): `Tests/MainframeEngine.Tests/Project/{ProjectSettingsTests,GameHostTests,GameSessionTests}.cs`, `Tests/MainframeEngine.Tests/UI/{SettingsMenuTests,UiServerTests}.cs`, `Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs` | Modify | Updated and new cases |
| Docs: `docs/design/{project-and-gamehost,game-ui,editor,distribution-nuget,release}.md`, `docs/design/future/brutal-backend.md`, `docs/milestones.md`, `memory/decisions/0192-brutal-backend-tech.md`, `memory/context/{current-state,progress-log}.md`, `README.md`, `CLAUDE.md`, `docs-site/src/components/landing/{LandingHero,Features}.astro`, `docs/images/splash/*.png`, `docs/images/editor-splash-settings.png` | Modify/Create | Docs, site, README, conventions |

---

### Task 1: Pin the BRUTAL packages

**Files:**
- Modify: `Directory.Packages.props` (the "Engine" `ItemGroup`, lines 13–32)
- Modify: `MainframeEngine/MainframeEngine.csproj` (the "NuGet Packages" `ItemGroup`, lines 33–54)
- Modify: `memory/decisions/0192-brutal-backend-tech.md` (Decision, "Approved new dependencies", lines 36–38)
- Test: `Tests/MainframeEngine.Tests/Project/BrutalPackagesTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: the `Ahwoo.BRUTAL.*` assemblies and natives in every engine build; MSBuild property `$(PkgAhwoo_BRUTAL_Core)` (from `GeneratePathProperty`) for Task 2.

- [ ] **Step 1: Branch and record the transitive packages before the change**

```bash
cd /Users/brogan/mainframe/mainframe-engine
git switch main && git pull --ff-only
test -f docs/design/future/brutal-backend.md   # the BRUTAL design PR (spec, ADR 0192, these plans) must be merged first
git switch -c brutal/b1-attribution
mkdir -p artifacts/b1
dotnet list MainframeEngine/MainframeEngine.csproj package --include-transitive > artifacts/b1/packages-before.txt
```

(If the `test -f` fails, the design PR has not merged yet: stop and ask the user. `main` is PR-only, so the spec never lands by a direct commit.)

- [ ] **Step 2: Write the failing test**

Create `Tests/MainframeEngine.Tests/Project/BrutalPackagesTests.cs`:

```csharp
using System.Xml.Linq;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL B1 (ADR 0192): every Ahwoo.BRUTAL package and its third-party dependencies are pinned exactly (upstream ranges are
/// minimum-only and the API is experimental), the engine references each BRUTAL package without its analyzers, and the
/// pinned versions are what the build resolved.
/// </summary>
public sealed class BrutalPackagesTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Ahwoo.BRUTAL.Core"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Interop"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Logging"] = "[2026.10.2]",
        ["Ahwoo.BRUTAL.Memory"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Numerics"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Strings"] = "[2026.10.1]",
        ["Ahwoo.BRUTAL.Jolt"] = "[2026.10.2]",
        ["CommunityToolkit.HighPerformance"] = "[8.4.2]",
        ["Microsoft.Extensions.Logging"] = "[10.0.0]",
        ["Microsoft.Extensions.Logging.Console"] = "[10.0.0]",
    };

    [Fact]
    public void EveryBrutalPackageAndItsThirdPartyDependenciesArePinnedExactly()
    {
        var props = XDocument.Load(Path.Combine(TestPaths.RepositoryRoot(), "Directory.Packages.props"));
        var versions = props.Descendants("PackageVersion").ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);
        foreach (var (id, version) in Expected)
            Assert.True(versions.TryGetValue(id, out var pinned) && pinned == version, $"{id} must be pinned to {version} (found {pinned ?? "nothing"}).");
        Assert.All(versions.Where(v => v.Key.StartsWith("Ahwoo.BRUTAL.", StringComparison.Ordinal)),
            v => Assert.Matches(@"^\[\d{4}\.\d{1,2}\.\d+\]$", v.Value));
    }

    [Fact]
    public void TheEngineReferencesEachBrutalPackageWithoutItsAnalyzers()
    {
        var project = XDocument.Load(Path.Combine(TestPaths.RepositoryRoot(), "MainframeEngine", "MainframeEngine.csproj"));
        var references = project.Descendants("PackageReference")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string?)e.Attribute("ExcludeAssets"));
        foreach (var id in Expected.Keys)
            Assert.True(references.ContainsKey(id), $"MainframeEngine.csproj does not reference {id}.");
        foreach (var id in Expected.Keys.Where(k => k.StartsWith("Ahwoo.BRUTAL.", StringComparison.Ordinal)))
            Assert.Equal("analyzers", references[id]);
    }

    [Fact]
    public void TheBuildResolvedThePinnedVersions()
    {
        var deps = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainframeEngine.Tests.deps.json"));
        foreach (var (id, version) in Expected)
            Assert.Contains($"\"{id}/{version.Trim('[', ']')}\"", deps, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalPackagesTests"`
Expected: FAIL — `Ahwoo.BRUTAL.Core must be pinned to [2026.10.1] (found nothing).`, `MainframeEngine.csproj does not reference Ahwoo.BRUTAL.Core.`, and the deps.json assertion.

- [ ] **Step 4: Pin the packages**

In `Directory.Packages.props`, inside the "Engine" `ItemGroup`, after the `StbImageSharp` line (line 31), add:

```xml
    <!--
      BRUTAL (Ahwoo Limited, ADR 0192): pinned exactly, because upstream dependency ranges are minimum-only and the API is
      experimental. Upgrades are their own PRs with the full test run. Jolt is referenced from B1 so its natives, notices
      and package size are settled before B3 writes code.
    -->
    <PackageVersion Include="Ahwoo.BRUTAL.Core" Version="[2026.10.1]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Interop" Version="[2026.10.1]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Jolt" Version="[2026.10.2]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Logging" Version="[2026.10.2]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Memory" Version="[2026.10.1]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Numerics" Version="[2026.10.1]" />
    <PackageVersion Include="Ahwoo.BRUTAL.Strings" Version="[2026.10.1]" />
    <!-- BRUTAL's third-party dependencies (Ahwoo.BRUTAL.Core / Logging), pinned centrally (ADR 0192, THIRD_PARTY_NOTICES.md). -->
    <PackageVersion Include="CommunityToolkit.HighPerformance" Version="[8.4.2]" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="[10.0.0]" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Console" Version="[10.0.0]" />
```

In `MainframeEngine/MainframeEngine.csproj`, in the "NuGet Packages" `ItemGroup`, after `<PackageReference Include="StbImageSharp" />` (line 53), add:

```xml
        <!--
          BRUTAL (ADR 0192): a dependency of the engine package itself, so its natives reach games through
          runtimes/<rid>/native like Silk.NET's, and games inherit its licence (THIRD_PARTY_NOTICES.md#brutal). Its Roslyn
          analyzers and Interop's source generator stay off (ExcludeAssets, which the package's dependency edges keep), so
          the 0-warning build never depends on another project's rule set; B5 may turn them on deliberately.
          GeneratePathProperty: $(PkgAhwoo_BRUTAL_Core) is the package folder holding NOTICE.txt.
        -->
        <PackageReference Include="Ahwoo.BRUTAL.Core" ExcludeAssets="analyzers" GeneratePathProperty="true" />
        <PackageReference Include="Ahwoo.BRUTAL.Interop" ExcludeAssets="analyzers" />
        <PackageReference Include="Ahwoo.BRUTAL.Jolt" ExcludeAssets="analyzers" />
        <PackageReference Include="Ahwoo.BRUTAL.Logging" ExcludeAssets="analyzers" />
        <PackageReference Include="Ahwoo.BRUTAL.Memory" ExcludeAssets="analyzers" />
        <PackageReference Include="Ahwoo.BRUTAL.Numerics" ExcludeAssets="analyzers" />
        <PackageReference Include="Ahwoo.BRUTAL.Strings" ExcludeAssets="analyzers" />
        <!-- Referenced only to pin BRUTAL's third-party dependencies (transitive pinning is off for the solution). -->
        <PackageReference Include="CommunityToolkit.HighPerformance" />
        <PackageReference Include="Microsoft.Extensions.Logging" />
        <PackageReference Include="Microsoft.Extensions.Logging.Console" />
```

- [ ] **Step 5: Restore, diff the transitive set, build**

```bash
dotnet restore MainframeEngine.slnx
dotnet list MainframeEngine/MainframeEngine.csproj package --include-transitive > artifacts/b1/packages-after.txt
diff artifacts/b1/packages-before.txt artifacts/b1/packages-after.txt | grep '^>' | sort
just build
```

Expected: restore succeeds; the diff lists only `Ahwoo.BRUTAL.*`, `CommunityToolkit.HighPerformance` 8.4.2 and `Microsoft.Extensions.*` 10.0.0 packages (Logging, Logging.Console, and what they depend on: Logging.Abstractions, Logging.Configuration, DependencyInjection(.Abstractions), Options, Options.ConfigurationExtensions, Configuration(.Abstractions, .Binder), Primitives). Any other third-party id is a new dependency CLAUDE.md requires discussing: stop and ask the user. `just build`: 0 warnings, 0 errors.

- [ ] **Step 6: Record the approved set in ADR 0192**

In `memory/decisions/0192-brutal-backend-tech.md`, replace the "Approved new dependencies" bullet (lines 36–38) with:

```markdown
- **Approved new dependencies**: `Ahwoo.BRUTAL.Core`, `Interop`, `Logging`, `Memory`, `Numerics`, `Strings` (2026.10.1;
  Logging 2026.10.2) and `Jolt` (2026.10.2), pinned exactly; their third-party transitive packages
  `CommunityToolkit.HighPerformance` 8.4.2 and `Microsoft.Extensions.Logging` / `.Console` 10.0.0, pinned centrally,
  which bring the `Microsoft.Extensions.*` 10.0.0 packages they depend on (all MIT, Microsoft): <the Microsoft.Extensions
  ids the Step 5 diff printed, comma-separated, each in backticks>. Later phases add BRUTAL packages through their own specs.
```

Replace the angle-bracket sentence with the literal ids from the Step 5 output (for example `` `Microsoft.Extensions.Options` ``); the committed ADR contains no angle brackets.

- [ ] **Step 7: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalPackagesTests"`
Expected: PASS (3 tests).

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props MainframeEngine/MainframeEngine.csproj memory/decisions/0192-brutal-backend-tech.md Tests/MainframeEngine.Tests/Project/BrutalPackagesTests.cs
git commit -m "$(printf 'Pin the BRUTAL packages exactly and reference them without analyzers (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 2: BRUTAL's NOTICE next to every game, and the notices

**Files:**
- Modify: `MainframeEngine/MainframeEngine.csproj` (new `ItemGroup` + `Target` after the "Copy everything in content folder" group, lines 116–121; `_MainframePackContent` filter, line 214)
- Modify: `build/package/MainframeEngine.targets` (after the content `ItemGroup`, lines 25–28)
- Modify: `build/template-smoke.sh` (lines 89–90, 110–111, 139–149)
- Modify: `THIRD_PARTY_NOTICES.md` (table, lines 13–52; new sections appended at the end)
- Create: `Templates/MainframeEngine.Templates/content/mfgame/README.md`
- Modify: `MainframeEngine/PACKAGE.md`, `docs/design/distribution-nuget.md` ("Content and natives", line 37)
- Test: `Tests/MainframeEngine.Tests/Project/BrutalNoticeTests.cs` (create)

**Interfaces:**
- Consumes: `$(PkgAhwoo_BRUTAL_Core)` (Task 1).
- Produces: `<output>/BRUTAL-NOTICE.txt` for every app referencing the engine; package path `mainframe/BRUTAL-NOTICE.txt`; `THIRD_PARTY_NOTICES.md` rows named `BRUTAL`, `Jolt Physics`, `CommunityToolkit.HighPerformance`, `Microsoft.Extensions.Logging, .Console and the Microsoft.Extensions.* packages they depend on` (parsed by Task 5).

- [ ] **Step 1: Write the failing test**

Create `Tests/MainframeEngine.Tests/Project/BrutalNoticeTests.cs`:

```csharp
namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL licence condition 2.3 (ADR 0192): BRUTAL's NOTICE.txt lies alongside every product. The engine project copies it
/// next to every app that references it (this test assembly is one), as BRUTAL-NOTICE.txt.
/// </summary>
public sealed class BrutalNoticeTests
{
    [Fact]
    public void BrutalsNoticeIsCopiedNextToTheAppAsBrutalNotice()
    {
        var notice = Path.Combine(AppContext.BaseDirectory, "BRUTAL-NOTICE.txt");
        Assert.True(File.Exists(notice), $"{notice} is missing: MainframeEngine.csproj copies BRUTAL's NOTICE.txt next to every app.");
        Assert.Contains("BRUTAL", File.ReadAllText(notice), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalNoticeTests"`
Expected: FAIL — `…/BRUTAL-NOTICE.txt is missing`.

- [ ] **Step 3: Copy the notice from the engine project, and pack it**

Check the notice is where the spec says: `ls ~/.nuget/packages/ahwoo.brutal.core/2026.10.1/NOTICE.txt` (it must exist; the target below fails the build with `MFBRUTAL001` if a future version moves it).

In `MainframeEngine/MainframeEngine.csproj`, after the `ItemGroup` that copies `Content\**` (ends line 121), add:

```xml
    <!--
      BRUTAL licence 2.3 (ADR 0192): BRUTAL's NOTICE.txt (Ahwoo.BRUTAL.Core's package root) next to every app that
      references the engine, as BRUTAL-NOTICE.txt, in the build and publish output. Checkout games get it through this
      project reference like Content/ and steamworks/; package games from buildTransitive/MainframeEngine.targets (the
      package carries it as mainframe/BRUTAL-NOTICE.txt, see _MainframePackContent).
    -->
    <ItemGroup Condition="'$(PkgAhwoo_BRUTAL_Core)' != ''">
      <None Include="$(PkgAhwoo_BRUTAL_Core)/NOTICE.txt" Link="BRUTAL-NOTICE.txt" CopyToOutputDirectory="PreserveNewest" Visible="false" />
    </ItemGroup>
    <Target Name="_MainframeCheckBrutalNotice" BeforeTargets="Build" Condition="'$(PkgAhwoo_BRUTAL_Core)' != ''">
      <Error Condition="!Exists('$(PkgAhwoo_BRUTAL_Core)/NOTICE.txt')" Code="MFBRUTAL001"
             Text="Ahwoo.BRUTAL.Core has no NOTICE.txt at $(PkgAhwoo_BRUTAL_Core): games must ship BRUTAL's notice (the BRUTAL licence, 2.3). Find where this BRUTAL version keeps it and update MainframeEngine.csproj." />
    </Target>
```

In `_MainframePackContent`, replace the `_MainframeContentFile Remove` line (line 214) with:

```xml
        <_MainframeContentFile Remove="@(_MainframeContentFile)" Condition="!($([System.String]::Copy('%(_MainframeContentFile.PackagePath)').StartsWith('mainframe/Content/')) or $([System.String]::Copy('%(_MainframeContentFile.PackagePath)').StartsWith('mainframe/steamworks/')) or '%(_MainframeContentFile.PackagePath)' == 'mainframe/BRUTAL-NOTICE.txt') or '%(_MainframeContentFile.Extension)' == '.pot'" />
```

and add `mainframe/BRUTAL-NOTICE.txt   BRUTAL's NOTICE.txt (the BRUTAL licence, 2.3); buildTransitive/MainframeEngine.targets copies it next to the game` to the "Package contents" comment (lines 151–166).

- [ ] **Step 4: Copy it for package games**

In `build/package/MainframeEngine.targets`, add a bullet to the header comment after the "Steamworks" bullet (line 14):

```xml
    - BRUTAL's notice: mainframe/BRUTAL-NOTICE.txt next to the game executable (build and publish), as the BRUTAL licence
      (2.3, ADR 0192) requires of every product that includes BRUTAL.
```

and after the content `ItemGroup` (line 28):

```xml
  <ItemGroup Condition="'$(_MainframeEngineAppProject)' == 'true'">
    <None Include="$(_MainframeEnginePackageRoot)/mainframe/BRUTAL-NOTICE.txt" Link="BRUTAL-NOTICE.txt"
          CopyToOutputDirectory="PreserveNewest" Visible="false" />
  </ItemGroup>
```

- [ ] **Step 5: Check it in the template smoke test**

In `build/template-smoke.sh`:
- line 89: `engine_files=(Content/UI/widgets/widgets.rcss Content/Shaders/Mesh/Mesh.vk.vert.spv Content/locale/fr/LC_MESSAGES/messages.mo BRUTAL-NOTICE.txt)` (the build and the publish checks both use `engine_files`).
- In `check_archive` calls (lines 139–149) add `"+$base/BRUTAL-NOTICE.txt"` to the `linux-*` and `win-*` lists and `"+$macos/BRUTAL-NOTICE.txt"` to the `osx-*` list.
- Header comment (lines 5–6): "Fails on any build error, a non-zero exit code, missing engine content, catalogs or BRUTAL-NOTICE.txt, …".

- [ ] **Step 6: Add the notices**

In `THIRD_PARTY_NOTICES.md`, add these rows to the table after the `Facepunch.Steamworks` row (line 33):

```markdown
| [BRUTAL](https://github.com/ahwootech/BRUTAL) (Ahwoo Limited: `Ahwoo.BRUTAL.Core`, `Interop`, `Logging`, `Memory`, `Numerics`, `Strings`, `Jolt`) | 2026.10.1 (Logging, Jolt 2026.10.2) | BRUTAL License v1.0: MIT-style plus three conditions on every product (see [below](#brutal)) | NuGet packages `Ahwoo.BRUTAL.*` | engine (games inherit the licence's conditions, see [below](#brutal)) |
| [Jolt Physics](https://github.com/jrouwe/JoltPhysics) (Jorrit Rouwe; built into `Ahwoo.BRUTAL.Jolt`) | 5.5.0 | MIT | inside `Ahwoo.BRUTAL.Jolt` | `jolt` native library (`jolt.dll`, `libjolt.so`, `libjolt.dylib`) |
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) (.NET Foundation; a dependency of `Ahwoo.BRUTAL.Core`) | 8.4.2 | MIT | NuGet package `CommunityToolkit.HighPerformance` | `CommunityToolkit.HighPerformance.dll` |
| [Microsoft.Extensions.Logging](https://github.com/dotnet/runtime), `.Console` and the `Microsoft.Extensions.*` packages they depend on (dependencies of `Ahwoo.BRUTAL.Logging`) | 10.0.0 | MIT | NuGet packages `Microsoft.Extensions.*` | `Microsoft.Extensions.*.dll` |
```

Change bullet 2 of the intro (lines 10–11) to:

```markdown
- Credits screens must show the FreeType credit (the FreeType License has an advertising clause) and BRUTAL's copyright
  line (the BRUTAL licence, 2.1). The engine's settings menu has a **Credits** tab listing both (`Credits.Entries`), and
  `Content/UI/credits.rml` is a ready-made document; a game with its own screen lists `Credits.Entries`.
- **Games inherit the BRUTAL licence** (see [BRUTAL](#brutal)): the engine shows BRUTAL's logo in the splash sequence and
  copies `BRUTAL-NOTICE.txt` next to the game, so a game is compliant without doing anything.
```

Append the BRUTAL and Jolt sections with the licence texts copied verbatim from the restored packages:

~~~bash
core=~/.nuget/packages/ahwoo.brutal.core/2026.10.1
lic="$(sed -n 's:.*<license type="file">\(.*\)</license>.*:\1:p' "$core/ahwoo.brutal.core.nuspec")"
test -n "$lic" && test -f "$core/$lic" || { echo "no licence file in $core (ls it and use the BRUTAL licence file there)"; exit 1; }
jolt_notice="$(grep -rl "Jorrit Rouwe" ~/.nuget/packages/ahwoo.brutal.jolt/2026.10.2/LICENSES/ | head -n 1)"
test -n "$jolt_notice" || { echo "no Jolt notice under ahwoo.brutal.jolt/2026.10.2/LICENSES"; exit 1; }
{
  cat <<'MD'

## BRUTAL

The engine depends on BRUTAL (Ahwoo Limited's C# game framework: the `Ahwoo.BRUTAL.*` packages, pinned exactly in
`Directory.Packages.props`), under the **BRUTAL License v1.0**: MIT-style, plus three conditions on every product that
includes BRUTAL. Every game made with Mainframe includes it, so **every game inherits them** (ADR 0192). The engine meets
all three by default:

1. **Credits** (2.1): "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited." on the credits screen or
   equivalent. The engine registers it in `Credits`; the settings menu's Credits tab and `Content/UI/credits.rml` show it.
2. **Logo before gameplay** (2.2): the BRUTAL logo, unaltered except for scaling, legible, no less prominent than other
   logos. The splash sequence's `@brutal` card (`project.mfproj` `splash`) is locked and fits the same box as every other
   card. The logo files (`MainframeEngine/Content/Brand/brutal/`) are BRUTAL's `docs/branding/` files byte for byte
   (checksums: `build/brand/brutal-logos.sha256`).
3. **NOTICE** (2.3): BRUTAL's `NOTICE.txt` alongside the product. Every build and publish copies it next to the game
   executable as `BRUTAL-NOTICE.txt`; ship it with the game.

Exceptions: Ahwoo's written permission, or a product that cannot display graphics (a `--headless` dedicated server shows
no splash). BRUTAL's README waives the logo for non-profits and where it would be detrimental: set `splash.nonProfit` and
ask Ahwoo (brutal@ahwoo.com) to confirm. The licence is governed by New Zealand law. Its text, as shipped in
`Ahwoo.BRUTAL.Core` 2026.10.1:

```text
MD
  cat "$core/$lic"
  cat <<'MD'
```

## Jolt Physics

`Ahwoo.BRUTAL.Jolt` 2026.10.2 carries Jolt Physics 5.5.0 (Jorrit Rouwe) as the `jolt` native library, MIT licensed. Its
notice, from the package's `LICENSES/`:

```text
MD
  cat "$jolt_notice"
  cat <<'MD'
```

## CommunityToolkit.HighPerformance and Microsoft.Extensions

`CommunityToolkit.HighPerformance` 8.4.2 (.NET Foundation) and `Microsoft.Extensions.Logging` / `.Console` 10.0.0 with the
`Microsoft.Extensions.*` packages they depend on (Microsoft) are BRUTAL's dependencies, all MIT; each package carries its
licence file.
MD
} >> THIRD_PARTY_NOTICES.md
~~~

Open the file and check both `text` blocks hold the licence texts and close properly.

- [ ] **Step 7: Tell new games what they inherit**

Create `Templates/MainframeEngine.Templates/content/mfgame/README.md` (`MyGame` is the template's `sourceName`, replaced on `dotnet new`):

```markdown
# MyGame

A game made with [Mainframe Engine](https://github.com/Mainframe-Games/mainframe-engine).

- `MyGame/`: your nodes and resources (C#). `MyGame.Desktop/`: the executable (`GameHost.Run`).
- `project.mfproj`: the project settings (main scene, window, input, splash, …). `Content/`: scenes, UI, art.

Run it with `dotnet run --project MyGame.Desktop` (add `-- --no-splash` while you develop), or open this folder in the
Mainframe editor.

## What your game inherits

Mainframe Engine is MIT. It is built on [BRUTAL](https://github.com/ahwootech/BRUTAL) (Ahwoo Limited), whose licence
(BRUTAL License v1.0) adds three conditions to every product that includes it. The engine meets all three for you:

1. **Credits**: "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited." is on the Credits tab of the
   engine's settings menu (`SettingsMenu`). If you build your own credits screen, list `Credits.Entries`.
2. **Logo before gameplay**: the splash sequence (`"splash"` in `project.mfproj`) shows the BRUTAL logo after the Mainframe
   logo every time the game starts. It cannot be removed unless the game is non-profit (`"nonProfit": true`; confirm
   with Ahwoo at brutal@ahwoo.com) or you have Ahwoo's written permission. Add your studio's card after it.
3. **NOTICE**: every build and publish copies `BRUTAL-NOTICE.txt` next to the game executable. Ship it.

A dedicated server (`--headless`) displays nothing and shows no splash. The engine's `THIRD_PARTY_NOTICES.md` lists
everything else it includes.
```

In `MainframeEngine/PACKAGE.md`, add a "Licence" paragraph at the end:

```markdown
## Licence

The engine is MIT. It depends on BRUTAL (`Ahwoo.BRUTAL.*`), whose licence every game inherits: BRUTAL's copyright on a
credits screen, its logo before gameplay and its `NOTICE.txt` next to the game. The engine does all three by default (the
settings menu's Credits tab, the splash sequence's locked `@brutal` card, `BRUTAL-NOTICE.txt` in every build and publish).
See THIRD_PARTY_NOTICES.md in the engine repository.
```

In `docs/design/distribution-nuget.md`, under "Content and natives: the same layout as a checkout" (line 37), add:

```markdown
**BRUTAL** (ADR 0192): the package depends on the exactly pinned `Ahwoo.BRUTAL.*` packages, whose natives (`jolt`) arrive
through NuGet's `runtimes/<rid>/native` like Silk.NET's (they are not in `Native/natives.lock`, which lists the engine's
own shims). BRUTAL's `NOTICE.txt` travels in the package as `mainframe/BRUTAL-NOTICE.txt` and `buildTransitive/
MainframeEngine.targets` copies it next to the game as `BRUTAL-NOTICE.txt` (checkout games get it from the engine
project's output); `just template-smoke[-package]` checks the build, the publish and the player archives. Linux games carry
BRUTAL's unstripped 50 MB `libjolt.so` until upstream strips it: run `strip --strip-unneeded libjolt.so` on the packaged
game to save ~47 MB (editor releases do, `build/package-editor.sh`).
```

- [ ] **Step 8: Run the test and the smoke tests**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalNoticeTests"
just template-smoke
just template-smoke-package
```

Expected: the test PASSes; both smoke runs end with `template-smoke: SmokeGame built and ran 30 frames` (the package run also prints three `packaged …` lines) and no `BRUTAL-NOTICE.txt is missing` message.

- [ ] **Step 9: Commit**

```bash
git add MainframeEngine/MainframeEngine.csproj build/package/MainframeEngine.targets build/template-smoke.sh THIRD_PARTY_NOTICES.md \
  Templates/MainframeEngine.Templates/content/mfgame/README.md MainframeEngine/PACKAGE.md docs/design/distribution-nuget.md \
  Tests/MainframeEngine.Tests/Project/BrutalNoticeTests.cs
git commit -m "$(printf 'Ship BRUTAL-NOTICE.txt next to every game and list BRUTAL and Jolt in the notices (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 3: Strip `libjolt.so` in editor releases

**Files:**
- Modify: `build/package-editor.sh` (the `linux-*` branch, lines 50–55)
- Modify: `docs/design/release.md` ("Publishing", line 39)

**Interfaces:**
- Consumes: the Linux editor publish folder (now holding `libjolt.so` from `Ahwoo.BRUTAL.Jolt`).
- Produces: a stripped `libjolt.so` in `MainframeEngine-<version>-linux-x64.tar.gz`.

- [ ] **Step 1: Write the failing check**

```bash
rm -rf artifacts/strip-check && mkdir -p artifacts/strip-check/publish
cp ~/.nuget/packages/ahwoo.brutal.jolt/2026.10.2/runtimes/linux-x64/native/libjolt.so artifacts/strip-check/publish/
printf '#!/bin/sh\n' > artifacts/strip-check/publish/MainframeEngine.Editor
cat > artifacts/strip-check/check.sh <<'SH'
#!/usr/bin/env bash
set -euo pipefail
apt-get update -qq && apt-get install -y -qq binutils file >/dev/null
build/package-editor.sh linux-x64 0.0.0-strip artifacts/strip-check/publish artifacts/strip-check/out
mkdir -p artifacts/strip-check/unpacked
tar -xzf artifacts/strip-check/out/MainframeEngine-0.0.0-strip-linux-x64.tar.gz -C artifacts/strip-check/unpacked
file artifacts/strip-check/unpacked/MainframeEngine-0.0.0-strip-linux-x64/libjolt.so
file artifacts/strip-check/unpacked/MainframeEngine-0.0.0-strip-linux-x64/libjolt.so | grep -q ", stripped"
SH
docker run --rm --platform linux/amd64 -v "$PWD":/w -w /w ubuntu:24.04 bash artifacts/strip-check/check.sh
```

Expected: FAIL — `file` prints `…, not stripped` and the last `grep` exits 1.

- [ ] **Step 2: Strip it**

In `build/package-editor.sh`, in the `linux-*` branch after `if [[ -f "$staging/$name/mfplughost" ]]; then …` (line 53), add:

```bash
    # BRUTAL's Jolt native ships unstripped (~50 MB, ADR 0192; upstream has an issue to ship it stripped): editor releases
    # strip its symbols. Needs GNU strip (the ubuntu release runner has it).
    jolt="$staging/$name/libjolt.so"
    if [[ -f "$jolt" ]]; then
      if strip --version 2>/dev/null | grep -q "GNU"; then
        before="$(wc -c < "$jolt")"
        strip --strip-unneeded "$jolt"
        echo "stripped libjolt.so: $before -> $(wc -c < "$jolt") bytes"
      else
        echo "warning: no GNU strip on this host: libjolt.so stays unstripped (~50 MB)" >&2
      fi
    fi
```

- [ ] **Step 3: Run the check to pass**

Run: `docker run --rm --platform linux/amd64 -v "$PWD":/w -w /w ubuntu:24.04 bash artifacts/strip-check/check.sh`
Expected: PASS — `stripped libjolt.so: 5… -> … bytes` and `file` prints `…, stripped`.

- [ ] **Step 4: Document it**

In `docs/design/release.md`, under "Publishing" (line 39), add:

```markdown
The Linux editor archive strips BRUTAL's `libjolt.so` (`build/package-editor.sh`, GNU `strip --strip-unneeded`): the
package ships it unstripped at ~50 MB (ADR 0192). Games packaged with `build/package-game.sh` keep it as published; see
docs/design/distribution-nuget.md for stripping it by hand. The first release with BRUTAL B1 calls out in its notes that
games now inherit the BRUTAL licence (logo in the splash, credits, `BRUTAL-NOTICE.txt`).
```

- [ ] **Step 5: Commit**

```bash
git add build/package-editor.sh docs/design/release.md
git commit -m "$(printf 'Strip libjolt.so in Linux editor releases (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 4: The BRUTAL logo files

**Files:**
- Create: `MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_WHT.png`, `MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_BLK.png`
- Create: `docs/images/brand/brutal/LOGO_BRUTAL_WHT.png`, `docs/images/brand/brutal/LOGO_BRUTAL_BLK.png`
- Create: `build/brand/brutal-logos.sha256`
- Test: `Tests/MainframeEngine.Tests/Project/BrutalLogoTests.cs` (create)

**Interfaces:**
- Consumes: BRUTAL's repository `docs/branding/`.
- Produces: content paths `Content/Brand/brutal/LOGO_BRUTAL_WHT.png` and `Content/Brand/brutal/LOGO_BRUTAL_BLK.png` (Tasks 9, 12, 14).

- [ ] **Step 1: Write the failing test**

Create `Tests/MainframeEngine.Tests/Project/BrutalLogoTests.cs`:

```csharp
using System.Security.Cryptography;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL licence 2.2 (ADR 0192): the logo is shown unaltered except for scaling, so the engine's copies are BRUTAL's
/// docs/branding files byte for byte. build/brand/brutal-logos.sha256 (written when they were fetched) guards them against
/// any later edit, re-export or image optimiser.
/// </summary>
public sealed class BrutalLogoTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void TheBrutalLogosAreBrutalsFilesByteForByte()
    {
        var root = TestPaths.RepositoryRoot();
        var lines = File.ReadAllLines(Path.Combine(root, "build", "brand", "brutal-logos.sha256")).Where(l => l.Length > 0).ToArray();
        Assert.Equal(4, lines.Length); // white and black, in the engine content and in docs/images
        foreach (var line in lines)
        {
            var hash = line[..64];
            var path = line[66..];
            var bytes = File.ReadAllBytes(Path.Combine(root, path));
            Assert.True(bytes.AsSpan().StartsWith(PngSignature), $"{path} is not a PNG.");
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void TheEngineContentCarriesBothVariants()
    {
        Assert.True(File.Exists(ContentPaths.Resolve("Content/Brand/brutal/LOGO_BRUTAL_WHT.png")));
        Assert.True(File.Exists(ContentPaths.Resolve("Content/Brand/brutal/LOGO_BRUTAL_BLK.png")));
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalLogoTests"`
Expected: FAIL — `FileNotFoundException` for `build/brand/brutal-logos.sha256`, and the content files are missing.

- [ ] **Step 3: Fetch the files and record their checksums**

```bash
mkdir -p MainframeEngine/Content/Brand/brutal docs/images/brand/brutal
upstream="$(git ls-remote https://github.com/ahwootech/BRUTAL HEAD | cut -f1)"; echo "BRUTAL HEAD: $upstream"
for v in WHT BLK; do
  curl -fsSL -o "MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_$v.png" \
    "https://raw.githubusercontent.com/ahwootech/BRUTAL/$upstream/docs/branding/LOGO_BRUTAL_$v.png"
  file "MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_$v.png" | grep -q "PNG image data" || { echo "LOGO_BRUTAL_$v.png is not a PNG"; exit 1; }
  cp "MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_$v.png" "docs/images/brand/brutal/LOGO_BRUTAL_$v.png"
done
shasum -a 256 MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_WHT.png MainframeEngine/Content/Brand/brutal/LOGO_BRUTAL_BLK.png \
  docs/images/brand/brutal/LOGO_BRUTAL_WHT.png docs/images/brand/brutal/LOGO_BRUTAL_BLK.png > build/brand/brutal-logos.sha256
file MainframeEngine/Content/Brand/brutal/*.png
```

Expected: two `PNG image data, W x H, …` lines. Add the upstream commit to the BRUTAL section of `THIRD_PARTY_NOTICES.md` (after "byte for byte (checksums: …)"): `, fetched from commit <the hash printed above>`, written as the literal hash.

- [ ] **Step 4: Run the tests to pass**

Run: `dotnet build Tests/MainframeEngine.Tests && dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BrutalLogoTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit** (PNGs go through Git LFS, `.gitattributes`)

```bash
git add MainframeEngine/Content/Brand/brutal docs/images/brand/brutal build/brand/brutal-logos.sha256 THIRD_PARTY_NOTICES.md \
  Tests/MainframeEngine.Tests/Project/BrutalLogoTests.cs
git commit -m "$(printf "Add BRUTAL's logos, byte for byte from its docs/branding (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")"
```

---

### Task 5: The `Credits` registry

**Files:**
- Create: `MainframeEngine/Src/Project/ThirdPartyNotices.cs`, `MainframeEngine/Src/Project/Credits.cs`
- Modify: `MainframeEngine/MainframeEngine.csproj` (new `ItemGroup` after the one added in Task 2)
- Test: `Tests/MainframeEngine.Tests/Project/CreditsTests.cs` (create)

**Interfaces:**
- Consumes: `THIRD_PARTY_NOTICES.md` (Task 2 rows).
- Produces:

```csharp
public readonly record struct CreditEntry(string Section, string Name, string? Notice);
public static class Credits
{
    public const string EngineSection = "Engine";
    public const string ThirdPartySection = "Third-party";
    public const string MainframeNotice = "Copyright (c) 2024 Mainframe Games. MIT License.";
    public const string BrutalNotice = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";
    public static IReadOnlyList<CreditEntry> Entries { get; }
    public static void Add(string section, string name, string? notice = null);
    public static IEnumerable<IGrouping<string, CreditEntry>> BySection();
    internal static CreditEntry[] CreateEngineEntries();
}
internal readonly record struct ThirdPartyNotice(string Name, string? Version, string Licence, string ShippedIn, string? Credit) { public bool ShipsInGames { get; } }
internal static partial class ThirdPartyNotices
{
    public const string ResourceName = "MainframeEngine.THIRD_PARTY_NOTICES.md";
    public static string ReadEmbedded();
    public static IReadOnlyList<ThirdPartyNotice> Parse(string markdown);
    internal static string PlainText(string markdown);
}
```

- [ ] **Step 1: Write the failing tests**

Create `Tests/MainframeEngine.Tests/Project/CreditsTests.cs`:

```csharp
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// The credits (BRUTAL B1, ADR 0192): the engine's entries come first and are there before any game code runs, the BRUTAL
/// licence's copyright line verbatim; the third-party entries are THIRD_PARTY_NOTICES.md's game-shipped rows, read from the
/// copy embedded at build, so they cannot drift; games append their own.
/// </summary>
public sealed class CreditsTests
{
    [Fact]
    public void TheEngineEntriesComeFirstWithBrutalsCopyrightLine()
    {
        var entries = Credits.Entries;
        Assert.Equal(new CreditEntry(Credits.EngineSection, "Mainframe Engine", Credits.MainframeNotice), entries[0]);
        Assert.Equal(new CreditEntry(Credits.EngineSection, "BRUTAL", "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."), entries[1]);
        Assert.Equal(Credits.ThirdPartySection, entries[2].Section);
    }

    [Fact]
    public void TheEmbeddedNoticesAreTheRepositorysFile()
    {
        var file = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot(), "THIRD_PARTY_NOTICES.md"));
        Assert.Equal(file.ReplaceLineEndings("\n"), ThirdPartyNotices.ReadEmbedded().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheThirdPartyEntriesAreTheNoticesRowsThatShipInGames()
    {
        var rows = ThirdPartyNotices.Parse(File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot(), "THIRD_PARTY_NOTICES.md")));
        var expected = rows.Where(r => r.ShipsInGames && r.Name != "BRUTAL")
            .Select(r => (r.Version is { } v ? $"{r.Name} {v}" : r.Name, r.Credit ?? r.Licence)).ToArray();
        var actual = Credits.CreateEngineEntries().Where(e => e.Section == Credits.ThirdPartySection).Select(e => (e.Name, e.Notice ?? "")).ToArray();
        Assert.Equal(expected, actual);
        Assert.Contains(actual, e => e.Name.StartsWith("Jolt Physics", StringComparison.Ordinal));
        Assert.Contains(actual, e => e.Name.StartsWith("CommunityToolkit.HighPerformance", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Tabler Icons", StringComparison.Ordinal)); // editor only
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Whisper.net", StringComparison.Ordinal)); // editor only
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("GetText.NET.Extractor", StringComparison.Ordinal)); // a tool
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Poly Haven", StringComparison.Ordinal)); // Demo and tests
    }

    [Fact]
    public void FreeTypesRequiredCreditIsItsNotice()
    {
        var freeType = Credits.Entries.Single(e => e.Name.StartsWith("FreeType ", StringComparison.Ordinal));
        Assert.Equal("Portions of this software are copyright © 2026 The FreeType Project (https://freetype.org). All rights reserved.", freeType.Notice);
    }

    [Fact]
    public void TheParserReadsLinksCodeAndVersions()
    {
        const string markdown = """
            | Component | Version | Licence | Where | Shipped in |
            |---|---|---|---|---|
            | [Thing](https://x.example) (a `detail`) | 1.2 | **MIT** | `a/b` | engine |
            | Tool | — | MIT | — | nothing: a tool |
            | Panel kit | 3 | MIT | — | editor only: panels |

            ## Thing

            > Thing must be credited.
            """;
        var rows = ThirdPartyNotices.Parse(markdown);
        Assert.Equal(new ThirdPartyNotice("Thing", "1.2", "MIT", "engine", "Thing must be credited."), rows[0]);
        Assert.True(rows[0].ShipsInGames);
        Assert.Null(rows[1].Version);
        Assert.False(rows[1].ShipsInGames);
        Assert.False(rows[2].ShipsInGames);
    }

    [Fact]
    public void GamesAppendTheirOwnEntriesAfterTheEngines()
    {
        var name = "Credits test " + Guid.NewGuid().ToString("N");
        Credits.Add("Studio", name, "  ");
        var entries = Credits.Entries;
        Assert.Equal(new CreditEntry("Studio", name, null), entries[^1]); // a blank notice is none
        Assert.Equal(Credits.EngineSection, entries[0].Section);
        Assert.Throws<ArgumentException>(() => Credits.Add(" ", "x"));
        Assert.Throws<ArgumentException>(() => Credits.Add("Studio", ""));
        Assert.Equal([Credits.EngineSection, Credits.ThirdPartySection], Credits.BySection().Select(g => g.Key).Take(2));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CreditsTests"`
Expected: FAIL to compile — `The name 'Credits' does not exist in the current context`, `ThirdPartyNotices`, `CreditEntry`.

- [ ] **Step 3: Embed the notices**

In `MainframeEngine/MainframeEngine.csproj`, after the `_MainframeCheckBrutalNotice` target (Task 2), add:

```xml
    <!-- Credits (BRUTAL B1): THIRD_PARTY_NOTICES.md, parsed at run time by ThirdPartyNotices, so the credits cannot drift from it. -->
    <ItemGroup>
      <EmbeddedResource Include="..\THIRD_PARTY_NOTICES.md" LogicalName="MainframeEngine.THIRD_PARTY_NOTICES.md" Visible="false" />
    </ItemGroup>
```

- [ ] **Step 4: Implement the parser**

Create `MainframeEngine/Src/Project/ThirdPartyNotices.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>One row of the table in <c>THIRD_PARTY_NOTICES.md</c>, as plain text.</summary>
/// <param name="Name">The component, without its parenthetical detail ("FreeType", "Jolt Physics").</param>
/// <param name="Version">The version column; null for "—".</param>
/// <param name="Licence">The licence column.</param>
/// <param name="ShippedIn">The "shipped in" column.</param>
/// <param name="Credit">A credit the notice requires verbatim (a <c>&gt; …</c> quote under the component's heading), or null.</param>
internal readonly record struct ThirdPartyNotice(string Name, string? Version, string Licence, string ShippedIn, string? Credit)
{
    /// <summary>
    /// The component reaches players' machines in a game: not editor-only ("editor only: …", "editor <c>Content/icons</c>"),
    /// not a development tool or a reference ("nothing …"), not the Demo's, the tests' or the Forest's own content.
    /// </summary>
    public bool ShipsInGames =>
        !(ShippedIn.StartsWith("editor", StringComparison.OrdinalIgnoreCase) || ShippedIn.StartsWith("nothing", StringComparison.OrdinalIgnoreCase)
          || ShippedIn.StartsWith("Demo", StringComparison.OrdinalIgnoreCase) || ShippedIn.StartsWith("the Forest", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <c>THIRD_PARTY_NOTICES.md</c>, embedded in the engine assembly at build (<see cref="ResourceName"/>), into
/// <see cref="ThirdPartyNotice"/> rows for <see cref="Credits"/>: the table's component, version, licence and "shipped in"
/// columns, plus the credit lines a notice requires verbatim (FreeType's).
/// </summary>
internal static partial class ThirdPartyNotices
{
    public const string ResourceName = "MainframeEngine.THIRD_PARTY_NOTICES.md";

    /// <summary>The embedded file's text.</summary>
    public static string ReadEmbedded()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The engine assembly has no {ResourceName} resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The rows of the first table whose header starts with "| Component |", in order.</summary>
    public static IReadOnlyList<ThirdPartyNotice> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var credits = new Dictionary<string, string>(StringComparer.Ordinal);
        string? heading = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                heading = PlainText(line[3..]);
            else if (heading is not null && line.StartsWith("> ", StringComparison.Ordinal))
                credits.TryAdd(heading, PlainText(line[2..]));
        }

        var rows = new List<ThirdPartyNotice>();
        var inTable = false;
        foreach (var line in lines)
        {
            if (!inTable)
            {
                inTable = line.StartsWith("| Component |", StringComparison.Ordinal);
                continue;
            }

            if (!line.StartsWith('|'))
                break; // the table ends at its first line that is not a row
            if (line.StartsWith("|---", StringComparison.Ordinal))
                continue;
            var cells = line.Trim().Trim('|').Split('|');
            if (cells.Length < 5)
                continue;
            var component = PlainText(cells[0]);
            var cut = component.IndexOf(" (", StringComparison.Ordinal);
            var name = cut > 0 ? component[..cut] : component;
            var version = PlainText(cells[1]);
            rows.Add(new ThirdPartyNotice(name, version is "" or "—" ? null : version, PlainText(cells[2]), PlainText(cells[4]),
                credits.GetValueOrDefault(name)));
        }

        return rows;
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    /// <summary>Markdown inline text as plain text: links become their text, code and bold marks go.</summary>
    internal static string PlainText(string markdown) =>
        Link().Replace(markdown, "$1").Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
}
```

- [ ] **Step 5: Implement the registry**

Create `MainframeEngine/Src/Project/Credits.cs`:

```csharp
namespace MainframeEngine;

/// <summary>One line of the credits: a section ("Engine", "Third-party", a game's own), a name and an optional notice.</summary>
/// <param name="Notice">Legal text (a copyright line, a licence name), shown as is and never translated; null for none.</param>
public readonly record struct CreditEntry(string Section, string Name, string? Notice);

/// <summary>
/// The credits every game shows (BRUTAL B1, ADR 0192). The engine's own entries are registered before any game code runs:
/// <see cref="EngineSection"/> holds Mainframe Engine and BRUTAL (the BRUTAL licence's condition 2.1 copyright line,
/// <see cref="BrutalNotice"/>), <see cref="ThirdPartySection"/> every component of <c>THIRD_PARTY_NOTICES.md</c> that ships
/// in games, with its licence or required credit. Games add their own with <see cref="Add"/>, for example from an
/// autoload's <see cref="Node.OnReady"/>. <see cref="SettingsMenu"/>'s Credits tab lists them; a game with its own screen
/// lists <see cref="Entries"/>. Section and component names are not translated; notices are legal text and stay in English.
/// </summary>
public static class Credits
{
    public const string EngineSection = "Engine";
    public const string ThirdPartySection = "Third-party";

    /// <summary>Mainframe Engine's MIT line (its <c>LICENSE</c>).</summary>
    public const string MainframeNotice = "Copyright (c) 2024 Mainframe Games. MIT License.";

    /// <summary>The BRUTAL licence's condition 2.1: this line on the credits screen or equivalent, verbatim.</summary>
    public const string BrutalNotice = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

    private static readonly Lock Gate = new();
    private static CreditEntry[] _entries = CreateEngineEntries();

    /// <summary>Every entry in registration order: the engine's first, then the games'.</summary>
    public static IReadOnlyList<CreditEntry> Entries => Volatile.Read(ref _entries);

    /// <summary>Appends an entry (thread-safe). A blank <paramref name="notice"/> is none.</summary>
    public static void Add(string section, string name, string? notice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = new CreditEntry(section, name, string.IsNullOrWhiteSpace(notice) ? null : notice);
        lock (Gate)
            Volatile.Write(ref _entries, [.. _entries, entry]);
    }

    /// <summary><see cref="Entries"/> grouped by section, sections in the order they first appear.</summary>
    public static IEnumerable<IGrouping<string, CreditEntry>> BySection() => Entries.GroupBy(static e => e.Section, StringComparer.Ordinal);

    /// <summary>The engine's own entries (the start of <see cref="Entries"/>).</summary>
    internal static CreditEntry[] CreateEngineEntries()
    {
        List<CreditEntry> entries =
        [
            new(EngineSection, "Mainframe Engine", MainframeNotice),
            new(EngineSection, "BRUTAL", BrutalNotice),
        ];
        foreach (var notice in ThirdPartyNotices.Parse(ThirdPartyNotices.ReadEmbedded()))
        {
            if (!notice.ShipsInGames || entries.Exists(e => string.Equals(e.Name, notice.Name, StringComparison.Ordinal)))
                continue;
            entries.Add(new CreditEntry(ThirdPartySection, notice.Version is { } version ? $"{notice.Name} {version}" : notice.Name,
                notice.Credit ?? notice.Licence));
        }

        return [.. entries];
    }
}
```

- [ ] **Step 6: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~CreditsTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add MainframeEngine/MainframeEngine.csproj MainframeEngine/Src/Project/ThirdPartyNotices.cs MainframeEngine/Src/Project/Credits.cs \
  Tests/MainframeEngine.Tests/Project/CreditsTests.cs
git commit -m "$(printf 'Add the Credits registry: the engine, BRUTAL and the notices table (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 6: The settings menu's Credits tab

**Files:**
- Modify: `MainframeEngine/Src/UI/Settings/SettingsMenu.cs` (`SettingsTabs` lines 8–16, `SettingsMenuOptions.Tabs` doc line 24, row types lines 59–106, fields lines 114–130, `Tab` doc line 155, `ShowTab` doc line 220, `OnReady` lines 230–283, `IsShown` lines 685–691, `StepTab` lines 693–705)
- Modify: `MainframeEngine/Content/UI/settings/settings.rml` (contract comment lines 7–19, tabs lines 27–31, new page before line 130), `settings.rcss` (append), `settings.pot` (regenerated)
- Modify: `MainframeEngine/Content/UI/credits.rml` (lines 18–23)
- Modify: `Tests/MainframeEngine.Tests/UI/SettingsMenuTests.cs` (`BumpersSwitchTabsAndEscapeClosesWithoutSavingWhenNothingChanged`, lines 134–157; new test), `Tests/MainframeEngine.Tests/UI/UiServerTests.cs` (`EngineCreditsCarryTheFreeTypeCredit`, lines 817–827)
- Modify: `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/ui-settings-menu_frame0030.png` (re-record)
- Modify: `docs/design/game-ui.md` ("Settings menu (G4)", line 328)

**Interfaces:**
- Consumes: `Credits.BySection()` (Task 5).
- Produces: `SettingsTabs.Credits = 8`; `SettingsTabs.All` now includes it; tab name `"credits"`; data model members `showCredits` (bool) and `credits` (`[ { heading, text, notice } ]`).

- [ ] **Step 1: Write the failing tests**

In `Tests/MainframeEngine.Tests/UI/SettingsMenuTests.cs`, replace the four bumper lines and the wrap comment in `BumpersSwitchTabsAndEscapeClosesWithoutSavingWhenNothingChanged` (lines 141–148) with:

```csharp
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("controls", menu.Tab);
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("display", menu.Tab);
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("credits", menu.Tab);
        f.Ui.Pad(ButtonName.RightBumper, true);
        Assert.Equal("audio", menu.Tab); // wraps
        f.Ui.Pad(ButtonName.LeftBumper, true);
        Assert.Equal("credits", menu.Tab);
```

and add this test to the class:

```csharp
    [Fact]
    public void TheCreditsTabListsEveryEntryUnderItsSection()
    {
        using var f = new Fixture(SettingsPath);
        var name = "Settings menu credits " + Guid.NewGuid().ToString("N");
        Credits.Add("Studio", name, "Made by us.");
        var menu = f.Open();
        menu.ShowTab("credits");
        f.Ui.Tick();

        Assert.Equal("credits", menu.Tab);
        var text = Fixture.Text(menu);
        Assert.Contains("Mainframe Engine", text, StringComparison.Ordinal);
        Assert.Contains(Credits.BrutalNotice, text, StringComparison.Ordinal);
        Assert.Contains("The FreeType Project", text, StringComparison.Ordinal);
        Assert.Contains(name, text, StringComparison.Ordinal);
        Assert.Contains("Made by us.", text, StringComparison.Ordinal);
        var sections = menu.Document.AsElement().QuerySelectorAll(".credit-section");
        Assert.Equal(Credits.BySection().Count(), sections.Length);
        Assert.Contains(Credits.EngineSection, sections[0].InnerRml, StringComparison.Ordinal);

        // A menu without the tab never shows it.
        menu.Close();
        f.Ui.Tick();
        var noCredits = f.Open(new SettingsMenuOptions { Settings = f.Settings, Tabs = SettingsTabs.Audio });
        noCredits.ShowTab("credits");
        Assert.Equal("audio", noCredits.Tab);
    }
```

In `Tests/MainframeEngine.Tests/UI/UiServerTests.cs`, in `EngineCreditsCarryTheFreeTypeCredit`, add after the FreeType assertion (line 826):

```csharp
        Assert.Contains(Credits.BrutalNotice, doc.Document.AsElement().InnerRml, StringComparison.Ordinal); // the BRUTAL licence, 2.1
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SettingsMenuTests|FullyQualifiedName~EngineCreditsCarryTheFreeTypeCredit"`
Expected: FAIL — `Assert.Equal() Failure: Expected "credits" Actual "audio"`, the Credits test (`credits` is not shown), and the missing BRUTAL line in `credits.rml`.

- [ ] **Step 3: Implement the tab**

In `SettingsMenu.cs`:

```csharp
/// <summary>The tabs a <see cref="SettingsMenu"/> shows.</summary>
[Flags]
public enum SettingsTabs
{
    Audio = 1,
    Controls = 2,
    Display = 4,

    /// <summary><see cref="Credits.Entries"/> by section: the BRUTAL licence's copyright line among them (ADR 0192).</summary>
    Credits = 8,
    All = Audio | Controls | Display | Credits,
}
```

`SettingsMenuOptions.Tabs` doc: `/// <summary>The tabs to show, in the order Audio, Controls, Display, Credits.</summary>`.

After the `Choice` class (line 85) add:

```csharp
    // One line of the Credits tab: a section heading, or an entry with its notice.
    private sealed class CreditRow
    {
        public required bool Heading { get; init; }
        public required string Text { get; init; }
        public string Notice { get; init; } = "";
    }
```

After `LocaleType` (line 106) add:

```csharp
    private static readonly RmlStructType<CreditRow> CreditType = new RmlStructType<CreditRow>()
        .Member("heading", static c => c.Heading)
        .Member("text", static c => c.Text)
        .Member("notice", static c => c.Notice);
```

After `private readonly List<Choice> _locales = [];` (line 118) add `private readonly List<CreditRow> _credits = [];`.

Docs of `Tab` and `ShowTab`: `<c>"audio"</c>, <c>"controls"</c>, <c>"display"</c> or <c>"credits"</c>`.

In `OnReady`: replace the `_tab = …` line (244) with

```csharp
        _tab = (_options.Tabs & SettingsTabs.Audio) != 0 ? "audio" : (_options.Tabs & SettingsTabs.Controls) != 0 ? "controls"
            : (_options.Tabs & SettingsTabs.Display) != 0 ? "display" : "credits";
```

add `BuildCredits();` after `BuildDisplayChoices();` (line 254), and in the model chain after `.Bind("showDisplay", …)` (line 262):

```csharp
            .Bind("showCredits", this, static m => (m._options.Tabs & SettingsTabs.Credits) != 0)
            .BindList("credits", _credits, CreditType)
```

`IsShown`: add `"credits" => (_options.Tabs & SettingsTabs.Credits) != 0,` before `_ => false`. `StepTab`: `string[] order = ["audio", "controls", "display", "credits"];`.

Add after `StepTab`:

```csharp
    // The Credits tab (BRUTAL B1): Credits.Entries by section, a heading row before each section. Shown as registered: names
    // are not translated and notices are legal text (the document marks them no-tr).
    private void BuildCredits()
    {
        _credits.Clear();
        foreach (var section in Credits.BySection())
        {
            _credits.Add(new CreditRow { Heading = true, Text = section.Key });
            foreach (var entry in section)
                _credits.Add(new CreditRow { Heading = false, Text = entry.Name, Notice = entry.Notice ?? "" });
        }
    }
```

- [ ] **Step 4: The document**

In `settings.rml`: in the contract comment, change the `tab` line to `tab (string: audio | controls | display | credits), showAudio, showControls, showDisplay, showCredits (bool)` and add `credits: [ { heading (bool: a section heading), text, notice } ]`. After the Display tab button (line 30):

```html
            <button data-if="showCredits" data-attr-class="tab == 'credits' ? 'tab active' : 'tab'" data-event-click="tab('credits')">Credits</button>
```

Before `</div>` closing `#settings` (line 130):

```html
        <!-- ── Credits (the BRUTAL licence, 2.1: its copyright line is among the engine's entries) ─────────────── -->
        <div class="page" data-if="tab == 'credits'">
            <div id="credits">
                <div data-for="c : credits" data-attr-class="c.heading ? 'credit-section' : 'credit'">
                    <span class="credit-name no-tr">{{ c.text }}</span>
                    <span class="credit-notice no-tr">{{ c.notice }}</span>
                </div>
            </div>
            <div class="footer">
                <span></span>
                <button class="primary" data-event-click="close()">Done</button>
            </div>
        </div>
```

Append to `settings.rcss`:

```css
/* Credits */
#credits { display: block; height: 340dp; overflow-y: auto; }
#credits .credit-section { display: block; margin: 10dp 6dp 4dp 6dp; }
#credits .credit-section .credit-name { font-weight: bold; color: #e6e9ef; }
#credits .credit-section .credit-notice { display: none; }
#credits .credit { display: block; margin: 0dp 6dp 6dp 6dp; }
#credits .credit-name { display: block; color: #e6e9ef; }
#credits .credit-notice { display: block; font-size: 12dp; color: #8a95ab; }
```

In `credits.rml`, replace the `<h3>Made with Mainframe Engine</h3>` line (18) with:

```html
    <h3>Made with Mainframe Engine and BRUTAL</h3>
    <p class="no-tr">BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.</p>
```

and the "Other" paragraph (line 23) with:

```html
    <p class="no-tr">Jolt Physics — Jorrit Rouwe (MIT). Spine Runtimes — Esoteric Software. ENet — Lee Salzman, nxrighthere (MIT). Silk.NET, StbImageSharp — see THIRD_PARTY_NOTICES.</p>
```

Regenerate the engine's catalog template (the new "Credits" tab label) and the Demo's, which merges it:

```bash
just l10n-engine
just l10n-extract
just l10n-compile
just l10n-check
```

Expected: `settings.pot` gains `msgid "Credits"`; `l10n-check` passes.

- [ ] **Step 5: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SettingsMenuTests|FullyQualifiedName~EngineCreditsCarryTheFreeTypeCredit"`
Expected: PASS.

- [ ] **Step 6: Re-record the settings-menu golden (one more tab button)**

```bash
caffeinate -u -t 600 &
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~TheSettingsMenuMatchesGolden"
open Tests/MainframeEngine.RenderTests/Goldens/moltenvk/ui-settings-menu_frame0030.png
git rm -q Tests/MainframeEngine.RenderTests/Goldens/lavapipe/ui-settings-menu_frame0030.png
just render-tests-linux --filter FullyQualifiedName~TheSettingsMenuMatchesGolden
cp artifacts/linux/render-tests/new-goldens/lavapipe/ui-settings-menu_frame0030.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~TheSettingsMenuMatchesGolden"
```

Expected: the PNG shows the four tabs Audio, Controls, Display, Credits on the Controls page and is otherwise unchanged; the last run PASSes.

- [ ] **Step 7: Document it**

In `docs/design/game-ui.md`, "Settings menu (G4)" (line 328), add:

```markdown
**Credits tab** (BRUTAL B1, ADR 0192): the fourth tab (`SettingsTabs.Credits`, in `SettingsTabs.All`) lists
`Credits.Entries` by section in a scrolling list: section "Engine" (Mainframe Engine and BRUTAL, with the BRUTAL licence's
condition 2.1 line "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."), "Third-party" (every
game-shipped component of `THIRD_PARTY_NOTICES.md`, read from the copy embedded at build, so they cannot drift; FreeType's
required credit among them), then the game's own sections: `Credits.Add("Studio", "Ada Lovelace", "Programming")` from an
autoload's `OnReady`. Names and notices are not translated (`no-tr`). A game with its own menu either keeps this tab or
lists `Credits.Entries` on its own credits screen; `Content/UI/credits.rml` is a static document with the same required
lines.
```

- [ ] **Step 8: Commit**

```bash
git add MainframeEngine/Src/UI/Settings/SettingsMenu.cs MainframeEngine/Content/UI/settings MainframeEngine/Content/UI/credits.rml \
  Examples/Demo/Content/locale Tests/MainframeEngine.Tests/UI/SettingsMenuTests.cs Tests/MainframeEngine.Tests/UI/UiServerTests.cs \
  Tests/MainframeEngine.RenderTests/Goldens/moltenvk/ui-settings-menu_frame0030.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/ui-settings-menu_frame0030.png \
  docs/design/game-ui.md
git commit -m "$(printf "Add a Credits tab to the settings menu with BRUTAL's copyright line (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")"
```

---

### Task 7: The `splash` project settings section

**Files:**
- Create: `MainframeEngine/Src/Project/Splash/SplashProjectSettings.cs`
- Modify: `MainframeEngine/Src/Project/ProjectSettings.cs` (class doc lines 5–14, new property after `Loading` line 94, `Load` lines 122–132)
- Modify: `MainframeEngine/Src/Project/ProjectSettingsFormat.cs` (`Current` line 20, `Migrations` lines 23–44, `Write` line 129, new `WriteSplash` after `WriteLoading` line 382, `Read` lines 477–505, new `ReadSplash` after `ReadLoading` line 679)
- Modify: `Tests/MainframeEngine.Tests/Project/ProjectSettingsTests.cs` (lines 29–100 `Everything`, 102–125 `AssertSame`, 136, 272, 289)
- Test: `Tests/MainframeEngine.Tests/Project/SplashSettingsTests.cs` (create)

**Interfaces:**
- Consumes: `Color.FromHtml`, `Color.HtmlIsValid`, `Color.SrgbToLinear`, `Color.Luminance` (`MainframeEngine/Src/Math/Color.cs:206,389,645,977`); `ContentPaths.FolderName`.
- Produces:

```csharp
public sealed class SplashProjectSettings
{
    public const string MainframeCard = "@mainframe";
    public const string BrutalCard = "@brutal";
    public const float MinHoldSeconds = 1.5f;
    public const float MaxHoldSeconds = 10f;
    public const float DefaultHoldSeconds = 2f;
    public const string DefaultBackground = "#000000";
    public List<string> Cards { get; }
    public string Background { get; set; }
    public float HoldSeconds { get; set; }   // clamped
    public bool Skippable { get; set; }
    public bool NonProfit { get; set; }
    public bool LockApplies { get; }
    public IReadOnlyList<string> ResolvedCards { get; }
    public bool UsesWhiteLogo { get; }
    public IReadOnlyList<string> Warnings { get; }
    public static string? CardError(string entry);
}
// ProjectSettings:
public SplashProjectSettings Splash { get; }
// ProjectSettingsFormat:
public const int Current = 4;
```

- [ ] **Step 1: Write the failing tests**

Create `Tests/MainframeEngine.Tests/Project/SplashSettingsTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Project;

/// <summary>project.mfproj's "splash" section (BRUTAL B1, ADR 0192): defaults, parsing, clamping, the @brutal lock, the migration.</summary>
public sealed class SplashSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-splash-settings").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    private static string Text(ProjectSettings settings) => Encoding.UTF8.GetString(settings.ToJson());

    private static string Current(string splash) => $$"""{ "format": {{ProjectSettingsFormat.Current}}, "name": "G", "splash": {{splash}} }""";

    private static List<string> Warnings(Action run)
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            run();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        return [.. sink.Snapshot().Select(e => e.Message)];
    }

    [Fact]
    public void TheDefaultsShowMainframeThenBrutalForTwoSecondsOnBlackAndAreNotWritten()
    {
        var s = new ProjectSettings { Name = "G" }.Splash;
        Assert.Equal([SplashProjectSettings.MainframeCard, SplashProjectSettings.BrutalCard], s.Cards);
        Assert.Equal(s.Cards, s.ResolvedCards);
        Assert.Equal(("#000000", 2f, true, false), (s.Background, s.HoldSeconds, s.Skippable, s.NonProfit));
        Assert.False(s.LockApplies);
        Assert.Empty(s.Warnings);
        Assert.True(s.UsesWhiteLogo);
        Assert.Null(JsonNode.Parse(Text(new ProjectSettings { Name = "G" }))!["splash"]);
    }

    [Fact]
    public void EveryKeyParsesAndRoundTrips()
    {
        var s = Parse(Current("""
            { "cards": ["@brutal", "@mainframe", "Content/Brand/studio.png"], "background": "#ffffff", "holdSeconds": 3.5,
              "skippable": false, "nonProfit": true }
            """));
        Assert.Equal(["@brutal", "@mainframe", "Content/Brand/studio.png"], s.Splash.Cards);
        Assert.Equal(("#ffffff", 3.5f, false, true), (s.Splash.Background, s.Splash.HoldSeconds, s.Splash.Skippable, s.Splash.NonProfit));
        Assert.False(s.Splash.UsesWhiteLogo); // black BRUTAL logo on a light background

        var json = JsonNode.Parse(Text(s))!["splash"]!;
        Assert.Equal("Content/Brand/studio.png", (string?)json["cards"]![2]);
        Assert.Equal(3.5f, (float)json["holdSeconds"]!);
        var again = Parse(Text(s));
        Assert.Equal(s.Splash.Cards, again.Splash.Cards);
        Assert.Equal(Text(s), Text(again));
    }

    [Theory]
    [InlineData(0.5f, 1.5f)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(10f, 10f)]
    [InlineData(45f, 10f)]
    public void TheHoldIsClampedForLegibilityAndAWarningNamesIt(float written, float used)
    {
        ProjectSettings? s = null;
        var warnings = Warnings(() => s = Parse(Current($$"""{ "holdSeconds": {{written.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""")));
        Assert.Equal(used, s!.Splash.HoldSeconds);
        Assert.Equal(written != used, warnings.Any(w => w.Contains("splash.holdSeconds", StringComparison.Ordinal)));
        Assert.Equal(1.5f, new SplashProjectSettings { HoldSeconds = 0f }.HoldSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplashProjectSettings { HoldSeconds = float.NaN });
    }

    [Theory]
    [InlineData("""["@mainframe", "Content/Brand/studio.png"]""", false, "@mainframe @brutal Content/Brand/studio.png")]
    [InlineData("""["Content/Brand/studio.png"]""", false, "@brutal Content/Brand/studio.png")]
    [InlineData("""["Content/Brand/studio.png", "@mainframe"]""", false, "Content/Brand/studio.png @mainframe @brutal")]
    [InlineData("""[]""", false, "@brutal")]
    [InlineData("""[]""", true, "")]
    [InlineData("""["@mainframe", "Content/Brand/studio.png"]""", true, "@mainframe Content/Brand/studio.png")]
    public void TheBrutalLockPutsTheCardBackUnlessNonProfit(string cards, bool nonProfit, string resolved)
    {
        var settings = Parse(Current($$"""{ "cards": {{cards}}, "nonProfit": {{(nonProfit ? "true" : "false")}} }"""));
        var written = JsonNode.Parse(cards)!.AsArray().Select(n => (string)n!).ToList();
        Assert.Equal(resolved, string.Join(' ', settings.Splash.ResolvedCards));
        Assert.Equal(written, settings.Splash.Cards);
        Assert.Equal(written, Parse(Text(settings)).Splash.Cards); // the saver writes what the user set, never the lock's card
    }

    [Fact]
    public void TheBrutalLockInsertsTheCardAndLoadWarnsOncePerLoad()
    {
        var file = Path.Combine(_directory, ProjectSettings.FileName);
        File.WriteAllText(file, Current("""{ "cards": ["@mainframe"] }"""));

        var loaded = Warnings(() => ProjectSettings.Load(file));
        Assert.Single(loaded, w => w.Contains("BRUTAL licence (condition 2.2)", StringComparison.Ordinal));
        Assert.Contains(loaded, w => w.Contains("brutal@ahwoo.com", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(() => ProjectSettings.Parse(File.ReadAllBytes(file), file)), w => w.Contains("BRUTAL licence", StringComparison.Ordinal));

        // Saving keeps the file as the user wrote it, so the next load warns again until it is fixed.
        ProjectSettings.Load(file).Save(file);
        Assert.Single(Warnings(() => ProjectSettings.Load(file)), w => w.Contains("BRUTAL licence (condition 2.2)", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateCardsShowOnceAndWarn()
    {
        var s = Parse(Current("""{ "cards": ["@mainframe", "@brutal", "@brutal", "Content/a.png", "Content/a.png"] }""")).Splash;
        Assert.Equal(["@mainframe", "@brutal", "Content/a.png"], s.ResolvedCards);
        Assert.Equal(2, s.Warnings.Count(w => w.Contains("more than once", StringComparison.Ordinal)));
        Assert.Equal(5, s.Cards.Count); // the file is not rewritten behind the user's back
    }

    [Theory]
    [InlineData("""{ "cards": ["@studio"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": ["@mainframe", "Content/Brand/studio.jpg"] }""", "splash.cards[1]")]
    [InlineData("""{ "cards": ["Brand/studio.png"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": ["Content/../secret.png"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": [""] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": [1] }""", "splash.cards")]
    [InlineData("""{ "background": "black" }""", "splash.background is invalid")]
    [InlineData("""{ "background": "#00000080" }""", "splash.background is invalid")]
    [InlineData("""{ "holdSeconds": "long" }""", "splash.holdSeconds")]
    [InlineData("""{ "skippable": 1 }""", "splash.skippable")]
    [InlineData("""1""", "splash must be an object")]
    public void InvalidEntriesNameTheirIndex(string splash, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => Parse(Current(splash))).Message, StringComparison.Ordinal);

    [Fact]
    public void Format3ProjectsGetTheDefaultSplashThroughTheMigration()
    {
        Assert.Equal(4, ProjectSettingsFormat.Current);
        var s = Parse("""{ "format": 3, "name": "Old" }""");
        Assert.Equal([SplashProjectSettings.MainframeCard, SplashProjectSettings.BrutalCard], s.Splash.ResolvedCards);
        Assert.Equal(4, (int)JsonNode.Parse(Text(s))!["format"]!);

        var root = new JsonObject { ["format"] = 3, ["name"] = "Old" };
        ProjectSettingsFormat.Upgrade(root, 3, ProjectSettingsFormat.Current, ProjectSettingsFormat.Migrations, "old.mfproj");
        Assert.IsType<JsonObject>(root["splash"]);
    }

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#000", true)]
    [InlineData("#808080", true)]   // sRGB 0.5 is linear 0.216: dark
    [InlineData("#bcbcbc", false)]  // linear 0.503: light
    [InlineData("#ffffff", false)]
    [InlineData("#ff0000", true)]   // relative luminance 0.2126
    public void TheBrutalLogoVariantFollowsTheBackgroundsRelativeLuminance(string background, bool white) =>
        Assert.Equal(white, new SplashProjectSettings { Background = background }.UsesWhiteLogo);
}
```

In `ProjectSettingsTests.cs`:
- line 136: `Assert.Equal($"{{\n  \"format\": {ProjectSettingsFormat.Current},\n  \"name\": \"Tiny\",\n  \"engineVersion\": \"0.3.0\"\n}}\n", json);`
- lines 272 and 289: `Assert.Equal(ProjectSettingsFormat.Current, (int)json["format"]!);`
- in `Everything()`, after `s.Loading.Screen = …` (line 89):

```csharp
        s.Splash.Cards.Add("Content/Brand/studio.png");
        s.Splash.Background = "#101820";
        s.Splash.HoldSeconds = 3f;
        s.Splash.Skippable = false;
        s.Splash.NonProfit = true;
```

- in `AssertSame`, after the `Loading` line (line 123): `Assert.Equivalent(expected.Splash, actual.Splash, strict: true);`

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashSettingsTests|FullyQualifiedName~ProjectSettingsTests"`
Expected: FAIL to compile — `'ProjectSettings' does not contain a definition for 'Splash'`, `SplashProjectSettings` not found.

- [ ] **Step 3: Implement the settings class**

Create `MainframeEngine/Src/Project/Splash/SplashProjectSettings.cs`:

```csharp
namespace MainframeEngine;

/// <summary>
/// The <c>splash</c> section of <c>project.mfproj</c> (BRUTAL B1, ADR 0192): the cards <see cref="SplashSequence"/> shows
/// before gameplay, their background, hold time and skipping.
/// </summary>
/// <remarks>
/// <see cref="BrutalCard"/> is locked: the BRUTAL licence (condition 2.2) requires the BRUTAL logo before gameplay, so
/// <see cref="ResolvedCards"/> puts it back after <see cref="MainframeCard"/> (or first) when <see cref="Cards"/> lacks it,
/// unless <see cref="NonProfit"/> (the README's waiver; confirm with Ahwoo). <see cref="MainframeCard"/> is not locked
/// (Mainframe is MIT). <see cref="Cards"/> keeps what the file says, and the saver writes it unchanged, so
/// <see cref="ProjectSettings.Load"/> warns (<see cref="Warnings"/>) on every load until the file is fixed.
/// </remarks>
public sealed class SplashProjectSettings
{
    /// <summary>The Mainframe Engine logo card.</summary>
    public const string MainframeCard = "@mainframe";

    /// <summary>The BRUTAL logo card (locked: the BRUTAL licence, 2.2).</summary>
    public const string BrutalCard = "@brutal";

    /// <summary>The shortest hold: a logo shown for less is not legible.</summary>
    public const float MinHoldSeconds = 1.5f;

    public const float MaxHoldSeconds = 10f;
    public const float DefaultHoldSeconds = 2f;
    public const string DefaultBackground = "#000000";

    /// <summary>
    /// The cards in order, as the file lists them: <see cref="MainframeCard"/>, <see cref="BrutalCard"/>, or a PNG in the
    /// game's <c>Content/</c> (<c>"Content/Brand/studio.png"</c>). Empty only makes sense with <see cref="NonProfit"/>.
    /// </summary>
    public List<string> Cards { get; } = [MainframeCard, BrutalCard];

    /// <summary>The clear colour behind every card: sRGB hex, <c>#RRGGBB</c> or <c>#RGB</c>.</summary>
    public string Background
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!(value.Length is 4 or 7 && value[0] == '#' && Color.HtmlIsValid(value)))
                throw new ArgumentException($"'{value}' is not an sRGB hex colour (#RRGGBB or #RGB).", nameof(value));
            field = value;
        }
    } = DefaultBackground;

    /// <summary>Seconds each card is fully visible, clamped to <see cref="MinHoldSeconds"/>–<see cref="MaxHoldSeconds"/>.</summary>
    public float HoldSeconds
    {
        get;
        set
        {
            if (float.IsNaN(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The hold must be a number of seconds.");
            field = Math.Clamp(value, MinHoldSeconds, MaxHoldSeconds);
        }
    } = DefaultHoldSeconds;

    /// <summary>A key, click or gamepad button ends the current card's hold once it has run <see cref="MinHoldSeconds"/>.</summary>
    public bool Skippable { get; set; } = true;

    /// <summary>The game is non-profit: <see cref="BrutalCard"/> may be left out (BRUTAL's README waiver; confirm with Ahwoo).</summary>
    public bool NonProfit { get; set; }

    /// <summary>The lock adds <see cref="BrutalCard"/>, which <see cref="Cards"/> lacks.</summary>
    public bool LockApplies => !NonProfit && !Cards.Contains(BrutalCard);

    /// <summary>
    /// The cards shown: <see cref="Cards"/> without repeats (the first of each stays), with <see cref="BrutalCard"/> inserted
    /// after <see cref="MainframeCard"/> (or first) when the lock applies.
    /// </summary>
    public IReadOnlyList<string> ResolvedCards
    {
        get
        {
            var cards = new List<string>(Cards.Count + 1);
            foreach (var card in Cards)
                if (!cards.Contains(card))
                    cards.Add(card);
            if (!NonProfit && !cards.Contains(BrutalCard))
                cards.Insert(cards.IndexOf(MainframeCard) + 1, BrutalCard);
            return cards;
        }
    }

    /// <summary>BRUTAL's white logo goes on a background whose relative luminance (linear) is below 0.5, its black one otherwise.</summary>
    public bool UsesWhiteLogo => Color.FromHtml(Background).SrgbToLinear().Luminance < 0.5f;

    /// <summary>What <see cref="ProjectSettings.Load"/> warns about: the lock applying, repeated cards.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (LockApplies)
                warnings.Add($"splash.cards has no \"{BrutalCard}\": the BRUTAL licence (condition 2.2) requires the BRUTAL logo before gameplay, so " +
                             $"it is shown {(Cards.Contains(MainframeCard) ? $"after \"{MainframeCard}\"" : "first")}. Add \"{BrutalCard}\" to splash.cards, " +
                             "or set splash.nonProfit if the game is non-profit (confirm with Ahwoo: brutal@ahwoo.com).");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in Cards)
                if (!seen.Add(card) && reported.Add(card))
                    warnings.Add($"splash.cards lists \"{card}\" more than once; it is shown once.");
            return warnings;
        }
    }

    /// <summary>Why <paramref name="entry"/> is not a card (phrased to follow its setting's name), or null when it is one.</summary>
    public static string? CardError(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return "must not be empty";
        if (entry.StartsWith('@'))
            return entry is MainframeCard or BrutalCard ? null : $"'{entry}' is not a built-in card (\"{MainframeCard}\" or \"{BrutalCard}\")";
        if (!entry.StartsWith(ContentPaths.FolderName + "/", StringComparison.Ordinal) || !entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("..", StringComparison.Ordinal))
            return $"'{entry}' is not a PNG in the game's Content/ folder (\"Content/…/name.png\")";
        return null;
    }
}
```

- [ ] **Step 4: Wire it into the project settings and the format**

`ProjectSettings.cs`: class doc (line 7) lists "…rendering, splash, Steam and autoloads…"; after the `Loading` property (line 94):

```csharp
    /// <summary>The splash sequence before gameplay (BRUTAL B1, ADR 0192): Mainframe, BRUTAL (locked), the game's cards.</summary>
    public SplashProjectSettings Splash { get; } = new();
```

`Load` (lines 129–131) becomes:

```csharp
        var settings = ProjectSettingsFormat.Parse(File.ReadAllBytes(file), file);
        settings.FilePath = file;
        // Once per file load (never per in-memory Parse: the editor clones settings on every edit).
        foreach (var warning in settings.Splash.Warnings)
            Log.Warning($"[Project] '{file}': {warning}");
        return settings;
```

`ProjectSettingsFormat.cs`: `public const int Current = 4;`; append to `Migrations` after the `2 → 3` step:

```csharp
        // 3 → 4: the "splash" section (ADR 0192, BRUTAL B1). An older project gets the defaults, Mainframe then BRUTAL, which
        // the BRUTAL licence requires before gameplay; the empty section is the defaults.
        new(3, static root =>
        {
            if (root["splash"] is null)
                root["splash"] = new JsonObject();
        }),
```

In `Write`, after `WriteLoading(w, settings.Loading);` (line 129): `WriteSplash(w, settings.Splash);`. After `WriteLoading` (line 382):

```csharp
    private static void WriteSplash(Utf8JsonWriter w, SplashProjectSettings s)
    {
        var d = new SplashProjectSettings();
        var cards = s.Cards.SequenceEqual(d.Cards);
        if (cards && s.Background == d.Background && s.HoldSeconds == d.HoldSeconds && s.Skippable == d.Skippable && s.NonProfit == d.NonProfit)
            return;
        w.WriteStartObject("splash");
        if (!cards)
            WriteStrings(w, "cards", s.Cards); // what the user set: the @brutal lock is applied on load, never written
        if (s.Background != d.Background)
            w.WriteString("background", s.Background);
        if (s.HoldSeconds != d.HoldSeconds)
            w.WriteNumber("holdSeconds", s.HoldSeconds);
        if (s.Skippable != d.Skippable)
            w.WriteBoolean("skippable", s.Skippable);
        if (s.NonProfit != d.NonProfit)
            w.WriteBoolean("nonProfit", s.NonProfit);
        w.WriteEndObject();
    }
```

In `Reader.Read`: add `"splash"` to the `Known(root, …)` list after `"loading"`; after the `loading` block (line 505):

```csharp
            if (Object(root, "splash") is { } splash)
                ReadSplash(splash, s.Splash);
```

After `ReadLoading` (line 679):

```csharp
        private void ReadSplash(JsonObject o, SplashProjectSettings s)
        {
            Known(o, "splash.", "cards", "background", "holdSeconds", "skippable", "nonProfit");
            if (Array(o, "cards", "splash.cards") is not null)
            {
                var cards = Strings(o, "cards", "splash.cards");
                for (var i = 0; i < cards.Count; i++)
                    if (SplashProjectSettings.CardError(cards[i]) is { } error)
                        throw Error($"splash.cards[{i.ToString(CultureInfo.InvariantCulture)}]", error);
                s.Cards.Clear();
                s.Cards.AddRange(cards);
            }

            if (String(o, "background", "splash.background") is { } background)
                Guard("splash.background", () => s.Background = background);
            var hold = Float(o, "holdSeconds", "splash.holdSeconds", s.HoldSeconds);
            s.HoldSeconds = hold;
            if (s.HoldSeconds != hold)
                Log.Warning($"[Project] '{source}': splash.holdSeconds {hold.ToString(CultureInfo.InvariantCulture)} is outside " +
                            $"{SplashProjectSettings.MinHoldSeconds.ToString(CultureInfo.InvariantCulture)}–{SplashProjectSettings.MaxHoldSeconds.ToString(CultureInfo.InvariantCulture)} s; " +
                            $"using {s.HoldSeconds.ToString(CultureInfo.InvariantCulture)}.");
            s.Skippable = Bool(o, "skippable", "splash.skippable", s.Skippable);
            s.NonProfit = Bool(o, "nonProfit", "splash.nonProfit", s.NonProfit);
        }
```

(`Object(root, "splash")` reports a non-object as `splash must be an object`.)

- [ ] **Step 5: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashSettingsTests|FullyQualifiedName~ProjectSettingsTests"`
Expected: PASS. Then `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~Project"` (the rest of the project tests still pass with format 4).

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Project/Splash/SplashProjectSettings.cs MainframeEngine/Src/Project/ProjectSettings.cs MainframeEngine/Src/Project/ProjectSettingsFormat.cs \
  Tests/MainframeEngine.Tests/Project/SplashSettingsTests.cs Tests/MainframeEngine.Tests/Project/ProjectSettingsTests.cs
git commit -m "$(printf 'Add the splash section to project.mfproj with the locked @brutal card (format 4, B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 8: `SplashTimeline`, the splash clock

**Files:**
- Create: `MainframeEngine/Src/Project/Splash/SplashTimeline.cs`
- Test: `Tests/MainframeEngine.Tests/Project/SplashTimelineTests.cs` (create)

**Interfaces:**
- Consumes: `SplashProjectSettings.MinHoldSeconds`, `MaxHoldSeconds` (Task 7).
- Produces:

```csharp
public enum SplashPhase { FadeIn, Hold, FadeOut, WaitingForLoad, Done }
public sealed class SplashTimeline
{
    public const float FadeSeconds = 0.3f;
    public const float SkipFloorSeconds = 1.5f;
    public const float MaxStepSeconds = 0.1f;
    public SplashTimeline(int cardCount, float holdSeconds, bool skippable);
    public int CardCount { get; }
    public float HoldSeconds { get; }
    public bool Skippable { get; }
    public int CardIndex { get; }
    public SplashPhase Phase { get; }
    public float PhaseTime { get; }
    public bool IsDone { get; }
    public bool IsLastCard { get; }
    public float Opacity { get; }
    public bool CanSkip { get; }
    public bool TrySkip();
    public void Abort();
    public void Advance(float deltaSeconds, bool loadCompleted);
}
```

- [ ] **Step 1: Write the failing tests**

Create `Tests/MainframeEngine.Tests/Project/SplashTimelineTests.cs`:

```csharp
namespace MainframeEngine.Tests.Project;

/// <summary>The splash clock (BRUTAL B1) on a fake 60 fps clock: fades, holds, the 1.5 s skip floor, waiting on the load.</summary>
public sealed class SplashTimelineTests
{
    private const float Frame = 1f / 60f;

    private static void Run(SplashTimeline timeline, float seconds, bool loaded = true)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Frame)
            timeline.Advance(Frame, loaded);
    }

    [Fact]
    public void EachCardFadesInHoldsAndFadesOutThenTheSequenceEnds()
    {
        var t = new SplashTimeline(2, 2f, skippable: true);
        Assert.Equal((0, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Run(t, 0.15f);
        Assert.Equal(0.5f, t.Opacity, 0.05f);
        Run(t, 0.25f); // 0.4 s
        Assert.Equal((0, SplashPhase.Hold, 1f), (t.CardIndex, t.Phase, t.Opacity));
        Run(t, 2.05f); // 2.45 s: 0.15 s into card 0's fade out
        Assert.Equal((0, SplashPhase.FadeOut), (t.CardIndex, t.Phase));
        Assert.Equal(0.5f, t.Opacity, 0.05f);
        Run(t, 0.3f); // 2.75 s
        Assert.Equal((1, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Run(t, 2.4f); // 5.15 s
        Assert.False(t.IsDone);
        Run(t, 0.1f); // 5.25 s: past 2 × (0.3 + 2 + 0.3)
        Assert.True(t.IsDone);
        Assert.Equal(0f, t.Opacity);
    }

    [Theory]
    [InlineData(0.2f, 1.5f)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(4f, 4f)]
    [InlineData(60f, 10f)]
    public void TheHoldIsClampedToOneAndAHalfToTenSeconds(float hold, float expected) =>
        Assert.Equal(expected, new SplashTimeline(1, hold, skippable: true).HoldSeconds);

    [Fact]
    public void NoCardCanBeSkippedBeforeOneAndAHalfSecondsOfItsHold()
    {
        var t = new SplashTimeline(2, 5f, skippable: true);
        Assert.False(t.TrySkip()); // fading in
        Run(t, 0.3f + 1.4f);
        Assert.Equal(SplashPhase.Hold, t.Phase);
        Assert.False(t.CanSkip);
        Assert.False(t.TrySkip());
        Run(t, 0.12f); // 1.52 s into the hold
        Assert.True(t.CanSkip);
        Assert.True(t.TrySkip());
        Assert.Equal((0, SplashPhase.FadeOut, 0f), (t.CardIndex, t.Phase, t.PhaseTime));
        Assert.False(t.TrySkip()); // fading out
    }

    [Fact]
    public void AnUnskippableSequenceIgnoresPresses()
    {
        var t = new SplashTimeline(1, 4f, skippable: false);
        Run(t, 2.5f);
        Assert.False(t.TrySkip());
        Assert.Equal(SplashPhase.Hold, t.Phase);
    }

    [Fact]
    public void TheLastCardWaitsAtFullOpacityForASlowLoadAndCannotBeSkippedThere()
    {
        var t = new SplashTimeline(1, 1.5f, skippable: true);
        Run(t, 10f, loaded: false);
        Assert.Equal((SplashPhase.WaitingForLoad, 1f), (t.Phase, t.Opacity));
        Assert.False(t.TrySkip());
        t.Advance(Frame, loadCompleted: true);
        Assert.Equal(SplashPhase.FadeOut, t.Phase);
        Run(t, 0.31f);
        Assert.True(t.IsDone);
    }

    [Fact]
    public void ALoadThatFinishedBeforeTheFirstCardShortensNothing()
    {
        var t = new SplashTimeline(2, 2f, skippable: true);
        Run(t, 5.1f, loaded: true);
        Assert.Equal((1, SplashPhase.FadeOut), (t.CardIndex, t.Phase));
        Run(t, 0.15f);
        Assert.True(t.IsDone);
    }

    [Fact]
    public void AFrameHitchCannotJumpOverACard()
    {
        var t = new SplashTimeline(2, 1.5f, skippable: true);
        t.Advance(30f, loadCompleted: true); // a 30 s stall counts as one 0.1 s step
        Assert.Equal((0, SplashPhase.FadeIn), (t.CardIndex, t.Phase));
        Assert.Equal(SplashTimeline.MaxStepSeconds, t.PhaseTime, 1e-5f);
        for (var i = 0; i < 16; i++)
            t.Advance(5f, loadCompleted: true); // 1.7 s in all
        Assert.Equal((0, SplashPhase.Hold), (t.CardIndex, t.Phase));
        t.Advance(float.NaN, loadCompleted: true);
        t.Advance(-1f, loadCompleted: true);
        Assert.Equal((0, SplashPhase.Hold), (t.CardIndex, t.Phase));
    }

    [Fact]
    public void NoCardsIsDoneAtOnceAndAbortEndsImmediately()
    {
        Assert.True(new SplashTimeline(0, 2f, skippable: true).IsDone);
        var t = new SplashTimeline(3, 2f, skippable: true);
        Run(t, 1f);
        t.Abort(); // a failed load
        Assert.True(t.IsDone);
        Assert.Equal(0f, t.Opacity);
        t.Advance(Frame, loadCompleted: true);
        Assert.True(t.IsDone);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplashTimeline(-1, 2f, skippable: true));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashTimelineTests"`
Expected: FAIL to compile — `SplashTimeline` and `SplashPhase` not found.

- [ ] **Step 3: Implement it**

Create `MainframeEngine/Src/Project/Splash/SplashTimeline.cs`:

```csharp
namespace MainframeEngine;

/// <summary>Where a <see cref="SplashTimeline"/> is.</summary>
public enum SplashPhase
{
    FadeIn,
    Hold,
    FadeOut,

    /// <summary>The last card, held at full opacity until the start scene's load completes.</summary>
    WaitingForLoad,
    Done,
}

/// <summary>
/// The splash sequence's clock (BRUTAL B1, ADR 0192): per card a <see cref="FadeSeconds"/> fade in, the hold, a
/// <see cref="FadeSeconds"/> fade out; after the last card's hold it waits for the start scene's load
/// (<see cref="SplashPhase.WaitingForLoad"/>) before fading out. A press ends a hold early once it has run
/// <see cref="SkipFloorSeconds"/>, so every card, BRUTAL's included, is legible. Pure state: no engine objects, no
/// allocation; <see cref="SplashSequence"/> drives it with the frame's delta.
/// </summary>
public sealed class SplashTimeline
{
    public const float FadeSeconds = 0.3f;

    /// <summary>No card's hold can be skipped before it has run this long.</summary>
    public const float SkipFloorSeconds = 1.5f;

    /// <summary>The most one <see cref="Advance"/> counts: a frame hitch never jumps over a card.</summary>
    public const float MaxStepSeconds = 0.1f;

    public SplashTimeline(int cardCount, float holdSeconds, bool skippable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cardCount);
        if (float.IsNaN(holdSeconds))
            throw new ArgumentOutOfRangeException(nameof(holdSeconds), holdSeconds, "The hold must be a number of seconds.");
        CardCount = cardCount;
        HoldSeconds = Math.Clamp(holdSeconds, SplashProjectSettings.MinHoldSeconds, SplashProjectSettings.MaxHoldSeconds);
        Skippable = skippable;
        Phase = cardCount == 0 ? SplashPhase.Done : SplashPhase.FadeIn;
    }

    public int CardCount { get; }

    /// <summary>Seconds each card is held, clamped like <see cref="SplashProjectSettings.HoldSeconds"/>.</summary>
    public float HoldSeconds { get; }

    public bool Skippable { get; }

    public int CardIndex { get; private set; }

    public SplashPhase Phase { get; private set; }

    /// <summary>Seconds into <see cref="Phase"/>.</summary>
    public float PhaseTime { get; private set; }

    public bool IsDone => Phase == SplashPhase.Done;

    public bool IsLastCard => CardIndex == CardCount - 1;

    /// <summary>The current card's nominal opacity (the document fades with RCSS transitions of the same length).</summary>
    public float Opacity => Phase switch
    {
        SplashPhase.FadeIn => Math.Clamp(PhaseTime / FadeSeconds, 0f, 1f),
        SplashPhase.Hold or SplashPhase.WaitingForLoad => 1f,
        SplashPhase.FadeOut => 1f - Math.Clamp(PhaseTime / FadeSeconds, 0f, 1f),
        _ => 0f,
    };

    /// <summary>A press would end the current hold now.</summary>
    public bool CanSkip => Skippable && Phase == SplashPhase.Hold && PhaseTime >= SkipFloorSeconds;

    /// <summary>Ends the current hold (straight to its fade out) when <see cref="CanSkip"/>; true when it did.</summary>
    public bool TrySkip()
    {
        if (!CanSkip)
            return false;
        Enter(SplashPhase.FadeOut, 0f);
        return true;
    }

    /// <summary>Ends the sequence now (a failed start load: the host takes the error path).</summary>
    public void Abort() => Enter(SplashPhase.Done, 0f);

    /// <summary>Moves the clock by <paramref name="deltaSeconds"/> (clamped to 0–<see cref="MaxStepSeconds"/>).</summary>
    /// <param name="loadCompleted">The start scene's load has completed (or there is none): the last card may fade out.</param>
    public void Advance(float deltaSeconds, bool loadCompleted)
    {
        if (IsDone)
            return;
        PhaseTime += float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0f, MaxStepSeconds) : 0f;
        while (true)
        {
            switch (Phase)
            {
                case SplashPhase.FadeIn when PhaseTime >= FadeSeconds:
                    Enter(SplashPhase.Hold, PhaseTime - FadeSeconds);
                    continue;
                case SplashPhase.Hold when PhaseTime >= HoldSeconds:
                    if (IsLastCard && !loadCompleted)
                        Enter(SplashPhase.WaitingForLoad, 0f);
                    else
                        Enter(SplashPhase.FadeOut, PhaseTime - HoldSeconds);
                    continue;
                case SplashPhase.WaitingForLoad when loadCompleted:
                    Enter(SplashPhase.FadeOut, 0f);
                    continue;
                case SplashPhase.FadeOut when PhaseTime >= FadeSeconds:
                    if (IsLastCard)
                    {
                        Abort();
                        return;
                    }

                    var carry = PhaseTime - FadeSeconds;
                    CardIndex++;
                    Enter(SplashPhase.FadeIn, carry);
                    continue;
                default:
                    return;
            }
        }
    }

    private void Enter(SplashPhase phase, float time)
    {
        Phase = phase;
        PhaseTime = time;
    }
}
```

- [ ] **Step 4: Run them to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashTimelineTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Project/Splash/SplashTimeline.cs Tests/MainframeEngine.Tests/Project/SplashTimelineTests.cs
git commit -m "$(printf 'Add SplashTimeline: fades, holds, the 1.5 s skip floor and the wait for the load (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 9: `SplashSequence` and its document

**Files:**
- Create: `MainframeEngine/Src/Project/Splash/SplashSequence.cs`, `MainframeEngine/Content/UI/splash/splash.rml`, `MainframeEngine/Content/UI/splash/splash.rcss`
- Modify: `MainframeEngine/Content/UI/widgets/widgets.rcss` (append the spinner)
- Test: `Tests/MainframeEngine.Tests/UI/SplashSequenceTests.cs` (create)

**Interfaces:**
- Consumes: `SplashProjectSettings` (Task 7), `SplashTimeline` (Task 8), `UiLayer` (`MainframeEngine/Src/UI/UiLayer.cs:42`), `UiDocument` (`UiDocument.cs:21`, `EnsureLoaded` line 201), `UiServer.Files.ResolvePath` (`UiFileInterface.cs` `ResolvePath`), `UiServer.ViewportSize` (`UiServer.cs:241`), `SceneTree.AddLoadingScreen/RemoveLoadingScreen` (`SceneTree.Loading.cs:38-40`), `InputState.KeyCount/MouseButtonCount/GamepadButtonCount/MaxGamepads` (`InputState.cs:40-45`), `SceneLoad.Stage/IsCompleted` (`SceneLoad.cs:103-106`), `StbImageSharp.ImageInfo.FromStream`.
- Produces:

```csharp
public readonly record struct SplashCard(string Entry, string ImagePath, string Alt, Vector2I Size);
public sealed class SplashSequence : UiLayer
{
    public const string DefaultSource = "Content/UI/splash/splash.rml";
    public const int DefaultLayer = int.MaxValue - 1;
    public const float BoxFraction = 0.4f;
    public const string MainframeLogo = "Content/Brand/logo-512.png";
    public const string BrutalLogoWhite = "Content/Brand/brutal/LOGO_BRUTAL_WHT.png";
    public const string BrutalLogoBlack = "Content/Brand/brutal/LOGO_BRUTAL_BLK.png";
    public SplashProjectSettings Settings { get; set; }
    public SceneLoad? Load { get; set; }
    public bool HoldsLoading { get; set; }
    public bool? Skippable { get; set; }
    public string Source { get; set; }
    public SplashTimeline? Timeline { get; }
    public IReadOnlyList<SplashCard> Cards { get; }
    public UiDocument? Document { get; }
    public Vector2 LayoutSize { get; }
    public Vector2 CardSize { get; }
    public bool IsFinished { get; }
    public event Action<int>? CardShown;
    public event Action? Finished;
    public static IReadOnlyList<SplashCard> ResolveCards(SplashProjectSettings settings, Func<string, string?> resolve);
    public static Vector2I? ReadImageSize(string file);
    public static Vector2 Fit(float box, Vector2I image);
    public static Vector2 FitBox(Vector2 layout, Vector2I image);
}
```

- [ ] **Step 1: Write the failing tests**

Create `Tests/MainframeEngine.Tests/UI/SplashSequenceTests.cs`:

```csharp
using System.Drawing;
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192) headless on an 800×600 UI: the cards and BRUTAL's logo variant, the equal box,
/// skipping and input, the start scene's load, and freeing itself.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class SplashSequenceTests
{
    private const string WideCard = "Content/Models/Spine/SpineBoy/spineboy-pro.png"; // 1024×256 (Tests/Content)

    private static SplashSequence Add(UiTestTree ui, Action<SplashProjectSettings>? configure = null, SceneLoad? load = null)
    {
        var settings = new SplashProjectSettings();
        configure?.Invoke(settings);
        var splash = new SplashSequence { Settings = settings, Load = load };
        ui.Tree.Root.AddChild(splash);
        ui.Tick(2); // ready, the document loaded and the first card laid out
        return splash;
    }

    private static string Src(SplashSequence splash) => splash.Document!.GetElementById("card")!.GetAttribute("src") ?? "";

    private static void TickIntoHold(UiTestTree ui, SplashSequence splash, int card, float seconds)
    {
        for (var guard = 0; !(splash.Timeline!.CardIndex == card && splash.Timeline.Phase == SplashPhase.Hold && splash.Timeline.PhaseTime >= seconds); guard++)
        {
            Assert.True(guard < 3000, $"Never reached {seconds} s of card {card}'s hold (card {splash.Timeline.CardIndex}, {splash.Timeline.Phase}).");
            ui.Tick();
        }
    }

    private static void TickUntilFreed(UiTestTree ui, SplashSequence splash)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!splash.IsFreed)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Stuck: {splash.Timeline?.Phase}, load {splash.Load?.Stage}.");
            ui.Tick();
            Thread.Sleep(1);
        }
    }

    private static List<string> Warnings(Action run, Log.Level levels = Log.Level.Warning | Log.Level.Error)
    {
        var sink = new MemoryLogSink { Levels = levels };
        Log.AddSink(sink);
        try
        {
            run();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        return [.. sink.Snapshot().Select(e => e.Message)];
    }

    [Fact]
    public void TheDefaultCardsAreMainframeThenBrutalsWhiteLogoOnBlack()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui);
        Assert.Equal(SplashSequence.DefaultLayer, splash.Layer);
        Assert.Equal(int.MaxValue - 1, splash.Layer);
        Assert.Equal(["@mainframe", "@brutal"], splash.Cards.Select(c => c.Entry));
        Assert.Equal([SplashSequence.MainframeLogo, SplashSequence.BrutalLogoWhite], splash.Cards.Select(c => c.ImagePath));
        Assert.True(ui.Tree.IsLoading); // hosts count frames once the game is visible
        Assert.True(splash.Document!.Modal);
        Assert.Equal("/Content/Brand/logo-512.png", Src(splash));
        Assert.Equal("img", splash.Document.GetElementById("card")!.GetAttribute("role"));

        TickIntoHold(ui, splash, 1, 0.1f);
        Assert.Equal("/Content/Brand/brutal/LOGO_BRUTAL_WHT.png", Src(splash));
        Assert.Equal("BRUTAL", splash.Document.GetElementById("card")!.GetAttribute("alt"));
        Assert.True(splash.Document.GetElementById("card")!.IsClassSet("visible"));

        using var light = new UiTestTree();
        Assert.Equal(SplashSequence.BrutalLogoBlack, Add(light, s => s.Background = "#f0f0f0").Cards[1].ImagePath);
    }

    [Fact]
    public void EveryCardFitsTheSameBoxOfFortyPercentOfTheShorterSide()
    {
        using var ui = new UiTestTree(); // 800×600: the box is 240 px
        var splash = Add(ui, s => s.Cards.Add(WideCard));
        Assert.Equal(new Vector2(800, 600), splash.LayoutSize);
        Assert.Equal(new Vector2(240, 240), splash.CardSize); // the 512×512 Mainframe logo
        TickIntoHold(ui, splash, 2, 0.1f);
        Assert.Equal(new Vector2(240, 60), splash.CardSize); // 1024×256 keeps its aspect: the longer side fills the box
        Assert.Equal(new Vector2(30, 7.5f), SplashSequence.Fit(30, new Vector2I(1024, 256)));
        Assert.Equal(new Vector2(60, 240), SplashSequence.FitBox(new Vector2(600, 1000), new Vector2I(256, 1024)));
        Assert.Equal(Vector2.Zero, SplashSequence.Fit(240, new Vector2I(0, 10)));
    }

    [Fact]
    public void AResizeMidSplashRefitsTheCard()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui);
        Assert.Equal(new Vector2(240, 240), splash.CardSize);
        splash.Region = new Rectangle(0, 0, 400, 300);
        ui.Tick();
        Assert.Equal(new Vector2(400, 300), splash.LayoutSize);
        Assert.Equal(new Vector2(120, 120), splash.CardSize);
        TickIntoHold(ui, splash, 1, 0.1f);
        Assert.Equal(120f, MathF.Max(splash.CardSize.X, splash.CardSize.Y), 0.01f); // the next card uses the new size too
    }

    [Fact]
    public void AMissingOrNonPngGameCardIsSkippedWithAWarning()
    {
        using var ui = new UiTestTree();
        File.WriteAllText(Path.Combine(ui.ContentDirectory, "fake.png"), "not an image");
        SplashSequence splash = null!;
        var warnings = Warnings(() => splash = Add(ui, s => s.Cards.AddRange(["Content/Brand/missing.png", "Content/fake.png", WideCard])));
        Assert.Equal(["@mainframe", "@brutal", WideCard], splash.Cards.Select(c => c.Entry));
        Assert.Contains(warnings, w => w.Contains("Content/Brand/missing.png", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Content/fake.png", StringComparison.Ordinal));

        var none = new SplashProjectSettings { NonProfit = true };
        none.Cards.Clear();
        Assert.Empty(SplashSequence.ResolveCards(none, _ => null));
    }

    [Fact]
    public void AKeyPressSkipsAHoldOnlyAfterOneAndAHalfSecondsAndTheGameNeverSeesIt()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui, s => s.HoldSeconds = 5f);
        ui.Game.Seen.Clear();
        TickIntoHold(ui, splash, 0, 1f);
        Assert.True(ui.Key(Key.Space, pressed: true));
        ui.Key(Key.Space, pressed: false);
        Assert.Equal((0, SplashPhase.Hold), (splash.Timeline!.CardIndex, splash.Timeline.Phase)); // too early

        TickIntoHold(ui, splash, 0, 1.5f);
        ui.Button(MouseButton.Left, pressed: true, 400, 300);
        ui.Button(MouseButton.Left, pressed: false, 400, 300);
        Assert.Equal((0, SplashPhase.FadeOut), (splash.Timeline.CardIndex, splash.Timeline.Phase));

        TickIntoHold(ui, splash, 1, 1.6f);
        ui.Pad(ButtonName.A, pressed: true);
        ui.Pad(ButtonName.A, pressed: false);
        Assert.Equal((1, SplashPhase.FadeOut), (splash.Timeline.CardIndex, splash.Timeline.Phase));
        ui.Move(10, 10);
        Assert.Empty(ui.Game.Seen); // keys, clicks, pads and motion were all consumed
    }

    [Fact]
    public void AKeyHeldFromLaunchOrItsRepeatsNeverSkip()
    {
        using var ui = new UiTestTree();
        ui.Key(Key.Enter, pressed: true); // held before the splash exists
        ui.Pad(ButtonName.Start, pressed: true);
        var splash = Add(ui, s => s.HoldSeconds = 5f);
        TickIntoHold(ui, splash, 0, 1.6f);
        ui.Key(Key.Enter, pressed: true); // auto-repeat of the held key
        ui.Key(Key.Enter, pressed: true);
        ui.Pad(ButtonName.Start, pressed: true);
        Assert.Equal(SplashPhase.Hold, splash.Timeline!.Phase);

        ui.Key(Key.Enter, pressed: false);
        ui.Key(Key.Enter, pressed: true); // a fresh press
        Assert.Equal(SplashPhase.FadeOut, splash.Timeline.Phase);
    }

    [Fact]
    public void AnUnskippableSplashIgnoresInput()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui, s => s.Skippable = false);
        TickIntoHold(ui, splash, 0, 1.6f);
        ui.Key(Key.Space, pressed: true);
        Assert.Equal(SplashPhase.Hold, splash.Timeline!.Phase);
        Assert.Empty(ui.Game.Seen);
    }

    [Fact]
    public void TheLastCardWaitsForTheLoadWithASpinnerThenTheSplashFreesItself()
    {
        var folder = Directory.CreateTempSubdirectory("mf-splash-load").FullName;
        try
        {
            var path = Path.Combine(folder, "Level.mscene");
            File.WriteAllBytes(path, SceneSaver.ToJson(new Node3D { Name = "Level" }));
            using var ui = new UiTestTree();
            var load = ui.Tree.ChangeSceneToFileAsync(path, new SceneLoadOptions { HoldSeconds = 2f }); // 2 s of wall clock
            var splash = Add(ui, s => s.HoldSeconds = 1.5f, load);
            var finished = 0;
            splash.Finished += () => finished++;

            for (var guard = 0; splash.Timeline!.Phase != SplashPhase.WaitingForLoad; guard++)
            {
                Assert.True(guard < 1000, $"Never waited: {splash.Timeline.Phase}, load {load.Stage}.");
                ui.Tick(); // 3.9 s of splash in well under a second of wall clock: the load is still held
            }

            Assert.Equal(1, splash.Timeline.CardIndex);
            Assert.True(splash.Document!.GetElementById("spinner")!.IsClassSet("visible"));
            Assert.True(splash.Document.GetElementById("card")!.IsClassSet("visible"));

            TickUntilFreed(ui, splash);
            Assert.Equal(1, finished);
            Assert.True(load.IsDone);
            Assert.Equal("Level", ui.Tree.CurrentScene!.Name);
            Assert.False(ui.Tree.IsLoading);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFailedLoadEndsTheSplashAtOnce()
    {
        using var ui = new UiTestTree();
        var load = ui.Tree.ChangeSceneToFileAsync(Path.Combine(Path.GetTempPath(), "mf-missing-" + Guid.NewGuid().ToString("N") + ".mscene"));
        var splash = Add(ui, load: load);
        TickUntilFreed(ui, splash);
        Assert.Equal(SceneLoadStage.Failed, load.Stage);
        Assert.True(splash.IsFinished);
        Assert.Equal(0, splash.Timeline!.CardIndex); // it did not play the rest
    }

    [Fact]
    public void AFastLoadStillPlaysEveryCardThenTheSplashFreesItself()
    {
        var folder = Directory.CreateTempSubdirectory("mf-splash-fast").FullName;
        try
        {
            var path = Path.Combine(folder, "Fast.mscene");
            File.WriteAllBytes(path, SceneSaver.ToJson(new Node3D { Name = "Fast" }));
            using var ui = new UiTestTree();
            var load = ui.Tree.ChangeSceneToFileAsync(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!load.IsCompleted)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Load stuck: {load.Stage}.");
                ui.Tick();
                Thread.Sleep(1);
            }

            var splash = Add(ui, load: load); // the scene was ready before the first card
            var frames = 2;
            while (!splash.IsFreed)
            {
                ui.Tick();
                Assert.True(++frames < 1000);
            }

            Assert.True(frames >= (int)(2 * (0.3f + 2f + 0.3f) * 60f), $"The sequence took {frames} frames: a card was shortened.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashSequenceTests"`
Expected: FAIL to compile — `SplashSequence` not found.

- [ ] **Step 3: The document**

Create `MainframeEngine/Content/UI/splash/splash.rml`:

```html
<rml>
<head>
    <title>Splash</title>
    <link type="text/rcss" href="/Content/UI/widgets/widgets.rcss"/>
    <link type="text/rcss" href="splash.rcss"/>
</head>
<!--
  The engine's splash sequence (SplashSequence, BRUTAL B1, ADR 0192). No text, nothing to translate. SplashSequence sets
  the body's background, #card's src, alt, size and position per card, and toggles .visible for the 0.3 s fades; #spinner
  shows (.visible) while the start scene is still loading after the last card.
-->
<body>
    <img id="card" role="img" alt="Mainframe Engine" src="/Content/Brand/logo-512.png"/>
    <div id="spinner" class="spinner"/>
</body>
</rml>
```

Create `MainframeEngine/Content/UI/splash/splash.rcss`:

```css
/*
 * The engine's splash sequence (SplashSequence, BRUTAL B1, ADR 0192): one card at a time on the project's background
 * (splash.background, set by SplashSequence). Every card's size is set in px by SplashSequence: each fits the same square
 * box of 40 % of the shorter side, so no logo is less prominent than another. The fades are #card's 0.3 s opacity
 * transition, driven by the .visible class.
 */

body {
    width: 100%;
    height: 100%;
    background-color: #000000;
}

#card {
    position: absolute;
    left: 50%;
    top: 50%;
    opacity: 0;
    transition: opacity 0.3s linear-in-out;
}

#card.visible { opacity: 1; }

#spinner {
    position: absolute;
    left: 50%;
    top: 80%;
    margin-left: -12dp;
    opacity: 0;
    transition: opacity 0.3s linear-in-out;
}

#spinner.visible { opacity: 1; }
```

Append to `MainframeEngine/Content/UI/widgets/widgets.rcss`:

```css
/* A busy spinner (the splash sequence's "still loading"): a ring with one bright quarter, turning once a second.
   .on-light for light backgrounds. */
.spinner {
    display: block;
    width: 24dp;
    height: 24dp;
    border: 3dp #ffffff33;
    border-top-color: #ffffffd9;
    border-radius: 12dp;
    animation: 1s linear infinite mf-spin;
}

.spinner.on-light {
    border-color: #00000033;
    border-top-color: #000000d9;
}

@keyframes mf-spin {
    from { transform: rotate(0deg); }
    to   { transform: rotate(360deg); }
}
```

- [ ] **Step 4: The node**

Create `MainframeEngine/Src/Project/Splash/SplashSequence.cs`:

```csharp
using System.Globalization;
using System.Numerics;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;
using StbImageSharp;

namespace MainframeEngine;

/// <summary>One resolved splash card: its <c>project.mfproj</c> entry, the image shown, its alt text and its size in pixels.</summary>
public readonly record struct SplashCard(string Entry, string ImagePath, string Alt, Vector2I Size);

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192): the project's <c>splash</c> cards (Mainframe, BRUTAL as its licence
/// requires, then the game's own) one after another on an engine <see cref="UiLayer"/> above every game layer
/// (<see cref="DefaultLayer"/>, under the developer overlay), each faded in, held and faded out
/// (<see cref="SplashTimeline"/>), while <see cref="GameHost"/> loads the start scene behind it. Every card's image fits the
/// same square box (<see cref="BoxFraction"/> of the shorter side, aspect kept), so no logo is less prominent than another.
/// A fresh key, click or gamepad press skips a hold once it has run <see cref="SplashTimeline.SkipFloorSeconds"/>; no input
/// reaches the game while the splash is up. It ends when the last card has faded out and <see cref="Load"/> has completed
/// (a spinner shows while it has not), raises <see cref="Finished"/> and frees itself.
/// </summary>
/// <remarks>
/// While it is up the tree reports <see cref="SceneTree.IsLoading"/> (<see cref="HoldsLoading"/>), like the loading screen,
/// so hosts count frames and take screenshots once the game is visible. Its clock is the frame's delta and it processes
/// while the tree is paused (<see cref="ProcessMode.Always"/>). Polled input (<see cref="Input.IsActionPressed"/>) still
/// sees keys held through the splash; only the events are consumed.
/// </remarks>
public sealed class SplashSequence : UiLayer
{
    /// <summary>The engine's splash document.</summary>
    public const string DefaultSource = "Content/UI/splash/splash.rml";

    /// <summary>Above every game layer and the loading screen, under the developer overlay (<c>int.MaxValue</c>).</summary>
    public const int DefaultLayer = int.MaxValue - 1;

    /// <summary>Every card fits a square box of this share of the shorter screen side.</summary>
    public const float BoxFraction = 0.4f;

    public const string MainframeLogo = "Content/Brand/logo-512.png";
    public const string BrutalLogoWhite = "Content/Brand/brutal/LOGO_BRUTAL_WHT.png";
    public const string BrutalLogoBlack = "Content/Brand/brutal/LOGO_BRUTAL_BLK.png";

    private readonly bool[] _keys = new bool[InputState.KeyCount];
    private readonly bool[] _mouseButtons = new bool[InputState.MouseButtonCount];
    private readonly bool[] _padButtons = new bool[InputState.MaxGamepads * InputState.GamepadButtonCount];
    private UiDocument? _document;
    private SplashCard[] _cards = [];
    private bool _primed;
    private int _shownCard = -1;
    private int _announcedCard = -1;
    private bool _cardVisible;
    private bool _spinnerVisible;
    private Vector2 _laidOut = new(-1, -1);
    private bool _registered;

    public SplashSequence()
    {
        Name = "SplashSequence";
        Layer = DefaultLayer;
        ProcessMode = ProcessMode.Always;
        InputBeforeUi = true; // every event, before any game UI can take it
    }

    /// <summary>The project's <c>splash</c> section (read in <see cref="Node.OnReady"/>).</summary>
    public SplashProjectSettings Settings { get; set; } = new();

    /// <summary>The start scene's load the last card waits for; null when there is none (it counts as completed).</summary>
    public SceneLoad? Load { get; set; }

    /// <summary>The tree reports <see cref="SceneTree.IsLoading"/> while the splash is up (default true; QA runs turn it off).</summary>
    public bool HoldsLoading { get; set; } = true;

    /// <summary>Overrides <see cref="SplashProjectSettings.Skippable"/> (QA captures are never skipped); null uses the settings.</summary>
    public bool? Skippable { get; set; }

    /// <summary>The document; default <see cref="DefaultSource"/>.</summary>
    public string Source { get; set; } = DefaultSource;

    /// <summary>The clock (null before <see cref="Node.OnReady"/>).</summary>
    public SplashTimeline? Timeline { get; private set; }

    /// <summary>The cards shown: <see cref="SplashProjectSettings.ResolvedCards"/> whose image exists.</summary>
    public IReadOnlyList<SplashCard> Cards => _cards;

    /// <summary>The splash document (null before <see cref="Node.OnReady"/>).</summary>
    public UiDocument? Document => _document;

    /// <summary>The size the current card was laid out for (the layer's region, else the UI viewport), in pixels.</summary>
    public Vector2 LayoutSize { get; private set; }

    /// <summary>The current card's size on screen, in pixels.</summary>
    public Vector2 CardSize { get; private set; }

    /// <summary>The sequence has ended (<see cref="Finished"/> was raised).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Raised with a card's index when it is halfway through its hold, fully visible (QA screenshots).</summary>
    public event Action<int>? CardShown;

    /// <summary>Raised once when the sequence ends, just before the splash frees itself.</summary>
    public event Action? Finished;

    /// <summary>
    /// The cards of <paramref name="settings"/> whose image <paramref name="resolve"/> finds and reads as an image, in order.
    /// A missing game card is skipped with a warning; a missing built-in one is an error (the engine's content is incomplete).
    /// </summary>
    public static IReadOnlyList<SplashCard> ResolveCards(SplashProjectSettings settings, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolve);
        var cards = new List<SplashCard>();
        foreach (var entry in settings.ResolvedCards)
        {
            var (image, alt) = entry switch
            {
                SplashProjectSettings.MainframeCard => (MainframeLogo, "Mainframe Engine"),
                SplashProjectSettings.BrutalCard => (settings.UsesWhiteLogo ? BrutalLogoWhite : BrutalLogoBlack, "BRUTAL"),
                _ => (entry, Path.GetFileNameWithoutExtension(entry)),
            };
            if (resolve(image) is not { } file || ReadImageSize(file) is not { } size)
            {
                if (entry.StartsWith('@'))
                    Log.Error($"[Splash] The engine's {entry} card image '{image}' is missing or unreadable: the engine's Content/ is incomplete.");
                else
                    Log.Warning($"[Splash] Splash card '{entry}' is missing or not a PNG; skipped.");
                continue;
            }

            cards.Add(new SplashCard(entry, image, alt, size));
        }

        return cards;
    }

    /// <summary>An image file's pixel size from its header, or null when it is not an image or cannot be read.</summary>
    public static Vector2I? ReadImageSize(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        try
        {
            using var stream = File.OpenRead(file);
            return ImageInfo.FromStream(stream) is { Width: > 0, Height: > 0 } info ? new Vector2I(info.Width, info.Height) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="image"/> scaled to fit a <paramref name="box"/>-sided square, aspect kept (zero for an empty image).</summary>
    public static Vector2 Fit(float box, Vector2I image)
    {
        if (image.X <= 0 || image.Y <= 0 || !(box > 0f))
            return Vector2.Zero;
        var scale = MathF.Min(box / image.X, box / image.Y);
        return new Vector2(image.X * scale, image.Y * scale);
    }

    /// <summary><see cref="Fit"/> into the splash box of a <paramref name="layout"/>-sized screen (<see cref="BoxFraction"/>).</summary>
    public static Vector2 FitBox(Vector2 layout, Vector2I image) => Fit(BoxFraction * MathF.Min(layout.X, layout.Y), image);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (HoldsLoading && Tree is { } tree && !_registered)
        {
            tree.AddLoadingScreen();
            _registered = true;
        }
    }

    protected override void OnExitTree()
    {
        if (_registered)
        {
            Tree?.RemoveLoadingScreen();
            _registered = false;
        }

        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        Func<string, string?> resolve = Server is { } server
            ? server.Files.ResolvePath
            : static path => ContentPaths.Resolve(path) is var full && File.Exists(full) ? full : null;
        _cards = [.. ResolveCards(Settings, resolve)];
        Timeline = new SplashTimeline(_cards.Length, Settings.HoldSeconds, Skippable ?? Settings.Skippable);
        if (Tree is { } tree)
            SeedHeldInput(tree.Input);
        _document = new UiDocument { Name = "Document", Source = Source, Modal = true, AutoFocus = false };
        AddChild(_document);
        Log.Info($"[Splash] Showing the splash: {_cards.Length.ToString(CultureInfo.InvariantCulture)} cards " +
                 $"({string.Join(", ", _cards.Select(c => c.Entry))}), {Timeline.HoldSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s each.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (IsFinished || Timeline is not { } timeline)
            return;
        if (!_primed)
        {
            // The first frame loads the document and lays out the first card hidden, so its fade in is a transition.
            if (timeline.IsDone || _document is null || !_document.EnsureLoaded())
            {
                if (!timeline.IsDone)
                    Log.Error($"[Splash] The splash document '{Source}' did not load; the splash is skipped.");
                Finish();
                return;
            }

            _primed = true;
            _document.Document.AsElement().SetProperty("background-color", Settings.Background);
            Present(timeline, allowVisible: false);
            return;
        }

        if (Load is { Stage: SceneLoadStage.Failed or SceneLoadStage.Canceled })
            timeline.Abort(); // the host takes the failed load's path (GameHost quits with an error)
        else
            timeline.Advance(gameTime.DeltaTime, Load is null || Load.IsCompleted);

        if (timeline.Phase == SplashPhase.Hold && timeline.PhaseTime >= timeline.HoldSeconds * 0.5f && _announcedCard != timeline.CardIndex)
        {
            _announcedCard = timeline.CardIndex;
            CardShown?.Invoke(_announcedCard);
        }

        if (timeline.IsDone)
        {
            Finish();
            return;
        }

        Present(timeline, allowVisible: true);
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        var fresh = inputEvent switch
        {
            InputEventKey key => Press(_keys, (int)key.Key, key.Pressed),
            InputEventMouseButton button => Press(_mouseButtons, (int)button.Button, button.Pressed),
            InputEventGamepadButton pad => (uint)pad.Device < InputState.MaxGamepads
                                           && Press(_padButtons, pad.Device * InputState.GamepadButtonCount + (int)pad.Button, pad.Pressed),
            _ => false,
        };
        if (fresh)
            Timeline?.TrySkip();
        GetViewport()?.SetInputAsHandled(); // the game never sees input while the splash is up
    }

    // A press counts only when that key or button was up: held from launch, or a key's auto-repeat, never skips.
    private static bool Press(bool[] held, int index, bool pressed)
    {
        if ((uint)index >= (uint)held.Length)
            return false;
        var wasHeld = held[index];
        held[index] = pressed;
        return pressed && !wasHeld;
    }

    private void SeedHeldInput(InputState input)
    {
        for (var i = 0; i < _keys.Length; i++)
            _keys[i] = input.IsKeyPressed((Key)i);
        for (var i = 0; i < _mouseButtons.Length; i++)
            _mouseButtons[i] = input.IsMouseButtonPressed((MouseButton)i);
        for (var device = 0; device < InputState.MaxGamepads; device++)
            for (var button = 0; button < InputState.GamepadButtonCount; button++)
                _padButtons[device * InputState.GamepadButtonCount + button] = input.IsGamepadButtonPressed(device, (ButtonName)button);
    }

    // Touches the DOM only when the card, the layout size or a visibility changes.
    private void Present(SplashTimeline timeline, bool allowVisible)
    {
        if (_document is not { IsLoaded: true } document || _cards.Length == 0)
            return;
        var card = document.Document.GetElementById("card");
        var spinner = document.Document.GetElementById("spinner");
        if (card.IsNull || spinner.IsNull)
            return;
        var index = Math.Clamp(timeline.CardIndex, 0, _cards.Length - 1);
        if (index != _shownCard)
        {
            _shownCard = index;
            card.SetAttribute("src", "/" + _cards[index].ImagePath);
            card.SetAttribute("alt", _cards[index].Alt);
            spinner.SetClass("on-light", !Settings.UsesWhiteLogo);
            _laidOut = new Vector2(-1, -1);
        }

        var layout = Region is { } region ? new Vector2(region.Width, region.Height) : Server?.ViewportSize ?? Vector2.Zero;
        if (layout != _laidOut)
        {
            _laidOut = layout;
            Layout(card, spinner, layout, _cards[index].Size);
        }

        var visible = allowVisible && timeline.Phase is SplashPhase.FadeIn or SplashPhase.Hold or SplashPhase.WaitingForLoad;
        if (visible != _cardVisible)
        {
            _cardVisible = visible;
            card.SetClass("visible", visible);
        }

        var waiting = timeline.Phase == SplashPhase.WaitingForLoad;
        if (waiting != _spinnerVisible)
        {
            _spinnerVisible = waiting;
            spinner.SetClass("visible", waiting);
        }
    }

    private void Layout(RmlElement card, RmlElement spinner, Vector2 layout, Vector2I image)
    {
        LayoutSize = layout;
        var size = CardSize = FitBox(layout, image);
        card.SetProperty("width", Px(size.X));
        card.SetProperty("height", Px(size.Y));
        card.SetProperty("margin-left", Px(-size.X * 0.5f));
        card.SetProperty("margin-top", Px(-size.Y * 0.5f));
        spinner.SetProperty("top", Px(layout.Y * 0.5f + size.Y * 0.5f + 0.05f * MathF.Min(layout.X, layout.Y)));
    }

    private static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private void Finish()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        Finished?.Invoke();
        if (!IsQueuedForDeletion)
            QueueFree();
    }
}
```

Note: `RmlElement` is the element handle type of `MainframeEngine.UI.Rml` (`RmlElement.cs`); `RmlDocument.GetElementById` returns it, as in `SplashScreen.SetStatus` (`MainframeEngine.Editor/Src/UI/SplashScreen.cs:58`).

- [ ] **Step 5: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashSequenceTests"`
Expected: PASS (10 tests). Then `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~UI"` (the widget library still loads everywhere).

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Project/Splash/SplashSequence.cs MainframeEngine/Content/UI/splash MainframeEngine/Content/UI/widgets/widgets.rcss \
  Tests/MainframeEngine.Tests/UI/SplashSequenceTests.cs
git commit -m "$(printf 'Add SplashSequence: equal-box cards, skip rules and the wait for the start scene (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 10: Launch flags and `GameHost` wiring

**Files:**
- Modify: `MainframeEngine/Src/Project/GameHostOptions.cs` (doc list lines 6–29, properties after line 76, `Parse` switch before line 181, `Validated` lines 191–196, `Apply` lines 199–224)
- Modify: `MainframeEngine/Src/Project/GameHost.cs` (usings line 1, class doc lines 5–15, fields lines 52–58, `OnUpdate` lines 414–443, `StartAsync` lines 469–485, new members after `StartAsync`)
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/GameHostRun.cs` (line 40), `build/forest-record.sh` (lines 22, 24), `build/forest-screenshots.sh` (line 19)
- Modify: `build/template-smoke.sh` (after line 182 and lines 184–197)
- Modify: `docs/design/project-and-gamehost.md` (`project.mfproj` section line 52; "GameHost" flags line 186)
- Modify: `Tests/MainframeEngine.Tests/Project/GameSessionTests.cs` (`GameHostOptionsTests`), `Tests/MainframeEngine.Tests/Project/GameHostTests.cs` (new class)

**Interfaces:**
- Consumes: `SplashSequence` (Task 9), `SplashProjectSettings.ResolvedCards` (Task 7), `GameHost.CaptureFrame`/`OnFrameCaptured` (`GameHost.cs:517-533`).
- Produces:

```csharp
// GameHostOptions
public bool NoSplash { get; init; }            // --no-splash
public bool QaSplash { get; init; }            // --qa-splash (and --qa-splash-out)
public string? QaSplashOutput { get; init; }   // --qa-splash-out <dir>
// GameHost
internal enum GameHostSplashMode { None, BeforeAsyncLoad, BeforeSyncLoad, QaOnly }
public static bool ShowsSplash(GameHostOptions options);
internal static GameHostSplashMode SplashModeFor(GameHostOptions options, ProjectSettings settings, bool hasUi);
public SplashSequence? Splash { get; }
```

- [ ] **Step 1: Write the failing tests**

In `Tests/MainframeEngine.Tests/Project/GameSessionTests.cs`, class `GameHostOptionsTests`, add:

```csharp
    [Fact]
    public void SplashFlagsParse()
    {
        Assert.True(GameHostOptions.Parse(["--no-splash"]).NoSplash);
        var qa = GameHostOptions.Parse(["--qa-splash-out", "out/splash"]);
        Assert.True(qa.QaSplash); // implied
        Assert.Equal("out/splash", qa.QaSplashOutput);
        Assert.True(qa.Apply(new ProjectSettings { Name = "G" }.ToEngineOptions()).EnableFrameCapture);
        var preview = GameHostOptions.Parse(["--qa-splash"]);
        Assert.True(preview.QaSplash);
        Assert.Null(preview.QaSplashOutput);
        Assert.False(preview.Apply(new ProjectSettings { Name = "G" }.ToEngineOptions()).EnableFrameCapture);
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--qa-splash", "--headless"]));
        Assert.Throws<ArgumentException>(() => GameHostOptions.Parse(["--qa-splash-out"]));
        var defaults = GameHostOptions.Parse([]);
        Assert.False(defaults.NoSplash || defaults.QaSplash);
        Assert.Null(defaults.QaSplashOutput);
    }
```

In `Tests/MainframeEngine.Tests/Project/GameHostTests.cs`, add a class:

```csharp
/// <summary>When the splash sequence runs (BRUTAL B1): never headless, from the editor, for a specific scene, in CI smoke or with --no-splash.</summary>
public sealed class GameHostSplashTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("--hidden", true)]
    [InlineData("--max-frames 30", true)]
    [InlineData("--fixed-fps 60 --screenshot a.png", true)]
    [InlineData("--headless", false)]
    [InlineData("--editor-port 5123", false)]
    [InlineData("--scene Content/Scenes/Level.mscene", false)]
    [InlineData("--max-frames 30 --hidden", false)]
    [InlineData("--no-splash", false)]
    [InlineData("--qa-splash --editor-port 5123", true)] // the editor's Splash › Preview
    [InlineData("--qa-splash-out shots --scene Content/Scenes/Level.mscene", true)]
    public void TheLaunchFlagsDecideWhetherTheSplashRuns(string args, bool shows) =>
        Assert.Equal(shows, GameHost.ShowsSplash(GameHostOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries))));

    [Fact]
    public void TheModeFollowsTheLoadingSettingsAndNeedsUiAndCards()
    {
        var settings = new ProjectSettings { Name = "G" };
        var none = GameHostOptions.Parse([]);
        Assert.Equal(GameHostSplashMode.BeforeAsyncLoad, GameHost.SplashModeFor(none, settings, hasUi: true));
        Assert.Equal(GameHostSplashMode.BeforeSyncLoad, GameHost.SplashModeFor(GameHostOptions.Parse(["--sync-load"]), settings, hasUi: true));
        Assert.Equal(GameHostSplashMode.None, GameHost.SplashModeFor(none, settings, hasUi: false));
        Assert.Equal(GameHostSplashMode.None, GameHost.SplashModeFor(GameHostOptions.Parse(["--no-splash"]), settings, hasUi: true));
        Assert.Equal(GameHostSplashMode.QaOnly, GameHost.SplashModeFor(GameHostOptions.Parse(["--qa-splash"]), settings, hasUi: true));

        settings.Splash.NonProfit = true;
        settings.Splash.Cards.Clear();
        Assert.Equal(GameHostSplashMode.None, GameHost.SplashModeFor(none, settings, hasUi: true)); // nothing to show
        settings.Splash.Cards.Add(SplashProjectSettings.MainframeCard);
        settings.Loading.Async = false;
        Assert.Equal(GameHostSplashMode.BeforeSyncLoad, GameHost.SplashModeFor(none, settings, hasUi: true));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~SplashFlagsParse|FullyQualifiedName~GameHostSplashTests"`
Expected: FAIL to compile — `'GameHostOptions' does not contain a definition for 'NoSplash'`, `GameHost.ShowsSplash`, `GameHostSplashMode`.

- [ ] **Step 3: The flags**

In `GameHostOptions.cs`, add to the doc list after `--loading-screenshot` (line 24):

```csharp
/// <item><term><c>--no-splash</c></term><description>skip the splash sequence while developing (ADR 0192; a shipped game launched normally always shows it)</description></item>
/// <item><term><c>--qa-splash</c></term><description>run only the splash sequence, then quit (the editor's Project Settings › Splash › Preview)</description></item>
/// <item><term><c>--qa-splash-out &lt;dir&gt;</c></term><description><c>--qa-splash</c>, unskippable, saving each card halfway through its hold as <c>dir/splash-N.png</c> (docs, QA)</description></item>
```

After `LoadingScreenshotPath` (line 76):

```csharp
    /// <summary><c>--no-splash</c>: the splash sequence does not run (ADR 0192; a development flag).</summary>
    public bool NoSplash { get; init; }

    /// <summary><c>--qa-splash</c>: run only the splash sequence (no session start), then quit.</summary>
    public bool QaSplash { get; init; }

    /// <summary><c>--qa-splash-out</c>: the folder for one PNG per splash card (implies <see cref="QaSplash"/> and frame capture).</summary>
    public string? QaSplashOutput { get; init; }
```

In `Parse`, before `case "--record":` (line 172):

```csharp
                case "--no-splash":
                    options = options with { NoSplash = true };
                    break;
                case "--qa-splash":
                    options = options with { QaSplash = true };
                    break;
                case "--qa-splash-out":
                    options = options with { QaSplash = true, QaSplashOutput = Value(args, ref i) };
                    break;
```

In `Validated`, before `return options;`:

```csharp
        if (options.Headless && options.QaSplash)
            throw new ArgumentException("--qa-splash needs a window: --headless shows no splash (drop one of the two).");
```

In `Apply`, after the `LoadingScreenshotPath` frame-capture line (line 214):

```csharp
        if (QaSplashOutput is not null)
            options.EnableFrameCapture = true;
```

- [ ] **Step 4: The host**

In `GameHost.cs`, add `using System.Globalization;` (line 1). Class doc (line 9): "…adds the autoloads, shows the splash sequence (ADR 0192: <see cref=\"SplashSequence\"/>, unless <see cref=\"ShowsSplash\"/> says otherwise) and starts the main scene…". Before the class, add:

```csharp
/// <summary>How <see cref="GameHost"/> runs the splash sequence for a launch.</summary>
internal enum GameHostSplashMode
{
    /// <summary>No splash: the start scene starts as without one.</summary>
    None,

    /// <summary>The splash above the asynchronous start-scene load; it ends when both are done.</summary>
    BeforeAsyncLoad,

    /// <summary>The splash first, then the synchronous load (a synchronous load would freeze the cards).</summary>
    BeforeSyncLoad,

    /// <summary><c>--qa-splash</c>: only the splash, then quit.</summary>
    QaOnly,
}
```

Fields (after line 58): `private bool _syncStartPending;`.

After `LoadingScreen` (line 412):

```csharp
    /// <summary>The splash sequence while it runs (null before, after, or when it does not run).</summary>
    public SplashSequence? Splash { get; private set; }

    /// <summary>
    /// Whether a launch with <paramref name="options"/> shows the splash sequence (ADR 0192). Never with <c>--headless</c>
    /// (nothing is displayed: the BRUTAL licence's own exception), <c>--editor-port</c> (the editor's Play), <c>--scene</c>
    /// (QA, screenshots and tests start a specific scene), <c>--max-frames</c> with <c>--hidden</c> (CI smoke runs) or
    /// <c>--no-splash</c>; always with <c>--qa-splash</c>. A shipped game launched normally always shows it.
    /// </summary>
    public static bool ShowsSplash(GameHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.QaSplash)
            return true;
        return !(options.Headless || options.EditorPort is not null || options.Scene is not null || (options.MaxFrames > 0 && options.Hidden)
                 || options.NoSplash);
    }

    /// <summary>The splash mode for a launch: <see cref="ShowsSplash"/>, a UI to draw it and cards to show, and how the start scene loads.</summary>
    internal static GameHostSplashMode SplashModeFor(GameHostOptions options, ProjectSettings settings, bool hasUi)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (options.QaSplash)
            return GameHostSplashMode.QaOnly;
        if (!hasUi || !ShowsSplash(options) || settings.Splash.ResolvedCards.Count == 0)
            return GameHostSplashMode.None;
        return settings.Loading.Async && !options.SyncLoad ? GameHostSplashMode.BeforeAsyncLoad : GameHostSplashMode.BeforeSyncLoad;
    }
```

In `OnUpdate`, replace the whole `if (_startPending) { … }` block (lines 416–443, up to but not including `LogStartTimes();`) with:

```csharp
        if (_startPending)
        {
            _startPending = false;
            DiscardFrameDelta();
            if (!StartCommandLineRecording())
            {
                Quit(ExitCode.Error);
                return;
            }

            switch (SplashModeFor(HostOptions, Settings, Ui is not null))
            {
                case GameHostSplashMode.QaOnly:
                    StartSplashQa();
                    break;
                case GameHostSplashMode.BeforeSyncLoad:
                    // A synchronous load would freeze the cards: the session starts on the update after they are done.
                    ShowSplash(load: null).Finished += () => _syncStartPending = true;
                    break;
                case GameHostSplashMode.BeforeAsyncLoad:
                    StartAsync();
                    ShowSplash(_startLoad);
                    break;
                default:
                    if (LoadsAsync)
                    {
                        StartAsync();
                    }
                    else if (!StartSync())
                    {
                        Quit(ExitCode.Error);
                        return;
                    }

                    break;
            }
        }
        else if (_syncStartPending)
        {
            _syncStartPending = false;
            if (!StartSync())
            {
                Quit(ExitCode.Error);
                return;
            }
        }
```

and add after `StartAsync`:

```csharp
    // The synchronous start (loading.async false or --sync-load). False when the scene failed to load.
    private bool StartSync()
    {
        // The synchronous load blocks this update: open a loading span now, the recorder's update below closes it. Today the
        // first captured frame comes after this load, and the live origin is anchored there, so the load is not in the video
        // and the span is empty (dropped); it is marked anyway so a recording that already has frames when a synchronous
        // start load runs never shows the hitch as play.
        Tree.Recorder?.OnUpdate(loading: true, paused: false);
        return Session.Start();
    }

    // The splash sequence (ADR 0192) above everything, waiting for load (null: none to wait for).
    private SplashSequence ShowSplash(SceneLoad? load, bool holdsLoading = true, bool? skippable = null)
    {
        var splash = Splash = new SplashSequence { Settings = Settings.Splash, Load = load, HoldsLoading = holdsLoading, Skippable = skippable };
        splash.TreeExited += () => Splash = null;
        Tree.Root.AddChild(splash);
        return splash;
    }

    // --qa-splash: only the sequence (the session starts nothing), then quit; --qa-splash-out saves each card halfway through
    // its hold, unskippable. Frames count (HoldsLoading off), so --max-frames still caps the run.
    private void StartSplashQa()
    {
        if (Ui is null || Settings.Splash.ResolvedCards.Count == 0)
        {
            Log.Error("[Splash] --qa-splash: nothing to show (no UI, or an empty splash.cards with nonProfit).");
            Quit(ExitCode.Error);
            return;
        }

        var output = HostOptions.QaSplashOutput;
        var splash = ShowSplash(load: null, holdsLoading: false, skippable: output is null ? null : false);
        if (output is not null)
        {
            splash.CardShown += index =>
            {
                _capturePath = Path.Combine(output, $"splash-{(index + 1).ToString(CultureInfo.InvariantCulture)}.png");
                CaptureFrame();
            };
        }

        splash.Finished += () => Quit(ExitCode.Ok);
    }
```

(`StartAsync`'s `Completed` handler already quits on a failed load; the splash sees `SceneLoadStage.Failed` and ends itself the same frame.)

- [ ] **Step 5: Callers that must not show the splash**

- `Tests/MainframeEngine.RenderTests.Host/Scenes/GameHostRun.cs` line 40: `List<string> args = ["--no-vsync", "--no-splash"]; // the allocation gate measures the game, not the splash`.
- `build/forest-record.sh` lines 22 and 24: add `--no-splash` after `dotnet Forest.Desktop.dll` (recordings start at the game).
- `build/forest-screenshots.sh` line 19: add `--no-splash` after `dotnet Forest.Desktop.dll`.

- [ ] **Step 6: Smoke-test the flags end to end**

In `build/template-smoke.sh`, after the windowed run's checks (after line 196, before the final `echo`):

```bash
# The splash (ADR 0192): the hidden --max-frames run above skipped it; --headless never shows it; --qa-splash-out runs
# only the sequence and saves one PNG per card (the template's project: @mainframe, @brutal).
if grep -q "Showing the splash" "$log"; then
  echo "template-smoke: the hidden --max-frames run showed the splash" >&2
  exit 1
fi
MAINFRAME_USER_DATA="$work/userdata-headless" dotnet "$out/SmokeGame.Desktop.dll" --headless --max-frames "$frames"
if grep -q "Showing the splash" "$work/userdata-headless/SmokeGame/logs/SmokeGame.log"; then
  echo "template-smoke: the headless run showed the splash" >&2
  exit 1
fi
MAINFRAME_USER_DATA="$work/userdata-splash" dotnet "$out/SmokeGame.Desktop.dll" --qa-splash-out "$work/splash" --hidden \
  --fixed-fps 60 --no-vsync --max-frames 1200
require "$work/splash" splash-1.png splash-2.png
```

and in the header comment (lines 2–6): "…runs it headless (no splash) and with --qa-splash-out (one PNG per splash card)…".

- [ ] **Step 7: Run the tests, the build and the smoke test**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~GameHostOptionsTests|FullyQualifiedName~GameHostSplashTests"
just build
just template-smoke
```

Expected: tests PASS; build 0 warnings; smoke ends `SmokeGame built and ran 30 frames` with `splash-1.png` and `splash-2.png` in `artifacts/template-smoke/splash`.

- [ ] **Step 8: Document it**

In `docs/design/project-and-gamehost.md`, add to the `project.mfproj` section (line 52) a subsection:

~~~markdown
### Splash (`splash`, ADR 0192)

Every game shows a splash sequence before gameplay: Mainframe, BRUTAL (its licence requires the BRUTAL logo before
gameplay), then the game's own cards. Format 4 added the section (the 3 → 4 migration gives older projects the defaults).

```json
"splash": {
  "cards": ["@mainframe", "@brutal", "Content/Brand/studio.png"],
  "background": "#000000",
  "holdSeconds": 2.0,
  "skippable": true,
  "nonProfit": false
}
```

| Key | Meaning |
|---|---|
| `cards` | Ordered. `@mainframe` and `@brutal` are built in; any other entry is a PNG in the game's `Content/`. A missing image is skipped with a warning |
| `background` | sRGB hex (`#RRGGBB`/`#RGB`) behind every card. BRUTAL's white logo shows on a background of relative luminance below 0.5, its black one otherwise |
| `holdSeconds` | Fully visible time per card, clamped to 1.5–10 (a warning names an out-of-range value) |
| `skippable` | A fresh key, click or gamepad press ends a card's hold once it has run 1.5 s; a key held from launch never does |
| `nonProfit` | Unlocks `@brutal` (BRUTAL's README waiver for non-profits; confirm with Ahwoo, brutal@ahwoo.com) |

**The lock**: without `nonProfit`, a `cards` list lacking `@brutal` shows it after `@mainframe` (or first);
`ProjectSettings.Load` warns once per load naming the licence until the file is fixed (the saver writes what you set).
Repeated entries show once, with a warning. `ProjectSettings.Splash.ResolvedCards` is the list shown.

`SplashSequence` (`Src/Project/Splash/`, owned by `GameHost`) draws on an engine `UiLayer` above every game layer
(`int.MaxValue - 1`, under the dev overlay) with `Content/UI/splash/splash.rml`. Every card's image fits a square box of
40 % of the shorter screen side, aspect kept, so no logo is less prominent than another. Per card: 0.3 s fade in, the
hold, 0.3 s fade out, on the frame's delta (paused trees too; one frame counts at most 0.1 s, so a hitch cannot jump a
card). `GameHost` starts the session with `GameSession.StartAsync` on the first update as before and shows the splash above
the loading screen; the sequence ends when the last card has faded out and the load has completed (a spinner shows under
the last card until then). With `loading.async` false or `--sync-load` the splash plays first and the synchronous load runs
after it. A failed load ends the splash and takes the usual error path. While the splash is up `SceneTree.IsLoading` is
true and no input event reaches the game (polled `Input` still sees held keys).

The splash does not run with `--headless`, `--editor-port` (the editor's Play), `--scene`, `--max-frames` with `--hidden`
(CI smoke) or `--no-splash` (development). `--qa-splash` runs only the sequence and quits (the editor's Preview);
`--qa-splash-out <dir>` also saves each card halfway through its hold as `splash-N.png`, unskippable.
~~~

and add `--no-splash`, `--qa-splash`, `--qa-splash-out <dir>` with one line each to the GameHost flags list (line 186).

- [ ] **Step 9: Commit**

```bash
git add MainframeEngine/Src/Project/GameHostOptions.cs MainframeEngine/Src/Project/GameHost.cs Tests/MainframeEngine.RenderTests.Host/Scenes/GameHostRun.cs \
  build/forest-record.sh build/forest-screenshots.sh build/template-smoke.sh docs/design/project-and-gamehost.md \
  Tests/MainframeEngine.Tests/Project/GameSessionTests.cs Tests/MainframeEngine.Tests/Project/GameHostTests.cs
git commit -m "$(printf 'Show the splash sequence in GameHost; add --no-splash and --qa-splash (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 11: Render test `splash`

**Files:**
- Create: `Tests/MainframeEngine.RenderTests.Host/Scenes/SplashScene.cs`
- Modify: `Tests/MainframeEngine.RenderTests.Host/Program.cs` (switch lines 17–70; the "Known" list line 123)
- Modify: `Tests/MainframeEngine.RenderTests/UiRenderTests.cs` (after `TheSettingsMenuMatchesGolden`, line 138)
- Create: `Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0063.png`, `splash_frame0189.png`, `splash_frame0315.png` and the same three under `Goldens/lavapipe/`

**Interfaces:**
- Consumes: `SplashSequence` (Task 9), `RenderTestGame` (`Tests/MainframeEngine.RenderTests.Host/Scenes/RenderTestGame.cs:26-110`), `HostRunner.Run`, `Gates.AssertMatchesGolden(result, frame)` (`Gates.cs:33`).
- Produces: host scene `splash`.

- [ ] **Step 1: Write the failing render test**

In `UiRenderTests.cs`, after `TheSettingsMenuMatchesGolden`:

```csharp
    [Fact]
    public void TheSplashSequenceMatchesGoldenPerCard()
    {
        // BRUTAL B1 (ADR 0192): Mainframe, BRUTAL (white logo on black) and a wide game card, each fitted into the same box,
        // captured halfway through its hold at full opacity.
        var result = HostRunner.Run("splash", Output("splash"), "--capture", "63,189,315", "--size", "640x360", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        foreach (var frame in SplashScene.CaptureFrames)
            Gates.AssertMatchesGolden(result, frame);
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~TheSplashSequenceMatchesGoldenPerCard"`
Expected: FAIL to compile — `SplashScene` not found.

- [ ] **Step 3: The scene**

Create `Tests/MainframeEngine.RenderTests.Host/Scenes/SplashScene.cs`:

```csharp
namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192) on black: @mainframe, @brutal (white logo) and a wide game card
/// (spineboy-pro.png, 1024×256) with 1.5 s holds, unskippable, on the fixed 60 fps step. Each card's 126-frame slot is
/// 18 frames of fade in, 90 of hold and 18 of fade out; <see cref="CaptureFrames"/> are the middles of the holds.
/// Self-check: at each capture the expected card is held and its longer side fills the 40 % box.
/// </summary>
public sealed class SplashScene(HostOptions host) : RenderTestGame(host)
{
    public const string GameCard = "Content/Models/Spine/SpineBoy/spineboy-pro.png";
    public static readonly uint[] CaptureFrames = [63, 189, 315];

    private SplashSequence _splash = null!;

    protected override void LoadScene()
    {
        var settings = new SplashProjectSettings { HoldSeconds = 1.5f };
        settings.Cards.Add(GameCard);
        _splash = new SplashSequence { Settings = settings, HoldsLoading = false, Skippable = false };
        Tree.Root.AddChild(_splash);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var index = Array.IndexOf(CaptureFrames, (uint)gameTime.FrameCount);
        if (index < 0)
            return;
        if (_splash.Timeline is not { } timeline)
        {
            Fail("The splash has no timeline.");
            return;
        }

        if (timeline.Phase != SplashPhase.Hold || timeline.CardIndex != index)
            Fail($"Frame {gameTime.FrameCount}: card {timeline.CardIndex} is in {timeline.Phase}; expected card {index} held.");
        var box = SplashSequence.BoxFraction * MathF.Min(_splash.LayoutSize.X, _splash.LayoutSize.Y);
        var longest = MathF.Max(_splash.CardSize.X, _splash.CardSize.Y);
        if (MathF.Abs(longest - box) > 1f)
            Fail($"Card {index} is {_splash.CardSize.X}×{_splash.CardSize.Y} px; its longer side should fill the {box} px box.");
    }

    protected override void DisposeScene()
    {
    }
}
```

In `Program.cs`, after `"ui-settings-menu" => new SettingsMenuScene(options),` add `"splash" => new SplashScene(options),`, and add `splash, ` after `ui-settings-menu, ` in the "Known:" message.

- [ ] **Step 4: Record and inspect the goldens**

```bash
caffeinate -u -t 600 &
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~TheSplashSequenceMatchesGoldenPerCard"
open Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0063.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0189.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0315.png
just render-tests-linux --filter FullyQualifiedName~TheSplashSequenceMatchesGoldenPerCard
cp artifacts/linux/render-tests/new-goldens/lavapipe/splash_frame00*.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
```

Expected PNGs: 640×360 black; frame 63 the Mainframe logo 144×144 px (×scale) centred; frame 189 BRUTAL's white logo, its longer side 144 px, unaltered; frame 315 the SpineBoy atlas 144×36 px. None faded.

- [ ] **Step 5: Run it to pass**

Run: `dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~TheSplashSequenceMatchesGoldenPerCard"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Tests/MainframeEngine.RenderTests.Host/Scenes/SplashScene.cs Tests/MainframeEngine.RenderTests.Host/Program.cs Tests/MainframeEngine.RenderTests/UiRenderTests.cs \
  Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0063.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0189.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/splash_frame0315.png \
  Tests/MainframeEngine.RenderTests/Goldens/lavapipe/splash_frame0063.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/splash_frame0189.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/splash_frame0315.png
git commit -m "$(printf 'Add the splash render test: one golden per card at full opacity (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 12: The editor's splash shows BRUTAL

**Files:**
- Modify: `MainframeEngine.Editor/Content/Editor/splash.rml` (`#logo` style line 27; markup line 67)
- Modify: `MainframeEngine.Editor/Src/UI/SplashScreen.cs` (class doc lines 6–11; `OnAttach` lines 68–72)
- Modify: `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/editor-splash_frame0020.png` (re-record)
- Test: `Tests/MainframeEngine.Editor.Tests/SplashScreenTests.cs` (create)

**Interfaces:**
- Consumes: `SplashSequence.Fit`, `ReadImageSize`, `MainframeLogo`, `BrutalLogoWhite` (Task 9).
- Produces: `SplashScreen.LogoBox = 168f`; element ids `logo`, `brutal-logo`; class `logo-box`.

- [ ] **Step 1: Write the failing test**

Create `Tests/MainframeEngine.Editor.Tests/SplashScreenTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests;

/// <summary>The editor ships BRUTAL too (ADR 0192): its splash shows the BRUTAL logo beside the Mainframe logo, in boxes of one size.</summary>
public sealed class SplashScreenTests
{
    [Fact]
    public void TheSplashShowsBrutalsLogoInABoxTheSizeOfMainframes()
    {
        using var editor = new HeadlessEditor(configure: o => o with { ShowSplash = true });
        var splash = editor.Workspace.Splash;
        splash.Show("Loading…");
        editor.Tick(2);

        var document = splash.Document;
        var mainframe = document.GetElementById("logo");
        var brutal = document.GetElementById("brutal-logo");
        Assert.False(brutal.IsNull);
        Assert.Equal("/Content/Brand/brutal/LOGO_BRUTAL_WHT.png", brutal.GetAttribute("src")); // white on the navy
        var boxes = document.AsElement().QuerySelectorAll(".logo-box");
        Assert.Equal(2, boxes.Length);
        Assert.Equal(boxes[0].Bounds.Width, boxes[1].Bounds.Width, 0.5f);
        Assert.Equal(boxes[0].Bounds.Height, boxes[1].Bounds.Height, 0.5f);
        Assert.Equal(boxes[1].Bounds.Width, MathF.Max(brutal.Bounds.Width, brutal.Bounds.Height), 1f); // fitted: its longer side fills the box
        Assert.True(brutal.Bounds.Width <= boxes[1].Bounds.Width + 0.5f && brutal.Bounds.Height <= boxes[1].Bounds.Height + 0.5f);
        Assert.Equal(boxes[0].Bounds.Width, mainframe.Bounds.Width, 1f);
        Assert.Empty(editor.RmlMessages);
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~SplashScreenTests"`
Expected: FAIL — `Assert.False() Failure` (no `#brutal-logo`).

- [ ] **Step 3: Implement it**

In `splash.rml`, replace the `#logo` rule (line 27) with:

```css
        #logos { display: block; height: 168dp; margin: 0dp auto 22dp auto; text-align: center; }
        .logo-box { display: inline-block; position: relative; width: 168dp; height: 168dp; margin: 0dp 12dp; }
        .logo-box img { position: absolute; left: 50%; top: 50%; }
```

widen `#center` to fit both boxes (lines 22–23): `width: 420dp;` stays (2 × 168 + 2 × 2 × 12 = 384 dp). Replace `<img id="logo" src="/Content/Brand/logo-512.png"/>` (line 67) with:

```html
        <div id="logos">
            <div class="logo-box"><img id="logo" src="/Content/Brand/logo-512.png" alt="Mainframe Engine"/></div>
            <div class="logo-box"><img id="brutal-logo" src="/Content/Brand/brutal/LOGO_BRUTAL_WHT.png" alt="BRUTAL"/></div>
        </div>
```

In `SplashScreen.cs`: add `using System.Numerics;`; class doc: "The editor's splash screen: the Mainframe logo and the BRUTAL logo (the editor ships BRUTAL, ADR 0192) in boxes of the same size, the "Mainframe Engine" wordmark, …". Add:

```csharp
    /// <summary>The side of the square box each logo fits (dp): Mainframe's and BRUTAL's are equally prominent.</summary>
    public const float LogoBox = 168f;
```

`OnAttach` becomes:

```csharp
    protected override void OnAttach(RmlDocument document)
    {
        SetText("version", "Editor " + EditorBrand.Version);
        FitLogo(document, "logo", SplashSequence.MainframeLogo);
        FitLogo(document, "brutal-logo", SplashSequence.BrutalLogoWhite);
        SetStatus(_status, _progress);
    }

    // Fits a logo into the LogoBox square, aspect kept, centred: the BRUTAL licence allows scaling only.
    private static void FitLogo(RmlDocument document, string id, string contentPath)
    {
        var element = document.GetElementById(id);
        if (element.IsNull || SplashSequence.ReadImageSize(ContentPaths.Resolve(contentPath)) is not { } image)
            return;
        var size = SplashSequence.Fit(LogoBox, image);
        element.SetProperty("width", Dp(size.X));
        element.SetProperty("height", Dp(size.Y));
        element.SetProperty("margin-left", Dp(-size.X * 0.5f));
        element.SetProperty("margin-top", Dp(-size.Y * 0.5f));
    }

    private static string Dp(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "dp";
```

(If `System.Numerics` ends up unused after the edit, drop that using; `dotnet format` reports it.)

- [ ] **Step 4: Run it to pass, re-record the editor-splash golden**

```bash
dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~SplashScreenTests"
caffeinate -u -t 600 &
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~SplashScreenMatchesGolden"
open Tests/MainframeEngine.RenderTests/Goldens/moltenvk/editor-splash_frame0020.png
git rm -q Tests/MainframeEngine.RenderTests/Goldens/lavapipe/editor-splash_frame0020.png
just render-tests-linux --filter FullyQualifiedName~SplashScreenMatchesGolden
cp artifacts/linux/render-tests/new-goldens/lavapipe/editor-splash_frame0020.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~SplashScreenMatchesGolden"
```

Expected: the unit test PASSes; the golden shows both logos side by side in equal boxes above the wordmark; the last run PASSes.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor/Content/Editor/splash.rml MainframeEngine.Editor/Src/UI/SplashScreen.cs Tests/MainframeEngine.Editor.Tests/SplashScreenTests.cs \
  Tests/MainframeEngine.RenderTests/Goldens/moltenvk/editor-splash_frame0020.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/editor-splash_frame0020.png
git commit -m "$(printf "Show BRUTAL's logo beside Mainframe's on the editor splash (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")"
```

---

### Task 13: Help → About lists the credits

**Files:**
- Modify: `MainframeEngine.Editor/Src/UI/MessageDialog.cs` (`MessageRequest` lines 8–46; `Show` lines 66–100)
- Modify: `MainframeEngine.Editor/Content/Editor/message.rml` (style block lines 7–10)
- Modify: `MainframeEngine.Editor/Src/EditorCommands.cs` (usings lines 1–7; `ShowAbout` lines 1263–1285)
- Test: `Tests/MainframeEngine.Editor.Tests/AboutCreditsTests.cs` (create)

**Interfaces:**
- Consumes: `Credits.Entries` (Task 5).
- Produces: `MessageRequest.Scrollable` (bool, init); `internal static string EditorCommands.CreditsText(IReadOnlyList<CreditEntry> entries)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/MainframeEngine.Editor.Tests/AboutCreditsTests.cs`:

```csharp
namespace MainframeEngine.Editor.Tests;

/// <summary>Help › About lists Credits.Entries (the engine's and the third-party sections, BRUTAL's line among them, ADR 0192).</summary>
public sealed class AboutCreditsTests
{
    [Fact]
    public void AboutListsTheCreditsInAScrollingMessage()
    {
        using var editor = new HeadlessEditor();
        editor.Workspace.Commands.Execute("help.about");
        editor.Tick(2);

        var about = editor.Workspace.Message.Current!;
        Assert.Equal("About", about.Title);
        Assert.True(about.Scrollable);
        Assert.Contains(Credits.BrutalNotice, about.Message, StringComparison.Ordinal);
        Assert.Contains("\n\nEngine:\n• Mainframe Engine — ", about.Message, StringComparison.Ordinal);
        Assert.Contains("\n\nThird-party:\n", about.Message, StringComparison.Ordinal);
        Assert.Contains("The FreeType Project", about.Message, StringComparison.Ordinal);
        Assert.True(editor.Workspace.Message.Document.GetElementById("message").IsClassSet("scroll"));
        Assert.Empty(editor.RmlMessages);
        editor.Workspace.Message.Answer(0);
    }

    [Fact]
    public void TheCreditsTextGroupsEntriesBySection()
    {
        var text = EditorCommands.CreditsText([new("Engine", "A", "a notice"), new("Engine", "B", null), new("Studio", "C", null)]);
        Assert.Equal("Credits\n\nEngine:\n• A — a notice\n• B\n\nStudio:\n• C", text);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~AboutCreditsTests"`
Expected: FAIL to compile — `'MessageRequest' does not contain a definition for 'Scrollable'`, `EditorCommands.CreditsText`.

- [ ] **Step 3: Implement it**

`MessageRequest` (after `Icon`, line 30):

```csharp
    /// <summary>The message scrolls within a limited height (long texts: the About box's credits).</summary>
    public bool Scrollable { get; init; }
```

In `MessageDialog.Show`, after `message.SetInnerRml(…)` (line 83), inside the same `if (!message.IsNull)` block (make it a braced block):

```csharp
        if (!message.IsNull)
        {
            message.SetInnerRml(RmlText.Escape(request.Message).Replace("\n", "<br/>", StringComparison.Ordinal));
            message.SetClass("scroll", request.Scrollable);
        }
```

In `message.rml`'s `<style>`, add: `#message.scroll { max-height: 360dp; overflow-y: auto; }`.

In `EditorCommands.cs`, add `using System.Text;` to the usings. In `ShowAbout`, append the credits to the message and make it scroll: change the `Message = …` expression's last line from `EditorBrand.RepositoryUrl.AbsoluteUri,` to `EditorBrand.RepositoryUrl.AbsoluteUri + "\n\n" + CreditsText(Credits.Entries),` and add `Scrollable = true,` after `Icon = "info-circle",`. Add after `ShowAbout`:

```csharp
    /// <summary>The About box's credits: <paramref name="entries"/> by section, one line each, notices verbatim.</summary>
    internal static string CreditsText(IReadOnlyList<CreditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder("Credits");
        foreach (var section in entries.GroupBy(static e => e.Section, StringComparer.Ordinal))
        {
            text.Append("\n\n").Append(section.Key).Append(':');
            foreach (var entry in section)
            {
                text.Append("\n• ").Append(entry.Name);
                if (entry.Notice is { Length: > 0 } notice)
                    text.Append(" — ").Append(notice);
            }
        }

        return text.ToString();
    }
```

- [ ] **Step 4: Run them to pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~AboutCreditsTests|FullyQualifiedName~IconTests"`
Expected: PASS (the icon lint still opens About).

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor/Src/UI/MessageDialog.cs MainframeEngine.Editor/Content/Editor/message.rml MainframeEngine.Editor/Src/EditorCommands.cs \
  Tests/MainframeEngine.Editor.Tests/AboutCreditsTests.cs
git commit -m "$(printf 'List the credits in Help › About (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 14: Project Settings → Splash

**Files:**
- Modify: `MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs` (`Sections` lines 74–88; `Settings` list end line 227; new operations before "Edits, undo, save", line 382)
- Modify: `MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs` (class doc lines 8–14; nav in `Render` lines 87–90; `switch (_section)` lines 94–107; `OnClickElement` switch lines 344–384; new members)
- Modify: `MainframeEngine.Editor/Content/Editor/project_settings.rml` (style block, after line 60)
- Modify: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs` (`EveryProjectSettingRoundTripsThroughTheModel` values, line 308+; new test)
- Modify: `docs/design/editor.md` ("Projects", line 413)

**Interfaces:**
- Consumes: `SplashProjectSettings` (Task 7), `SplashSequence.BrutalLogoWhite/BrutalLogoBlack` (Task 9), `PlayController.PlayMain(IReadOnlyList<string>)` (`PlayController.cs:153`), `FilePickerModel(FilePickerMode, string, IReadOnlyList<string>?, string?)` (`Files/FilePickerModel.cs:29`).
- Produces:

```csharp
// ProjectSettingsModel
public const string SplashLockMessage;
public string? AddSplashCard(string path);
public string? RemoveSplashCard(int index);
public string? MoveSplashCard(int index, int delta);
public string? SetSplashCard(int index, string path);
// ProjectSettingsDialog
public void PreviewSplash();
// element ids: ps-nav-<section lowercased, spaces as '-'>, ps-splash-lock, ps-splash-{up,down,remove,pick}-<i>, ps-splash-add, ps-splash-preview
```

- [ ] **Step 1: Write the failing tests**

In `EveryProjectSettingRoundTripsThroughTheModel`, add to the `values` dictionary after `["saves.thumbnail.height"] = "270",`:

```csharp
            ["splash.background"] = "#102030",
            ["splash.holdSeconds"] = "3",
            ["splash.skippable"] = "false",
            ["splash.nonProfit"] = "true",
```

Add to `ProjectWorkflowTests`:

```csharp
    [Fact]
    public void TheSplashPageLocksBrutalEditsTheCardsAndPreviews()
    {
        _builder.Result = new GameBuildResult(true, [], TimeSpan.FromMilliseconds(1), false, null, []);
        var w = Open().Workspace;
        Assert.True(w.ProjectSettings.Open());
        _editor!.Tick();
        var model = w.ProjectSettings.Model!;
        w.ProjectSettings.SelectSection("Splash");
        _editor.Tick();
        Assert.Empty(_editor.RmlMessages);

        var doc = w.ProjectSettings.Document;
        Assert.False(doc.GetElementById("ps-nav-splash").IsNull);
        var lockIcon = doc.GetElementById("ps-splash-lock");
        Assert.False(lockIcon.IsNull);
        Assert.Contains("BRUTAL licence (2.2)", lockIcon.GetAttribute("data-tooltip") ?? "", StringComparison.Ordinal);
        Assert.True(doc.GetElementById("ps-splash-remove-1").IsClassSet("disabled"));
        Assert.Equal(ProjectSettingsModel.SplashLockMessage, model.RemoveSplashCard(1));

        Directory.CreateDirectory(_project.Abs("Content/Brand"));
        File.Copy(ContentPaths.Resolve("Content/Brand/logo-256.png"), _project.Abs("Content/Brand/studio.png"));
        Assert.Null(model.AddSplashCard("Content/Brand/studio.png"));
        Assert.NotNull(model.AddSplashCard("Content/Brand/studio.png")); // already a card
        Assert.NotNull(model.AddSplashCard("Content/Brand/studio.jpg"));
        Assert.Null(model.MoveSplashCard(2, -1));
        Assert.NotNull(model.MoveSplashCard(0, -1));
        Assert.Equal(["@mainframe", "Content/Brand/studio.png", "@brutal"], model.Current.Splash.Cards);
        Assert.NotNull(model.SetSplashCard(0, "Content/Brand/studio.png")); // a built-in card has no image to change

        Assert.Null(model.Set("splash.nonProfit", "true"));
        Assert.Null(model.RemoveSplashCard(2));
        Assert.Equal(["@mainframe", "Content/Brand/studio.png"], model.Current.Splash.ResolvedCards);
        Assert.Null(model.Set("splash.nonProfit", "false"));
        Assert.Equal(["@mainframe", "@brutal", "Content/Brand/studio.png"], model.Current.Splash.ResolvedCards); // the lock puts it back
        _editor.Tick();
        Assert.Empty(_editor.RmlMessages);

        w.ProjectSettings.PreviewSplash(); // saves, then builds and runs only the splash
        Assert.False(model.IsDirty);
        Assert.Equal(model.Current.Splash.Cards, ProjectSettings.Load(_project.Root).Splash.Cards);
        TickUntil(() => _launcher.Games.Count == 1);
        Assert.Contains("--qa-splash", _launcher.Games[0].Request.Arguments);
        w.Commands.Execute("play.stop");
        TickUntil(() => !w.Play.IsPlaying);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~EveryProjectSettingRoundTripsThroughTheModel|FullyQualifiedName~TheSplashPageLocksBrutalEditsTheCardsAndPreviews"`
Expected: FAIL to compile — `RemoveSplashCard`, `SplashLockMessage`, `PreviewSplash` not found.

- [ ] **Step 3: The model**

In `ProjectSettingsModel.Sections`, after `("UI", "layout"),` add `("Splash", "sparkles"),`. At the end of `Settings` (before `];`, line 227) add:

```csharp
        new("splash.background", "Splash", "Background", SettingKind.Text, static s => s.Splash.Background,
            static (s, v) => s.Splash.Background = v.Trim(),
            "The colour behind every card, sRGB hex (#000000). BRUTAL's white logo shows on dark backgrounds, its black one on light ones."),
        new("splash.holdSeconds", "Splash", "Hold (s)", SettingKind.Number, static s => Num(s.Splash.HoldSeconds),
            static (s, v) => s.Splash.HoldSeconds = ParseFloat(v), "How long each card stays fully visible: 1.5 to 10 seconds (shorter is not legible)."),
        new("splash.skippable", "Splash", "Skippable", SettingKind.Bool, static s => Bool(s.Splash.Skippable),
            static (s, v) => s.Splash.Skippable = ParseBool(v), "A key, click or gamepad button ends a card early, once it has been shown for 1.5 s."),
        new("splash.nonProfit", "Splash", "Non-profit", SettingKind.Bool, static s => Bool(s.Splash.NonProfit),
            static (s, v) => s.Splash.NonProfit = ParseBool(v),
            "A non-profit game may leave the BRUTAL card out (the BRUTAL licence's README waiver). Confirm it with Ahwoo first: brutal@ahwoo.com."),
```

Before `// ── Edits, undo, save` (line 382) add:

```csharp
    // ── Splash (BRUTAL B1, ADR 0192) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The locked BRUTAL card's tooltip, and why it cannot be removed.</summary>
    public const string SplashLockMessage =
        "Required by the BRUTAL licence (2.2): the BRUTAL logo must be shown before gameplay. Check Non-profit to remove it (confirm with Ahwoo: brutal@ahwoo.com).";

    /// <summary>Adds a game card (a <c>Content/…</c> PNG) at the end.</summary>
    public string? AddSplashCard(string path)
    {
        var entry = path?.Trim() ?? "";
        if (SplashProjectSettings.CardError(entry) is { } error)
            return $"The splash card {error}.";
        if (Current.Splash.ResolvedCards.Contains(entry))
            return $"'{entry}' is already a splash card.";
        return EditSplashCards($"Add Splash Card {entry}", cards => cards.Add(entry));
    }

    /// <summary>Removes the card at <paramref name="index"/> (of the cards shown); the BRUTAL card only with Non-profit.</summary>
    public string? RemoveSplashCard(int index)
    {
        var cards = Current.Splash.ResolvedCards;
        if ((uint)index >= (uint)cards.Count)
            return "There is no such splash card.";
        if (cards[index] == SplashProjectSettings.BrutalCard && !Current.Splash.NonProfit)
            return SplashLockMessage;
        return EditSplashCards($"Remove Splash Card {cards[index]}", list => list.RemoveAt(index));
    }

    /// <summary>Moves the card at <paramref name="index"/> by <paramref name="delta"/> places.</summary>
    public string? MoveSplashCard(int index, int delta)
    {
        var count = Current.Splash.ResolvedCards.Count;
        var target = index + delta;
        if ((uint)index >= (uint)count || (uint)target >= (uint)count)
            return "The card cannot move further.";
        return EditSplashCards("Move Splash Card", list => (list[index], list[target]) = (list[target], list[index]));
    }

    /// <summary>Replaces a game card's image with <paramref name="path"/>.</summary>
    public string? SetSplashCard(int index, string path)
    {
        var cards = Current.Splash.ResolvedCards;
        var entry = path?.Trim() ?? "";
        if ((uint)index >= (uint)cards.Count || cards[index].StartsWith('@'))
            return "Only a game card's image can change.";
        if (SplashProjectSettings.CardError(entry) is { } error)
            return $"The splash card {error}.";
        return EditSplashCards($"Set Splash Card {entry}", list => list[index] = entry);
    }

    // Edits the cards as shown (the resolved list), so a file that lacked @brutal gets it back with any card edit.
    private string? EditSplashCards(string name, Action<List<string>> change) => Edit(name, s =>
    {
        var cards = s.Splash.ResolvedCards.ToList();
        change(cards);
        s.Splash.Cards.Clear();
        s.Splash.Cards.AddRange(cards);
    });
```

- [ ] **Step 4: The page**

In `ProjectSettingsDialog.cs`, class doc: add "splash (the card list with the locked BRUTAL card, background, hold, skippable, non-profit, Preview)". In `Render`'s nav loop (lines 88–90), give each item an id:

```csharp
                nav.Append("<div id=\"ps-nav-").Append(NavId(name)).Append("\" class=\"ps-nav").Append(name == _section ? " selected" : "").Append("\" data-section=\"").Append(RmlText.Escape(name))
                    .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span><span>").Append(RmlText.Escape(name)).Append("</span></div>");
```

In `switch (_section)` add before `default:`:

```csharp
                case "Splash":
                    RenderSplash(body, model.Current.Splash, Workspace.Session.ProjectRoot);
                    foreach (var setting in ProjectSettingsModel.Settings)
                        if (setting.Section == "Splash")
                            RenderSetting(body, setting, setting.Get(model.Current), Workspace.Session.ProjectRoot);
                    break;
```

Add after `RenderAutoloads`:

```csharp
    private const string SplashHelp =
        "Shown before the main scene, in order, each logo fitted into the same box. A shipped game always shows them; " +
        "--no-splash skips them while you develop, and Play from the editor never shows them.";

    // The nav item's element id: "ps-nav-input-map" (QA scripts click them).
    private static string NavId(string section) => section.ToLowerInvariant().Replace(' ', '-');

    private static void RenderSplash(StringBuilder rml, SplashProjectSettings splash, string? projectRoot)
    {
        rml.Append("<div class=\"ps-small ps-help\">").Append(RmlText.Escape(SplashHelp)).Append("</div>");
        var cards = splash.ResolvedCards;
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var locked = card == SplashProjectSettings.BrutalCard && !splash.NonProfit;
            rml.Append("<div class=\"ps-splash-card").Append(locked ? " locked" : "").Append("\">");
            if (SplashThumbnail(card, splash, projectRoot) is { } thumbnail)
                rml.Append("<img class=\"ps-splash-thumb\" src=\"").Append(RmlText.Escape(thumbnail)).Append("\"/>");
            else
                rml.Append("<span class=\"icon icon-sm icon-photo ps-splash-thumb\"></span>");
            rml.Append("<span class=\"ps-splash-name mono\">").Append(RmlText.Escape(card)).Append("</span>");
            if (locked)
                rml.Append("<span id=\"ps-splash-lock\" class=\"icon icon-sm icon-lock ps-splash-lock\" data-tooltip=\"")
                    .Append(RmlText.Escape(ProjectSettingsModel.SplashLockMessage)).Append("\"></span>");
            if (!card.StartsWith('@'))
                SplashButton(rml, "splash-pick", i, "photo", "Change Image — pick another PNG of the project", disabled: false);
            SplashButton(rml, "splash-up", i, "arrow-up", "Move Up — show this card earlier", disabled: i == 0);
            SplashButton(rml, "splash-down", i, "arrow-down", "Move Down — show this card later", disabled: i == cards.Count - 1);
            SplashButton(rml, "splash-remove", i, "trash", locked ? ProjectSettingsModel.SplashLockMessage : "Remove Card", disabled: locked);
            rml.Append("</div>");
        }

        rml.Append("<div class=\"ps-add\">")
            .Append("<button id=\"ps-splash-add\" class=\"tool-button small\" data-op=\"splash-add\" data-tooltip=\"Add Card — a PNG of the project, such as your studio's logo\"><span class=\"icon icon-sm icon-plus\"></span></button>")
            .Append("<button id=\"ps-splash-preview\" class=\"tool-button small\" data-op=\"splash-preview\" data-tooltip=\"Preview — save, build and run the game's splash sequence once\"><span class=\"icon icon-sm icon-player-play\"></span></button>")
            .Append("</div>");
    }

    private static void SplashButton(StringBuilder rml, string op, int index, string icon, string tooltip, bool disabled)
    {
        var at = index.ToString(CultureInfo.InvariantCulture);
        rml.Append("<button id=\"ps-").Append(op).Append('-').Append(at).Append("\" class=\"tool-button small").Append(disabled ? " disabled" : "")
            .Append("\" data-op=\"").Append(op).Append("\" data-index=\"").Append(at).Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip))
            .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span></button>");
    }

    // A card's thumbnail: the engine's logos, or the game's PNG when it exists.
    private static string? SplashThumbnail(string card, SplashProjectSettings splash, string? projectRoot) => card switch
    {
        SplashProjectSettings.MainframeCard => "/Content/Brand/logo-48.png",
        SplashProjectSettings.BrutalCard => "/" + (splash.UsesWhiteLogo ? SplashSequence.BrutalLogoWhite : SplashSequence.BrutalLogoBlack),
        _ => IconPreviewPath(projectRoot, card),
    };

    private void PickSplashImage(Action<string> then)
    {
        var root = Workspace.Session.ProjectRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = Directory.Exists(Path.Combine(root, ContentPaths.FolderName)) ? Path.Combine(root, ContentPaths.FolderName) : root;
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Open, start, ["*.png"]), "Choose Splash Card", "Choose",
            path => then(AssetDatabase.Current.ToProjectPath(path)));
    }

    /// <summary>
    /// Splash › Preview: saves unsaved edits (the build copies project.mfproj next to the game), then builds and runs the game
    /// with <c>--qa-splash</c>: only the splash sequence, after which it quits.
    /// </summary>
    public void PreviewSplash()
    {
        if (Model is { IsDirty: true } && !Save())
            return;
        Workspace.Play.PlayMain(["--qa-splash"]);
    }
```

In `OnClickElement`'s `switch (element.GetAttribute("data-op"))`, add before `case "autoload-add":`:

```csharp
            case "splash-up":
                Apply(Model.MoveSplashCard(index, -1));
                break;
            case "splash-down":
                Apply(Model.MoveSplashCard(index, 1));
                break;
            case "splash-remove":
                Apply(Model.RemoveSplashCard(index));
                break;
            case "splash-pick":
                PickSplashImage(path => Apply(Model.SetSplashCard(index, path)));
                break;
            case "splash-add":
                PickSplashImage(path => Apply(Model.AddSplashCard(path)));
                break;
            case "splash-preview":
                PreviewSplash();
                break;
```

In `project_settings.rml`'s `<style>`, after `.ps-autoload button` (line 59):

```css
        .ps-splash-card { display: flex; flex-direction: row; align-items: center; height: 32dp; margin-bottom: 4dp; padding: 0dp 6dp; background-color: #2a2d36; border: 1dp #2a2d36; border-radius: 4dp; }
        .ps-splash-card.locked { border-color: #d97706; }
        .ps-splash-thumb { flex: 0 0 auto; width: 22dp; height: 22dp; margin-right: 8dp; border-radius: 3dp; background-color: #000000; }
        .ps-splash-name { display: block; flex: 1 1 auto; font-size: 11dp; }
        .ps-splash-lock { margin: 0dp 4dp; color: #fbbf24; }
        .ps-splash-card button { width: 22dp; padding: 0dp; margin-left: 3dp; }
```

- [ ] **Step 5: Run the tests to pass**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~ProjectWorkflowTests|FullyQualifiedName~IconTests"`
Expected: PASS (`sparkles`, `lock`, `photo`, `arrow-up`, `arrow-down`, `trash`, `plus`, `player-play` are already in `MainframeEngine.Editor/Content/icons/icons.txt`).

- [ ] **Step 6: Document it**

In `docs/design/editor.md`, "Projects" (line 413), add:

```markdown
**Project Settings › Splash** (BRUTAL B1, ADR 0192; mockup: docs/images/brutal-splash-sequence.svg): the cards in order,
each with its thumbnail, Move Up/Down and Remove icon buttons (and Change Image for a game card); Add Card picks a PNG of
the project. The BRUTAL card shows a lock whose tooltip quotes the licence condition (2.2), and its Remove is disabled until
**Non-profit** is checked (whose tooltip says to confirm with Ahwoo). Background, Hold (s), Skippable and Non-profit are
ordinary settings. Card edits apply to the cards as shown, so a hand-edited file that lacked `@brutal` gets it back with
the first edit. **Preview** saves, builds and runs the game with `--qa-splash` (the sequence only, then the game quits).
**Help › About** lists `Credits.Entries` (the engine's and third-party sections) in a scrolling message, and the editor's
splash shows the BRUTAL logo beside the Mainframe logo in boxes of one size (the editor ships BRUTAL too).
```

- [ ] **Step 7: Commit**

```bash
git add MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs MainframeEngine.Editor/Content/Editor/project_settings.rml \
  Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs docs/design/editor.md
git commit -m "$(printf 'Add the Splash page to Project Settings: the locked BRUTAL card and Preview (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 15: Editor QA and the README screenshot

**Files:**
- Modify: `Tests/QA/editor-walkthrough.qa` (before `quit`, last line)
- Modify: `Tests/QA/readme-screenshots.qa` (after `command play.stop` / `wait-for stopped 30`, before `quit`)
- Modify: `justfile` (`readme-screenshots` name list)
- Create: `docs/images/editor-splash-settings.png` (generated)

**Interfaces:**
- Consumes: element ids from Task 14; QA verbs `command`, `click #id`, `move #id`, `wait`, `capture`, `key`, `cancel`, `wait-for playing|stopped` (`MainframeEngine.Editor/Src/Qa/EditorQaScript.cs:145-501`).
- Produces: QA captures `18-splash-lock` … `23-about-credits`; `docs/images/editor-splash-settings.png`.

- [ ] **Step 1: Add the walkthrough steps** (before the final `quit` of `Tests/QA/editor-walkthrough.qa`)

```text
# BRUTAL B1: Project Settings › Splash (the lock's tooltip, add, reorder and undo, preview) and Help › About's credits.
command project.settings
wait 3
click #ps-nav-splash
wait 3
move #ps-splash-lock
wait 60
capture 18-splash-lock
click #ps-splash-add
wait 3
capture 19-splash-add-picker
cancel
wait 2
click #ps-splash-down-0
wait 3
capture 20-splash-reordered
key Z cmd
wait 3
capture 21-splash-undone
click #ps-splash-preview
wait-for playing 180
wait 60
capture 22-splash-preview
wait-for stopped 60
key Escape
wait 2
command help.about
wait 3
capture 23-about-credits
cancel
wait 2
```

(The undo leaves the dialog unmodified, so Preview builds the Demo without saving `Examples/Demo/project.mfproj`.)

- [ ] **Step 2: Run the walkthrough and review it**

Run: `caffeinate -u -t 900 & just qa-editor`
Expected: exits 0; `artifacts/qa-editor/18-splash-lock.png` shows the tooltip "Required by the BRUTAL licence (2.2) …", `19` the file picker on `Content/`, `20` @brutal first, `21` the original order, `22` the Demo's splash window (or the Output panel with `[Splash] Showing the splash`), `23` About with the "Credits" block. Open each PNG.

- [ ] **Step 3: The README screenshot**

In `Tests/QA/readme-screenshots.qa`, after `wait-for stopped 30` (before `quit`):

```text
# 7. Project Settings › Splash: Mainframe, the locked BRUTAL card (its tooltip), the options.
command project.settings
wait 3
click #ps-nav-splash
wait 3
move #ps-splash-lock
wait 60
capture editor-splash-settings
key Escape
wait 2
```

In the `justfile` `readme-screenshots` recipe, add `editor-splash-settings` to the `for name in …` list.

Run: `just readme-screenshots` and inspect `docs/images/editor-splash-settings.png` (1600×960).

- [ ] **Step 4: Commit**

```bash
git add Tests/QA/editor-walkthrough.qa Tests/QA/readme-screenshots.qa justfile docs/images/editor-splash-settings.png
git commit -m "$(printf 'QA the Splash page and About; capture the Splash page for the README (B1)\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

(If `just readme-screenshots` also rewrote other `docs/images/editor*.png` files with visible differences, review them; commit them only if they are correct, otherwise `git checkout` them.)

---

### Task 16: Docs, README, site, conventions; every gate; the PR

**Files:**
- Create: `docs/images/splash/splash-1.png`, `docs/images/splash/splash-2.png` (generated)
- Modify: `README.md` (intro line 18–21, Features table line 42, "Getting Started" line 90, Tech Stack line 397, License line 597), `docs-site/src/components/landing/LandingHero.astro` (meta list lines 37–42), `docs-site/src/components/landing/Features.astro` (Physics card lines 111–115; new card in "Tools")
- Modify: `CLAUDE.md` (Project Structure `Src/Project` line; GameHost flags in "Game projects (M10)"; Dependencies list)
- Modify: `docs/milestones.md` (B1 row, line 348), `docs/design/future/brutal-backend.md` (status line 3), `memory/context/current-state.md`, `memory/context/progress-log.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the PR.

- [ ] **Step 1: The splash images for the docs**

```bash
mkdir -p docs/images/splash
just demo --qa-splash-out "$PWD/docs/images/splash" --fixed-fps 60 --no-log-file
ls docs/images/splash
```

Expected: `splash-1.png` (Mainframe) and `splash-2.png` (BRUTAL, white on black). Inspect both.

- [ ] **Step 2: README**

- Intro (line 18): "…Jitter2/Box2D physics, …" stays; after the MIT sentence (line 21) add: "Mainframe is **built on [BRUTAL](https://github.com/ahwootech/BRUTAL)** (Ahwoo's C# game framework), and every game made with it inherits BRUTAL's licence: its logo before gameplay, its copyright on a credits screen and its `NOTICE.txt` next to the game. The engine does all three by default (see [What your game inherits](#what-your-game-inherits))."
- Features table: add a row `| Splash & credits | Built on BRUTAL: a splash sequence (Mainframe, BRUTAL, your cards) configured in project.mfproj, a Credits tab in the settings menu, BRUTAL-NOTICE.txt next to every game | [Project and GameHost](docs/design/project-and-gamehost.md#splash-splash-adr-0192) |`; Physics row: "Jitter2 3D (Jolt via BRUTAL is coming as a second backend, B3) + Box2D.NET 2D: …".
- Under "Getting Started", a subsection:

```markdown
### What your game inherits

The engine is MIT, and it is built on BRUTAL, whose licence (BRUTAL License v1.0) adds three conditions to every game:
BRUTAL's copyright line on a credits screen, the BRUTAL logo before gameplay (unaltered, no less prominent than other
logos) and BRUTAL's `NOTICE.txt` alongside the game. Mainframe does all three for you: the settings menu's **Credits** tab,
the splash sequence's locked `@brutal` card (`project.mfproj` `splash`; non-profit games may remove it after confirming
with Ahwoo) and `BRUTAL-NOTICE.txt` in every build and publish. Dedicated servers (`--headless`) show no splash.

| ![The Mainframe card](docs/images/splash/splash-1.png) | ![The BRUTAL card](docs/images/splash/splash-2.png) |
|---|---|

![Project Settings › Splash](docs/images/editor-splash-settings.png)
```

- Tech Stack: add `| Framework | [BRUTAL](https://github.com/ahwootech/BRUTAL) (Ahwoo Limited) | 2026.10 (pinned exactly) |`.
- License (line 597): add a bullet "BRUTAL (`Ahwoo.BRUTAL.*`): BRUTAL License v1.0, **not MIT**; games inherit its conditions (see THIRD_PARTY_NOTICES.md#brutal)."
- The "Editor" section: one sentence and the screenshot reference for Project Settings › Splash.

- [ ] **Step 3: Landing page**

`LandingHero.astro` meta list: replace `<li>MIT</li>` with `<li>MIT</li>` and `<li>Built on BRUTAL</li>`. `Features.astro`: Physics text → `'Jitter2 in 3D (Jolt via BRUTAL coming as a second backend) and Box2D in 2D: rigid, static, character and area bodies, layers, raycasts, MoveAndSlide and interpolation.'`; in the "Tools" group add:

```ts
      {
        icon: 'sparkles',
        title: 'Built on BRUTAL',
        text: 'A splash sequence (Mainframe, BRUTAL, your studio) set in project.mfproj and the editor, a Credits tab in the settings menu and BRUTAL-NOTICE.txt next to every game: licence-compliant by default.',
        doc: 'project-and-gamehost',
      },
```

(`sparkles` is in the editor's icon set, which `TablerIcon.astro` draws.) Run `just docs-build` and check the landing page renders (`npm --prefix docs-site run preview`).

- [ ] **Step 4: CLAUDE.md, milestones, memory**

- `CLAUDE.md` Project Structure, `Src/Project/` line: add "`Splash/` (ADR 0192: `SplashProjectSettings`, `SplashTimeline`, `SplashSequence`: the boot splash with the locked BRUTAL card), `Credits` (engine + THIRD_PARTY_NOTICES entries, `SettingsMenu`'s Credits tab)".
- "Game projects (M10)" flags list: add "`--no-splash` (skip the splash while developing; a shipped game always shows it), `--qa-splash` (only the splash, then quit; `--qa-splash-out <dir>`: a PNG per card)".
- Key Patterns, after "Game projects": "**BRUTAL licence (ADR 0192):** games inherit it. Never remove the `@brutal` splash card, the `Credits` engine entries or the `BRUTAL-NOTICE.txt` copy; the BRUTAL logo files are byte-for-byte copies (`build/brand/brutal-logos.sha256`). `project.mfproj` is format 4 (`splash`)."
- Dependencies: add "- BRUTAL (`Ahwoo.BRUTAL.Core`, `Interop`, `Logging`, `Memory`, `Numerics`, `Strings`, `Jolt`; ADR 0192) — pinned exactly, analyzers excluded; transitive `CommunityToolkit.HighPerformance` 8.4.2 and `Microsoft.Extensions.Logging(.Console)` 10.0.0 pinned centrally. Jolt is referenced but unused until B3".
- `docs/milestones.md` line 348: B1 row status `⬜` → `✅`.
- `docs/design/future/brutal-backend.md` line 3: `**Status:** B1 ✅ (plumbing and attribution); B2–B7 ⬜ planned ·`.
- `memory/context/current-state.md` and `memory/context/progress-log.md`: one entry each: "2026-10-16: BRUTAL B1 (plumbing and attribution) on `brutal/b1-attribution`: pins, BRUTAL-NOTICE.txt, Credits, the splash (format 4), editor splash/About/Splash page."

- [ ] **Step 5: Every gate**

```bash
caffeinate -u -t 3600 &
just build
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test
just test-render
just format-check
just shaders-check
just template-smoke
just template-smoke-package
just l10n-check
just docs-build
```

Expected: every command exits 0: builds with 0 warnings; all unit, editor, video and render tests pass (goldens included, allocation and validation gates clean); format and shaders clean; both smoke runs end with `SmokeGame built and ran 30 frames`. Fix anything that fails in the task that owns it and re-run all gates.

- [ ] **Step 6: Commit, push once, open the PR**

```bash
git add README.md docs-site/src/components/landing/LandingHero.astro docs-site/src/components/landing/Features.astro CLAUDE.md docs/milestones.md \
  docs/design/future/brutal-backend.md memory/context/current-state.md memory/context/progress-log.md docs/images/splash
git commit -m "$(printf 'Document BRUTAL B1: README, landing page, docs and conventions\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
git push -u origin brutal/b1-attribution
gh pr create --base main --title "BRUTAL B1: plumbing and attribution (ADR 0192)" --body "$(cat <<'EOF'
BRUTAL B1 (docs/design/future/brutal-backend.md, ADR 0192): BRUTAL becomes a pinned dependency of the engine package and
every game is licence-compliant by default.

- Packages: `Ahwoo.BRUTAL.*` pinned exactly, analyzers excluded; CommunityToolkit.HighPerformance and Microsoft.Extensions.Logging(.Console) pinned centrally.
- Notices: `BRUTAL-NOTICE.txt` next to every game (build, publish, player archives; checked by template-smoke); THIRD_PARTY_NOTICES BRUTAL and Jolt sections; the template README says what games inherit; editor releases strip `libjolt.so`.
- `Credits` registry (engine, BRUTAL's 2.1 line, third-party rows from the embedded notices) and a Credits tab in `SettingsMenu`.
- `project.mfproj` format 4 `splash` section with the locked `@brutal` card; `SplashSequence` (equal 40 % box, 0.3 s fades, 1.5 s skip floor, waits for the async start load); `--no-splash`, `--qa-splash[-out]`.
- Editor: BRUTAL logo on the splash, Help › About credits, Project Settings › Splash (lock, cards, Preview).
- Render test `splash` (moltenvk + lavapipe); re-recorded `ui-settings-menu` and `editor-splash`.

Licence note for release notes: games made with this engine now inherit the BRUTAL licence (logo in the splash, credits, NOTICE).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Then bind the PR and turn on Auto-fix (`ccd_pr` `set_monitor` with auto_fix and address_comments on), per the user's standing preference.

---

## Self-review against the spec (B1)

| B1 requirement (docs/design/future/brutal-backend.md) | Task |
|---|---|
| Exact `Ahwoo.BRUTAL.*` pins (Core, Interop, Logging, Memory, Numerics, Strings, Jolt) | 1 |
| Transitive CommunityToolkit.HighPerformance 8.4.2, Microsoft.Extensions.Logging/.Console 10.0.0 pinned and recorded (ADR, notices) | 1, 2 |
| Analyzers excluded (`ExcludeAssets="analyzers"`) | 1 |
| Natives through NuGet `runtimes/<rid>/native`, not in `natives.lock` | 1 (references), 2 (distribution doc) |
| `libjolt.so` stripped in `package-editor.sh`; games documented | 3, 2 |
| `NOTICE.txt` → `BRUTAL-NOTICE.txt` via `MainframeEngine.targets` and for project-source games; template-smoke checks build and publish | 2 |
| `THIRD_PARTY_NOTICES.md` BRUTAL section (licence, conditions in plain words) and Jolt's notice; template README | 2 |
| `Credits` API (`Entries`, `Add`, `CreditEntry`); engine entries before game code; third-party entries generated from the notices so they cannot drift | 5 |
| `SettingsMenu` Credits page by section, scrolling, untranslated names/notices | 6 |
| Format bump + own `ProjectMigration`; `splash` section, defaults, `cards`/`background`/`holdSeconds`/`skippable`/`nonProfit`; clamp 1.5–10; empty only with nonProfit | 7 |
| The `@brutal` lock (insert after `@mainframe` or first, warn naming the licence, `ProjectSettings.Splash` resolved list, saver writes what the user set) | 7 |
| `SplashSequence` in `Src/Project/Splash/`, owned by `GameHost`, `UiLayer` at `int.MaxValue - 1`, `Content/UI/splash/splash.rml/.rcss` | 9, 10 |
| Equal 40 % box, aspect kept; logo files byte-for-byte; white/black by luminance; `@mainframe` = `logo-512.png` | 4, 9 |
| Timing: 0.3 s fades, hold, RCSS opacity by class; real delta, not affected by pause | 8, 9 |
| Skipping: key/click/pad from input, consumed, 1.5 s floor, `skippable` false ignores input | 8, 9 |
| Loading behind it (`StartAsync`), last card held with a spinner until the load completes; failed load path | 9, 10 |
| No text; `role="img"`, `alt` | 9 |
| Layer removed when done | 9 |
| Launch flags that skip it (`--headless`, `--editor-port`, `--scene`, `--max-frames`+`--hidden`, `--no-splash`); render tests never run it | 10 (GameHostRun gets `--no-splash`) |
| `--qa-splash` (screenshot per card) for docs and the `splash` golden | 10, 11, 16 |
| Editor `SplashScreen` with the BRUTAL logo, equal boxes | 12 |
| Help → About lists `Credits.Entries` | 13 |
| Project Settings → Splash (icon section, card list, image picker, remove, add, reorder icon buttons, lock with tooltip, Non-profit, background, hold, skippable, Preview) | 14 |
| Testing: unit tests (settings, migration, lock, timeline, flags, credits); render test `splash` both drivers; template-smoke; editor QA (Splash page, About) | 1–15 |
| Docs: project-and-gamehost, game-ui, editor, distribution-nuget, release, THIRD_PARTY_NOTICES; README, landing page, docs site "Built on BRUTAL" with editor screenshots and the licence in Getting started; release-notes callout | 2, 3, 6, 10, 14, 15, 16 |
| CLAUDE.md conventions | 16 |

Name and type consistency checked across tasks: `SplashProjectSettings.{MainframeCard,BrutalCard,MinHoldSeconds,MaxHoldSeconds,ResolvedCards,UsesWhiteLogo,Warnings,CardError}` (Task 7) are what Tasks 8, 9, 10, 14 use; `SplashTimeline`/`SplashPhase` members (Task 8) match their uses in Tasks 9 and 11; `SplashSequence.{Settings,Load,HoldsLoading,Skippable,Timeline,Cards,Document,LayoutSize,CardSize,IsFinished,CardShown,Finished,Fit,FitBox,ReadImageSize,BoxFraction,MainframeLogo,BrutalLogoWhite,BrutalLogoBlack,DefaultLayer}` (Task 9) match Tasks 10, 11, 12, 14; `GameHostOptions.{NoSplash,QaSplash,QaSplashOutput}` and `GameHost.{ShowsSplash,SplashModeFor}`/`GameHostSplashMode` (Task 10) match their tests; `Credits.{Entries,Add,BySection,EngineSection,ThirdPartySection,BrutalNotice,MainframeNotice,CreateEngineEntries}` (Task 5) match Tasks 6 and 13; `ProjectSettingsModel.{SplashLockMessage,AddSplashCard,RemoveSplashCard,MoveSplashCard,SetSplashCard}` and `ProjectSettingsDialog.PreviewSplash` (Task 14) match Task 14's test and Task 15's element ids.

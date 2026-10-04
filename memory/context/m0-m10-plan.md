# Mainframe Engine — M0→M10 autonomous build plan

## Context

`main` holds a working but early Vulkan engine (~9.1k LOC, net10.0): lit/shadowed 3D, sky, Spine, ImGui, ENet/Steam scaffolds. `docs/milestones.md` + 17 proposals in `docs/design/future/` define M0–M11. There are no tests, no CI, no `.github/`, no editor project, and no versioning or packaging. The user wants:
- one branch implementing **M0 through M10 inclusive** (M11 is out of scope)
- built autonomously, with heavy QA, automated CI, and a manual publish workflow
- main locked down (PR-only, tests required, linear history)
- the open issues resolved
- a NuGet distribution plan
- progress tracked in memory
- auto-compaction relied on for long-running context

The user reviews only the finished product: an open PR against `main` with green CI.

## Decisions captured (from user)

- **Publish:**
  - Manual `workflow_dispatch`, callable only by `brogan89`, only off `main`.
  - SemVer with an automatic patch bump. Version comes from git tags, so publishing never commits to main.
  - Produces self-contained **editor** builds for **osx-arm64, win-x64 and linux-x64**, attached to a GitHub Release.
- **Approved new dependencies:**
  - xunit.v3, Microsoft.NET.Test.Sdk, coverlet (test only)
  - NVorbis
  - BenchmarkDotNet (tooling only)
  - ENet natives built in CI (no new package)
  - Previously agreed: Jitter2 2.9.0, Box2D.NET 3.1.654, SoundFlow 1.4.1, GetText.NET 10.0.1
  - **Anything else gets an in-house replacement or comes back to the user.**
- **Final merge:** rebase-merge, keeping the milestone commit series linear on main.
- **QA:**
  - Local computer-use QA on this Mac, plus CI headless Vulkan (lavapipe) golden images.
  - A `justfile` for all local commands (`just` 1.58 is installed).
- **Main history fix:** replace merge commit `7d68bc5` with a squash of PR #3.

## Step 0 — Repo governance (first, in this order)

1. **Rewrite main to linear.**
   - `git checkout -b tmp fcce89d && git merge --squash 7d68bc5 && git commit -m "Add design docs, milestones roadmap and future proposals (#3)"`.
   - Verify `git diff 7d68bc5 tmp` is empty.
   - `git push --force-with-lease=main:7d68bc5 origin tmp:main`, then reset local main.
   - Tell GDW90 to `git fetch && git reset --hard origin/main` (noted in the final report).
2. **Repo settings** (`gh api -X PATCH`):
   - `allow_merge_commit=false`, squash and rebase allowed
   - `delete_branch_on_merge=true`
3. **Ruleset `main-protection`** on `refs/heads/main`. The org is on the Team plan, so rulesets work on this private repo. **No bypass actors**, admins included. Rules:
   - `pull_request`: 0 required approvals, since solo dev and tests are the gate
   - `required_status_checks: ["ci-success"]`, strict (the branch must be up to date)
   - `required_linear_history`, `non_fast_forward`, `deletion`
   - This applies after the history rewrite.
4. **Tag ruleset** on `refs/tags/v*`: block update and deletion, so release tags are immutable.
5. **Environment `release`:**
   - Required reviewer `brogan89`
   - Deployment branch policy: `main` only
6. **Submodule URL:** change `.gitmodules` from SSH to `https://github.com/Mainframe-Games/spine-csharp.git`. It's a public repo, so CI can check it out without a key.
7. **Housekeeping:**
   - Delete the stray empty `/tmp/null_unused_never_written` left by exploration.
   - Install `cmake` and `ninja` via brew for local native builds (RmlUi shim, ENet).
8. **Auto-compaction:**
   - It's on by default, and there's no `autoCompactEnabled` override in `~/.claude.json` or the settings files. Leave it on.
   - To make compaction lossless, keep all durable state in the repo progress log and the plan, not in the conversation.
   - Delegate heavy work to subagents so the orchestrator's context stays small.

## Step 1 — Branch, build hygiene, test and CI scaffolding

Create the branch `feature/m0-m10` from the rewritten main, and open a **draft PR** to main right away so CI runs on every push. It's marked ready at the end.

- **Build properties:**
  - `global.json` pins the .NET 10 SDK (10.0.401, rollForward latestFeature).
  - `Directory.Build.props`:
    - Nullable, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, deterministic builds
    - `Version=0.0.0-dev`; publish overrides it with `-p:Version`
    - The vendored Spine project is exempt from warnings-as-errors.
  - `Directory.Packages.props` (central package management): unify Silk.NET versions to 2.22.0 across the board, and keep Assimp (used in M3).
- **New projects:**
  - `Tests/MainframeEngine.Tests`: xUnit v3 unit tests.
  - `Tests/MainframeEngine.RenderTests`: headless Vulkan, golden PNGs, a validation-error gate and an allocation gate.
  - `Tests/MainframeEngine.Benchmarks`: BenchmarkDotNet with MemoryDiagnoser.
  - Editor tests are added in M10.
- **`justfile` recipes:**
  - `build`, `test`, `test-render`, `shaders` (glslc over all `*.vk.*`)
  - `sandbox`, `editor`, `bench`, `golden-update`, `publish-local rid=…`, `natives`, `qa`
  - `qa` runs the Sandbox and Editor with `--qa-capture <dir>`, which takes automatic screenshots for review.
- **`.github/workflows/ci.yml`** (on `pull_request` and pushes to main):
  - `build-test` matrix on ubuntu-24.04, windows-latest and macos-14:
    - checkout with submodules
    - `dotnet build -warnaserror`
    - `dotnet test` for the unit tests
    - verify shaders: compile every `.vk.*` and fail if a committed `.spv` is stale
    - `dotnet format --verify-no-changes`
  - `render-tests` on ubuntu: `mesa-vulkan-drivers` (lavapipe) + `xvfb`; golden-image and validation-layer checks. Diff PNGs are uploaded as artifacts when a test fails.
  - `ci-success`: aggregate job that `needs` all jobs; this is the single required check.
- **`.github/workflows/natives.yml`** (`workflow_dispatch` and path-filtered PRs):
  - Builds the ENet universal dylib, win and linux natives, and later the RmlUi shim, per RID.
  - The artifacts are committed to `MainframeEngine/runtimes/<rid>/native/` through the branch, so they're reproducible from the workflow.
- **`.github/workflows/publish.yml`** (`workflow_dispatch`):
  - The job runs only `if: github.ref == 'refs/heads/main' && github.actor == 'brogan89'`, in environment `release`.
  - Steps:
    1. Re-run the full CI.
    2. Compute the next version from the latest `v*` tag with a patch bump; start at `v0.1.0` if no tags exist.
    3. Matrix-publish `MainframeEngine.Editor` self-contained, ReadyToRun, `-p:Version=X.Y.Z`, natives included.
    4. Package per platform:
       - macOS: an `.app` bundle in a tar.gz
       - Windows: zip
       - Linux: tar.gz
       - Each package includes `Content/`.
    5. Create tag `vX.Y.Z` and a GitHub Release with generated notes and the artifacts.
  - **No commits to main**, so the ruleset needs no bypass.
  - Concurrency group: `publish`.

## Step 2 — Milestones (dependency-ordered, with parallel lanes)

The dependency graph comes from `docs/milestones.md`. Each proposal doc in `docs/design/future/` is the spec for its milestone. Implement it as written, using the defaults below for open questions. Each open-question decision is recorded as an ADR in `memory/decisions/`.

| Wave | Lanes (parallel subagents, each in its own worktree) |
|---|---|
| W1 | **A:** M0 SDL switch (`sdl-windowing.md`), then M1 stabilization (`renderer-stabilization.md`). **B:** Step 1 infrastructure. **C:** M5 scaffold fixes for ENet and Steam that don't touch the renderer, plus the ENet natives workflow. |
| W2 | **A:** M2 node system + scene serialization + `MainframeEngine.Generators`. **B:** M3 parts independent of M2 (GPU allocator, upload and deletion queues, colour pipeline, build-time shader compilation, `ContentPaths`, pipeline cache). **C:** RmlUi C shim, CMake build and the natives CI (M8 native layer). |
| W3 | **A:** M3 remainder (Material/Mesh/`MeshNode`, Assimp, offscreen and object-ID targets). **B:** M6 physics. **C:** M7 audio. **D:** M5 replication (needs M2). |
| W4 | **A:** M4 Shadows v2 (closes #2). **B:** M8 managed binding + `VulkanUiRenderer` + `UiServer`. |
| W5 | M9 localization, then M10 editor E1→E5. M10 is sequential within itself; inspector, undo and filesystem panels can run in parallel once E1 lands. |

**Defaults for open questions** (each recorded as an ADR):
- SDL2 via Silk.NET 2.x.
- Validation is on in Debug, with an `EngineOptions.EnableValidation` override.
- Reversed-Z is deferred.
- Godot names; JSON scenes; no binary bake.
- Assimp for import; Blinn-Phong for now, PBR later.
- In-house GPU allocator; ACES-fitted tonemap; no bloom.
- Commit `.spv` files, compiled by MSBuild when glslc is present.
- Central package management; PCF only, no EVSM.
- `[Replicated]` via the M2 source generator; server-authoritative.
- ENet is the primary transport; Steam sockets are used when Steam is present.
- Physics: no deterministic mode, single precision.
- NVorbis for OGG.
- Editor is code-driven: no simulate mode, single window.
- RmlUi shim: fork PourrezJ after a licence check. If the licence is incompatible, write it in-house.

**Integration flow:**
- Lane branches are named `lane/<m>-<topic>`. When a lane passes its gate, the orchestrator rebases it onto `feature/m0-m10`. The integration branch is never merge-committed.
- Commit messages are prefixed `M<n>:`.
- Pushes trigger CI on the draft PR.

## Step 3 — Definition of done per milestone (quality gate)

1. CI is green on all 3 OSes with 0 warnings. `dotnet format` is clean.
2. Every new feature has unit tests. Rendering changes add or update golden images (lavapipe). Coverage is reported.
3. The validation layers report **zero** errors in render tests; the test fails on any.
4. **Allocation gate:** steady-state Sandbox frames allocate 0 B of managed memory, measured with `GC.GetAllocatedBytesForCurrentThread` over 300 frames in RenderTests.
5. **Benchmarks:**
   - Covers scene tree process and transform propagation for 10k nodes, serialization round-trip, physics step and UI layout.
   - The baseline is stored in `Tests/MainframeEngine.Benchmarks/baseline.json`. A regression over 10% blocks the milestone locally.
6. A code-review subagent (`superpowers:requesting-code-review`) reviews the milestone diff, and its findings are fixed.
7. Local macOS QA:
   - `just qa` screenshots, plus computer-use passes through the Sandbox and Editor (MoltenVK).
   - Check for ≥120 fps on Apple Silicon in the Sandbox.
8. Docs:
   - The proposal's content moves into the current-state doc, and the milestone row in `milestones.md` is marked ✅.
   - Sync CLAUDE.md and README.
   - Add an ADR, a progress-log entry, and a memory update.

## Step 4 — Distribution plan (document only)

Add `docs/design/future/distribution-nuget.md`. It covers:
- **Packages:**
  - `MainframeEngine`: library, `runtimes/<rid>/native`, and engine `Content/` delivered via `buildTransitive` targets that copy it.
  - `MainframeEngine.Generators`: bundled as an analyzer in the main package.
  - `MainframeEngine.Templates`: `dotnet new mfgame`, which creates `MyGame` + `MyGame.Launcher` (`GameHost`) + `project.mfproj`.
  - Later, a `MainframeEngine.Sdk` MSBuild SDK that owns shader compilation and content rules.
- **Feed:** GitHub Packages (private org feed) first, then nuget.org.
- **Versioning:** package versions share the editor's release tag. The editor's new-project wizard pins the engine version to its own version.
- **Future work:** a `publish-packages` job added to `publish.yml`.

## Step 5 — Issues

- **#1 Skybox:** already implemented in `Src/Rendering/Sky/`.
- **#2 Shadow mapping:** fixed by the M1 per-pass matrices and M4 Shadows v2, with a multi-light golden test.
- The final PR body says `Closes #1` and `Closes #2`, so both close when the user merges.

## Step 6 — Memory and progress logging

- **`memory/context/progress-log.md`** (new; imported from CLAUDE.md):
  - A dated entry per lane and milestone: what landed, the commit SHAs, gate results, and open risks.
  - A "resume here" header that is always current, so compaction or a new session can continue.
- **Other repo memory:**
  - `memory/context/current-state.md` is refreshed at each milestone.
  - ADRs go in `memory/decisions/`.
- **Claude auto-memory** (`~/.claude/projects/.../memory/`):
  - a `project` memory pointing at the plan and progress log
  - `feedback` memories: squash/rebase only, use `just` for local commands, and the publish rules

## Verification (end-to-end, before handing back)

- **CI:** `just build && just test && just test-render` locally; CI `ci-success` green on the PR.
- **Rulesets:**
  - A direct push to main is rejected.
  - A PR with a failing test cannot merge.
  - The merge-commit button is disabled.
- **Publish dry run:** run `publish.yml` from the feature branch. The guard skips it, which proves the main-only check. Then verify the packaging steps with `just publish-local` for each RID locally or via a CI artifact job. The **first real publish is left for the user** to trigger after merging.
- **Editor QA:** the published osx-arm64 editor launches, creates a project from the template, edits a scene (inspector, undo), saves, reloads, and plays out-of-process. Covered by computer-use and an automated editor smoke test.
- **Final state:**
  - The PR is marked ready, with a summary per milestone, QA evidence (screenshots and golden diffs), and benchmark numbers.
  - The user merges it with a rebase-merge.

## Risks / notes

- **Size:** this is very large. If a lane hits a hard external blocker, it's logged, the lane is marked ⚠ in the progress log, and the work continues on the other lanes. Blockers include Steam natives needing partner login or an RmlUi shim licence problem.
- **Unsigned macOS app:** first launch needs right-click → Open. Notarization is out of scope.
- **Editor needs the .NET SDK:** the editor shells out to `dotnet build` for game projects (E4), so users need the .NET 10 SDK installed even though the editor is self-contained.
- **Permissions:** an autonomous run needs this session's permission mode to allow edits and shell commands without prompting.

# Lane agent brief (read first)

You are one lane of the autonomous M0→M10 build. The orchestrator integrates lanes onto `feature/m0-m10`.

## Setup
- You are in an isolated git worktree. Create your branch first: `git checkout -b lane/<id>` (id given in your prompt), based on the current `feature/m0-m10` HEAD (`git reset --hard feature/m0-m10` if your worktree started elsewhere — check with `git log -1`).
- Init submodules you need: `git submodule update --init Plugins/Spine` (native submodules only if your lane needs them).
- Read: `CLAUDE.md`, `memory/context/m0-m10-plan.md` (esp. Step 3 Definition of Done and the "Defaults for open questions"), `memory/context/progress-log.md`, `docs/design/testing.md`, `docs/design/build-and-platforms.md`, plus the design docs named in your prompt.

## Rules
- Build is warnings-as-errors with central package management (`Directory.Packages.props`). Approved NuGet packages only (see plan); anything else → stop and report.
- Never modify `Plugins/Spine` (vendored).
- Use `just` recipes (`just build`, `just test`, `just test-render`, `just shaders`, `just shaders-check`, `just format`, `just qa`). After any shader edit run `just shaders` and commit the regenerated `.spv` + `shaders.lock`.
- High performance: zero managed allocations per steady-state frame (the render-test allocation gate enforces it); no LINQ/closures/boxing in hot paths; `Span`/`stackalloc` (bounded)/pooling where appropriate; avoid `QueueWaitIdle` in frame paths.
- Rock solid: check every Vulkan `Result`, dispose deterministically, no `!` nullable-suppression spam, guard public API args.
- TDD: write tests alongside every feature (unit tests in `Tests/MainframeEngine.Tests`; rendering → render tests with goldens in `Tests/MainframeEngine.RenderTests`, regenerate `moltenvk` goldens with `just golden-update` and LOOK at the PNGs with the Read tool). Validation layers must stay at 0 warnings/errors.
- Render tests and `just qa` need the display awake: wrap with `caffeinate -u -t 600 &` or similar.
- Docs: when a proposal in `docs/design/future/` ships, move its content into the current-state doc in `docs/design/`, update `docs/milestones.md` rows (✅), and repoint links. Record open-question decisions as ADRs in `memory/decisions/NNNN-title.md` (next free number).
- Do NOT edit `memory/context/progress-log.md` (orchestrator owns it) — put your summary in your final report instead.
- Commits: logical, messages prefixed `M<n>:` (or the prefix in your prompt), ending with trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Do not push. Do not touch `main`.

## Before you report done
1. `just build` (Debug) and `dotnet build -c Release -warnaserror` → 0 warnings.
2. `just test` and `just test-render` pass; `just format-check` and `just shaders-check` clean.
3. Self-review your diff (`git diff feature/m0-m10...HEAD`) for bugs, allocations, leaks; fix.
4. Final report (<400 words): branch, commits (SHA + title), what shipped vs the design doc, deviations + why, tests added, gate results, API changes other lanes must know, risks/follow-ups.

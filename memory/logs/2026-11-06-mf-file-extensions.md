---
summary: >-
  Every engine file extension now starts with .mf (ADR 0249): .mscene/.mres/.mmusic/.msong/.msave/.meta/.probes and the
  shader .spvlock/.layout.json renamed across 508 files and ~370 references, with legacy reads in the runtime, a save
  rename at start, an editor migration on open, and FileExtensionRuleTests to keep the rule.
---

# 2026-11-06: Engine file extensions start with `mf`

Branch `feature/mf-file-extensions`, off `origin/main` (f3d2bd8d), in its own worktree, because the main checkout held
G19 phase 3's uncommitted work. Plan: [2026-11-06-mf-file-extensions](../plans/2026-11-06-mf-file-extensions.md).
Decision: [ADR 0249](../decisions/0249-engine-file-extensions-start-with-mf.md). The owner reviewed the full list
first and chose to include the sidecars (`.meta`, `.probes`, `.spvlock`, `.layout.json`).

## What changed

- **`FileExtensions`** (`MainframeEngine/Src/Resources/`): one constant per engine format, `Legacy` (old → new),
  `Matches`, `Upgrade` (scene/resource/music/song paths only) and `FindExisting` (sidecars). `SceneFormat`,
  `AssetDatabase.MetaExtension`, `SaveFile`, `MusicManifest`, `SongFormat`, the shader lock and layout and the capture
  sidecar point at it. `MainframeEngine.Video` does not reference the engine, so `.mftimeline` stays a literal there.
- **Runtime reads old projects:** `ResourceLoader` dispatch, the asset scan (old sidecars skipped, old self-describing
  files read), `ReadOrCreateMeta`/`Move`/`WriteMeta` (old sidecar found, replaced by the new one), `IsBuildOutput`,
  `AssetUid` prefixes, the surface-shader layout and lock, export staging, the l10n extractor. `AssetDatabase.ToAbsolutePath`
  and `ContentPaths.Resolve` send an old path whose file is gone to its renamed file (old game code and `mainScene`).
  `SurfaceShaderBuild` deletes the old layout/lock when it writes the new ones.
- **Saves:** `SaveServer` renames `slot.msave*` (main, `.tmp`, `.bakN`, `.corrupt-…`) to `.mfsave*` at construction,
  skipping a name that is taken or not a slot.
- **Editor:** `FileExtensionMigration` (renames under `Assets/`, rewrites referenced names in text files; a taken name
  is left with a warning) behind a **Rename and Open / Cancel** prompt, `EditorCommands.AfterFileExtensionCheck`,
  chained after the `Content/` check (ADR 0197's pattern). `AssetsFolderMigration` keeps the old names in its text list,
  since it only runs on old projects.
- **Repo:** 508 files `git mv`ed (Demo, Forest, Neon, template, test content; `.mfprobes` stay LFS through the
  `.gitattributes` rules, which were renamed too), and references rewritten in code, scenes, docs, the site, the
  template, `justfile` and build scripts. History (`memory/logs`, `memory/plans`, older ADRs, `progress-log.md`,
  `docs/superpowers`) keeps the old names. The `.meta` CSS class in `video.rml` was kept. Upper-case test data
  (`A.MSCENE`) needed a second pass, because the first `sed` was case-sensitive.
- **Rule:** `FileExtensionRuleTests` scans `const string …Extension…/…Suffix…` in the engine, editor, video, generator
  and tools sources (exempt list with reasons); AGENTS.md and `asset-pipeline.md#file-extensions` say it.
- Fixed a broken link in `docs/design/future/music-editor.md` (`adaptive-music.md` → `../adaptive-music.md`). It was
  there before, and `check.sh` only reported it once the file was in this diff.

## Verified

- `just build`, Release `-warnaserror`, `just format-check`, `just shaders-check` (161 + 20 surface shaders), `check.sh`.
- `just test`: engine 3392/3394 on the first run, where `MusicLinkTests.ReportsGoOutOnlyWhileTheEditorWatches` missed
  its timing window (0.55 against 0.5) while another session was building; it passed 3/3 alone. Editor 1693/1700 (two upper-case
  fixtures fixed, then `FilePickerModelTests` 11/11); video 464/471. New: `LegacyExtensionTests`,
  `FileExtensionRuleTests`, `LegacySaveFileTests`, `FileExtensionMigrationTests`.
- `just test-render` 234/234; Demo 128/128; Neon 215/215; Forest 227/227 after fixing a hard-coded `".meta".Length` (`[..^5]`) in `ForestAssetsTests`, and the same `[:-5]` in both `fetch_assets.py` scripts, which would have deleted every sidecar as an orphan; `just template-smoke` (build, 30 frames, export, headless run).
- `just qa-editor`: the Demo opens with `.mfscene` tabs and `.mfres` resources, no warnings beyond the script's own unsaved `QaSound.mfres`.

## Not done

- The migration prompt itself has no scripted click-through: `qa-editor` opens the already-renamed Demo, and
  `FileExtensionMigrationTests` cover what the prompt runs.
- Open branches (G19 phase 3, navigation avoidance, world-space UI, G6 follow-ups) will need a rebase over the renames.

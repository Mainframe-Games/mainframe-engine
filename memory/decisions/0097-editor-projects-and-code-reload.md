# ADR 0097 — Editor projects: Project Manager, ProjectService and scene-preserving code reload

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4 editor side; lane m10c-projects)

## Context

E4 needs the editor to work on game *projects*: start without one, create one from the `mfgame` template, open a
`project.mfproj` folder, load the game's node types, and pick up code changes without restarting — while keeping the
user's open scenes, unsaved edits and context. The engine side (ADRs 0090–0094) provides `ProjectSettings`,
`GameAssemblyLoader` and the template; the editor had only an interim "project of the opened scene" rule.

## Decision

- **Start-up:** a project argument (folder or `project.mfproj`) opens it; a scene argument keeps the old rule; neither
  shows the **Project Manager** (recent projects in `~/.mainframe/recent_projects.json`, a missing folder flagged and
  removable, New Project, Open Folder, the .NET SDK check with a download link). Tests and scripted runs default to a
  new scene (`EditorWorkspaceOptions.ShowProjectManager` is opt-in; per-user files are opt-in paths).
- **New Project** runs `dotnet new mfgame` from a private template hive (`~/.mainframe/templates`, installed from the
  engine checkout found above the editor, or a packaged `Templates/mfgame`) with `--engine-path`; the user's global
  templates are untouched. The SDK check requires a .NET ≥ 10 SDK.
- **`ProjectService`** owns the open project: `EditorSession.OpenProject` (asset database scanned with `.meta`
  creation), the game library/launcher/solution found by `GameProjectLayout` (real paths: MSBuild fails through
  symlinks), the game assembly loaded into a collectible context (built first when missing or failing to load), a
  debounced watcher on the build output (reload after any build, the editor's or an IDE's) and one on the sources
  ("rebuild needed").
- **Code reload re-creates only scenes that use game code** (`GameCodeScanner`: game or missing node/resource types,
  nested resources included). They are serialized (unsaved edits included), freed, the assembly unloaded and verified
  collected, reloaded, and re-instantiated at the same tab with file, dirty state (`UndoRedo.MarkUnsaved`),
  selection paths and camera; their undo history is dropped (its actions point at freed nodes). Other scenes keep
  their history. A type the new build lacks loads as `MissingNode` with its data.
- **Leaks are named:** when the old context survives, `ReferencePathFinder` walks the editor's objects and engine/editor
  statics and logs the reference path (it found the scene tree's process-list snapshot holding freed game nodes —
  fixed in `SceneTree.ReleaseCodeOf`).

## Consequences

- Reloads take milliseconds (5–15 ms on the QA project) plus the build; the 2 s collection wait only happens on a leak.
- Undo history of game scenes does not survive a reload (documented); selection, view and dirty state do.
- The editor writes `.meta` sidecars into opened projects (the asset database's documented editor behaviour).

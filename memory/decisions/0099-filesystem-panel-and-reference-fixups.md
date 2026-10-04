# ADR 0099 — FileSystem panel: project tree, reference fix-ups by UID, OS trash

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4 editor side; lane m10c-projects)

## Context

The FileSystem panel must browse the project, create and organise files without breaking scenes that reference them,
and never delete data silently. Scenes reference other files three ways: `{"ref": uid, "path": hint}` (external
resources), `{"instance": uid, "path": hint}` (nested scenes), and plain path strings (`File`-hinted properties such
as textures, the sky panorama; `project.mfproj` keys).

## Decision

- **Model** (`ProjectFileSystem`): the project folder (not only `Content/`, so C# and project files are visible) as a
  tree; build/tool folders and `.meta` sidecars hidden by default (toggles); scenes and resources peeked once per
  size+mtime for their root/resource type (icon and family tint), UID and dependencies; badges for unsaved scenes,
  missing dependencies and import errors (invalid JSON, undecodable images). A debounced watcher only flags changes;
  the panel applies them on the main thread (and scans for new `.meta` sidecars).
- **Rename/move** goes through `AssetDatabase.Move` (the `.meta` follows), then `ReferenceFixer` rewrites every
  `.mscene`/`.mres` and `project.mfproj` that points at a moved file: `path` hints of objects whose `ref`/`instance`
  UID moved, and string values equal to a moved project path (whole folders map every file inside). Only changed
  files are written, atomically, in the scene writer's style. Open scenes follow the move (`EditorSession.FilesMoved`).
- **Delete** moves files to the OS trash (`NSFileManager trashItemAtURL` on macOS, `SHFileOperation` with
  `FOF_ALLOWUNDO` on Windows, the freedesktop trash on Linux). When there is no trash for a file, the user is asked
  before a permanent delete. Open scenes inside are closed first.
- **Thumbnails** are decoded and downscaled off the main thread into `<project>/.mainframe/cache/thumbnails`
  (validated by source size+mtime); the grid view shows them.

## Consequences

- Hand comments in a rewritten scene file are lost (JSON re-serialisation); untouched files keep them.
- Cached resources keep their old `ResourcePath` in memory after a move until reloaded (scenes resolve by UID).
- The real-trash test is opt-in (`MAINFRAME_TEST_SYSTEM_TRASH=1`) so `just test` does not litter the user's Trash.

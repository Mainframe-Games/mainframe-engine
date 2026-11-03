---
summary: >-
  The editor's first Output line names its version: "Mainframe Editor v1.2.3 started.".
---

# 2026-11-03: Editor start line shows the version

Branch `fix/editor-start-version`. No plan (one line).

## What changed

- `EditorWorkspace.OnReady` logs `EditorBrand.NameWithVersion + " started."`, so the first Output line reads
  "Mainframe Editor v1.2.3 started." ("v0.0.0-dev" in local builds), the same text as the window title and About box.

## Decisions

- Reuse `EditorBrand.NameWithVersion` rather than format the version again, so the log, title and About box agree.

## Dead ends

- None.

## Verified

- `just build`, the Release `-warnaserror` build, `just format-check` and `just test` pass. `check.sh` passes.

## Not verified

- `just test-render` and `just shaders-check` were not run: no rendering or shader change (and no `slangc` here).
- The line in a running editor, by eye.

## Next

- Nothing.

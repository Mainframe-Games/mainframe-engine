# ADR 0083 — Editor files before projects (E4), crash safety and window-close interception

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 E1 (lane m10a)
- **Spec:** docs/design/editor.md

## Decisions

1. **Project = the folder above the scene's `Content/`** until `project.mfproj` loading lands (E4, lane m10c): opening
   or first-saving a scene points `AssetDatabase.Current` at it and sets the new `ContentPaths.ProjectDirectory`, so
   `Content/…` references (sky panoramas, Spine folders, models) resolve from the project sources, falling back to the
   editor's own content. Opening a scene of another project while scenes of the current one are open is refused.
2. **Atomic writes everywhere**: scenes through `SceneSaver` (temp + rename), editor settings through `AtomicFile`
   (temp + flush + rename). A failed save leaves the old file and the scene dirty, and is reported, never thrown out of
   the frame. Every command runs in a recoverable `try`; failures go to the Output panel and a message box.
3. **Crash recovery copies**: an unhandled exception writes every dirty scene to `~/.mainframe/recovery/` before the
   process ends.
4. **Window close is intercepted with an SDL event filter** (`SDL_QUIT` and window-close are dropped and flagged; Silk
   would otherwise close the window and dispose the engine synchronously). The workspace then asks about unsaved scenes
   (Save All / Don't Save / Cancel) and quits through `Engine.Quit`, which is unaffected by the filter.
5. **Interim log listener**: `Log.MessageLogged` (level + raw text, any thread, exceptions swallowed) feeds the Output
   panel through a concurrent queue drained once per frame. It is deliberately tiny so lane m10b's `ILogSink` can
   replace it.

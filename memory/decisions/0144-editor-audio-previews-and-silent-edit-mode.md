# ADR 0144 — Editor audio previews; edit mode is silent

- **Date:** 2026-10-08
- **Status:** accepted (proposal [sound-designer.md](../../docs/design/future/sound-designer.md), phases 2–3: editor)

## Context

The editor ran without audio (`AudioOptions.Enabled = false`), so nobody could listen to a project's sounds, or design
a ZzFX sound (ADR 0143), without running the game. Turning audio on naively would make every `Autoplay` player in an
open scene start in `OnReady`, because lifecycle callbacks run in edit mode.

## Decision

1. **The editor runs an `AudioServer`** on the engine's default bus layout (`BusLayoutPath = null`): a project that
   mutes or ducks a bus cannot silence previews. No device → the null device, as for games.
2. **Audio nodes are inert in edit mode.** `AudioPlayback.Attach` takes no server while `Tree.EditMode` is set, so
   `Play()` and `Autoplay` do nothing (and nothing is preloaded); `AudioListener3D` does not register as a listener.
   `[Tool]` nodes are not exempt (nobody asked; it can be added later).
3. **One preview voice** (`AudioPreview`, owned by `EditorWorkspace`): a non-positional one-shot on Master with
   `ProcessMode.Always`, polled once per frame for its end; Play (running the game) stops it. The FileSystem panel
   (menu, double-click, stop badge), inspector `AudioStream` rows and the sound designer play through it.
4. **`ICustomInspector.OnPropertyChanged`** (default method) reports every committed change once per history entry —
   row commits, the end of a merged slider drag, and (with a null property) undo, redo and actions — so a custom
   inspector can react to edits made by the generated rows. A custom header is not rebuilt per drag tick.
5. **The sound designer** (`ZzfxStreamInspector`) is a custom inspector over engine code (`ZzfxPresets`,
   `ZzfxParameters`, `WavWriter`); preset/Randomize/Mutate/Paste set all parameters as one `CompositeAction`. Its
   Auto-play preference lives in `editor_layout.json` (`SoundDesignerAutoPlay`). Custom inspectors that need the editor
   implement the internal `IWorkspaceInspector`; the inspector panel binds the workspace before each call.

## Consequences

- Opening a scene never makes a sound; games are unaffected (`EditMode` is false outside the editor).
- Previews play at 0 dB on Master; a "Preview volume" setting is the follow-up if that proves loud.
- Seven Tabler icons were added to the editor atlas (coin, flame, activity, sparkles, dice-5, wand, clipboard).
- QA scripts gained `inspector-header <action>`, `fs-select <path>` and `delete-file <path*>`.

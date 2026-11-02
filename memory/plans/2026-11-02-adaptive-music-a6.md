# Plan: Adaptive music A6 — editor link, dev overlay, Demo, docs

**Status:** done, see [log](../logs/2026-11-02-adaptive-music-a6.md)
**Docs:** [Adaptive music (was the proposal)](../../docs/design/adaptive-music.md) ("Editor link and dev overlay", the A6 row
of "Delivery", "As built: A1"–"A5"), [ADR 0196](../decisions/0196-adaptive-music-baked-stems.md),
[Audio](../../docs/design/audio.md), [Editor → Song tab](../../docs/design/editor.md),
[Developer overlay](../../docs/design/dev-overlay.md), [Projects and GameHost](../../docs/design/project-and-gamehost.md),
[Demo](../../docs/design/demo.md)
**Started:** 2026-11-02

## Goal

Adaptive music is finished: a game's `AdaptiveMusicPlayer`s can be watched and driven from the editor's Adaptive panel
(the Target menu) and from a Music panel in the F12 dev overlay; baking while a game plays reloads the music in the
game; the Demo has an "Adaptive music" scene; and the feature's docs describe what was built.

## Not in scope

- Random variation sets (v1.1, decided question 8), tempo per section, `sync` entry without decoding forward.
- Watching music in exported (non-editor) builds: the link only exists with `--editor-port`.

## Decisions taken from reading the code

- **Protocol version 6, not 5.** Version 5 already shipped the crash report (G15) and the profiler (G10). A version 5
  game drops its link on an unknown command kind (`EditorLinkProtocol.TryDecode` refuses it and `ReadCommands` breaks),
  so the editor sends music commands only to games whose hello reports 6 or later (`EditorLinkProtocol.MusicVersion`).
- **`ReloadMusic` carries the project root.** Its argument is `<mus_uid>\n<project folder>`. A game launched by the editor
  reads `Assets/` from its build output, but a bake writes into the project's source folder, so the game reads the new
  manifest and its stems from the folder the editor names. The switch happens at the next pass boundary (`Bar(1)`), the
  section restarting at its downbeat in the new bake, parameters carried over.
- **The dev overlay Music panel** is one RmlUi list of instances, each with nested lists of parameters, layers and
  stingers (`RmlStructType.List`), refreshed at the overlay's rate without allocating.

## Steps

- [x] Protocol 6 (`MainframeEngine/Src/EditorLink/`): commands `WatchMusic`, `UnwatchMusic`, `SetMusicParameter`,
  `TriggerMusicStinger`, `GoToMusicSection`, `ReloadMusic`; the `Music` message (latest-wins, up to 8 instances: node
  path, manifest UID, song UID, section, bar.beat, pending and its countdown, parameter values, layer gains);
  `EditorLinkClient.ReportMusic`. Verified by: round-trip and malformed-frame tests in the engine's EditorLink tests.
- [x] Game side: `GameSession` applies the music commands to the tree's players and reports at up to 10 Hz, only while
  watched and on change (`MusicLinkReporter`); `ReloadMusic` loads in the background and switches at the next pass.
  `AudioServer` lists the playing instances. Verified by: `GameSession` tests on a headless tree with the manual null
  audio device (commands drive a real instance; reports only while watched; reload switches and keeps parameters).
- [x] Dev overlay Music panel (`DevOverlayPanels`, added when the first instance plays): per instance the song, section,
  bar.beat, pending with countdown, layer gain bars, a control per parameter, Fire per stinger, Next. Verified by: a
  dev overlay test (panel appears on play, controls call the instance, steady refresh allocates nothing).
- [x] Editor: `PlayService` watches music while an Adaptive tab shows, keeps each instance's last `Music` report, sends
  the music commands (protocol-gated) and `ReloadMusic` after every bake; the Adaptive panel's Target menu (Editor and
  every connected game's instance of this song), the Now row, sliders, Fire and the Sections lane following the target,
  Copy to Defaults (one undo entry). Verified by: editor tests with a fake game link (target listing by song UID,
  commands sent, version 5 game gets none, Copy to Defaults undoes in one step).
- [x] Demo `adaptive_music` scene: an adaptive song (`Assets/Music/adaptive_theme.msong`, ZzFX tracks in three layers)
  baked with `just bake-song`, an `AdaptiveMusicPlayer`, a visualiser that pulses on beats and shows layer gains, and a
  panel with an intensity slider, an area select, a stinger button and section/bar readouts; registered in
  `DemoScenes`, scene written by `--write-scenes`. Verified by: `Demo.Tests` (the scene plays on the null device;
  area = Combat moves to Combat on the next bar; intensity raises the layer gains; the stinger fires), the scene-file
  contract test, `just demo-screenshots` for the new PNG inspected.
- [x] Docs: `docs/design/adaptive-music.md` (the proposal rewritten as built, with "Changes from the proposal"; the
  proposal file removed and links repointed), `audio.md` Adaptive music section, `editor.md` Song tab, `dev-overlay.md`,
  `project-and-gamehost.md` (protocol 6), `demo.md`, `AGENTS.md`, README and the landing page's DAW section with an
  editor screenshot of the Adaptive panel, milestone row ✅, state file, log. Verified by: `check.sh`, `just docs-build`.
- [x] Gates: `just build`, the Release build, `just test`, `just test-render`, `just format-check`, `just shaders-check`.

## Done when

- The adaptive music row in `docs/milestones.md` is ✅ and `docs/design/future/adaptive-music.md` no longer exists.
- `Demo.Tests` show the `adaptive_music` scene changing section when `area` changes and layer gains when `intensity`
  changes, and its screenshot is in `docs/images/demo/`.
- Tests show a version 6 game applying every music command and reporting only while watched, and a version 5 game never
  being sent one.
- Every gate in `.agents/workflow.md` passes locally and a PR is open.

## Open questions

- None blocking. How the Demo song sounds is a human check (audio feel is on the workflow's list).

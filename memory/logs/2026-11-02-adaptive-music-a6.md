---
summary: >-
  Adaptive music A6, the last step: editor link protocol 6 (Target menu, reload after a bake), the dev overlay's Music
  panel, the Demo's Music scene, and the proposal moved to docs/design/adaptive-music.md. The milestone row is ✅.
---

# 2026-11-02: Adaptive music A6 — editor link, overlay, Demo, docs

Branch `feature/adaptive-music-a6`. Plan: [Adaptive music A6](../plans/2026-11-02-adaptive-music-a6.md).

## What changed

- Editor link protocol 6: `WatchMusic`, `UnwatchMusic`, `SetMusicParameter`, `TriggerMusicStinger`, `GoToMusicSection`,
  `ReloadMusic` and the latest-wins `Music` message (`EditorLinkMusicInstance`); `EditorLinkClient.ReportMusic`;
  `MusicLink` on the game side (commands by node path, reports at most 10 Hz while watched and on change, reload on a
  worker, switched at the next `Bar(1)`).
- Engine API: `AudioServer.MusicInstances`, `AdaptiveMusicInstance.Player` and `PendingSeconds` (also on the player),
  `AdaptiveMusic.LoadFromProject`.
- Dev overlay Music panel (`DevOverlayMusic`): added once music plays; per instance section, bar.beat, pending and
  countdown, layer gains, a control per parameter, Fire, Next.
- Editor: `PlayService.WatchMusic` (on while an adaptive song is the active tab), music commands gated on
  `PlayInstance.CanMusic`, `ReloadMusic` after every bake; the Adaptive tab's Target menu (`GameMusicTarget`,
  `SongTab.GameTarget`): audition values and stingers go to the game, the Now row, meters, ghosts, firing rule, Sections
  lane and playhead follow it; Copy to Defaults (`SongDocument.SetParameterDefaults`, one undo entry). QA verbs
  `song target <n>` and `wait-for music`.
- Demo: the Music tab (`adaptive_music`): `Assets/Music/adaptive_theme.msong` (Demo loop instruments, Intro → Explore /
  Combat, Hit stinger, Perc and Lead layers) baked with `just bake-song`, `MusicVisualizer2D`, `AdaptiveMusicPanel`;
  Spanish strings; nav tabs at 14 dp so sixteen fit (every Demo screenshot re-taken).
- Docs: `docs/design/future/adaptive-music.md` moved to `docs/design/adaptive-music.md` with "As built: A6" and
  "Changes from the proposal"; `audio.md` Adaptive music section; `editor.md` Target; `dev-overlay.md`;
  `project-and-gamehost.md` protocol 6; `demo.md`; `AGENTS.md`; README (feature row, Showcase, editor table) and the
  landing page (DAW section, Audio feature, overlay); milestone ✅.

## Decisions

- Protocol 6, not the proposal's 5: G15 and G10 shipped version 5 first, and a version 5 game drops its link on an
  unknown command kind, so the editor gates every music command on the hello's version. No ADR: it follows the
  protocol's own versioning rule.
- `ReloadMusic` carries the editor's project folder: a game started by the editor reads its build output's `Assets/`,
  which the bake does not touch. The game reads the manifest and its stems by path under that folder.
- Reload is a fresh `Play` of the same section at the next pass (short prefill, no old tails), not a seamless
  hand-over: enough for a dev tool, recorded under "Changes from the proposal".
- The overlay's labeled parameters are buttons and the labels a flat list: RmlUi bindings allow one level of nested
  lists. The Next button falls back to the first rule from the section when none matches.
- The Demo tab is "Music", not "Adaptive Music": sixteen tabs did not fit the nav bar.

## Dead ends

- `Label2D` captions in the Demo scene drew nothing: it needs a `Font` resource, and the engine's Lato is not a Demo
  asset with a UID. The visualiser loads the font at run time and draws the captions itself.
- A label button inside a nested `data-for` got a stale outer index (`pi` was 0 for the second parameter). Every event
  now names its rows by indices the rows carry.
- `GameSession.Update` returned before the music link's update without a socket, so a reload applied by `Apply` never
  finished; the music update now runs first.

## Verified

- Tests: `EditorLinkProtocolTests` (music frames, commands, malformed frames, version 6), `MusicLinkTests` (commands on a
  real player, reports only while watched, reload from another project folder heard), `DevOverlayMusicTests` (panel,
  buttons, Next, 0-byte refresh), `AdaptiveTargetTests` (watch, gating on version 5, Target, playhead, Copy to Defaults,
  fallback), `AdaptiveMusicSceneTests` in the Demo (bake loads, sections on the bar, layers, stinger, stop/start).
- Gates: `just build`, the Release build, `just test` (3159 engine, 1779 editor, 460 video), Demo.Tests (103),
  `just format-check`, `just shaders-check`, `just l10n-check`, `just docs-build`, `check.sh`. `just test-render`: 220
  of 222, the two failures being `editor-music-adaptive` and `editor-music-curves`, whose Now row gained the Target row;
  both re-recorded on moltenvk and on lavapipe (Docker), inspected, and the editor render tests pass on both (14 of 14 on
  lavapipe).
- The editor screenshot (`editor-song-adaptive.png`) was captured by the QA script with the Demo's Music scene running:
  "Explore · bar 1.1 · in the game", "Next: Combat in 1.6 s", Target "Game #1 · Music". All Demo PNGs inspected.

## Not verified

- The lavapipe `editor-music-curves` golden shows "1 xrun" in the toolbar: Docker's x86 emulation gives one on every
  run, where the old CI-recorded golden showed "0 xruns". The few pixels are inside the 0.5 % tolerance, so a native run
  still passes; re-record it from CI when convenient.

- How the Demo song sounds (a human check), and the reload's audible switch on real hardware.
- The full `just readme-screenshots` run: only the new capture was taken (a focused copy of its steps).

## Next

- Random variation sets (v1.1, decided question 8) if a game asks for them.

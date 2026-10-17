# ADR 0192 — Music mode: the music editor over the whole window

- **Date:** 2026-10-17
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/editor.md#editor-modes, docs/design/editor.md#song-tab-music-editor

## Context

The music editor (song tabs, `.msong`; ADRs 0145–0148) opened as a game-mode tab: the song panel filled only the centre
view, with the Scene tree, FileSystem, Inspector and Output docked around it, and the transport row took the top of the
panel. A DAW needs the width and height: the arrangement and the piano roll are long timelines. ADR 0191 left room for
a third `EditorMode` for this.

## Decision

- **A third mode, `EditorMode.Music`** (named Music, toggle order Game | Music | Video). `SongTab.Mode` is Music, so
  song tabs show only in music mode's tab strip, and opening a song switches to it (the existing auto-switch).
- **Whole window**, like video mode: the Scene tree, FileSystem and Inspector are hidden; the Output shows only with
  music mode's own View › Show/Hide Output (`EditorLayoutSettings.MusicShowOutput`, off by default). Audio clips come in
  through the clip file picker (double-click an audio lane); FileSystem drops still work in code but have no source here.
- **The whole transport moves into the toolbar** (`#music-tools`, `ToolbarPanel.UpdateMusic`, `song.*` commands):
  rewind, play/stop, stop, record, loop, tempo, time signature, position, snap, input quantize, underruns, read-only
  badge, Render. The song panel keeps the track headers, canvases, switcher and mixer.
- **Own menus:** File, Edit, View, Song, Help (Project and Run are game mode's), and a music shortcut table
  (`MusicShortcutFor`).
- **Songs are project tabs** (`EditorMode.UsesProject`: game and music, not video): opening, switching or closing a
  project asks about unsaved songs with the scenes and closes them; opening a project in music mode stays in it.
- **Empty state:** New Song, Open Song and the project's songs (newest first); without a project, a Project Manager
  button. The mode persists (`EditorSettings.LastMode`) and comes back behind the Project Manager at start-up.
- The ZzFX sound designer stays the Inspector's (game mode).

## Alternatives considered

- **Keep songs openable in both modes:** two tab strips sharing tabs, and special cases in every per-mode list.
- **Keep the FileSystem dock in music mode** for drag-and-drop: not the whole window; the picker already covers adding
  clips.
- **Leave the transport in the song panel:** the toolbar would be empty in music mode, and the panel lose a row.

## Consequences

- Song tabs can no longer sit beside scenes in one tab strip; switching modes switches between them.
- The music editor gained a steady-frame allocation gate (`editor-music-song` golden). It found and removed
  per-frame allocations that predated the mode: outlines drawn through the canvas's polyline path, a delegate and a
  file-name string in `SongPlayer.Update`, and a closure in `PluginRack.Maintain`.

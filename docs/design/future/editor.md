# Proposal: editor, after M10

**Milestone:** after M10 · **Status:** ⬜ open items only. Everything the M10 editor proposal planned (E1 shell, E2 tree
and inspector, E3 viewport, E4 projects and Play, E5 polish) has shipped and is described in the current-state
[Editor](../editor.md) doc; the original proposal is in the git history of this file.

## Open items

| Item | Notes |
|---|---|
| Remote scene tree | Inspect the running game's tree and properties over the editor link (Godot's "Remote" tab). The link carries hello, logs, status and commands today. |
| Docking and multi-window | Tear-off panels and editor windows on other monitors, once SDL multi-window is available. Layout today: fixed regions with persisted splitters. |
| Export | File › Export with per-platform presets. Proposal: [Game export](game-export.md) (G5). |
| Animation panel | Timeline for `Animation` resources. Proposal: [Keyframe animation](keyframe-animation.md) (G1). |
| Shared widgets | Move the editor's controls (`tree-view`, `property-*` editors, `splitter`, `tabs`, `context-menu`) into core content so games' settings menus can use them. |
| 2D content | Tile maps with paint tools in the 2D view. Proposal: [2D content](2d-content.md) (G2b). Animated sprites shipped (G2a): [Animated sprites](../animated-sprites.md). |
| Editor previews and handles | Collision shape handles, camera FOV handles and "create collision from mesh", on the handle API that shipped with G7 ([Editor → Range handles](../editor.md#range-handles-and-the-handle-api): light and audio ranges; Save as .mres / Make Unique are [Resource actions](../editor.md#resource-actions)). |

## Open questions

- Should the editor itself become scene-driven (a `GameHost` project) rather than the code-driven `EditorApp`?
- How should undo history survive a code reload (today the scenes using game code lose theirs)?

## Related

[Editor](../editor.md) · [Project & game host](../project-and-gamehost.md) · [Milestones](../../milestones.md) ·
[Game UI](../game-ui.md)

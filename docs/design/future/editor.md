# Proposal: editor, after M10

**Milestone:** after M10 · **Status:** ⬜ open items only. Everything the M10 editor proposal planned (E1 shell, E2 tree
and inspector, E3 viewport, E4 projects and Play, E5 polish) has shipped and is described in the current-state
[Editor](../editor.md) doc; the original proposal is in the git history of this file.

## Open items

| Item | Notes |
|---|---|
| Remote scene tree | Inspect the running game's tree and properties over the editor link (Godot's "Remote" tab). The link carries hello, logs, status and commands today. |
| Simulate mode | An in-process mode that runs physics and scripts in the edited world, next to out-of-process Play. Proposal: [Editor viewport tools](editor-viewport-tools.md) (G7). |
| Box selection | Drag a rectangle in the viewport (3D: an ID-target region read back; 2D: shape bounds). Proposal: [Editor viewport tools](editor-viewport-tools.md) (G7). |
| Multi-node gizmo | Move/rotate/scale several selected nodes around a shared pivot; today the gizmo acts on the last selected node (the inspector already edits several). Proposal: [Editor viewport tools](editor-viewport-tools.md) (G7). |
| Docking and multi-window | Tear-off panels and editor windows on other monitors, once SDL multi-window is available. Layout today: fixed regions with persisted splitters. |
| Export | File › Export with per-platform presets. Proposal: [Game export](game-export.md) (G5). |
| Animation panel | Timeline for `Animation` resources. Proposal: [Keyframe animation](keyframe-animation.md) (G1). |
| Shared widgets | Move the editor's controls (`tree-view`, `property-*` editors, `splitter`, `tabs`, `context-menu`) into core content so games' settings menus can use them. |
| 2D content | Animated sprites (sprite sheets) and tile maps with paint tools in the 2D view. Proposal: [2D content](2d-content.md) (G2). |
| Editor previews and handles | Draggable range handles for lights and audio, collision shape handles, "create collision from mesh", "make unique" / "save as .mres" for inline resources. Range handles and the inline-resource actions: [Editor viewport tools](editor-viewport-tools.md) (G7). |

## Open questions

- Should the editor itself become scene-driven (a `GameHost` project) rather than the code-driven `EditorApp`?
- How should undo history survive a code reload (today the scenes using game code lose theirs)?

## Related

[Editor](../editor.md) · [Project & game host](../project-and-gamehost.md) · [Milestones](../../milestones.md) ·
[Game UI](../game-ui.md)

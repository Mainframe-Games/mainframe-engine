# Proposal: editor, after M10

**Milestone:** after M10 · **Status:** ⬜ open items only. Everything the M10 editor proposal planned (E1 shell, E2 tree
and inspector, E3 viewport, E4 projects and Play, E5 polish) has shipped and is described in the current-state
[Editor](../editor.md) doc; the original proposal is in the git history of this file.

## Open items

| Item | Notes |
|---|---|
| Remote scene tree | Inspect the running game's tree and properties over the editor link (Godot's "Remote" tab). The link carries hello, logs, status and commands today. |
| Simulate mode | An in-process mode that runs physics and scripts in the edited world, next to out-of-process Play. |
| Box selection | Drag a rectangle in the viewport (3D: an ID-target region read back; 2D: shape bounds). |
| Multi-node gizmo | Move/rotate/scale several selected nodes around a shared pivot; today the gizmo acts on the last selected node (the inspector already edits several). |
| Docking and multi-window | Tear-off panels and editor windows on other monitors, once SDL multi-window is available. Layout today: fixed regions with persisted splitters. |
| Shared widgets | Move the editor's controls (`tree-view`, `property-*` editors, `splitter`, `tabs`, `context-menu`) into core content so games' settings menus can use them. |
| 2D content | Sprites and tile maps in the 2D view (the engine has no sprite renderer yet). |
| Editor previews and handles | Audio preview, draggable range handles for lights and audio, collision shape handles, "create collision from mesh", "make unique" / "save as .mres" for inline resources. |

## Open questions

- Should the editor itself become scene-driven (a `GameHost` project) rather than the code-driven `EditorApp`?
- How should undo history survive a code reload (today the scenes using game code lose theirs)?

## Related

[Editor](../editor.md) · [Project & game host](../project-and-gamehost.md) · [Milestones](../../milestones.md) ·
[Game UI](../game-ui.md)

# ADR 0253 — UiPanel3D input: screen layers first, then the pointer's panel and the focused panel

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G11 (G11.6 input)
- **Docs:** docs/design/world-space-ui.md#panel-input, memory/plans/2026-11-06-world-ui-panels.md

## Context

A panel takes pointer input from a 3D ray (the mouse cursor, a crosshair while the mouse is raw, or code such as VR
hands), and keyboard, text and gamepad input once it has focus. The proposal left open question 6 open: should a
focused panel see keys before screen layers, and how do `InputBeforeUi` nodes interact with panels?

## Decision

- **Order** (open question 6's default): `InputBeforeUi` nodes, the screen layers top to bottom (a HUD menu over a
  terminal wins), then panels, then game nodes. A layer that consumes the pointer takes it off Mouse-mode panels.
- **Pointer modes:** `Mouse` (the cursor's ray from the panel viewport's camera, not while the mouse is raw), `Crosshair`
  (the screen-centre ray, recomputed every frame, also while raw; `ClickAction` read through the tree's input map
  presses and releases the left button), `Manual` (`PushPointer`/`PushButton`/`PushWheel`). The nearest front-facing quad
  hit within `MaxInteractDistance` wins, if a physics ray that excludes the panel's nearest `CollisionObject3D` ancestor
  hits nothing first (`RequireLineOfSight`). Rays only hit the quad: custom meshes are G11.9.
- **Capture and focus:** a press on a panel is consumed (the game does not fire through a terminal) and focuses the
  panel. The panel then gets moves along its plane past the edge until release. A press elsewhere releases the focus,
  and so does Escape when no text field has it. Keys, text and gamepad navigation go to the focused panel after the
  layers; a text field there takes every key.
- Panels of an edited scene take no input and render once per document load.

## Consequences

- First-person games use `Crosshair` with a raw mouse and an action, with no cursor juggling.
- A focused panel does not get keys a HUD layer consumes. A game that wants the opposite moves its HUD menu or hides it.

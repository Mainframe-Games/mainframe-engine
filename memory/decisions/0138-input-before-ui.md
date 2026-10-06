# ADR 0138 — Opt-in Godot `_input` placement: `Node.InputBeforeUi`

- **Date:** 2026-10-06
- **Status:** accepted (Crash Site Defense port: TAB did not toggle the crew menu)
- **Amends:** [ADR 0051](0051-ui-input-first.md) (the UI sees input first)

## Context

Godot calls `_input` before the GUI. Crash Site Defense reads its crew-menu toggles (TAB, Q, M, C) in `_Input` for
exactly that reason: TAB is also `ui_focus_next`, which a focused button would consume. The engine routes every event to
the UI servers before any `OnInput` (ADR 0051). In the port, once a menu element had focus, RmlUi took Tab for focus
navigation and the crew menu never saw it, so TAB stopped opening or closing the menu. Flipping the order for everything
would break the editor, whose shortcuts rely on the UI having a focused text field first.

## Decision

`Node.InputBeforeUi` (default false): such nodes get `OnInput` in a first pass, in reverse tree order like the regular
pass, before the input servers. Handling the event (`SetInputAsHandled`) stops it; otherwise the UI and then the
remaining `OnInput`/`OnUnhandledInput` nodes get it as before. Godot ports set it on nodes whose `_Input` must win over
the GUI.

## Consequences

- Crash Site Defense's `CrewMenu` sets it: TAB opens and closes the menu with a skill node focused (checked with real
  keys). Test `ANodeWithInputBeforeUiSeesTabEvenWhileAButtonHasFocus`.
- Every other node keeps UI-first, and the editor is unchanged.

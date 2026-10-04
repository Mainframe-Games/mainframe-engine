# ADR 0051 — UI sees input first; consumption rules; IME without an ABI change

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M8 (W4 lane m8)
- **Spec:** docs/design/game-ui.md#input-routing

## Context

The design routes SDL input to the UI layers (top to bottom) before `Node.OnInput`/`OnUnhandledInput` (Godot instead
calls `_input` before GUI). RmlUi's raw `Process*` return value means "not consumed". The native shim (ABI 1.0) has no
IME composition API; Silk.NET does not surface `SDL_TEXTEDITING`; changing the shim would require new CI-built
binaries for every platform.

## Decisions

1. **`IInputServer`** (new, in `ServerRegistry`): `SceneTree.PushInput` offers each event to input servers first; a
   consumed event stops there. The UI server is the only one. The shim already returns `CONSUMED`/`PROPAGATE`, and the
   managed `RmlContext.Process*` methods return `true` = consumed, so the inversion is handled in one place.
2. **Consumption rules** (all unit-tested): mouse input is consumed only over interactive elements
   (`pointer-events: none` HUD bodies); a button press the UI did not consume keeps the mouse with the game until
   release (drags cross the HUD); raw cursor mode bypasses the UI; a focused text field takes every key; the release
   of a key or gamepad button whose press the UI took is consumed too; a visible modal document blocks its layer's
   lower layers and the game; F8 (debugger) is always the UI's.
3. **Gamepad → navigation keys** (D-pad/left stick → arrows with hysteresis and repeat, A/Start → Return,
   B → Escape). RmlUi 6.3's `nav-*` spatial navigation works (verified by test), so no fallback navigator was needed;
   the first direction press focuses the first tab-able element when nothing is focused.
4. **IME within ABI 1.0:** `ActivateKeyboard` places SDL's text-input rectangle at the caret so the OS draws the
   composition and candidates there; an SDL event watch (`SDL_AddEventWatch`) publishes the composition string as
   `UiServer.CompositionChanged`; committed text arrives through the normal text events. SDL text input stays enabled
   (ImGui and Silk's `KeyChar` rely on it). Inline preedit rendering is deferred to ABI 1.1 (`TextInputContext`).

## Consequences

- Game code that needs input regardless of the UI must ask the UI server (`IsPointerOverUi`, `TextInputActive`) or use
  raw devices; the Sandbox's right-drag look and Escape do.
- An ABI 1.1 bump (append-only) can add IME preedit without breaking this binding.

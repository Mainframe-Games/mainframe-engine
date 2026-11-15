---
summary: >-
  G1.17: `mfe anim render` writes a contact sheet (key times × front/side/three-quarter views, a trajectory strip with
  contact bands, the check report) from a hidden-window Engine in the CLI, captured cell by cell; `--frames`, `--frame`,
  `--views`, `--size`, `--flat`; a render smoke test runs mfe.dll out of process.
---

# 2026-11-15: G1c contact sheets (G1.17)

Branch `feature/g1c-contact-sheet`, stacked on `feature/g1c-mfe` (#214). Plan:
[G1c contact sheet](../plans/2026-11-15-g1c-contact-sheet.md). As built:
[Animation and skinning → Contact sheets](../../docs/design/animation.md#contact-sheets-g1c).

## What changed

- **`Tools/MainframeEngine.Cli/ContactSheet/`:** `ContactSheetLayout` (frames, views, geometry, render size),
  `SheetCanvas` (CPU drawing: fills, lines, box-filtered blits, text through `Font.RenderLine`), `ContactSheetComposer`
  (header, labels, cells, strip, report), `ContactSheetRenderer` (an `Engine` with a hidden window: sky, sun, ground,
  the model posed with `ClipSampler` per cell, a 12° camera per view, one `CaptureFrame` per cell; its log goes to stderr).
- **`mfe anim render`** (+ `check`'s options through a shared `CheckOptions`); `RenderUnavailableException` exits 1.
- **Tests:** `ContactSheetLayoutTests` (10), `MfeCliTests` gains render's option errors; RenderTests:
  `ContactSheetTests` (2, out of process through `HostRunner.RunTool`; RenderTests references the CLI).
- **Docs:** animation.md (Contact sheets with a real sheet, `docs/images/animation-contact-sheet.png`; the CLI table;
  Testing), the proposal (G1.17 built and its changes), testing.md, AGENTS.md.

## Decisions

- None needing an ADR: the plan's design notes and the proposal's "Changes" entry for G1.17.

## Dead ends

- The engine logs to stdout, which carries mfe's own output: `--json` would not parse. The renderer swaps the console
  sink for one that writes warnings and errors to stderr while it runs.
- The flat material's grey comes out lighter after the tonemap than its albedo; the test looks for a light neutral grey.

## Verified

- `just build`, Release `-warnaserror`, `just test`, `just test-render`, `just format-check`, `just shaders-check`,
  `just state-guard`, `check.sh` (numbers in the PR).
- By hand: sheets of the squat (shaded and flat) and the root-motion test jump; I looked at both. The jump's sheet shows
  what its checks say (the deep crouch, the toes into the floor on landing, a 60 cm flight).
- Not run: the no-display path (exit 1 with the message) on a machine without a display.

## Not done

- G1.18 (editor reload of changed resources, player preview), G1.19 (the `animate` skill).

## State changes

- milestones G1c: "G1.16 `mfe` CLI ✅; G1.17–G1.19 ⬜" → "G1.16 `mfe` CLI ✅; G1.17 contact sheets ✅; G1.18–G1.19 ⬜"
  (after the G1.16 entry from 2026-11-15-g1c-mfe).

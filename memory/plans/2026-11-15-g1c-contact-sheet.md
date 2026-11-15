# Plan: G1.17 contact sheets (`mfe anim render`)

**Status:** done, see [log](../logs/2026-11-15-g1c-contact-sheet.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (Contact sheet rendering, the
`mfe` table, task list G1.17), [Animation and skinning → The `mfe` CLI](../../docs/design/animation.md#the-mfe-cli),
[Testing](../../docs/design/testing.md)
**Started:** 2026-11-15

## Goal

`mfe anim render <library> <clip> --model <m> -o sheet.png` writes one picture Claude can look at: a header (clip, model,
length, loop, baked state, the check summary), one row per view (front, side, three-quarter) and one column per frame
(time, key or sampled), the model posed on a lit ground with shadows, a trajectory strip (hips, feet and hands heights
over time with the feet's contact bands and the frame times) and the check report. `--frames n`, `--views a,b`,
`--size px`, `--flat` (one unlit grey material), `--frame t` (one large frame, the views side by side).

## Not in scope

- Rendering without a display (proposal open question 6): with none, `render` fails with a clear message (exit 1).
- IK pin marks on the cells (pins are not kept; contacts show in the strip and the shadows).
- Editor reload (G1.18), the skill (G1.19).

## Design notes (decided here)

- In the CLI (`Tools/MainframeEngine.Cli/ContactSheet/`): `ContactSheetLayout` (times, views, cell and sheet geometry),
  `SheetCanvas` (RGBA drawing on the CPU: fills, lines, box-filtered blits, text through `Font.RenderLine`),
  `ContactSheetComposer` (header, labels, cells, strip, report), `ContactSheetRenderer : Engine` (the captures).
- **Capture:** one hidden window the size of a cell (2× for anti-aliasing, box-filtered down); the model instance is
  posed with `ClipSampler` per cell and the camera moved per view; a few frames later `CaptureFrame`. Cells are captured
  one after another (simpler and sturdier than one `SubViewport` per cell; ~4 frames a cell).
- **Cameras:** the engine has no orthographic camera, so a 12° perspective camera far enough away to fit 1.3× the
  character's rest height reads as near-orthographic. Front looks along the character's −forward, side from its left,
  three-quarter from front-left and 12° above; each follows the hips' ground position at its time (root motion stays
  framed; the strip shows the travel).
- **Scene:** a procedural sky, a sun with shadows, a large ground plane at the rest pose's ground; the model's own
  materials, or with `--flat` one unshaded grey.
- **Frames:** default the clip's key times (times keyed by every track; refinement-only times left out), evenly thinned to
  12; `--frames n` spaces n times from 0 to the length.
- **Fonts:** Lato for labels, Roboto Mono for the report (engine `Assets/UI/fonts`).

## Steps

- [x] `ContactSheetLayout` + `SheetCanvas` + `ContactSheetComposer`. Verified by: unit tests (times default/`--frames`/
  thinning, cell geometry for 1–12 columns and `--frame`, canvas blit/line/text, a composed sheet from synthetic cells
  has the header, labels and strip in place).
- [x] `ContactSheetRenderer` and `mfe anim render`. Verified by: a render smoke test (RenderTests, out of process like the
  host) on the test character: exit 0, the PNG's size, every cell shows the character over the background, `--flat`.
- [x] Docs (animation.md, proposal G1.17 built + changes, testing.md, AGENTS.md), log. Verified by: `check.sh`,
  `just state-guard`.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, `just test-render`, `just format-check`,
  `just shaders-check`, `check.sh`.

## Done when

- `mfe anim render` on the test character's squat writes a sheet whose every cell has the character in it, with the
  header, strip and report drawn; I have looked at it.
- With no display it exits 1 with a message naming the cause.
- The gates pass, `just test-render` included; a PR (stacked on #214) is open with Auto-fix on.

## Open questions

- None for the owner.

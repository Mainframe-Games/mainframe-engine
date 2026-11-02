---
summary: >-
  G9.7: joint limits are drag handles in the editor viewport (hinge, slider, cone-twist, distance, physical bone and the
  2D joints) through the G7.3 handle API, one undo entry per drag; G9.10 is all that is left of G9.
---

# 2026-11-02: Joint limit handles (G9.7)

Branch `feature/joint-limit-handles`. Plan: [Joint limit handles](../plans/2026-11-02-joint-limit-handles.md).

## What changed

- `MainframeEngine.Editor/Src/Viewport/Handles/JointHandles.cs`: `JointFrame` (a joint's unscaled frame, 3D or 2D),
  `JointHandles` (ray–plane angles, unwrapping near the previous value, view-relative arc radius, arc/line drawing) and
  providers for `HingeJoint3D`, `SliderJoint3D`, `ConeTwistJoint3D`, `DistanceJoint3D`, `PhysicalBone3D`, `PinJoint2D`,
  `SliderJoint2D`, `WheelJoint2D`, `GrooveJoint2D` and `DampedSpringJoint2D`. No change to the handle machinery (as ADR
  0213 expected).
- Off limits have hollow handles; dragging one turns the toggle on in the same undo entry. Lower/min handles stop at
  upper/max and back. Angles past ±180° stop instead of wrapping.
- `JointHandleTests` (22 cases). `editor-walkthrough.qa` drags the bridge hinge's upper limit (`25a-joint-limit-handle`);
  `readme-screenshots.qa` captures `editor-joint-limits` (and `editor-joints`, which now shows the handles, re-captured).
- Docs: `physics-joints.md` "Limit handles" section, `editor.md` handle table, G9 row (G9.1–G9.9 ✅), README editor
  table and systems row, landing page tools and features text, state file.

## Decisions

- **Handles at the node's frame**, not the bodies' current poses: the editor never steps physics, so the node is where
  the joint is authored (and the debug draw draws inactive joints there too).
- **View-relative arcs** (15 % of the view's height, at least the debug draw's 0.2 m / 10 px): at the debug draw's fixed
  radius a hinge's two handles overlapped when zoomed out. The handles' arc is drawn in light blue next to the debug arc.
- **Light blue** rather than the joint debug amber: the first capture's orange arc vanished against the Demo bridge's
  planks.
- **Scope beyond the mockup**: `PhysicalBone3D` (the doc lists its "Joint" group, and tuning a created physical skeleton
  is the main use) and the 2D groove/spring `Length` (their only limit). Motor and spring targets, `AnchorB`,
  `InitialOffset` and `RestLength` have no handles. Recorded under the doc's "Differences from the plan"; no ADR (it
  follows 0211/0213).

## Dead ends

- None. The worktree first failed to build because `Plugins/Spine` (a submodule) was not checked out:
  `git submodule update --init Plugins/Spine`.

## Verified

- `JointHandleTests` 22/22. Gates: `just build` (0 warnings), Release `-warnaserror` build, `just test` (3151 engine,
  1798 editor, 460 video), `just test-render` (222), `just format-check`, `just shaders-check`, `check.sh`;
  `just docs-build`.
- `just qa-editor`: `25a-joint-limit-handle.png` shows the bridge hinge's upper handle dragged to 72°, its label and the
  inspector's Upper Angle Degrees following; the rest of the walkthrough ran to the end (undo is covered by the tests).

## Not verified

- Feel of the drags on real hardware beyond the scripted QA drag (a person should drag a hinge, cone and 2D pin).
- `just readme-screenshots` was not run end to end: the two images came from the same steps in a shorter script with
  the same flags (`--scale 2`, Release) and the recipe's `magick` downscale.

## Next

- G9.10: `Generic6DofJoint3D`, active ragdolls, `MotorJoint2D`.

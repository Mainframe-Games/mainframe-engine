# Plan: Joint limit handles (G9.7)

**Status:** done, see [log](../logs/2026-11-02-joint-limit-handles.md)
**Docs:** [Physics joints → Debug draw and editor](../../docs/design/physics-joints.md#debug-draw-and-editor),
[Editor → Range handles and the handle API](../../docs/design/editor.md#range-handles-and-the-handle-api),
[ADR 0211](../decisions/0211-3d-joints-on-jitter2.md), [ADR 0212](../decisions/0212-ragdolls-and-physical-bones.md),
[ADR 0213](../decisions/0213-viewport-tools-gizmo-resources-handles.md) ("G9 joint limits add `IHandleProvider`s with
angle handles using `HandleDrag.RayOrigin/RayDirection`; no editor change"), the mockup
[`physics-joint-gizmos.svg`](../../docs/images/physics-joint-gizmos.svg) (callout 4)
**Started:** 2026-11-02

## Goal

A selected joint's limits are drag handles in the viewport, through the G7.3 handle API: each drag is one undo entry,
Esc or a right click restores it, Snap snaps angles to the rotate step and distances to the move step. Handles sit on
the joint's frame at the node (edit mode; none while simulating, as for every handle).

| Node | Handles |
|---|---|
| `HingeJoint3D` | Lower and Upper angle on an arc about local Z |
| `SliderJoint3D` | Lower and Upper distance on local X |
| `ConeTwistJoint3D` | Swing span on the cone's rim (towards the camera's right), twist span on the twist arc |
| `DistanceJoint3D` | Min and Max length on the line to `AnchorB` |
| `PhysicalBone3D` | Its `JointType`'s limits (cone-twist, hinge or slider) at body × `JointOffset` |
| `PinJoint2D` | Lower and Upper angle (radians, shown in degrees) |
| `SliderJoint2D`, `WheelJoint2D` | Lower and Upper translation on local X |
| `GrooveJoint2D`, `DampedSpringJoint2D` | `Length` on local +Y (the groove's end; the spring's longest stretch) |

A limit that is off (`UseLimit`/`AngularLimitEnabled` false, a 180° span, a 0 = unset length) shows a hollow handle
("… : no limit — drag to set"), like the audio player's unlimited max distance; dragging it turns the limit on in the
same undo entry. Each lower/min handle stops at its upper/max and the other way round. Angle handles intersect the mouse
ray with their arc's plane and keep the angle nearest the previous one, so a drag past ±180° stops there instead of
jumping. Arcs are drawn at a view-relative radius (about 10 % of the view height, at least the debug draw's), so the
handles stay apart at any zoom.

## Not in scope

- G9.10 (`Generic6DofJoint3D`, active ragdolls, `MotorJoint2D`), humanoid limits (G1c).
- Handles for non-limit values (motor and spring targets, `AnchorB`, `InitialOffset`, `RestLength`).
- Editor changes to the handle machinery (ADR 0213: none needed).

## Steps

- [x] `MainframeEngine.Editor/Src/Viewport/Handles/JointHandles.cs`: shared frame and angle math (ray–plane angle,
  unwrap, arc drawing) and one provider per row above. No allocation in `GetHandles`/`Draw`. Verified by: the tests
  below, the editor's idle allocation gate.
- [x] Tests `Tests/MainframeEngine.Editor.Tests/JointHandleTests.cs`, in `HandleTests`' style: handle positions per
  type, angle from the ray, snap, clamp, lower ≤ upper, hollow → limit on, ±180° unwrap, physical bone frame, 2D
  providers, and a viewport drag of a hinge's upper handle that is one undo entry and restores on Esc. Verified by:
  `just test`.
- [x] `just qa-editor`: the walkthrough's bridge hinge step drags a limit handle (`handle-drag`); a new screenshot
  `docs/images/editor-joint-limits.png`. Verified by: the capture in `artifacts/qa-editor`, inspected.
- [x] Docs: `physics-joints.md` (handles table, screenshot, "Not yet" without G9.7), `editor.md` handle table,
  `milestones.md` G9 row (G9.7 ✅, G9.10 open), README and landing page feature lists with the screenshot, state file,
  log. Verified by: `check.sh`, `just docs-build`.
- [x] Gates from `.agents/workflow.md`.

## Done when

- `HandleProviders.Find` returns a joint provider for each type in the table, and the new tests pass.
- Dragging a hinge's limit handle in the editor viewport changes `UpperAngleDegrees`, adds one history entry, and undo
  restores it (a test asserts it).
- The G9 milestone row shows G9.7 done and G9.10 open; `physics-joints.md` no longer lists limit handles as "Not yet".
- The screenshot is in the docs, README and landing page.
- Every gate in `.agents/workflow.md` passes locally, and a PR is open.

## Open questions

- None blocking. The view-relative arc radius differs from the debug draw's fixed 0.2 m / 10 px; both are drawn, the
  handles' arc in the handle colour.

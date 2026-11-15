# Plan: G1.14 describe and edit scripts

**Status:** done, see [log](../logs/2026-11-15-g1c-describe-edits.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (Edits: keys are the truth,
task list G1.14), [Animation and skinning](../../docs/design/animation.md#pose-scripts-and-baking-g1c),
[ADR 0281](../decisions/0281-humanoid-map-and-canonical-angles.md), [ADR 0282](../decisions/0282-pose-baker-keys-pins-and-linked-libraries.md)
**Started:** 2026-11-15

## Goal

A clip on a humanoid rig, baked or hand-made, reads back in canonical terms (per key time: the hips, every mapped bone's
angles, the feet's and hands' positions and ground contact, the ease), and an edit script changes it on top of its
current keys: absolute, relative and scaled values at a key, over a range, at an inserted key, a rescaled arc, IK pins
that hold planted limbs. Existing key times, counts and eases stay, hand-placed keys included, and an edit never makes a
hand-edited clip look like an untouched bake.

## Not in scope

- The checks table (G1.15), `mfe` and `--json` (G1.16), contact sheets (G1.17), editor reload (G1.18), the skill (G1.19).
- Retargeting, length or loop changes in edits.

## Design notes (decided here)

- Code in `MainframeEngine/Src/Animation/Authoring/`: `ClipDescriber` (+ `ClipDescription` model and text format),
  `PoseEditScript` + `PoseScriptParser.ParseEdit`, `PoseEditor`, shared key insertion (`TrackKeys`) used by the baker too.
- **Key times** of a clip: the union of its transform tracks' key times on the skeleton, merged within 1 ms.
- **Describe** poses the skeleton with `ClipSampler` and reads it with `HumanoidRig.ReadPose`. Effector positions are in
  character space (the space of IK `pos` targets, new `HumanoidRig.ToCharacterSpace`); the ground is the rest pose's
  lowest foot or toe; an effector is in contact within 2 cm of it. A time keyed by only some tracks lists those bones
  (refinement keys); a time keyed by none is "sampled".
- **Edit values:** a number (absolute), `"+=n"`, `"-=n"`, `"*n"`, per component; hips `x/y/z/yaw/pitch/roll`.
- **Targets:** `t` must be a clip key time (within 1 ms) unless `insert: true`; `range: [a, b]` takes every key time in
  it. Each named bone gets its key at that time set (a key inserted into its track when missing, taking the split
  segment's ease); a bone without a track gets one with keys at the edited times and the clip key times just outside
  them, so the change stays local. Unmapped bones and hinge non-flex components are errors.
- **Arc edits** (`range` + `arc.height`): the hips height above the linear take-off-to-landing line is scaled on the
  hips keys inside the range so the measured apex (60 Hz, above take-off) reaches the new height.
- **IK in edits:** `"pin"` holds the effector where the clip had it before the edit (rotation too, unless the edit names
  the end bone), `{ "pos": [...] }` reaches a target; the chain's keys at the edited times are re-solved. Between
  consecutive edited times that pin an effector at the same spot, a 60 Hz pass adds chain keys where it slides over 1 cm.
- **`BakedHash`:** an edit of an untouched bake stores the new hash; an edit of a hand-edited or hand-made clip keeps the
  old (stale or empty) one, so a later full re-bake still needs `replace`. Writing an edit over its own base is allowed;
  a variant (`name` ≠ `base`) is guarded like a bake.

## Steps

- [x] `TrackKeys` (set or insert a key, hemisphere, transitions) and the baker on it. Verified by: the G1.13 baker tests
  unchanged and green.
- [x] `ClipDescriber`: key times, canonical pose per time, effectors, contact, eases, keyed bones, text. Verified by:
  unit tests on the baked jump (angles match the script's keys within 0.5°, feet in contact while pinned, refinement
  times list their bones, `--times`-style sampling, a hand-made clip).
- [x] `PoseEditScript` + `ParseEdit`. Verified by: unit tests for a full edit script and each error (JSON path named).
- [x] `PoseEditor`: values, `t`/`range`/`insert`, mirror, ease, new tracks, arcs, IK pins and targets, refinement,
  `BakedHash`. Verified by: unit tests — relative edit moves only the named bones' keys, times/counts/eases unchanged,
  hand-placed keys kept, insert adds one key per touched track, arc apex ×1.3 within 1 cm, a pinned foot stays within
  1 cm after lowering the hips, unknown time/unmapped bone errors, hash rules.
- [x] `PoseLibrary` edit write (own base allowed, variant guarded). Verified by: temp-project unit tests.
- [x] Docs (animation.md section, proposal G1.14 built + changes), log with State changes. Verified by: `check.sh`,
  `just state-guard`.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check`, `check.sh`.
  `just test-render` only if rendering changes (it should not).

## Done when

- `ClipDescriber` on the baked test jump reports each script key's angles within 0.5° and both feet in contact at t = 0.
- An edit script raising the arc by ×1.3 and lowering the arms by 10° at a key changes only those keys and leaves every
  track's key times, counts and eases as they were.
- An edit of a hand-edited clip leaves it "changed since baked"; an edit of an untouched bake leaves it untouched.
- Every edit-script error test names the JSON path.
- The gates pass; a PR is open with Auto-fix on.

## Open questions

- None for the owner.

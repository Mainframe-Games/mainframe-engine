---
name: animate
description: Use when asked to make, change or check an animation clip (jump, wave, crouch, landing, idle, …) for a rigged humanoid model in a Mainframe Engine project, to bake it from a pose script with mfe, check it, look at its contact sheet and fix it, without Blender.
---

# Animate a rigged humanoid with `mfe anim`

You write a **pose script** (JSON: a few key poses in joint angles), `mfe anim bake` turns it into ordinary keys in a
library `.mfres` and checks them, `mfe anim render` draws a contact sheet you look at, and you fix and repeat. Once a clip
exists, its keys are the truth: change it with an **edit script** on top of them, never by re-baking over it.
Design: `docs/design/animation.md` (Humanoid map, Pose scripts and baking, Describe and edit scripts, Checks, The `mfe`
CLI, Contact sheets) in the engine repository.

## Running mfe

`mfe` reads and writes the project's files; the editor reloads what it changes. Find it once per session:

- **In the engine repository:** `just mfe anim …` (from the game project's folder, or add `--project <game folder>`).
- **In a game project built against an engine checkout:** the checkout is `MainframeEnginePath` in
  `Directory.Build.props`; run `dotnet run --project <that path>/Tools/MainframeEngine.Cli -c Release -- anim …` from the
  game's folder (the folder with `project.mfproj`).
- **In a game project on the MainframeEngine package:** use any engine checkout of the same version or newer with
  `--project <game folder>`. If there is none, say so and stop: the package does not carry `mfe` yet.

Every command takes `--json` for one JSON document. Exit codes: 0 ok, 1 the checks found errors (the clip is still
written), 2 a bad script or input (each error names its JSON path; nothing written). `render` needs a GPU and a display;
without one, rely on `describe` and `check`.

## Workflow: a new clip

1. **Rig** (once per model): `mfe anim rig Assets/Characters/hero.glb`. "Missing required bones: none" means it is
   ready. If bones are missing, report them and the rig's unmapped bones to the user and suggest the `.mfmeta`
   `"humanoid"` overrides (`{ "LeftUpperLeg": "Thigh.L" }`); do not guess. Note the height and rest pose.
2. **Reference:** `mfe anim poses` lists the canonical bones, which are hinges, and every range of motion.
3. **Write the script in your scratch space**, never in the project (scripts are input only).
4. **Bake:** `mfe anim bake <script.json>`. Read the notes and the checks it prints.
5. **Look:** `mfe anim render <library> <clip> --model <model> -o <scratch>/sheet.png --root <inPlace|motion> --start stand --end stand`,
   then open the PNG with the Read tool. Check the silhouettes in the side view, the feet on the ground, the strip's
   contact bands, and the report at the bottom. `--frames 8` adds in-between frames; `--frame 0.4` one large frame;
   `--flat` grey silhouettes.
6. **Fix and repeat.** Change the script and bake again (a clip only you baked and nobody edited is replaced freely).
   At most five rounds; then report what is still off instead of looping.
7. **Report** the clip name, its library, the checks' summary, the sheet's path, and how to play it:
   `GetNode<AnimationPlayer>("…/AnimationPlayer").Play("jump")` on any instance of the model (the first bake links the
   library in the model's `.mfmeta`, so every instance has it). For `root: "motion"`, set the player's `RootMotionTrack`
   to the skeleton (`Armature`) and `RootMotionBone` to the hips bone, use the `Physics` callback mode, and call
   `CharacterBody3D.ApplyRootMotion(player)`. In the editor, selecting the model's instance shows its clips in the
   Animation panel, read-only, to preview them.

## Workflow: changing a clip

1. `mfe anim describe <library> <clip> --model <model>` first: the clip as it is now, hand fixes included (times keyed by
   only some tracks list their bones).
2. Write an **edit script** and bake it: only what it names changes; key times, counts and eases stay.
3. Never pass `--replace` unless the user asked for the clip to be rebuilt from scratch: `bake` refuses to overwrite a
   clip someone changed by hand, and `--replace` throws their work away.
4. Check and render as above.

## Pose script

A whole jump (in place, for a jump the game's physics performs; it passes every check):

```json
{
  "model": "Assets/Characters/hero.glb",
  "library": "Assets/Characters/hero_anims.mfres",
  "name": "jump",
  "length": 1.3,
  "loop": "None",
  "root": "inPlace",
  "keys": [
    { "t": 0.0, "pose": "stand", "ease": "in", "ik": { "leftFoot": "pin", "rightFoot": "pin" } },
    { "t": 0.22, "ease": "out", "hips": { "y": -0.12, "pitch": 14 },
      "joints": { "Spine": { "flex": 10 }, "LeftUpperArm": { "flex": -40 }, "RightUpperArm": { "flex": -40 } } },
    { "t": 0.36, "hips": { "y": 0.01, "pitch": 4 },
      "joints": { "Spine": { "flex": 2 }, "LeftUpperArm": { "flex": 60 }, "RightUpperArm": { "flex": 60 } },
      "ik": { "leftFoot": "release", "rightFoot": "release" } },
    { "t": 0.6, "hips": { "y": 0.0, "pitch": 6 },
      "joints": { "LeftUpperLeg": { "flex": 55 }, "LeftLowerLeg": { "flex": 80 }, "LeftFoot": { "flex": 10 },
                  "LeftUpperArm": { "flex": 80 } }, "mirror": true },
    { "t": 0.84, "hips": { "y": -0.03, "pitch": 4 },
      "joints": { "LeftUpperLeg": { "flex": 20 }, "LeftLowerLeg": { "flex": 35 }, "LeftUpperArm": { "flex": 40 } }, "mirror": true,
      "ik": { "leftFoot": { "pos": [0.1, 0.1, 0], "pin": true }, "rightFoot": { "pos": [-0.1, 0.1, 0], "pin": true } } },
    { "t": 1.0, "hips": { "y": -0.12, "pitch": 12 },
      "joints": { "Spine": { "flex": 8 }, "LeftUpperArm": { "flex": 20 }, "RightUpperArm": { "flex": 20 } } },
    { "t": 1.3, "pose": "stand" }
  ]
}
```

- **Space:** Y up, +Z the character's forward, +X its left; metres; angles in degrees from the canonical **T-pose**
  (legs straight down, arms straight out, palms down).
- **Angles** per bone: `flex` (sagittal: hips and shoulders swing forward, knees and elbows bend, spine and neck bend
  forward, ankles point the toes up), `abduct` (away from the midline: a leg sideways, an arm up; negative brings an arm
  down), `twist` (about the bone, internal positive). Angles are **mirrored**: the same numbers mean the same motion on
  both sides. **Hinges** (`LeftLowerLeg`, `LeftLowerArm`, …) take `flex` only.
- **Named poses:** `stand` (arms down: shoulder abduct −80, elbow flex 10; the start of most clips), `tpose` (all 0),
  `rest` (the rig's own rest). The first key needs one; later keys start from the previous key, and a bone or
  component a key does not name holds its value.
- **Key fields:** `t`, `pose`, `hips` (`x/y/z` metres from rest, `yaw` turn left, `pitch` lean forward, `roll` lean
  left), `joints`, `mirror: true` (copy this key's left joints to the right, or right to left), `ik`, `ease`.
- **IK:** `"pin"` holds a foot or hand where it is at this key, rotation too, through the key that says `"release"`
  (the effector leaves after that key). `{ "pos": [x, y, z] }` reaches a target in character space (`describe` prints
  the effectors' positions in the same space: read the standing feet at `t = 0`), and `{ "pos": [...], "pin": true }`
  reaches it and holds it from there, the foot put down level: land on exactly the spots the clip started from. IK
  replaces the leg's or arm's flex and abduct; a pinned foot stays level unless the key gives `Foot` angles.
- **Ease:** `"linear"`, `"in"` (slow start), `"out"` (slow end), `"inOut"`, or a number; it shapes the segment
  **after** its key.
- **Root:** `"inPlace"` (default: the game's `CharacterBody3D` does the travelling and jumping; arcs are ignored, the
  hips' offsets stay) or `"motion"` (arcs and horizontal hips motion are kept for root motion).
- **Arcs** (`"motion"` only): `{ "from", "to" }` puts the hips on a ballistic arc between take-off and landing; without
  `height` the apex follows the project's gravity and the flight time (`g·T²/8`); a `height` far from that is reported.
- **Loop:** `"None"`, `"Linear"`, `"PingPong"` (or `true`). A linear loop's last key repeats the first pose.

## Edit script

```json
{ "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "base": "jump", "name": "jump",
  "edits": [
    { "range": [0.38, 0.82], "arc": { "height": "*1.3" } },
    { "t": 0.25, "joints": { "LeftUpperArm": { "flex": "-=10" } }, "mirror": true },
    { "t": 0.55, "insert": true, "joints": { "Spine": { "flex": -8 } } },
    { "range": [0, 0.25], "hips": { "y": "-=0.03" }, "ik": { "leftFoot": "pin", "rightFoot": "pin" } }
  ] }
```

- Values: `20` (absolute), `"+=5"`, `"-=10"` (relative), `"*1.3"` (scaled), per component; hips the same.
- `t` must be an existing key time (`describe` lists them); add `"insert": true` for a new one. `range: [a, b]` edits
  every key in it. `ease` changes the segment after each edited key.
- `ik` in an edit: `"pin"` keeps a foot or hand where the clip had it before the edit (lowering the hips over planted
  feet needs it, or the feet sink); `{ "pos": [...] }` reaches a target. No `"release"` in edits.
- `arc.height` rescales the hips' rise between the range's ends (the clip needs hips keys inside the range).
- `name` other than `base` writes a variant (`jump_high`) and keeps the original.
- Bones the rig does not map are errors in edits.

## Reading the checks

Errors fail; warnings inform. Each finding has a time range, a subject and a fix.

| Check | Means | Usual fix |
|---|---|---|
| `ground` | a foot, toe, hand or the hips under the floor | pin the feet over contacts; level the foot (`Foot` flex) when the toes dig in; raise the hips |
| `footSlide` | a foot on the ground slides (warning) | pin it from the key it lands until the key it leaves; an in-place walk slides by design |
| `range` | a joint past its limit, or a hinge twisting | smaller angles; IK-bent legs past range mean the hips are too low for where the feet are |
| `flip` | keys in opposite quaternion hemispheres | re-bake (the baker never writes one; a hand edit can) |
| `spike` | a bone faster than 1 500°/s | more time between the keys, an ease, or an in-between key |
| `loop` | a loop's ends do not match | make the last key the first pose (`"pose": "stand"`) and ease both ends |
| `start`, `end` | the first or last pose away from its named pose (warning) | fine when IK pins move the legs; otherwise key the named pose |
| `root` | in-place hips drift, or a root-motion jump or spin | keep `x`/`z` near 0 in place; continuous hips keys with motion |
| `physics` | a flight's height off what gravity gives (warning) | fine for game feel; drop `height` for a physical arc |

`--tolerance footSlide=0.03,range=10` loosens a check for one run; say so if you use it.

## Motion that looks right

- **Anticipation:** crouch 0.1–0.25 s before a jump (hips down 0.08–0.15 m, a forward lean, arms swinging back), and ease
  into it (`"ease": "in"` on the first key): straight knees bend very fast for the first centimetres of hip drop.
- **Take-off:** extend fast (`"ease": "out"` on the crouch key), arms swing forward and up, the spine straightens; release
  the pins on the take-off key.
- **Apex:** tuck the knees (hip flex 60–80, knee flex 80–100) with `"mirror": true`.
- **Landing:** land on the standing spots with `{ "pos": [...], "pin": true }` (the feet then end where `stand` puts
  them, so the last key's `stand` matches), or pin where the feet come down and give each `Foot` a flex that keeps it
  level (knee flex − hip flex + the hips' pitch: the shin's lean); absorb over 0.1–0.2 s (hips down, knees bend), then
  recover to `stand`.
- **Overlap:** the spine and arms reach their pose a frame or two after the hips; a deep crouch with level feet needs
  ankle flex near its 30° limit, so lean the hips forward instead of dropping lower.
- **In place vs motion:** a jump the game's physics performs is `"inPlace"`; a vault or a scripted leap that must land
  where the animator put it is `"motion"` with an arc.

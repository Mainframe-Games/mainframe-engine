# ADR 0129 — Godot 4's just-pressed timing

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM2 / E10 (the game's `docs/porting.md`)

## Context

The Driving Range dev harness presses actions from `OnPhysicsProcess` (an autoload that runs before the player) and
diffs the player's state against Godot's. A jump pressed on tick 240 and released on tick 241 jumped once in Godot, on
tick 241, and twice in the engine (ticks 240 and 241: an extra 7.5 cm of height). Godot (`core/input/input.cpp`):
`action_press` and input events record `pressed_physics_frame = physics_frames + 1` ("the earliest we can react to it
is the next physics tick"), where the counter is advanced at the start of a step; and with
`legacy_just_pressed_behavior` off (the default) `is_action_just_pressed` does not require the action to still be held.
The engine advances its counter at the end of a step, recorded the current value, and required `Pressed`.

## Decisions

1. **Next physics step**: a change made while `SceneTree.IsInPhysicsStep` records `PhysicsFrames + 1`; between steps it
   records `PhysicsFrames` (the next step's number), unchanged.
2. **No held requirement**: `IsActionJustPressed/JustReleased` compare frames only, as Godot 4 does by default.

## Consequences

- A code press in a physics step is seen by every node exactly once, in the next step; a tap shorter than a frame is
  still a just-pressed. Hardware events between steps behave as before.
- Tests: `InputMapTests.ActionPressedByCodeInAPhysicsStepIsJustPressedInTheNextStepOnly`,
  `ATapReleasedBeforeTheNextStepIsStillJustPressed`.

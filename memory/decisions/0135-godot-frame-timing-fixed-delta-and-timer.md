# ADR 0135 — Fixed-delta first frame and Godot's Timer countdown

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM4 / E13 (the game's `docs/porting.md`)

## Context

Driving Range's parity runs (`--fixed-fps 60`, dumps compared with Godot's) found two one-frame differences.

1. **The first frame.** `GameHost` starts the session on the first update and discards that update's delta (ab4a233),
   because the delta is the start-up time.
   - With `--fixed-fps`, the delta was discarded too, so the first frame ran `OnProcess` with no physics step.
   - Godot's first frame at `--fixed-fps` steps physics and then processes, as every frame does.
   - The game's charge ring showed the first frame's value at t=0 (allowlisted in its diff), and timers ran a frame late.
   - `--fixed-fps` is documented as "every update gets a 1/n s delta".
2. **`Timer`.** The engine's timer counted down a float and fired at ≤ 0.
   - Godot keeps `time_left` in a double and fires when it drops below zero.
   - At 60 FPS, the float countdown of a 1 s timer still had +2.8e-7 s left after 60 steps, so it fired on frame 61; Godot fires on frame 60. The game's wallet ticked a frame late.

## Decisions

1. **`Engine.FrameDelta(fixed, measured, discard)`:** a fixed delta applies to every update, the discarded start-up frame included. Only real-time runs zero that frame. Real-time start-up behaviour (CSD's intro timing) is unchanged.
2. **`Timer` follows Godot:**
   - a double countdown;
   - times out below zero;
   - a repeating timer adds `WaitTime` back, keeping the remainder;
   - one-shot and `Stop()` stop it (Godot sets −1);
   - a separate running flag, so a timer sitting at exactly 0 keeps running for one more frame, as in Godot.
   - `TimeLeft` stays a float property (0 when stopped).

## Consequences

- **Fixed-delta runs** now start with a physics step on frame 1, like Godot.
  - Render tests are unaffected: their host is an `Engine` subclass that never discards.
  - CSD's fixed-fps parity runs see their first physics step one frame earlier, matching Godot better.
- **Tests:** `GameTimeTests.AFixedDeltaAppliesToEveryUpdateEvenTheDiscardedStartUpFrame`,
  `SceneTreeTests.ATimerFiresOnTheFrameGodotsDoes`.

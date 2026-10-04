# ADR 0022 — Physics signals: computed after the step, dispatched in a fixed order

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6

## Context

The proposal suggested Jitter2's `BeginCollide/EndCollide` events for contacts and a narrow-phase filter that records
area pairs on worker threads, and Box2D's event arrays in 2D. Measured on the pinned versions: Jitter2's
`EndCollide` does not fire when a body is removed or its contact cache is cleared; kinematic-vs-static pairs never
reach the narrow phase (so a kinematic "area" body can't see static bodies) and kinematic bodies fall asleep.
User code must never run inside a solver, and freeing nodes from handlers must be safe.

## Decision

- After each step the space **scans current state** and diffs it against the previous step (per-receiver
  dictionaries stamped with the step number):
  - contacts of `ContactMonitor` bodies: 3D `RigidBody.Contacts` arbiters with intact points; 2D
    `b2Body_GetContactData` (touching contacts);
  - areas: 3D tree query of the area's AABB + `NarrowPhase.Overlap` (areas have no Jitter2 body at all); 2D
    `b2Shape_GetSensorData` of kinematic, never-sleeping sensor bodies;
  - sleep: `IsActive` / `b2Body_IsAwake` changes.
- Events are queued and **dispatched on the main thread after the step**, sorted: contact exits, area exits, contact
  enters, area enters, sleep changes; then receiver creation order, then the other body's. So a body moving between
  two areas in one step always gets `exit` before `enter`.
- A body leaving the tree emits `BodyExited` immediately on areas containing it / monitors touching it (Godot
  semantics), except for pairs whose `BodyEntered` is still queued in the same dispatch (then neither fires).
  Queued events of removed bodies are skipped.

## Consequences

- Robust to removal, filter changes and sleeping; no locks or worker-thread code in the engine.
- Contacts that begin and end within one step are not reported.
- Cost is per monitored body / per area per step (bounded by their contacts / candidates), not per pair.

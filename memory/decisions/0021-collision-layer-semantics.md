# ADR 0021 — Collision layers: Godot OR semantics on both engines

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6

## Context

The proposal specified Godot semantics (A and B collide if `(A.layer & B.mask) != 0 || (B.layer & A.mask) != 0`)
but also said Box2D filter bits are "native" — Box2D's test is an AND
(`(A.category & B.mask) != 0 && (B.category & A.mask) != 0`), so mapping layer → category and mask → mask would
give different rules in 2D and 3D. Areas need a one-way rule (an area detects bodies in its mask) and Box2D 3.1
sensors cannot see sensors.

## Decision

- **One rule for both dimensions**: OR semantics for bodies; areas detect a body when `(area.mask & body.layer) != 0`;
  areas never detect areas (also in 3D, for parity).
- **Jitter2**: an `IBroadPhaseFilter` reads layer/mask from the body record (`RigidBody.Tag`). It runs on worker
  threads, so it only reads fields that change on the main thread between steps. A layer/mask change clears the
  body's contact cache and wakes what it touched (Jitter2 keeps resting contacts until they break).
- **Box2D**: every shape gets `maskBits = ~0` (the built-in AND always passes) and `categoryBits = layer | mask << 32`;
  `enableCustomFiltering` on every shape routes pairs (and sensor overlaps) through one static custom-filter callback
  that applies the rules above. Queries pass `maskBits = collisionMask`, which matches the layer in the low 32 bits.
- `CollisionLayers.ShouldCollide`, `Layer(n)`, `Default`, `All` document the rule in code.

## Consequences

- Scenes behave identically in 2D and 3D, and like Godot.
- Box2D calls the custom filter for every new pair (cheap: two filter reads and bit tests).
- One-way collision (A collides with B but B is not pushed) is not supported, as in Box2D/Jitter2.

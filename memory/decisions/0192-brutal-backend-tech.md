# ADR 0192 — BRUTAL as the engine's backend tech; Jolt as a second 3D physics backend

- **Date:** 2026-10-16
- **Status:** proposed
- **Milestone:** BRUTAL backend (B1–B7)
- **Docs:** docs/design/future/brutal-backend.md

## Context

BRUTAL (Ahwoo Limited; `Ahwoo.BRUTAL.*` on NuGet since 2026-10-15) is a C# framework of the layer Mainframe currently
assembles from Silk.NET, Jitter2 and Box2D.NET: generated bindings for Vulkan + VMA, SDL3, Jolt, Box2D, Slang, FreeType,
RenderDoc and more, plus interop, collections, memory and numerics. The engine's author is a founding contributor,
and being built on BRUTAL (alongside Kitten Space Agency and RimWorld's Waylith) helps market the engine and its games.
Its first real improvement for the engine is Jolt 5.5 in place of Jitter2 for 3D physics.

BRUTAL's licence is MIT plus three conditions on every product that includes it: its copyright on a credits screen,
its logo before gameplay (unaltered, legible, no less prominent than other logos), and `NOTICE.txt` alongside the
product. Exceptions are Ahwoo's written permission and products that cannot display graphics; the README waives the logo
for non-profits on request.

## Decision

- **BRUTAL is a dependency of the `MainframeEngine` package itself**, and games inherit its licence. The engine makes
  every game compliant by default: a splash sequence in `project.mfproj` with a locked `@brutal` card (unlocked only by
  `nonProfit`), a `Credits` registry with BRUTAL's line shown on the `SettingsMenu` Credits page, and `NOTICE.txt`
  copied next to the game by `buildTransitive`. Headless runs show no splash.
- **Adoption is phased** (B1 plumbing and attribution, B2 physics seam, B3 Jolt backend, B4 Jolt by default, B5
  low-level packages and RenderDoc, B6 SDL3, B7 Vulkan + VMA inside M11's Vulkan backend); each later phase gets its own
  spec.
- **3D physics gets an internal backend seam** under `PhysicsSpace3D`. Signals, interpolation, layer semantics, user
  moves, debug draw and `MoveAndSlide` stay engine code; Jitter2 and Jolt implement `PhysicsBackend3D`. Projects pick
  one with `physics.3d.backend`; Jitter2 is the default until Jolt meets the B4 parity criteria, then Jolt is, and
  Jitter2 stays as the pure-C# fallback (Intel Macs and platforms without Jolt natives).
- **Every `Ahwoo.BRUTAL.*` package is pinned exactly** in `Directory.Packages.props`; their calls stay inside the
  backend folder that uses them. BRUTAL's analyzers are excluded from the engine's projects.
- **Approved new dependencies**: `Ahwoo.BRUTAL.Core`, `Interop`, `Logging`, `Memory`, `Numerics`, `Strings`,
  `Collections` (2026.10.1; Logging 2026.10.2) and `Jolt` (2026.10.2), pinned exactly; their third-party transitive
  packages `CommunityToolkit.HighPerformance` 8.4.2, `Microsoft.Extensions.Logging` / `.Console` 10.0.0 and
  `Microsoft.Extensions.ObjectPool` 11.0.0-preview.5.26302.115 (a preview, required by `Ahwoo.BRUTAL.Strings`; approved
  2026-10-16), pinned centrally, which bring the `Microsoft.Extensions.*` 10.0.0 packages they depend on (all MIT,
  Microsoft): `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Configuration.Abstractions`,
  `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.DependencyInjection`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`,
  `Microsoft.Extensions.Logging.Configuration`, `Microsoft.Extensions.Options`,
  `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Primitives`. Later phases add BRUTAL
  packages through their own specs.

## Alternatives considered

- **An opt-in `MainframeEngine.Brutal` package** so only games that choose BRUTAL inherit its licence: keeps the core
  MIT-only, but splits the engine into two physics stacks for packaging reasons and weakens "built on BRUTAL". Remains
  the way out if the licence becomes a problem for users.
- **A commercial BRUTAL licence** removing the conditions for Mainframe's users: possible later; not needed to start.
- **Two parallel 3D physics servers** (Jitter2 as today, a Jolt copy beside it): duplicates ~1 200 lines of engine
  logic that would drift.
- **Jolt's own high-level features** (`CharacterVirtual`, contact-listener signals) in the Jolt backend: faster, but the
  backends would behave differently and the shared test suite would need per-backend expectations. Possible per
  feature after B4.
- **Replacing Silk.NET first**: the largest rewrite for parity at best; deferred to B6/B7 (B7 with M11).

## Consequences

- Every game made with Mainframe shows the BRUTAL logo and ships its notice unless it is non-profit or has Ahwoo's
  permission. Release notes, the README and the template README say so.
- Linux builds carry a 50 MB unstripped `libjolt.so` until upstream strips it (editor releases strip it).
- Jolt is unavailable on osx-x64 (no native), so those builds run Jitter2 with a log line.
- Physics behaviour can differ slightly between backends (contact start distance, resting penetration, non-uniform
  scale on spheres and capsules); differences are named tolerances in the test harness and listed in the physics doc.
- `ProjectSettingsFormat` gains one migration step per phase that changes it (B1: `splash`; B2: Jitter2-only keys
  move under `physics.3d.jitter2`), in merge order.
- G9 joints are designed on the seam once B2 lands, not on Jitter2.

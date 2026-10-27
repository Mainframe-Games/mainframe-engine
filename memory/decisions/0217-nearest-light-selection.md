# ADR 0217 — Nearest-light selection over the UBO limits

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** Neon example (`Examples/Neon`): an apartment with about 25 point and 10 spot lights
- **Docs:** docs/design/lighting.md (`LightEnvironment`, Known issues)

## Context

The forward renderer's lights UBO holds 16 point and 8 spot lights (`Assets/Shaders/limits.json`). `WriteUbo` took the
first 16/8 of each list, which is the order the light nodes entered the tree, and the shadow planner indexed the same
lists. A level with more lamps than that lost the ones added last, wherever the camera was: in the Neon apartment, whole
rooms went dark. Raising the limits costs every lit pixel a longer loop and UBO space; clustered or tiled lighting is a
much larger change than the Neon example needs.

## Decision

- **`LightEnvironment.SelectLights(Vector3 cameraPosition)`** reorders `PointLights` and `SpotLights` in place so the
  lights nearest the camera come first. `WriteUbo` and `ShadowPlanner` are unchanged: they still take the first
  `MaxPoint`/`MaxSpot`, so the UBO slots and the shadow maps index the same lights by construction.
- **Score** = `max(0, distance(camera, light.Position) − light.Range)`: every light whose range contains the camera
  scores 0 and ranks first; ties go to registration order (`Light.RegistrationOrder`, set by `AddLight`).
- **Hysteresis:** the first `max` lights of the list are last frame's selection. Their key is lowered by
  `0.25 + 10 %` of their score (`SelectionMarginDistance`, `SelectionMarginFraction`), so a challenger replaces one only
  when it is clearly nearer, and a lamp at the cutoff does not flicker as the camera hovers there.
- **The selection keeps registration order** (a second insertion sort over the first `max`). Its UBO slots, and which of
  its lights get the four point-shadow cubes and the spot atlas tiles (the first casters, as before), then change only
  when the set changes, not whenever two selected lights swap distances.
- **Strict no-op within the limits:** a list at or under its limit is never touched (and is restored to registration
  order if it was reordered while over the limit), so scenes under the limits render exactly as before (no golden
  changed). Directional lights are never reordered.
- **Insertion sorts on the list's span**, keys stored on the lights (`Light.SelectionKey`): no allocation per frame,
  and close to linear because the order barely changes between frames.
- **Who selects, with which camera:** `RenderServer.PrepareFrame`, once per world per frame (a frame stamp on the
  `LightEnvironment`; the hysteresis compares frames, so a second selection in the same frame would corrupt it), before
  the shadow plan and every UBO write. The root world uses the main camera; each `SubViewport`'s own world uses that
  viewport's camera. Every viewport owns its `World3D`, so this is per-view selection without a per-view copy of the
  lists; if worlds are ever shared between views, the first view to render the world this frame (the root first)
  chooses, and the others see its selection — the shadow maps, rendered once per world, need one order anyway.
- Light gizmos still draw every light.

## Alternatives rejected

- **Raise the limits:** a longer loop for every lit pixel, still a cliff, just later.
- **Clustered/tiled forward lighting:** the long-term answer, but a renderer project of its own (compute light culling,
  new set 0 data, shader changes); not needed to ship the Neon example.
- **Write the nearest lights into the UBO without reordering the lists:** the shadow planner would then need its own
  index map from list index to UBO slot; reordering keeps one source of truth.
- **Full distance order inside the selection:** UBO slots and shadow-cube ownership would churn every time two selected
  lamps swap distances.

## Consequences

- Scenes over the limits now light the area around the camera; lights far away drop out (still without a warning).
  A lamp that leaves the selection disappears at once, not faded.
- Point-shadow cubes go to the first four casters of the selection in registration order, not to the four nearest.
  Shadowing the nearest casters would need its own hysteresis; left for later.
- `Light` gained three internal members (`SelectionRange`, `RegistrationOrder`, `SelectionKey`).
- Tree-less code that builds a `LightEnvironment` by hand keeps the first lights added unless it calls `SelectLights`.

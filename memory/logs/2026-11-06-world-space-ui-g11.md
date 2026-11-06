---
summary: >-
  G11.1–G11.4: Camera3D frustum helpers, a shared line breaker and fallback fonts, the full Label3D option set on the
  mesh batcher (multi-line, autowrap, FixedSize on the CPU, alpha cut; ADR 0249), and UiMarker3D markers as rows of one
  UiMarkerHost document, moved from C# between content refreshes (ADR 0250); a Demo World UI tab, an editor marker
  badge. Panels on meshes (G11.5/6) and the batched text renderer are left.
---

# 2026-11-06: World-space UI (G11.1–G11.4)

Branch `feature/world-space-ui-g11`. Plan: [World-space UI G11](../plans/2026-11-06-world-space-ui-g11.md).
As built: [World-space UI](../../docs/design/world-space-ui.md); the [proposal](../../docs/design/future/world-space-ui.md)
keeps G11.5–G11.9.

## What changed

- **G11.1:** `Camera3D.GetFrustum`, `IsPositionInFrustum`, `GetFrustumPlanes` (inward normals), `Camera3D.Project`;
  `Frustum.Contains`/`CopyPlanes`.
- **G11.2:** `TextLayout.BreakLines` (Label2D's breaking moved unchanged), `Font.Fallbacks` plus per-call extra
  fallbacks (one atlas, face ids in the cache key), `LocaleFallbackFonts` from the project's `FontFallbackTable`.
- **G11.3:** `Label3D` gains `Width`, `Autowrap`, `LineSpacing`, `Uppercase`, `FixedSize`, `AlphaCut`,
  `TextRenderPriority`, `GetAabb`, `GetLines`; `GeometryInstance3D.RenderFixedPixelSize` + `Billboard.FixedSize`.
- **G11.4:** `UiMarker3D`, `UiMarkerHost`, `Assets/UI/markers/markers.{rml,rcss}` (nameplate, health, prompt, waypoint),
  edge clamp with angle, physics occlusion with fade, element mode, signals; `UiDocument.PrepareFrame` is virtual.
- **G11.7 (part):** the editor's `marker.svg` scene badge at the marker's point with a dashed line; `Tests/QA/world-ui.qa`.
- **G11.8 (part):** Demo `world_ui` tab (`WorldUiScene`, `WorldUiWalker`, `WorldUiPanel`), Spanish and qps, Demo tests,
  screenshot.
- Docs: world-space-ui.md (new), proposal status, canvas, localization, game-ui, editor, demo, testing, milestones,
  AGENTS.md, README, landing page (feature card, editor and Demo screenshots).

## Decisions

- [ADR 0249](../decisions/0249-label3d-on-the-mesh-batcher.md): no `Text3DRenderer` yet; `FixedSize` on the CPU.
- [ADR 0250](../decisions/0250-markers-as-rows-of-one-document.md): the row model; positions from C# between refreshes.
- Proposal open questions decided by default: 1 (coverage bitmaps), 3 (measured), 4 (row model, no shim rebuild).
- ADR numbers 0247/0248 were left for the parallel G19 and G3.7 sessions (noted in the shared session notes).

## Dead ends

- Dirtying the marker list every frame: RmlUi re-evaluates every row's views (≈ 1.1 ms for 100 four-kind rows; 0.32 ms
  with only the transform binding). Fixed by the fast path (≈ 0.55 ms).
- RmlUi has no user-agent style sheet: the first marker golden ran title, name and distance inline; every div in the
  marker document is `display: block` now.
- The first editor QA run selected nodes before the scene had loaded: `open` needs `wait-for project`.

## Verified

- See the PR for the gate results (filled in at ship time).

## Not done

- G11.5 UI render targets, G11.6 `UiPanel3D`, `Texture2D.FromGpuSource`, G11.7's panel preview, the batched
  `Text3DRenderer`, G11.9; a bundled CJK face (Japanese in the Demo); markers at ≤ 0.3 ms (0.55 ms measured, busy machine);
  `baseline.json` entries for `WorldUiBenchmarks` (needs the quiet baseline machine).

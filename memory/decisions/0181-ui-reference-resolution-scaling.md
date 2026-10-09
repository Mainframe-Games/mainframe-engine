# ADR 0181 — Game UI scales with the screen from a reference resolution (Unity's CanvasScaler)

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** M8 game UI follow-up (requested during the Forest slice, G8e)
- **Spec:** docs/design/game-ui.md → Scaling; docs/design/project-and-gamehost.md → `project.mfproj` (UI)

## Context

Brogan asked for "all UI to have a reference scale of 1920x1080 and expand with the screen size". Until now a layer's
dp ratio was the content scale (`UiScaleMode.Dpi`, the default: the display's pixels per point, or the fixed
`EngineOptions.ContentScale`). A game UI therefore kept its pixel size whatever the window: the Forest's FPS HUD and
the F12 overlay (just enlarged ×1.4 to read well at 1080p) were small at 1440p and tiny at 4K. A per-layer
`UiScaleMode.ReferenceResolution` (smaller axis ratio) existed, but every layer had to opt in, and the dev overlay and
the RmlUi debugger could not.

## Decision

- **`UiScaling`** (a record): `Mode` `ConstantPixelSize` (dp = content scale, the old behaviour) or
  `ScaleWithScreenSize`; `ReferenceResolution` (default 1920×1080); `MatchWidthOrHeight` (0 width, 1 height, between a
  log-space blend: `2^lerp(log2(w/refW), log2(h/refH), m)`, Unity's formula); optional `MinScale`/`MaxScale` (0 = none).
  Computed from the layer's **pixel** size (framebuffer or region), so HiDPI needs no special case: a 1920×1080-point
  window on a 2× display is 3840×2160 px and gets a 2× UI, the same physical size as 1080p at 1×.
- **One setting for every game UI.** `UiServer.Scaling` (from `UiServerOptions.Scaling`) drives the new default layer
  mode `UiScaleMode.Project`, the developer overlay (a `Project` layer) and the debugger. Explicit layer modes (`Dpi`,
  `Pixels`, `ReferenceResolution`) still override it; `UiLayer.Scaling` replaces it for one layer (not serialized).
  The ratio is recomputed each frame from the layer size (arithmetic only, 0 B); RmlUi re-lays out when it changes.
  Mouse input needs nothing (contexts and the mouse are both in pixels).
- **Default match: height (1).** Games are landscape and authored for a design height: with height the layout is always
  1080 dp tall, so vertical stacks fit on every aspect and wider screens get more room at the sides (Unreal's default
  DPI rule, "shortest side", does the same for landscape). 0.5 would make an ultrawide UI taller than the screen
  (3440×1440 → 1.545, 932 dp of height for a 1080 dp layout). On 16:9 every match gives the same result.
- **project.mfproj `ui` section** (format 3): `scaleMode`, `referenceResolution`, `matchWidthOrHeight`, `minScale`,
  `maxScale`; `UiProjectSettings.ToScaling()` → `GameHost.CreateUiOptions`. The editor's Project Settings get a UI
  section.
- **Defaults.** New projects (`new ProjectSettings()`, the `mfgame` template, the editor's New Project) and the Forest
  scale with the screen from 1920×1080. The 2 → 3 migration writes `ui.scaleMode: "ConstantPixelSize"` into older
  project files, so existing games keep their UI size until they choose otherwise. The engine default
  (`UiServerOptions.Scaling`, used by `Engine` subclasses: the editor, the render-test host, unit tests) stays
  `ConstantPixelSize`, so no existing golden changes.
- **The Demo uses its own 1280×720 base**, not 1920×1080 (a deviation from the request's letter): its panels, nav bar
  and the shared widget library are authored in dp for its 1280×720 window, the same base its 2D canvas stretches from
  (`canvas_items`). A 1080p reference would draw them at two thirds of today's size at the default window (8–9 px text
  on a 1× display); 1280×720 keeps today's look and still grows with the window. One line in `Examples/Demo/project.mfproj`
  switches it.
- **The editor stays on the display's scale.** Its UI server keeps `ConstantPixelSize` and every editor layer is pinned
  to `UiScaleMode.Dpi`. Games started with Play are separate processes and use their project's setting. The UI preview
  tab lays the previewed document out with the open project's scale at the preview's size.
- Small additions on the way: the `--dev-overlay` host flag (start with F12 shown, for QA captures), and
  `project.mfproj` now writes the `window` section when only its stretch or `contentScale` values differ (they were
  dropped).

## Consequences

- At 1280×720 / 1920×1080 / 2560×1440 the Forest's FPS HUD and dev overlay cover the same fraction of the frame
  (QA captures; the dev overlay's ×1.4 sizes read as at 1080p everywhere). The other lane's ESC menu, a `UiLayer`, scales
  the same way with no code.
- A game UI authored in `px` does not scale; documents should use `dp` (and `%`/anchoring for other aspects).
- Portrait (mobile, M12/M13) will want width matching or a "shortest side" mode; `MatchWidthOrHeight` covers it per
  project for now.
- Render test `ui-scaling` (moltenvk and lavapipe goldens) checks proportional layout across a resize; unit tests cover
  the math, migration, overrides, input and allocation.

## Alternatives considered

- **Change `UiScaleMode.ReferenceResolution`'s default on every layer.** Per-layer opt-in misses the dev overlay and the
  debugger, and every scene would carry the reference; one project setting is what Unity, Godot and Unreal do.
- **Make ScaleWithScreenSize the engine-wide default.** It would churn every UI golden (render tests run at 320–520 px
  wide, so a 1080p reference shrinks them to ~0.3×) and the editor; the migration-plus-new-project default gives new
  games the behaviour without touching existing ones.
- **Godot's `canvas_items` stretch for the UI.** Godot scales UI through the root viewport's content scale; the engine's
  RmlUi layers lay out in their own contexts, and the dp ratio is the native way to scale them without resampling.
- **Unity's `Expand`/`Shrink` match modes.** Not needed yet; `ReferenceResolution` layers already give "fit" (smaller
  axis) when a layer must always fit.

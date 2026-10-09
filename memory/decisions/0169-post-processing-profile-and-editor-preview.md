# ADR 0169 — Post-processing as a resource, previewed and edited in the editor

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/pp`, forest slice wave 5.5)
- **Milestone:** Gameplay toolkit G8e (forest slice W5.5)
- **Spec:** the user's request ("make the post processing in the editor so I can tweak it later; make it a resource file
  as well"); current state in docs/design/post-processing.md, editor.md, scene-serialization.md and forest.md

## Context

Waves 1–5 put tonemap, auto exposure, glow, light shafts, SSAO and the colour adjustments with a LUT (ADR 0124, 0154,
0160, 0165, 0168) on `WorldEnvironment` as ~45 flat properties, packed into the internal `PostProcessSettings` struct.
Three problems for tuning the Forest's look:

- The look lived in each scene: two scenes could not share it, and the Forest's builder held every value.
- Only the tree's root view ran post effects. The editor's scene view is a `SubViewport`, so the editor showed the engine
  tonemap alone: no glow, SSAO, TAA, grade, DoF or film. Tuning meant playing the game.
- The editor could create inline resources but not save one as a `.mres`, nor edit a `.mres` in place, and nested
  resources showed their `[ExportGroup]`s as one flat list.

## Decision

- **`PostProcessProfile : Resource`** (Godot names, export groups Tonemap, Auto Exposure, Glow, Light Shafts, SSAO,
  Adjustments) holds every post setting that was on the environment. Its setters keep the packed `PostProcessSettings`
  (`Settings`) up to date and raise `Changed`: the per-frame read stays a struct copy (0 B). `FromSettings` builds one
  from a struct; code writes `new PostProcessProfile { SsaoEnabled = true, … }`.
- **The Godot split.** Sky, ambient and reflected light, fog and wind stay on `WorldEnvironment` (the world's light and
  atmosphere; fog is shaded in the lit shaders, not a screen pass). The lens stays its own resource:
  `CameraAttributesPractical` on `WorldEnvironment.CameraAttributes` and `Camera3D.Attributes`, as in Godot, which keeps
  camera attributes out of `Environment`. `WorldEnvironment.PostProcess` is the profile reference (`.mres` or inline);
  `WorldEnvironment.PostProcessSettings` is profile + lens, what the renderer reads.
- **Callers move, no forwarding properties.** Every caller in the repository (render-test host scenes, unit tests, the
  Forest) sets the profile; the flat properties are removed. Saved scenes migrate: `WorldEnvironment` is
  `[SerializedVersion(2)]`, and its 1 → 2 migration moves the old keys into an inline profile. Migrations only see a
  `PropertyBag`, so `PropertyBag.SetInlineResource` writes `{"type": …, "props": {…}}` and the resource codec reads that
  shape as a new inline resource of the file (`DeserializationContext.CreateInlineResource`; its `{"res": …}` references,
  such as the LUT, resolve against the file's table). Files never contain the shape: the next save writes a normal
  sub-resource.
- **Post-processing in sub-viewports, one stack per view.** `SubViewport.PostProcessing` (default off, Godot-style
  per-viewport `AntiAliasing` and `TaaSharpness`) gives the view a `SubViewportPost`: its own `PostProcessStack` with an
  instance of every built-in effect, a target pool, a depth prepass on the view's HDR target, the post tonemap and the
  `AfterTonemap` ping-pong, the last effect writing the view's LDR image (the `ColorTarget` the UI shows). The main view's
  code is untouched (other lanes edit it); the view repeats its tonemap step in `PostTonemapPass`, and a render test
  checks the view registers the main view's built-in list. `FrameContext` keeps SSAO, contact shadows and jitter per view
  (`PostView`, `SetViewJitter`, `ResetView`); `PostEffectContext` carries the view's `JitterIndex` and `LightShaftsSun`.
- **Debug visuals after post.** In a post-processed view the `Grid3D`, `DebugLines` and `OverlayLines` draw after the
  effects, in an overlay pass on the view's LDR image with the scene depth read-only, through pipeline variants whose
  fragment shaders write display values (specialization constant 0). They use the camera unjittered, from a second view
  slot, so TAA's jitter never shimmers them; picking keeps the jittered view (under a pixel, as in the main view).
- **A resized view gets a new post state.** Effects destroy their objects directly (they expect an idle device), so the
  old `SubViewportPost` goes to `DeletionQueue.EnqueueDispose`, disposed once the frames that used it finished.
- **The editor previews it.** The scene view sets `PostProcessing` (toolbar toggle "Preview Post-Processing", icon
  `sparkles`, on by default; View menu) with the open project's `rendering.antiAliasing`, `taaSharpness` and `exposure`.
- **Editing the profile.** Nested `[ExportGroup]`s are collapsible titles, closed by default (Godot's sub-resources).
  A `.mres` opens in place too (Edit); its edits go through the scene's history and are written to the file when the
  scene is saved (`EditedScene.EditedResourceFiles`), as Godot saves edited sub-resources. Imported assets (a LUT, a
  texture) stay closed. "Save as .mres" (`device-floppy`) on an inline resource is the minimal G7.2 path: a deep copy is
  saved and every slot of the scene that held the inline resource gets the file, in one undo step.
- **The Forest.** Its look is `Content/PostProcess/forest.mres` and its lens `forest-lens.mres`, referenced by the
  scene; the files are the source of truth (`--write-scenes` writes them only when missing, so editor tweaks survive).
  `ForestValley` is a `[Tool]`, so the editor shows the valley (gameplay hooks run in the game only), and
  `SceneSaver`'s save hooks skip runtime (unowned) nodes, so saving the scene in the editor never writes the generated
  terrain's layers.

## Alternatives considered

- **Forwarding `[Obsolete]` properties on `WorldEnvironment`.** Keeps old code compiling but doubles the inspector and
  the API; every caller was in the repository, and saved scenes have the migration.
- **The lens inside the profile.** One file for the whole look, but a third place for DoF next to `Camera3D.Attributes`,
  and unlike Godot. The Forest saves its lens as a sibling `.mres` instead.
- **Refactoring the main view into a shared "post view".** The cleanest end state, but it rewrites
  `VulkanRenderer.Presentation`, which the G8e lanes are changing in parallel. The sub-viewport class is written so the
  main view can adopt it later.
- **Debug visuals in the HDR scene pass (as before).** The grade would tint, glow would bloom and DoF would blur the grid
  and gizmos, and TAA would smear them.
- **An sRGB view of the LDR image for the overlay pass** (hardware encode, linear blending): needs mutable-format images.
  The display-value shaders raise the grid's alpha halfway towards its sRGB encoding instead, which keeps faint lines as
  visible as they were.
- **Making `Terrain3D`'s save hook check ownership.** The hook is generic; skipping unowned subtrees in `SceneSaver`
  matches what it saves.

## Consequences

- Existing scenes load unchanged (migration), and every existing golden matches, except the editor's two
  (`editor_frame0051`, `editor-filesystem_frame0040`: the new toolbar and row buttons, the grid after post; the FileSystem
  golden also gains the template's `icon.png`, which had been under the tolerance).
- Scenes saved now write `"v": 2` on `WorldEnvironment` (the Demo scenes and the showcase fixture were regenerated).
- A post-processed view costs what the main view's post costs, at its own size: the Forest in the editor at 2× (about
  2500 × 1650 view pixels) runs at ~20 fps with the preview on and ~43 fps with it off on an Apple M5; the toggle is
  there for that. Every open tab with post keeps its state (TAA history, pool) while hidden.
  *Update (2026-10-09):* profiled per pass, nothing in the view's post ran twice; every pass scales with the view's
  pixels. The 3D view now renders one pixel per point by default (Editor Settings › 3D view: Auto, Full, 75 %, 50 %),
  which takes the Forest from 30 to 61–69 fps with the preview at 2× ([Editor → Performance](../../docs/design/editor.md#performance)).
- Open: the main view could move onto `SubViewportPost`; Make Unique, Open and Show in FileSystem from G7.2's More menu;
  hidden tabs could drop their post state.

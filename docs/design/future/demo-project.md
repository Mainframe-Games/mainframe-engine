# Proposal: MainframeEngine.Demo — one scene per feature

**Milestone:** [Demo & polish](../../milestones.md#demo--polish-) (D1, first) · **Status:** ✅ ·
**Depends on:** [M10 projects & GameHost](../project-and-gamehost.md); the 2D canvas renderer from the Crash Site
Defense port (`origin/mainframe-engine-port`: `CanvasItem` draw API, `Sprite2D`, `CanvasLayer`, Godot `Camera2D`,
Y-down 2D, window stretch — [Canvas](../canvas.md), ADRs 0110–0114), which this work is based on ·
**Followed by:** [Remove ImGui](remove-imgui.md), [Project icons](project-icons.md),
[Demo download](demo-download.md)

## Problem

The engine has two showcase projects and neither does the job:

- **`MainframeEngine.Sandbox`** is an `Engine` subclass (`Game : Engine`, `MainframeEngine.Sandbox/Src/Game.cs:20`),
  not a game project. The editor cannot open it (its node types load as `MissingNode`), it cannot be downloaded, and
  it shows everything at once in one scene (`Sandbox.mscene`: shadows, glTF, physics crates, Spine, audio, HUD, the
  ImGui developer window). There is nothing for 2D physics, `AudioPlayer2D`, `Camera2D` or Spine under a 2D camera.
- **`Examples/EditorShowcase`** is a GameHost project used only for README editor screenshots
  (`justfile:76-77`, `Tests/QA/readme-screenshots.qa`). It duplicates Sandbox content.
- The README has a single Sandbox screenshot; nothing advertises the individual features.

At the same time the Sandbox has picked up jobs that have nothing to do with showcasing: it owns the l10n catalogs the
CI check runs on, the assets linked into unit and render tests, the editor smoke scene and the `just qa` capture flags.

## Goals

- One GameHost project, **`Examples/Demo`** (`MainframeEngine.Demo`), that the editor opens like any game and that
  [Demo download](demo-download.md) ships as a zip.
- One **isolated scene per feature**, switched from a nav bar at the top of the screen.
- Scenes that look good enough for marketing: a **screenshot of every scene** in the README.
- Every non-showcase job of the Sandbox moves somewhere neutral; the Sandbox and `Examples/EditorShowcase` are deleted.

## Non-goals

- New engine 2D node types. The port provides `CanvasItem` (`OnDraw` + `DrawRect`/`DrawCircle`/`DrawPolygon`/…),
  `Sprite2D` and `CanvasLayer`; there is no `Polygon2D`, gradient resource or 2D text. The Demo draws its shapes with
  small Demo-owned `CanvasItem` nodes (`DemoShape2D`, per-vertex colours for gradients) and puts text in RmlUi.
- A networking demo (dropped from the showcase; networking stays covered by its tests).
- New engine features beyond the two small changes listed under [Engine changes](#engine-changes).

## Project layout

Created from the `mfgame` template (`Templates/MainframeEngine.Templates/content/mfgame`) and then edited, so it
matches what users get from New Project:

```
Examples/Demo/
├── Demo.slnx
├── Directory.Build.props        MainframeEnginePath = ../..   (rewritten by Demo download)
├── global.json, .gitignore, .gitattributes   (template copies)
├── project.mfproj               name "Mainframe Demo", main scene = Basic 3D, window.icon, autoloads, locales
├── Demo/                        node library (references MainframeEngine + Generators as analyzer)
│   └── Src/{Nav,Basic3D,Basic2D,Audio,Ui,Physics,Spine}/…
├── Demo.Launcher/               return GameHost.Run(args, typeof(Demo.DemoNav).Assembly);
└── Content/
    ├── icon.png                 project / window icon (see Project icons)
    ├── Nav/Nav.mscene, Nav.rml, nav.rcss
    ├── Scenes/{basic_3d,basic_2d,audio_2d,audio_3d,ui,physics_2d,physics_3d,spine}.mscene
    ├── UI/showcase.rml, showcase.rcss
    ├── Audio/, Models/TestModel/, Models/Spine/SpineBoy/, Sky/
    └── locale/messages.pot, {es,qps}/LC_MESSAGES/messages.{po,mo}
```

The project is **not** in `MainframeEngine.slnx` (it is a game, built through its own `Demo.slnx`, exactly like a
user project). Scene files are authored as `.mscene` JSON in the editor or by a builder; node types are the Demo's
own plus engine types.

## Navigation

- An autoload scene, `Content/Nav/Nav.mscene`, declared in `project.mfproj` `autoloads`. `GameSession.AddAutoloads`
  (`MainframeEngine/Src/Project/GameSession.cs:226`) adds it as `/root/Nav` before the main scene, so it survives
  scene changes (`SceneTree.ChangeSceneToFile` replaces only `CurrentScene`, `SceneTree.cs:180-220`).
- It holds a `UiLayer` (`Layer = 100`, above any scene UI) with `DemoNav : UiDocument` (`Source = Content/Nav/Nav.rml`).
- Top bar, full width, ~40 dp, styled with the engine widget library (`/Content/UI/widgets/widgets.rcss`):
  - left: logo + one tab per scene — **Basic 3D · Basic 2D · Audio 2D · Audio 3D · UI · Physics 2D · Physics 3D ·
    Spine**; the active tab is highlighted;
  - right: FPS readout, language picker (en / es / qps), an "F12: dev overlay" hint (see [Remove ImGui](remove-imgui.md)).
- Tabs call `Tree.ChangeSceneToFile("Content/Scenes/<scene>.mscene")`. `DemoNav` follows `SceneTree` scene changes
  to highlight the right tab, so `--scene` (GameHost) and the editor's "play scene" deep-link correctly.
- Number keys 1–8 switch tabs; Escape quits through the session's quit request.
- All labels go through `Tr` / `Atr` (the Demo keeps the es + qps catalogs).

## Scenes

Each scene is self-contained: its own camera, environment and lights, a small RmlUi side panel (`UiLayer` inside the
scene, `Layer = 10`) for its controls, and nothing that depends on another scene. Every scene must look finished at
1920×1080 for the README screenshot.

| Scene | Content | Controls |
|---|---|---|
| **Basic 3D** | Primitive composition on a reflective-looking checker/plinth: boxes, spheres, cylinders, capsules, tori-like arrangements with varied `StandardMaterial3D` (albedo, emission, blended glass); the glTF test model; panoramic sky (`WorldEnvironment` + `Sky`); one cascaded-shadow sun, a coloured omni and two spot lights, all shadowed; slow orbiting camera. | pause orbit, sun angle, toggle lights, exposure |
| **Basic 2D** | Primitive composition under a `Camera2D` (canvas units, Y-down): a gradient sky (per-vertex-colour quad), layered rolling hills (polygons), sun with rays, orbiting circles, a `Sprite2D` (engine logo), slowly animated with `OnDraw` + `QueueRedraw`. Drawn by Demo `DemoShape2D` nodes. | zoom, pause animation |
| **Audio 2D** | `AudioPlayer2D` emitter sweeping left↔right past the `Camera2D` listener (listener = active Camera2D, `AudioServer.cs:828`), drawn as a moving marker; click anywhere to play a one-shot at that position (`AudioServer.PlayOneShot`). | Master / Music / SFX bus volume sliders, panning strength, max distance |
| **Audio 3D** | `AudioPlayer3D` orbiting an `AudioListener3D` (a visible head mesh at the centre); the emitter's distance ring is drawn with `DebugLines`. | attenuation model, low-pass at max distance on/off, doppler on/off, orbit speed |
| **UI + hot reload** | Engine widget gallery (`/Content/UI/widgets/demo.rml` content) plus a data-bound panel (`CreateDataModel`, sliders/toggles driving a live value). A banner: *"Edit `Content/UI/showcase.rcss` (or `.rml`) and save — this page reloads live"*, with a **reload counter** and last-reload time driven by `UiDocument.Reloaded`. In builds without hot reload (Release engine) the banner says so. | theme accent picker, counter reset |
| **Physics 2D** | Funnel / pegboard of `StaticBody2D`s; `RigidBody2D` circles and boxes rain in (gravity +Y, Y-down). Each body has a `DemoShape2D` child drawing its shape. Clicks are mapped window → canvas through the root viewport's `StretchTransform × CanvasTransform`. | click to spawn, reset, debug shapes |
| **Physics 3D** | Stacked crates and a ramp (`StaticBody3D` + `RigidBody3D`, shared `BoxShape3D`), a `CharacterBody3D` pusher. | click to drop a body at the cursor (ray query via `DirectSpaceState`), reset, debug shapes |
| **Spine** | SpineBoy (`SpineNode`, `Folder = Content/Models/Spine/SpineBoy`) on a lit floor. **Both** a `Camera3D` and a `Camera2D` are in the scene; the toggle switches between them, and the 2D mode detaches the `Camera3D` from the tree (a viewport keeps a `Camera3D` active while any is in it, and `RenderServer.GetRenderCamera` prefers it, so clearing `Current` is not enough; built this way, see [Demo](../demo.md)). Under `Camera2D` (Y-down, looking along +Z) a Y-up skeleton appears upside down and from the back (ADR 0110), so the 2D mode puts SpineBoy under a pivot rotated 180° about X; `SpineScale` is set per mode (~1 under Camera2D, where 1 unit = 1 canvas pixel). 3D visuals render below every canvas item, so the scene keeps `rendering.canvasClearColor` unset. | **Camera2D / Camera3D toggle**, animation picker (walk / run / jump / idle), time scale |

## Engine changes

1. **UI hot reload for GameHost games.** Today only an `Engine` subclass that sets
   `EngineOptions.Ui.SourceContentDirectories` gets hot reload of *source* `.rml`/`.rcss` (the Sandbox does,
   `Game.cs:33`); `ProjectSettings.ToEngineOptions()` (`ProjectSettings.cs:116-135`) never sets `Ui`, so a GameHost
   game only watches the copied `bin/.../Content/UI`. Change: when the engine is a Debug build
   (`UiServerOptions.DefaultHotReload`), `GameHost` adds the game assemblies' `MainframeContentSource` folders
   (`UiServerOptions.SourceDirectoriesOf`) and the project folder's `Content/`. The template gains the Debug-only
   `MainframeContentSource` assembly attribute the Sandbox csproj has (lines 25-31), so every new project gets it.
2. **Spine under `Camera2D`.** `SpineNode.Draw` only uses the camera's view/projection; with the port's Y-down
   `Camera2D` the skeleton needs the 180°-about-X pivot described above. Add a render test (golden) that draws SpineBoy
   under a `Camera2D` with no `Camera3D` and the pivot, upright and front-facing.
3. **Hot reload status.** `UiServer.HotReloadEnabled` (public) and `UiServer.HotReloaded` (event with the reload kind
   and path) — `UiDocument.Reloaded` does not fire for stylesheet-only reloads, and the UI scene's counter must.
4. **Camera rays.** `Camera3D.ProjectRayOrigin(Vector2)` / `ProjectRayNormal(Vector2)` (Godot API, viewport pixels)
   for the Physics 3D click-to-drop.

## Screenshots

- `just demo-screenshots` builds `Examples/Demo/Demo.slnx` and runs the Launcher once per scene:
  `--scene Content/Scenes/<scene>.mscene --fixed-fps 60 --max-frames 180 --screenshot docs/images/demo/<scene>.png`
  at 1920×1080 (GameHost's existing `--screenshot` saves the last frame; `--fixed-fps` makes it deterministic).
  The window size comes from `project.mfproj`; a `--window-size WxH` GameHost flag is added if the project size is not
  the capture size.
- The nav bar is visible in every screenshot (it is part of the product).
- README: a new **Showcase** section near the top with a 2-column gallery of the 8 images, each captioned with the
  feature and linking to its design doc. It replaces `docs/images/sandbox.png`.
- Editor screenshots (`Tests/QA/readme-screenshots.qa`) open `Examples/Demo` instead of `Examples/EditorShowcase`;
  `just qa-editor` (`Tests/QA/editor-walkthrough.qa`) opens the Demo's Basic 3D scene and selects one of its named
  nodes instead of the Sandbox's `Column`.
- Screenshots are re-taken whenever a scene changes visibly; `docs/images/*.png` are LFS (root `.gitattributes`).

## Migrating the Sandbox's other jobs

| Job today | Where it goes |
|---|---|
| Test assets linked from `MainframeEngine.Sandbox/Content` into `Tests/MainframeEngine.Tests` (csproj lines 49-72) and `Tests/MainframeEngine.RenderTests.Host` (lines 17-43): TestModel, SpineBoy, `ambient_hum.ogg`, branding PNG, locale `.mo` | Move to a shared **`Tests/Content/`** and relink. CI excludes `Examples/**` from LFS (`ci.yml:42,53,200,211`), so tests must not link Demo content. The Demo keeps its own copies (LFS dedups identical blobs). |
| `SerializationTests.TheSandboxSceneParsesAndResavesIdentically` (`SerializationTests.cs:596`), `AudioSerializationTests.TheSandboxAmbiencePlaysFromItsSceneFile` (`:144`), `ModelImportTests` TestModel check (`:217`) | A fixture scene `Tests/Content/Scenes/Showcase.mscene` generated by the moved `SandboxSceneBuilder` (renamed `ShowcaseSceneBuilder`, test-only); tests renamed. |
| Allocation gate `SceneTests.SandboxSteadyStateAllocatesNothing` on `RenderTests.Host/Scenes/SandboxScene.cs` | Renamed `ShowcaseScene` / `ShowcaseSteadyStateAllocatesNothing`; same content minus ImGui (see [Remove ImGui](remove-imgui.md)). |
| Editor smoke (`EditorSmokeRun.cs:11,139,243`, `EditorRenderTests.cs:19-27`): opens `Sandbox.mscene`, frames `Column` | Opens the fixture `Showcase.mscene`; splash text updated. |
| l10n: `justfile` `l10n_dir` + `l10n-extract` (lines 170-179), `ci.yml:113`, `build/linux/inside.sh:42` | Point at `Examples/Demo/Content/locale`, extract from `Examples/Demo/Demo/Src` + `Content` + the Demo assembly. `.po`/`.mo` are not LFS, so the CI check works without LFS. |
| `just sandbox`, `just qa` (`--qa-capture`, `--qa-resize`, `--qa-minimize`, `--qa-input`) | `just demo` (run the Launcher); `just qa` becomes an alias of `just demo-screenshots`. Resize is already covered by render tests (`SceneTests.SwapchainRecreationOnResizeAndVSyncToggleIsClean`, `--resize`); minimise and the synthetic mouse-look input move to the render-test host as new `--minimize frame` / `--input frame` options (`Tests/MainframeEngine.RenderTests.Host/HostOptions.cs`) with one test each. |
| `IconTests.cs:154` scans `MainframeEngine.Sandbox/Src` | Scans `Examples/Demo/Demo/Src`. |
| `just editor` with no project + docs: `just editor Examples/EditorShowcase` | `just editor Examples/Demo`; `justfile:76-77` links and builds the Demo instead. |
| CI: Sandbox builds as part of `MainframeEngine.slnx` | The `template` job additionally runs `dotnet build Examples/Demo/Demo.slnx` and a headless `--max-frames 10` run of every scene (no LFS needed to build; a run needs content, so the job pulls LFS for `Examples/Demo/**`). |
| LFS filters `MainframeEngine.Sandbox/**` in `ci.yml:300,311`, `publish.yml:72,83` | Removed; `publish.yml` keeps `Examples/**` excluded except where [Demo download](demo-download.md) needs it. |

Then delete `MainframeEngine.Sandbox/` (and its entry in `MainframeEngine.slnx:21`), `Examples/EditorShowcase/` and
`docs/design/sandbox.md`; add `docs/design/demo.md` (current-state doc once built). Update `CLAUDE.md` (project
structure, `just sandbox`/`qa`, `--write-scene`), `README.md`, `THIRD_PARTY_NOTICES.md` (SpineBoy and sky paths),
`docs/README.md`, the design docs that name the Sandbox (list in the exploration notes: architecture-overview,
asset-pipeline, audio, build-and-platforms, cameras-and-input, game-ui, localization, physics, testing, …) and
`docs/images/architecture-layers.svg` ("Game : Engine (MainframeEngine.Sandbox)" → GameHost game).

## Testing

- Unit: `DemoNav` tab ↔ scene mapping; every scene file in `Examples/Demo/Content/Scenes` parses and re-saves
  identically (a test in `Tests/MainframeEngine.Tests` that reads the Demo's scenes as text — no LFS content needed).
- Render: Spine-under-Camera2D golden; `ShowcaseSteadyStateAllocatesNothing`; validation clean.
- CI `template` job: Demo builds; every scene runs 10 frames headless with validation on and exits 0.
- Manual: `just demo`, click through every tab; edit `showcase.rcss` and watch the counter increase.

## Acceptance

- `Examples/Demo` opens in the editor with no `MissingNode`s and plays from the editor (F5) and from `just demo`.
- 8 tabs; switching keeps the nav bar and frees the previous scene; `--scene` highlights the right tab.
- Hot reload works in the Demo run from the editor or `just demo` (Debug).
- `docs/images/demo/*.png` (8) committed and shown in the README Showcase section.
- No reference to `MainframeEngine.Sandbox` or `EditorShowcase` remains outside `memory/` history
  (`git grep -n "Sandbox\|EditorShowcase" -- ':!memory'` only finds historical notes).
- All gates in `CLAUDE.md` pass.

## Task list

1. Engine: GameHost source hot reload + template `MainframeContentSource`; Spine/Camera2D render test.
2. `Tests/Content/` move, fixture scene + builder, test renames, editor smoke switch.
3. `Examples/Demo` skeleton from the template, nav autoload, Basic 3D, UI, Audio 3D, Physics 3D, Spine scenes.
4. Audio 2D, Physics 2D and Basic 2D on the port's canvas renderer (`DemoShape2D`).
5. l10n move, justfile/CI/QA scripts, screenshots + README Showcase.
6. Delete Sandbox + EditorShowcase, docs sweep.

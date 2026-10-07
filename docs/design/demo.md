# Demo

## Purpose

[`Examples/Demo`](../../Examples/Demo/) ("Mainframe Demo") is a real game project: a `project.mfproj`, a node library
and a `GameHost` desktop project (`Demo.Desktop`), built from the `mfgame` template and opened by the editor like any user game. It has one
isolated scene per engine feature, switched from a nav bar, and every scene is a README screenshot. It replaced the
old test game (an `Engine` subclass the editor could not open) and the editor-screenshot project. The proposal it was
built from is [future/demo-project.md](future/demo-project.md); this page is the current state.

The Demo is **not** in `MainframeEngine.slnx`. It is built through its own `Examples/Demo/Demo.slnx`, exactly like a
user project (engine by project reference, `MainframeEnginePath` in `Directory.Build.props` = `../..`).

## Layout

```
Examples/Demo/
├── Demo.slnx, Directory.Build.props, global.json, .gitignore, .gitattributes   (template copies)
├── project.mfproj            "Mainframe Demo", main scene basic_3d, window.icon, canvas_items stretch,
│                             autoload Nav (DemoNavLayer), locales
├── Demo/                     the node library: Src/{Nav,Basic3D,Basic2D,Audio,Ui,Physics,Spine,Shared}
├── Demo.Desktop/            `GameHost.Run(args, typeof(Demo.DemoScenes).Assembly)` + `--write-scenes <dir>`
├── Demo.Tests/               xUnit v3 tests of the Demo (see Tests and CI)
└── Content/
    ├── Scenes/{basic_3d,basic_2d,audio_2d,audio_3d,ui,physics_2d,physics_3d,spine}.mscene
    ├── Nav/, UI/, Basic3D/, Basic2D/, Audio/, Physics/, Spine/   RmlUi panels (.rml/.rcss), one per scene
    ├── Audio/, Models/TestModel/, Models/Spine/SpineBoy/, Sky/, Basic2D/logo.png, icon.png
    ├── Settings/AudioBusLayout.mres
    └── locale/messages.pot, {es,qps}/LC_MESSAGES/messages.{po,mo}
```

Assets have editor-generated `.meta` files (their UIDs) and **they are tracked**: scene files reference assets by UID
(`"ref": "tex_…"`), and the scene writer derives resource keys from them, so a clone without the `.meta` files would
write different scenes. Binary content (PNG, OGG, `.bin`) is Git LFS (`Examples/Demo/.gitattributes`); `.po`, `.pot`
and `.mo` are plain files so the localization check works without LFS.

## Navigation

`project.mfproj` declares the autoload `Nav` (`DemoNavLayer`). `GameSession.AddAutoloads` adds it as `/root/Nav`
before the main scene, so it survives scene changes (`SceneTree.ChangeSceneToFile` replaces only `CurrentScene`).
`DemoNavLayer` is a `UiLayer` (`Layer = 100`, above any scene UI) holding `DemoNav : UiDocument`
(`Content/Nav/nav.rml`):

- one tab per scene, in the order of `DemoScenes.All` (the single registry: id, title, icon, path, builder);
- an FPS readout and a **language picker** (`en`, `es`, `qps`);
- keys **1–8** switch tabs, **Escape** quits (`Tree.Quit`).

`DemoNav` follows `SceneTree.CurrentScene` and highlights the tab whose id equals the scene root's name, so `--scene`
and the editor's "play this scene" light the right tab. Switching frees the previous scene.

## Scenes

Every scene is self-contained: its own camera, environment and lights, and a small RmlUi side panel (a `UiLayer` with
`Layer = 10` inside the scene) for its controls. A scene's root node is named after its id.

| Id (tab) | What it shows | Controls |
|---|---|---|
| `basic_3d` (Basic 3D) | Primitives with distinct `StandardMaterial3D`s (opaque, blended glass, emissive lamp), the glTF test model, panoramic sky, a cascaded-shadow sun, a coloured omni and two shadowed spots, an orbiting camera (`OrbitCamera`) | pause orbit, sun, lamps, exposure |
| `basic_2d` (Basic 2D) | A `Camera2D` canvas (Y-down): gradient sky, layered hills, a sun with rays, orbiting circles and a `Sprite2D` (the engine logo), all `CanvasItem` draws (`DemoShape2D`, `SunRays2D`, `Orbit2D`) | zoom, pause animation |
| `audio_2d` (Audio 2D) | An `AudioPlayer2D` emitter sweeping left and right past the `Camera2D` listener (`Sweeper2D`); click anywhere for a positional blip and a fading ring (`ClickToPlay2D`) | Master / SFX / Music bus faders, panning, max distance |
| `audio_3d` (Audio 3D) | An `AudioPlayer3D` emitter orbiting an `AudioListener3D` (`Orbiter`), its audible range drawn with `DebugLines` (`DistanceRing`) | attenuation model, low-pass at distance, Doppler, orbit speed |
| `ui` (UI) | The RmlUi widget gallery and a data-bound form (`UiShowcase`, `Content/UI/showcase.rml`), plus a live counter of UI hot reloads | accent, name, volume, quality, reset counter |
| `physics_2d` (Physics 2D) | Box2D: a funnel and pegboard of `StaticBody2D`s, `RigidBody2D` circles and boxes raining in (`Spawner2D`) | click to spawn, collision shapes, reset |
| `physics_3d` (Physics 3D) | Jitter2: a crate stack, a ramp, a `CharacterBody3D` pusher that shoves crates (`Pusher3D`), click-to-drop crates by ray query (`Dropper3D`) | click the floor, rain crates, collision shapes, reset |
| `spine` (Spine) | SpineBoy (`SpineNode`) on a lit floor, casting his shadow onto a backdrop wall (3D view), seen by a `Camera3D` or a `Camera2D` | 2D camera toggle, animation picker |

Mouse input in the 2D scenes is mapped from window points to canvas units through the root viewport's stretch and
camera transforms (`CanvasInput.WindowToCanvas`); Physics 3D uses `Camera3D.ProjectRayOrigin/ProjectRayNormal` and
`DirectSpaceState`.

### Spine: Camera3D and Camera2D

Spine is drawn through the 3D pipeline, so under a Y-down `Camera2D` the Y-up skeleton appears upside down; the 2D mode
puts SpineBoy under a pivot rotated 180° about X (and scales per mode: 1 unit = 1 canvas pixel under the 2D camera).
Both cameras live in the scene file, but the **2D mode detaches the `Camera3D` from the tree**
(`SpinePanel.SetMode`): the engine keeps a `Camera3D` active as long as any is in the viewport, so `Current = false`
alone would never hand the view to the `Camera2D` (see [Cameras & input](cameras-and-input.md#camera-nodes)). The
panel re-adds the camera for 3D mode and frees it itself in `OnExitTree` when the scene ends while it is detached.

### Panels restore global state

Several panel controls change state that is not part of the scene: the renderer's exposure (Basic 3D), the audio bus
volumes (Audio 2D) and the physics servers' collision-shape drawing (Physics 2D and 3D). Each panel records the value
on `OnReady` and **restores it in `OnExitTree`**, so leaving a scene does not change how the next one looks or
sounds.

## Scene files: `--write-scenes` and the contract

Scenes are built in code (`DemoScenes.All[i].Build`, helpers in `DemoBuild.Add`, which sets `Owner` so children are
saved) and saved as `.mscene` (format 2). The committed files are generated:

```bash
dotnet run --project Examples/Demo/Demo.Desktop -- --write-scenes Examples/Demo/Content/Scenes
```

`SceneFilesTests.CommittedScenesMatchTheBuilders` is the contract: for every scene, building it and serializing it with
the committed scene's UID must reproduce the committed file byte for byte. Change a builder, regenerate, commit the
scenes; a regeneration that leaves a diff means the file was stale (or an asset's `.meta` is missing, see Layout).
The scenes stay editable in the editor, which loads the Demo's node types from its assembly (no `MissingNode`s).

## Hot reload

A Debug build of the Demo records its `Content/` folder in the `MainframeContentSource` assembly attribute
(`Demo.csproj`); `GameHost` hands those folders to `UiServer`, which watches them. Editing a `.rml` or `.rcss` and
saving reloads the document in the running game without a rebuild. The UI scene shows it: `UiServer.HotReloadEnabled`
tells the page whether it is on, and `UiServer.HotReloaded` (also raised for stylesheet-only reloads) drives the reload
counter and the last-reload time. Release builds of the engine do not hot reload and say so on the page.

## Screenshots

`just demo-screenshots` (alias `just qa`; `build/demo-screenshots.sh`) builds the Demo in Release and runs `Demo.Desktop`
once per scene (`--fixed-fps 60 --max-frames 240 --screenshot docs/images/demo/<id>.png`; `GameHost` saves the last
frame, and the fixed step makes it deterministic). The nav bar is part of every image. The eight PNGs
(`docs/images/demo/`, Git LFS) form the README's Showcase gallery; re-take them when a scene changes visibly. They need
the display awake (`caffeinate -u`).

## Tests and CI

- `Demo.Tests` (`dotnet test Examples/Demo/Demo.Tests`): the scene registry (order, paths, root-name mapping), the scene
  file contract above, switching every tab twice leaves exactly one scene, the project file (stretch mode, main scene
  and icon exist), canvas mouse mapping, `DemoShape2D`, and one test class per scene for its builder and panel
  (including that leaving a Physics scene restores the collision-shape overlay and toggling Spine back restores the
  `Camera3D`). Tests that touch RmlUi are in the `SerialRmlUi` collection.
- `build/demo-smoke.sh <work-dir> [frames]` (CI job `template`; `just` has no recipe, run it directly) builds
  `Demo.slnx` with warnings as errors, runs `Demo.Tests`, then runs **every `Content/Scenes/*.mscene`** headless
  (`--hidden --fixed-fps 60 --no-vsync`) for a few frames with its own user-data folder, and fails on a non-zero exit, a
  missing screenshot or an `[ERROR]`/`[FATAL]` line in the scene's log. It only cleans the per-scene folders and PNGs it
  creates under the work directory.
- The engine's own render tests cover Spine under a `Camera2D` (a golden) independently of the Demo.

## Localization

Catalogs are in `Examples/Demo/Content/locale` (`en` source, hand-written `es`, `qps` pseudo-locale). `just
l10n-extract` reads `Examples/Demo/Demo/Src`, the Demo's `Content` (RML and scene strings) and the engine's widget
library; `just l10n-check` (CI) fails on a stale `.mo`. See [Localization](localization.md#demo).

## Related docs

[Cameras & input](cameras-and-input.md) · [Canvas](canvas.md) · [Game UI](game-ui.md) · [Audio](audio.md) ·
[Physics](physics.md) · [Projects & GameHost](project-and-gamehost.md) · [Testing](testing.md) ·
[Localization](localization.md) · [Milestones](../milestones.md)

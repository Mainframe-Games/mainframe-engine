# CLAUDE.md — Mainframe Engine

## Build & Run

Local commands are `just` recipes (run `just` to list them); CI calls `dotnet` directly. The solution is
`MainframeEngine.slnx` (XML format; there is no `.sln`).

```bash
just build            # dotnet build MainframeEngine.slnx — 0 warnings, warnings are errors
just test             # unit tests: engine (Tests/MainframeEngine.Tests) + editor (Tests/MainframeEngine.Editor.Tests)
just test-render      # render tests: goldens + validation gate + allocation gate (needs a GPU/display)
just test-linux       # CI's Linux unit/editor tests in Docker (x86_64 ubuntu:24.04)
just render-tests-linux  # render tests on lavapipe in Docker: checks/records the `lavapipe` goldens locally
just demo             # the Demo game: dotnet run --project Examples/Demo/Demo.Desktop (extra args: --scene Content/Scenes/x.mscene, ...)
just demo-screenshots # one PNG per Demo scene in docs/images/demo (README Showcase); `just qa` is an alias
just golden-update    # re-record render-test goldens for this driver; inspect the PNGs before committing
just format           # dotnet format (format-check is what CI runs)
just bench            # benchmarks vs Tests/MainframeEngine.Benchmarks/baseline.json (quiet machine only)
just editor [project|scene]  # the editor (no argument: the Project Manager)
just qa-editor        # scripted editor QA (Tests/QA/editor-walkthrough.qa) → artifacts/qa-editor
just qa-projects      # create a game, play it, edit its C#, reload (needs the .NET SDK) → artifacts/qa-projects
just template-smoke   # dotnet new mfgame against this checkout, build it, run it headless (CI job "template")
just publish-local    # package the editor like the release workflow (artifacts/release)
just l10n-check       # committed .mo files match their .po (CI runs it)
```

Gates before committing anything substantial: `just build`, Release `dotnet build MainframeEngine.slnx -c Release
-warnaserror -p:CompileShaders=false`, `just test`, `just test-render`, `just format-check`, `just shaders-check`.

Build settings live in `Directory.Build.props` / `Directory.Packages.props` (central package
versions — never put `Version` on a `PackageReference`); the SDK is pinned in `global.json`.

Shaders are **Slang** (ADR 0144; rules in `docs/design/shaders.md#writing-shaders`: `mul(v, M)` with the C#
matrices, `SV_VulkanVertexID`, `glsl_mod`). They compile during `dotnet build` (`build/Shaders.targets`: `slangc
-target spirv -capability spirv_1_5 -matrix-layout-row-major -I Content/Shaders/include`, incremental; without slangc
the build warns and ships the committed `.spv`). The committed `.spv` files are that fallback, so after any shader or
`include/*.slang` change also refresh them and commit them with `MainframeEngine/Content/Shaders/shaders.lock` (CI
fails on stale `.spv`). A vertex shader writes only what its fragment shader reads (Slang drops unread fragment inputs,
and the leftover output is a validation warning):

```bash
just shaders          # slangc (the build's flags) + spirv-val on every shader, rewrites shaders.lock
just shaders-check
```

Light/shadow limits live only in `MainframeEngine/Content/Shaders/limits.json` (the build generates
`ShaderLimits.g.cs` and `include/limits.slang`). Load content with `ContentPaths.Resolve`, never a
working-directory-relative path.

Rendering changes must keep the render tests green; if output changes intentionally, run
`just golden-update`, inspect the PNGs and commit them (see `docs/design/testing.md`). Per-frame code
must not allocate (allocation gate) and must produce no validation warnings.

### macOS

Vulkan runs through MoltenVK, bundled via the `Silk.NET.MoltenVK.Native` package — no install
needed to run. The Vulkan SDK (lunarg.com) is recommended for development (validation layers,
`slangc`). Windowing and input are SDL2 (Silk.NET SDL backend; GLFW is not referenced).
`VulkanLoaderBootstrap` (called in the `Engine` constructor after SDL is selected, before the
window exists) probes for a Vulkan library and hands the same one to SDL (`SDL_Vulkan_LoadLibrary`)
and to Silk.NET's `Vk` — required because modern macOS dyld no longer searches `/usr/local/lib` for
leaf-name dlopen, and the two would otherwise bind different libraries whose instances are not
interchangeable. HiDPI: use `Engine.FramebufferSize` (pixels), not `Window.FramebufferSize` (points
under SDL). The renderer enables `VK_KHR_portability_enumeration`/`VK_KHR_portability_subset`
capability-conditionally; Windows/Linux are unaffected. Render tests and `just qa` need the display
awake (`caffeinate -u -t 600 &`).

The editor's macOS app name (Dock tooltip, menu bar) is "Mainframe Engine", which only a bundle's Info.plist can set:
every macOS editor build assembles `bin/<cfg>/net10.0/Mainframe Engine.app` (a symlink to the apphost) and `dotnet run`
/ `just editor` start it from there (`build/macos/DevAppBundle.targets`, ADR 0096). The bare `bin/…/MainframeEngine.Editor`
still runs but shows the executable name. The executable/assembly name stays `MainframeEngine.Editor`. Games get the same
bundle, named and iconed from `project.mfproj` `name`/`window.icon`, through `build/MainframeGame.props` (ADR 0182:
`just forest` runs `Forest Demo.app`).

## Project Structure

- `MainframeEngine/Src/Core/` — `Engine` base class, `GameTime`, `FPSCounter`, `Time` (ticks, Unix time)
- `MainframeEngine/Src/Math/` — 2D value types from GodotSharp (MIT): `Mathf`, `Vector2I`, `Rect2I`, `Color`/`Colors`, Godot's
  `Vector2` members as extensions on `System.Numerics.Vector2` (the one 2D vector). See `docs/design/math.md`
- `MainframeEngine/Src/Scene/` — Godot-style node tree: `Node`, `SceneTree`, `Node3D`/`Node2D`, transforms, `NodePath`,
  camera/light/sky/grid nodes (`Nodes3D/`, `Nodes2D/`), input events
- `MainframeEngine/Src/Nodes/` — drawable/network nodes: `SpineNode`, `NetworkNode` (meshes: `Scene/Nodes3D/GeometryInstance3D.cs`)
- `MainframeEngine/Src/Rendering/Resources/`, `Rendering/Meshes/` — `Mesh`/primitives, `StandardMaterial3D`, `Texture2D`;
  `MeshRenderer` batching, `PipelineStateCache`, picking, sub-viewports; `Src/Resources/Import/` — texture/model (Assimp) importers
- `MainframeEngine/Src/Servers/` — `ServerRegistry`, `RenderServer`
- `MainframeEngine/Src/Audio/` — `AudioServer` (SoundFlow; null device when there is no audio device), buses,
  `AudioStream`, audio nodes (`AudioPlayer`/`2D`/`3D`, `AudioListener3D`); audio-thread code in `Graph/`, lock-free
  rings + streaming in `Threading/`, decoders in `Decoding/`. Game code never calls SoundFlow: it goes through
  `AudioServer` (commands to the audio thread). See `docs/design/audio.md`
- `MainframeEngine/Src/Physics/` — M6 physics: `PhysicsServer3D` (Jitter2) / `PhysicsServer2D` (Box2D.NET), spaces, body
  nodes (`StaticBody*`, `RigidBody*`, `CharacterBody*`, `Area*`), `CollisionShape*`, shape resources, queries
- `MainframeEngine/Src/Resources/`, `Src/Serialization/` — `Resource`, `PackedScene`, loader/savers, `AssetDatabase`;
  `[Export]`/`[Signal]` attributes, `TypeRegistry`, JSON scene format
- `MainframeEngine.Generators/` — Roslyn source generator registering node/resource types (referenced as an analyzer)
- `MainframeEngine/Src/Rendering/` — Vulkan renderer, meshes/materials/textures, camera math, sky, shadows, scene grids,
  Spine renderer, `DebugLines`, `Gizmos/` (`ScreenGizmos`: light + axis gizmos), `Post/` (ADR 0163: `PostEffect` stages and
  stack, the depth prepass and motion vectors, auto exposure, glow, light shafts, FXAA; `docs/design/post-processing.md`)
- `MainframeEngine/Src/UI/` — game UI (M8): `Rml/` managed RmlUi binding over the `mfrmlui` C ABI, `Rendering/`
  `VulkanUiRenderer`, `UiServer`/`UiLayer`/`UiDocument`/`UiElement`; widget library and fonts in `Content/UI/`
- `MainframeEngine/Src/Text/` — canvas text (ADR 0118): `Font` (.ttf), managed `TrueTypeFont` reader and `GlyphRasterizer`
- `MainframeEngine/Src/Lighting/` — `LightEnvironment`, `DirectionalLight`, `PointLight`, `SpotLight`
- `MainframeEngine/Src/Networking/` — messages, transports, `MultiplayerApi` replication (`[Replicated]`/`[Rpc]`, generated by `MainframeEngine.Generators`)
- `MainframeEngine/Src/Steamworks/` — Steam API wrappers
- `MainframeEngine/Src/Debugging/` — `Log` + `ILogSink` sinks (console, rotating file, memory ring); `DevOverlay/` — the F12 developer
  overlay (RmlUi panels; games add panels with `DevOverlay.AddPanel`; see `docs/design/dev-overlay.md`)
- `MainframeEngine/Src/Localization/` — `Tr` (gettext catalogs, locale switching), RML text contract, font fallback
- `MainframeEngine/Src/Project/` — `ProjectSettings` (`project.mfproj`), `GameHost`/`GameSession`, `GameAssemblyLoader`
  (collectible code reload), `DebouncedFileWatcher`; `Src/EditorLink/` — game ↔ editor protocol, client and server
- `Templates/MainframeEngine.Templates/` — `dotnet new mfgame` template (not published; `build/template-smoke.sh`)
- `Examples/Demo/` — the Demo game (a `GameHost` project of its own: `Demo`, `Demo.Desktop`, `Demo.Tests`): one scene per feature, nav bar; README screenshots (`just demo-screenshots`). Standalone, not part of `MainframeEngine.slnx` (`Examples/Demo/Demo.slnx`); see `docs/design/demo.md`
- `MainframeEngine.Editor/` — the editor exe (M10; `just editor [project|scene]`): `EditorApp : Engine` runs the tree in
  `SceneTree.EditMode` (only `[Tool]` nodes process) with an `EditorWorkspace` node; RmlUi panels in `Content/Editor/`;
  `Session/` (tabs, one `SubViewport` world per scene), `Undo/`, `Inspector/`, `SceneTree/`, `Viewport/` (camera,
  3D/2D gizmos, picking), `Projects/` (`ProjectService`: game assembly in a collectible ALC, code reload; New Project,
  Project Settings), `Play/` (`dotnet build` + game processes over the editor link), `FileSystem/` (file tree, moves
  with UID reference fix-ups, OS trash), `Settings/` (editor settings, code editor), `Updates/` (self-update from GitHub Releases: check, download, `--apply-update` swap), `UI/` (panels, dialogs), `Qa/`
  (`--qa-script`, `--smoke`). The RmlUi debugger is F9 (F5–F8 are Play keys). Code reload re-creates scenes that use
  game code: editor code must not keep game nodes/types in fields or statics past `ReleaseEditorReferences`.
  References engine core only (no generator: editor nodes stay unregistered). See `docs/design/editor.md`
- `Tools/MainframeEngine.L10n/` — `mf-l10n` localization tool (extract, update, pseudo, `.po` → `.mo`)
- `Plugins/Spine/` — Spine C# runtime (vendored, do not modify)
- `Tests/` — shared test assets (`Tests/Content`: showcase fixture scene, glTF/Spine/sky/audio, render-test catalogs), unit tests (engine, editor), render tests (+ host, editor smoke; goldens per driver in
  `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}`), benchmarks (`baseline.json`), QA scripts
  (`Tests/QA`); see `docs/design/testing.md`
- `Native/` — in-house native shims and their sources (`mfrmlui` over RmlUi + FreeType, ENet), built per platform by
  `.github/workflows/natives.yml`; the binaries are committed under `MainframeEngine/runtimes/<rid>/native`
  (`Native/natives.lock`, `docs/design/natives.md`)
- `build/` — scripts shared by `justfile` and CI: `shaders.sh` (+ `Shaders.targets`), `Localization.targets`,
  `template-smoke.sh`, `package-editor.sh` / `next-version.sh` (release), `linux/` (the Docker Linux test
  environment), `macos/` (the editor's dev `.app` bundle), `editor-icons/`, `brand/`
- `.github/workflows/` — `ci.yml` (build-test on 3 OSes, format, shaders, lavapipe render tests, template smoke;
  `ci-success` is the required check), `publish.yml` (releases), `natives.yml` (native shims)
- `docs/images/brand/` — the logo (SVG sources, PNG/ICO/ICNS; `just brand` regenerates); app copies in `MainframeEngine/Content/Brand/`
- `docs/` — design docs (`docs/design/`, one topic per file) and the roadmap (`docs/milestones.md`); update the matching doc when changing a subsystem

## Key Patterns

### Extending the Engine

**Game projects (M10):** no `Engine` subclass. `dotnet new mfgame -n MyGame --engine-path <checkout>` (template in
`Templates/`, `just template-smoke`) creates `MyGame` (node library + generator), `MyGame.Desktop`
(`return GameHost.Run(args, typeof(SomeNode).Assembly);`) and `project.mfproj` (`ProjectSettings`: main scene, window,
physics, input map, audio, localization, rendering, Steam, autoloads; versioned JSON with migrations). Steam demo
builds: `isDemo` in `project.mfproj` (or `-p:MainframeDemo=true`) defines `DEMO` for game code (`#if DEMO`) through
`build/MainframeGame.props`, which the template's `Directory.Build.props` imports; `GameHost.IsDemo` at run time. `GameHost` flags:
`--scene`, `--editor-port`, `--max-frames`, `--hidden`, `--fixed-fps`, `--headless` (`HeadlessHost`: dedicated servers, no window/Vulkan/audio device). Input actions: `Input.IsActionPressed("jump")`
/ `SceneTree.Input`. Logs are `LogEntry`s routed to `ILogSink`s (`Log.AddSink`; console, `FileLogSink`,
`MemoryLogSink`); filtered `Log.X($"...")` costs nothing. Editor-facing: `EditorLinkServer`/`EditorLinkClient`,
`GameAssemblyLoader` (collectible, GC-verified unload — never keep game objects in locals of the unloading method).
See `docs/design/project-and-gamehost.md`.

**Engine subclass (the editor, tests):** subclass `Engine` (not an interface) and put nodes in the engine-owned `SceneTree` (Godot model);
the tree processes and renders them. Behaviour is node subclasses (`OnReady`, `OnProcess`,
`OnPhysicsProcess`, `OnInput`, …), not code in the `Engine` subclass:

```csharp
public sealed class Game(in EngineOptions options) : Engine(options)
{
    protected override void OnLoad()
    {
        base.OnLoad();
        Tree.ChangeSceneToFile("Content/Scenes/Main.mscene"); // or Root.AddChild(nodeBuiltInCode)
    }
}
```

The legacy hooks `OnUpdate`, `OnShadowPass`, `OnRenderMainPass` are optional virtuals. Call
`base.OnLoad()` at the start of any `OnLoad` override. Call `base.OnClose()` at the end of any `OnClose`
override (frees the scene tree, disposes servers, then renderer, input; keeps the exit code set by
`Quit`). `Quit(code)` closes the window after the current frame — prefer it to `Window.Close()`.

`EngineOptions.EnableValidation` defaults to on in Debug builds and off in Release; set it to override.

### Nodes, servers and scenes

There is no `Node.Initialize`: nodes reach engine servers through `Tree.Servers` (`RenderServer` is
registered in `base.OnLoad()`). Visual nodes (`VisualInstance3D`: `MeshInstance3D`, `Sprite3D`, `SpineNode`, `Grid3D`)
create their GPU objects through the render server when they enter the tree and release them when freed;
lights (`DirectionalLight3D`, `OmniLight3D`, `SpotLight3D`), cameras (`Camera3D`) and the sky
(`WorldEnvironment` + `Sky`) are nodes too. Node constructors must stay cheap and side-effect free (the
type registry instantiates every serialized type; scenes are instantiated outside the tree).
`RenderServer.ShadowsEnabled = false` (before visuals initialize) runs without a `ShadowSystem`: lit
pipelines then bind the "no shadows" fallback set.

Serialized members are `[Export]` (public/internal, read-write) and signals are `[Signal]` C# events; any
project declaring node/resource types references `MainframeEngine.Generators` as an analyzer
(`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`). Scenes are `.mscene` JSON
(`SceneSaver.Save`, `ResourceLoader.Load<PackedScene>`, `Instantiate()`); see
`docs/design/scene-serialization.md`. Regenerate the Demo scenes with
`dotnet run --project Examples/Demo/Demo.Desktop -- --write-scenes Examples/Demo/Content/Scenes`.
`MainframeEngine.Timer` (the Godot node) shadows `System.Threading.Timer` inside `MainframeEngine.*`
namespaces — qualify the latter.

### Physics

`PhysicsServer3D` (Jitter2 2.9.0) and `PhysicsServer2D` (Box2D.NET 3.1.654, nodes in pixels,
`EngineOptions.Physics2D.PixelsPerMeter` = 100) are registered in `base.OnLoad()` and step on the tree's fixed tick.
Bodies are nodes with `CollisionShape3D/2D` children holding `Shape3D/2D` resources; move characters with
`CharacterBody*.MoveAndSlide()` from `OnPhysicsProcess`; query through `GetWorld3D().DirectSpaceState`. Moving bodies'
transforms are interpolated render poses outside `OnPhysicsProcess`. Signals fire after the step on the main thread.
Keep every Jitter2/Box2D call inside `Src/Physics` (pinned versions; APIs change between minors). Box2D.NET allocates
inside its own step; engine code must not. See `docs/design/physics.md`.

### Frame Order

1. `OnUpdate` — game logic (legacy hook)
2. `Tree.Tick` — fixed-step `OnPhysicsProcess` + physics step (60 Hz, ≤5 steps, 0.25 s clamp), physics interpolation, `OnProcess`, deferred calls
   and `QueueFree`, transform sync (`OnTransformChanged`), frame servers (`UiServer`: RmlUi update + render
   into its command list)
3. `RenderServer.PrepareFrame(Root)` — cull/sort meshes, create/update their GPU resources, decide the post state (the
   root world's `PostProcessSettings`, whether the depth prepass runs, the TAA projection jitter); then `Renderer.BeginFrame`
4. `OnShadowPass` (no render pass active), then `RenderServer.RenderShadows(Root)` — the tree's casters — and
   `RenderServer.RenderOffscreen(Root)` (sub-viewports, object-ID picking)
5. `RenderServer.RenderPrepass(Root)` (ADR 0163) — starts the post effects (`OnBeginFrame`); when an enabled effect needs
   it (SSAO, TAA, the velocity view) or `ForceDepthPrepass`: the depth prepass (opaque + cutout into the scene depth and an
   RG16F velocity buffer, then the sky's velocity) and the `AfterPrepass` stage (SSAO → set 0 binding 5)
6. `RenderServer.RenderMain(Root)` — writes the shared set 0 (`IVulkanContext.Frame.Begin(camera, lights)`),
   then sky, then the tree's visuals; then `OnRenderMainPass` for hand-drawn geometry
   (`node.Draw(camera, lights)`). All of it renders into the HDR scene target (linear colour); after a prepass the scene
   pass loads its depth and prepassed surfaces test it (LESS_OR_EQUAL, cutouts EQUAL without `discard`)
7. `IVulkanContext.BeginOverlayPass` (or `EndFrame`) — UI layers render offscreen, the `BeforeTonemap` stage (TAA, auto
   exposure, glow, light shafts), the tonemap, the `AfterTonemap` stage (FXAA; the last effect draws into the swapchain),
   then the overlay pass draws in `OverlayOrder`: the 2D canvas, the screen gizmos (`RenderServer.ScreenGizmos`), the UI
   layers (sRGB, exact) and, on top, the dev overlay (an RmlUi layer, `DevOverlayVisible`, F12; panels via
   `DevOverlay.AddPanel`). Post effects: `docs/design/post-processing.md`

Colours authored by people (lights, shapes, sky, clear colour) are sRGB; the engine converts them to linear.
Exposure: `IVulkanContext.Exposure`. See `docs/design/color-pipeline.md`.

Shadow pass and main pass only run when `IVulkanContext.FrameStarted` (a frame can be skipped while the
swapchain is rebuilt; while minimised the engine renders nothing and blocks on window events).

### Per-frame resources

Key per-frame GPU resources (UBOs, dynamic vertex buffers, their descriptor sets) by
`IVulkanContext.FrameSlot` and size them `IVulkanContext.MaxFramesInFlight` — never by swapchain image
(`SwapchainImageCount`/`CurrentImageIndex` are driver-chosen and change on recreation).

GPU memory comes from `IVulkanContext.Allocator` through `GpuBuffer`/`GpuImage`/`GpuTexture`; uploads go
through `IVulkanContext.Uploads` (recorded at frame start — never `vkQueueWaitIdle`), and `Dispose`
hands objects to `IVulkanContext.Deletions` (never `vkDeviceWaitIdle`). Create pipelines through
`IVulkanContext.Pipelines` and shader modules through `IVulkanContext.Shaders`
(`docs/design/gpu-resources.md`).

### Game UI (RmlUi, M8)

`UiLayer` nodes (one RmlUi context each, ordered by `Layer`) hold `UiDocument` nodes (`Source = "Content/UI/x.rml"`).
Create data models in `OnReady` (`CreateDataModel("hud").Bind("hp", this, static d => d.Hp)`) — documents load lazily
afterwards — and call `Model.Dirty(name)` when values change. `GetElementById(id)!.Click += ...` for element events.
Input reaches the UI before `OnInput` (`IInputServer`); a consumed event never reaches nodes. Author sizes in `dp`;
link `/Content/UI/widgets/widgets.rcss` for the widget library; HUD bodies use `class="hud"` (`pointer-events: none`).
RmlUi is process-global and single-threaded (`UiServer` owns it; tests use the `SerialRmlUi` collection). F8 toggles the
RmlUi debugger; Debug builds hot-reload `.rml`/`.rcss` from the project's `Content/`. See `docs/design/game-ui.md`.

### Shadow Pass

`ShadowSystem` (M4): cascades for the first shadowed directional light, one atlas for spot and other directional lights, cubes for point lights; per-light `CastsShadows`/`ShadowResolution` on the light nodes. `RenderShadows(lights, camera, casterBounds, state, cull, draw)` plans the passes on the CPU (`ShadowPlanner`), calls `cull` for every pass (return false to skip it), then `draw` per rendered pass; the render server does this for the tree. Call it at most once per frame; each sub-pass gets its own light matrix from a per-frame-slot dynamic-offset ring. Use static lambdas with explicit state (no closure allocations). Tree-less code can use the `draw2D`/`drawPoint` overloads (no culling).

### SpineNode

Use `SpineNode` (not `SpineRenderer` directly): set `Folder` (and optionally `Animation`) and add it to the
tree — it advances in `OnProcess` and the render server draws it and its shadows. Tree-less code uses
`new SpineNode(renderer, folder)` + `Advance(gameTime)` + `Draw(camera, lights)`. `SpineScale` applies
immediately; `SetAnimation` replaces track 0 (`QueueAnimation` appends).

### Localization

Player-facing strings go through `Tr` (`Tr._("…")`, `Tr.P(ctx, "…")`, `Tr.N(singular, plural, n)`) with string
literals at the call site (the extractor only sees literals). Scene text uses `[Export(Translatable = true)]` and is
shown with `Atr(...)` in `OnReady` and `OnLocaleChanged`. After changing strings: `just l10n-extract`, translate the
`.po`, `just l10n-compile` (commit the `.po` and `.mo`). See `docs/design/localization.md`.

## Rendering Backend

Vulkan 1.2 only for now. `IVulkanContext` must be cast from `IRenderer` to access Vulkan-specific operations (begin render pass, shadow system). Always guard casts:

```csharp
if (Renderer is IVulkanContext vk) { ... }
```

## Unsafe Code

The engine uses `unsafe` for Vulkan buffer/matrix operations. This is expected — do not remove `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` from the csproj.

## Dependencies

- Silk.NET — windowing + input (SDL2 backend: `Silk.NET.Windowing.Sdl`, `Silk.NET.Input.Sdl`), Vulkan bindings, Assimp
- RmlUi 6.3 + FreeType — game UI, through the in-house `mfrmlui` native shim (`Native/RmlUi`, binaries in
  `MainframeEngine/runtimes/`)
- Steamworks.NET — Steam platform (optional; only activate if Steam is running)
- StbImageSharp — texture/image loading
- SoundFlow 1.4.1 (audio device/mixer, miniaudio natives in the package) and NVorbis 0.10.5 (OGG) — audio (M7)
- Jitter2 (3D) and Box2D.NET (2D) — physics (pinned exactly)

Do not add NuGet packages without discussing the dependency first. Versions are central in
`Directory.Packages.props`.

## Branching & release

- `main` is PR-only: the `ci-success` check must pass, history stays linear (squash or rebase merges — never
  merge commits), no force pushes. Work on a branch and open a PR.
- Releases: `publish.yml` (manual, `main` only, actor `brogan89`) tags `vX.Y.Z` (patch bump) and attaches
  self-contained editor builds. Never bump versions in files. See `docs/design/release.md`.

## Project memory

@import memory/README.md
@import memory/context/current-state.md
@import memory/context/progress-log.md

# Testing

## Purpose

How the engine is tested: fast unit tests, headless Vulkan render tests with golden images and two
gates (validation, allocation), and benchmarks with a stored baseline. Every recipe below is in the
[`justfile`](../../justfile); CI runs the same commands (see [Build & platforms](build-and-platforms.md#ci)).

## Projects

| Project | Kind | Runs on |
|---|---|---|
| [`Tests/MainframeEngine.Tests`](../../Tests/MainframeEngine.Tests/) | xUnit v3 unit tests, no window/GPU | every OS in CI (`just test`) |
| [`Tests/MainframeEngine.RenderTests`](../../Tests/MainframeEngine.RenderTests/) | xUnit v3 render tests (also the editor's `--smoke` run) | lavapipe in CI and in Docker (`just render-tests-linux`), MoltenVK locally (`just test-render`) |
| [`Tests/MainframeEngine.RenderTests.Host`](../../Tests/MainframeEngine.RenderTests.Host/) | Console app that runs one scene | launched by the render tests |
| [`Tests/MainframeEngine.Editor.Tests`](../../Tests/MainframeEngine.Editor.Tests/) | xUnit v3 editor tests: models and the whole editor UI headless; projects, Play (fake builder/launcher), FileSystem, code reload (Roslyn-compiled game assemblies); one `Category=Slow` test runs the real `dotnet new mfgame` + build | every OS in CI (`just test`) |
| [`Tests/QA`](../../Tests/QA/) | Editor QA scripts (`--qa-script`) | locally (`just qa-editor`, `just qa-projects`), see [Editor](editor.md#testing-and-qa) |
| [`Tests/MainframeEngine.Benchmarks`](../../Tests/MainframeEngine.Benchmarks/) | BenchmarkDotNet, `MemoryDiagnoser` | locally (`just bench`) |

The test projects run through VSTest (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`, with
`UseMicrosoftTestingPlatformRunner=false`) so `coverlet.collector` works; `just coverage` writes
Cobertura XML to `artifacts/coverage`. The engine exposes internals to the test assemblies with
`InternalsVisibleTo`.

Unit tests cover pure logic: `GameTime`/`FPSCounter`, `EngineOptions` defaults (validation on in
Debug, off in Release, `ContentScale` 0), the fixed-content-scale size math (`WindowPixelsTests`: layout points ×
scale → pixels, pixels ÷ display scale → OS window points; `UiServerTests`: a fixed `ContentScale` is the dp ratio of
`Dpi` layers while input keeps the display's pixels per point), the macOS Vulkan loader probe order, the shadow light-VP ring offsets and pass
indices, `ChooseUp`, the shadow-fallback matrix, `SpineNode` (scale setter, `SetAnimation` replace vs
`QueueAnimation`, Spine's update order, vertex growth past the initial capacity, atlas pixel release —
SpineBoy is linked into the test output and a non-Vulkan `IRenderer` skips GPU work), `NetBuffer` round trips
and the pool, `PeerId`/`NodeId`, `Log` filtering, camera matrices against
[Coordinate conventions](coordinate-conventions.md), the 1200-byte std140 lights UBO (linear colours), the
PNG codec, `VulkanValidationLog`, the M3 meshes/materials suites
([Materials & meshes](materials-and-meshes.md#testing): generators, bounds/culling, pipeline keys and cache, draw
sorting, mesh/material/texture serialization, `.meta` import settings, the Assimp glTF import), and since M3: the GPU allocator through a fake device (`FreeListBlock`
alignment, granularity, best fit, fragmentation/coalescing, randomised invariants; memory-type choice,
dedicated path, block reuse and release, exhaustion fallback, stats), the staging ring (wrap-around,
release), the deletion queue (frame ordering, zero allocation), `ContentPaths`, the generated shader
limits against `limits.json`, the pipeline-cache header/file name/directory rules, `FrameData` (std140
size, near/far recovery), sRGB curves, the ACES tonemap, the swapchain-format choice and `SilkNativeResolver` (portable `runtimes/<rid>/native` probing). Tests that touch process-wide state (`NetBufferPool`, `Console`, `Log.LogLevel`)
share a non-parallel collection.

The scene system (M2) has its own suites under [`Scene/`](../../Tests/MainframeEngine.Tests/Scene/), with
test node and resource types in `TestNodes.cs` (registered by the source generator like a game's):

| Suite | Covers |
|---|---|
| `NodeTreeTests` | children/order, unique and sanitized names, the name index, owners, reparenting, `NodePath` (relative, absolute, `%unique`), `GetPathTo`, wildcards, freeing |
| `SceneTreeTests` | enter/ready/exit order, ready-once, built-in signals, children added during callbacks, priority/tree order, `SetProcess`, the fixed-step accumulator (clamp, substep cap, backlog drop), pause modes, `QueueFree` during iteration, deferred call order, groups, `NodeId` registry, `ChangeScene`, timers, input routing, shutdown |
| `SignalTests` | generated signal infos, connect/disconnect by name, `[SignalHandler]`, one-shot, deferred, auto-disconnect on free |
| `TransformTests` | Euler ↔ quaternion vs the legacy `Rx·Ry·Rz` matrices, model matrix, dirty propagation, global setters, `Reparent` keep/drop global, `LookAt`, notification batching, `Transform3D`/`Transform2D` math |
| `WorldTests` | visual registration order, light nodes ↔ `LightEnvironment`, active camera selection, camera sync vs the legacy camera, `WorldEnvironment` ambient, render-server resource tracking (non-Vulkan renderer), server ticking |
| `SerializationTests` | every property type round-trips, non-default-only output, LF-only files on every OS, encodings, owned-only saving, outside-the-tree instancing, groups/unique names, persisted connections and flags, nested instances (overrides, added children, sub-scene edits flowing through), self-instancing rejection, external resources and the cache, ref counting, UID stability across re-save and file moves, asset database scan/meta/index, migrations, `MissingNode`, error reporting, the Sandbox scene re-saving byte-identically |
| `GeneratorTests` | `CSharpGeneratorDriver` over game-like sources: emitted registration compiles, loads in a collectible context and works (properties, groups, hints, signals, migrations, abstract/tool/TypeName), every diagnostic, incremental caching; the engine's own registrations |
| `SceneTreeAllocationTests` | **0 bytes** over 120 steady-state ticks of a 10 002-node tree (physics, process, transform propagation and notifications, input, groups) and over `GetNode` lookups |

The networking suites live under [`Networking/`](../../Tests/MainframeEngine.Tests/Networking/), with networked test
node types in `ReplicationTestNodes.cs` and `NetHarness` (a server and clients, each with its own `SceneTree` and
`MultiplayerApi`, over `LoopbackTransport`, optionally degraded by a seeded `SimulatedTransport`, stepped at a fixed
60 Hz on a manual clock):

| Suite | Covers |
|---|---|
| `ReplicationTests` | handshake and peer ids, spawn with initial state, networked descendants, every codec type, delta size (only the changed member), derived types, spawn under networked/path parents, nested spawn and despawn cascade, spawn+free in one frame, freeing a descendant, late join (full state, absent descendants, authority), authority replication, locally freed nodes not stalling acks, bandwidth stats, tick rate |
| `RpcTests` | authority client → server with `RemoteSender`, local refusal and forged calls rejected by the server, server-only RPCs, targeted calls, unreliable `AnyPeer`, `CallLocal`, offline calls, derived wire indices, RPC after a same-frame spawn, unknown ids and bad indices |
| `InterpolationTests` | buffer blending, hold samples, extrapolation clamp, capacity, every blend type (quaternion extrapolation), client motion matching the server's samples at `RenderTick`, extrapolation stopping when snapshots stop, still members not drifting, interpolation off |
| `ConnectionLifecycleTests` | owned nodes despawned (or handed back) when a client leaves, server shutdown, kicks, registry and replication fingerprint mismatches, handshake timeouts (both sides), silent peers, misuse |
| `TransportTests` | multi-client loopback, `SimulatedTransport` latency, seeded loss/duplication, jitter reordering (reliable stays ordered), replication converging under 10–30 % loss with jitter and duplication |
| `TransportHandoffTests` | `NetworkAddress` parsing/formatting, connect lists, selector fallback, a lobby connect string connecting a `MultiplayerApi` over loopback |
| `ReplicationGeneratorTests` | emitted state/dispatch/senders compile, load and round-trip in a collectible context, RPC parameter names that clash with generated locals, every MFG007–MFG009 diagnostic, the handshake fingerprint ignoring unrelated registered types |
| `NestedSceneReplicationTests` | scenes registered by path and UID from `.mscene` files, nested scene instances replicating |
| `EnetReplicationTests` | real ENet: a server and two clients in-process (spawn, authority RPCs, server RPCs, leave, shutdown) |
| `ReplicationRobustnessTests` | review regressions: a throwing client RPC is contained (and optionally kicks), despawn handlers freeing other nodes, snapshot byte budgets (spread and converge, also under loss), stalled acknowledgements disconnect, malformed spawns and snapshot lengths, connect-string fallback after a failed attempt, misuse (tick rate while running, unknown authority, kicked peers stop receiving), messages from non-server peers ignored |
| `ReplicationAllocationTests` | **0 bytes** over 240 frames of a server and a client with 100 interpolated boxes moving and RPCs both ways |

Audio (M7) suites live under [`Audio/`](../../Tests/MainframeEngine.Tests/Audio/). They run the real SoundFlow mixer
graph on the **manual null device** (`AudioDeviceMode.NullManual`): `RenderNullDevice(frames, capture)` renders on the
test thread into a buffer, so levels, panning, ramps, pitch and timing are asserted on output samples. Test sounds
(`Content/Audio/`, stored in LFS) are generated: a 440 Hz mono 16-bit WAV, and a 440/660 Hz stereo tone as OGG, MP3
and FLAC; WAV layouts are written by the tests themselves.

| Suite | Covers |
|---|---|
| `AudioMathTests` | dB conversions, every attenuation curve against its formula (monotonic, max-distance cut, rolloff, custom curve, NaN safety), listener-space projection (orientation, elevation, scale), the pan law, doppler, 2D attenuation |
| `SpscRingTests` | the lock-free ring: FIFO across wrap-around, batch publishing, slot references released, a two-thread 2-million-item ordering stress |
| `AudioDecoderTests` | WAV 8/16/24/32-bit PCM, float 32/64, `EXTENSIBLE`, extra chunks; 5.1 downmix; OGG (NVorbis), MP3 and FLAC (miniaudio) content checks; sample-accurate decoder seeks; memory/stream/auto load modes, shared clips, error reporting; `.meta` import settings |
| `AudioServerTests` | default layout, real-time null device, broken layout fallback, unity gain through the bus chain, voice × bus × master volumes, mute and solo (incl. nested solo), one-shot finish, click-free stop, play + stop in one batch, stealing (priority → quietest → oldest, rejection), node polyphony, `Finished` only on natural ends, autoplay/exit, pause vs `ProcessMode` (`Pausable`, `Always`, `WhenPaused`, `Disabled`, `StreamPaused`), resampling and pitch, seek/position, 3D panning/attenuation/zipper bound/listener rotation, camera as listener, distance low-pass, doppler, streamed loop continuity and finish, streamed vs memory sample equality, bus layout save/load/apply with effects, runtime layout swap, unknown bus / broken stream |
| `AudioSerializationTests` | every audio node property and a shared inline `AudioStream` round-trip through `.mscene` byte-identically; defaults write nothing; scene audio plays in a tree; the Sandbox scene's ambience plays |
| `AudioRegressionTests` | review regressions: the engine reverb (tail, decay, 0 B), a bus with every effect mixing at 0 B, a seek racing a stream's natural end, stream seek position, a stream whose file vanishes (error, no `Finished`), stream generations never read into the next one, NaN pitch/velocity sanitizing, listener switches not causing doppler, whole-or-nothing command batches, unsent graph swaps disposed |
| `AudioAllocationTests` | **0 bytes** over 300 frames of tree tick + `AudioServer.Process` + rendering on the same thread, with 8 moving 3D voices (every attenuation model, low-pass, doppler), a streamed music loop, a 2D voice, UI one-shots finishing and restarting, `PlayOneShot`, pause toggles and bus fader changes |

Physics (M6) has its suites under [`Physics/`](../../Tests/MainframeEngine.Tests/Physics/), on headless trees with
the physics servers (`PhysicsHarness3D`/`PhysicsHarness2D`; 3D single-threaded). Box2D keeps worlds in a process-wide
table, so the 2D suites share the non-parallel `SerialBox2D` collection.

| Suite | Covers |
|---|---|
| `Physics3DTests` | free fall, rest and sleep, static moves, layer/mask OR semantics (incl. runtime changes on resting bodies), gravity scale, axis locks, contact monitor, areas (static/character bodies, mask, `Monitoring`), removal and `QueueFree` during signals, dispatch order, main-thread dispatch, lifecycle, shape rebuilds, kinematic pushing, server disposal, default server registration |
| `Physics3DQueryTests` | raycasts (closest, exclude, mask, misses, moved bodies, areas ignored), shape casts, overlaps, concave/height-map shapes, every shape with scale, `MoveAndSlide` (floor, walls, 30° slope, steep slope, ceiling, snap down a step, recovery, pushing crates), interpolation (exact lerp, physics pose in `OnPhysicsProcess`, teleport, pause) |
| `Physics2DTests` | the same behaviours on Box2D in pixels |
| `PhysicsSerializationTests` | every 3D/2D body and shape round-trips through `.mscene` (shared resources stay shared), a loaded 2D scene simulates, every type is registered |
| `PhysicsAllocationTests`, `Physics2DAllocationTests` | **0 bytes** per steady-state frame with ~500 bodies, monitors, areas, characters, debug draw and every query (3D single- and multi-threaded); 2D: 0 bytes beyond Box2D.NET's own step allocations |
| `DebugDrawTests`, `FixedStepHookTests` | `DebugLines` primitives and cap, server debug draw; `BeforeFixedSteps`/`AfterFixedSteps` order and pause |

Projects and tooling (M10, engine side) have suites under [`Project/`](../../Tests/MainframeEngine.Tests/Project/)
(project file and migrations, `GameHost` options and `GameSession`, the editor link over real sockets, collectible
game-assembly load → unload → **GC-verified collection** → reload with Roslyn-compiled game assemblies), plus
`LogRoutingTests`/`LogSinkTests` (0 B when filtered) and `InputMapTests` (0 B polling); details in
[Projects & GameHost → Testing](project-and-gamehost.md#testing). The `mfgame` template is built and run by CI's
`template` job (`just template-smoke`).

The game UI (M8) has its own suites under [`UI/`](../../Tests/MainframeEngine.Tests/UI/), in the serial
`SerialRmlUi` collection because RmlUi is process-global: `RmlBindingTests` (the managed binding against the real
`mfrmlui`), `UiServerTests` (headless `UiServer` with `NullUiRenderer`: documents, layers, input routing, hot reload,
0 B per HUD frame) and `UiHelperTests` (input maps, reload batching, premultiplication, blur parameters, paths). See
[Game UI → testing](game-ui.md#testing).

## Render tests

```mermaid
sequenceDiagram
    participant T as RenderTests (xUnit)
    participant H as RenderTests.Host (child process)
    participant E as Engine + VulkanRenderer
    T->>H: dotnet Host.dll <scene> --out artifacts/render-tests/<scene> --capture 1,60 --hidden
    H->>E: Run() with MaxFrames, FixedDeltaTime = 1/60, EnableValidation, EnableFrameCapture, VSync off
    loop every frame
        E->>H: OnUpdate: CaptureFrame() on listed frames; allocation counter at window edges
        E-->>H: OnFrameCaptured → <scene>_frameNNNN.png
    end
    H->>T: result.json (device, platform tag, validation counts + messages, allocated bytes, captures)
    T->>T: gates + golden comparison
```

- **Out of process.** Windowing must own the process main thread on macOS (Cocoa), and a driver crash
  then fails one test, not the run. The host is referenced by the test project, so it sits in the test
  output folder; the runner launches it with `DOTNET_HOST_PATH`.
- **Deterministic frames.** `EngineOptions.FixedDeltaTime` gives every update the same delta, so frame
  *N* always shows the same pose. `CapturesAreDeterministicAcrossRuns` checks two runs are bit-identical.
- **Scenes** (in the host, built as node trees that the engine's scene tree and render server draw):
  `lit-shapes` (procedural sky, grid, floor quad,
  two boxes, one shadow-casting directional light), `multi-light` (same geometry, directional + spot +
  point shadow casters; self-checks that every shadow sub-pass's ring slot holds its own matrix),
  `spine` (adds SpineBoy, content linked from the Sandbox), `spine-no-shadows` (Spine and shapes with
  no `ShadowSystem`: the fallback shadow set), `sandbox` (adds the Sandbox's ImGui windows — including
  `RendererDebugWindow` — gizmos, the audio bus mixer and the RmlUi HUD with bindings dirtied every frame, a
  streamed ambience and an orbiting doppler voice, and self-checks that both play, plus a stack of physics crates on
  single-threaded Jitter2; not used for goldens because it shows timings), `color-pipeline` (a solid-colour sRGB
  panorama fills the frame; ImGui rectangles; exposure changes on frame 8), `physics` (crates and a ball dropped on a
  floor and ramp; Jitter2's deterministic solver on one thread so frames reproduce), `physics-debug` (the same with
  collision-shape debug lines, drawn into the HDR scene target with sRGB-authored colours converted to linear),
  `sky-grid` (only the procedural sky and the grid, camera inside the grid so lines pass beside and behind it), since
  M3 `materials` (textured, cutout, normal-mapped, emissive, mirrored and blended primitives), `gltf` (the generated
  glTF test model imported through Assimp, plus a mirrored instance), `instances` (`--count` boxes, default 1 000,
  one mesh and material; self-checks the batch statistics) and `picking` (object-ID picks in the main view and a
  `SubViewport` shown through ImGui; self-checked), and the game-UI scenes (M8) `ui-hud` (HUD over lit-shapes),
  `ui-effects`, `ui-text`, `ui-widgets` (documents in the host's `Content/UI/` and the engine's widget demo;
  `--size` gives them larger windows), and the shadow scenes (M4) `csm`, `shadow-pcf`, `shadow-lights`,
  `shadow-cutout` and `shadow-shimmer` ([Shadow system → testing](shadow-system.md#testing)). The lit and physics scenes use `MeshInstance3D`s with primitive meshes since
  M3 (the physics crates share one `BoxMesh` and one material per colour). Every host scene runs audio on the silent
  null device.
- **Host hooks** (command line): `--size WxH` (layout points), `--scale S` (fixed content scale, see *Window* below),
  `--resize WxH@frame` (layout points, through `Engine.ResizeWindow`), `--toggle-vsync frame` (swapchain recreation
  mid-run), `--quit-error frame` (`Quit(ExitCode.Error)`; the host exits with `Run()`'s code and
  `result.json` records it), `--pipeline-cache dir`, `--count N` (scene size), `--perf warmup:frames` (wall-clock
  and CPU frame times: average and p95 in `result.json`, plus the shadow pass's CPU/GPU milliseconds), `--no-validation`,
  `--no-shadows` (every light's `CastsShadows` off). Scene self-check failures are reported in
  `SceneCheckFailures`; `result.json` also records the Vulkan device type (`DeviceType`), GPU allocator totals, shader-module count and the
  pipeline-cache bytes loaded. The runner points `MAINFRAME_PIPELINE_CACHE_DIR` at
  `artifacts/render-tests/pipeline-cache`.
- **Tests:** `LitShapesRenderCleanlyAndMatchGoldens`, `MultipleShadowCastingLightsEachUseTheirOwnMatrix`,
  `SpineRendersCleanlyAndMatchesGolden`, `SpineRendersWithoutAShadowSystem`,
  `SwapchainRecreationOnResizeAndVSyncToggleIsClean` (exact pixel sizes before and after the resize),
  `FixedContentScaleCapturesAtTheExactPixelSize` (`--scale 1` and `--scale 3` — 3 is no Mac or CI display's backing scale,
  so the window is always resized — at start-up and after a resize), `QuitWithErrorReturnsErrorExitCode`,
  `SandboxSteadyStateAllocatesNothing`, `CapturesAreDeterministicAcrossRuns`,
  `PipelineCacheIsPersistedAndReloaded` (cold run writes, warm run loads),
  `HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath` (scene pixels = sRGB decode × exposure → ACES →
  encode within ±2 at two exposures; ImGui colour exact and blended in sRGB space),
  `PhysicsSceneRendersCleanlyAndMatchesGoldens` (frames 30, 150), `PhysicsDebugDrawRendersCleanlyAndMatchesGolden`,
  `PhysicsCapturesAreDeterministicAcrossRuns`,
  `ProceduralSkyAndGridMatchTheReferenceMath` (`SkyGridReference` recomputes each pixel's sky colour and
  projects the grid lines on the CPU: every pixel > 2 px from a line must be the sky within ±3, and every
  line over the ground must be visibly drawn — driver-independent, so it runs on both drivers without
  goldens), `MaterialFeaturesRenderCleanlyAndMatchGolden`, `ImportedGltfModelRendersAndMatchesGolden`,
  `ThousandInstancesBatchIntoAFewDrawsAndMatchGolden`, `ObjectIdPickingAndSubViewportsWork`,
  `TenThousandInstancesAllocateNothingPerFrame` (0 B over 120 frames with 10 000 instances) and
  `TenThousandInstancesRenderAtSixtyFps` (validation off, Release builds: the CPU frame time —
  `Engine.LastFrameCpuMilliseconds`, update + draw lists + command recording without GPU/swapchain waits — must be
  < 16.7 ms on every device; the wall-clock frame time too, except on a `VK_PHYSICAL_DEVICE_TYPE_CPU` device such as
  lavapipe, whose software rasterizer takes ~50 ms per frame of this scene). The
  multi-light test also asserts sub-allocation (≤ 16 `VkDeviceMemory`). `UiRenderTests`:
  `HudOverTheSceneMatchesGoldenWithExactSrgbColours`, `ClipMasksTransformsFiltersAndGradientsMatchGolden`,
  `TextMatchesGolden`, `WidgetLibraryMatchesGolden`, `UiCapturesAreDeterministicAcrossRuns`,
  `UiSurvivesSwapchainRecreation`. `ShadowTests`: `CascadesCoverTheShadowDistanceAndMatchGoldens`,
  `PcfSoftensShadowEdges`, `EveryLightTypeCastsShadowsAtOnce`, `ShadowMapViewerIsValidationClean`,
  `ShadowsOfEveryLightTypeAllocateNothingPerFrame`, `CutoutMaterialsCastCutoutShadows`,
  `ShadowEdgesDoNotShimmerWhenTheCameraMoves`.
- **Window.** 320×240 layout points, hidden (`--hidden`); the swapchain still presents on MoltenVK and Xvfb. The host
  sets `EngineOptions.ContentScale` (`--scale`, default `HostOptions.CanonicalScale`: **2 on macOS, 1 elsewhere**), so
  captures are exactly the layout size × the scale in pixels — 640×480 for the `moltenvk` goldens, 320×240 for
  `lavapipe` — whatever the backing scale of the display the window lands on (a 1× external monitor or a 2× Retina
  panel), and RmlUi's dp ratio and ImGui's scale are that scale too, so UI goldens match as well. The engine sizes the
  OS window for the display (640×480 pt on a 1× monitor, 320×240 pt on Retina) before the swapchain exists and fails
  start-up if the framebuffer cannot reach the request; `result.json` records the scale (`ContentScale`).
  The editor's smoke runs (`EditorRenderTests`) pass the same canonical `--scale` to `MainframeEngine.Editor`: the
  editor window golden is 1280×720 points (2560×1440 `moltenvk`, 1280×720 `lavapipe`), the splash 960×600 points;
  the Project Manager and FileSystem panel goldens (`--smoke-golden project-manager|filesystem`) use the editor window
  size.
- **Comparing drivers at one resolution.** `--scale 1` on a Mac renders 320×240, the size of the lavapipe frames, so
  MoltenVK and lavapipe output can be diffed pixel for pixel.

### Frame capture

`Engine.CaptureFrame()` (requires `EngineOptions.EnableFrameCapture`) asks the renderer to copy the
frame being built. After the main pass, `VulkanRenderer` transitions the swapchain image
`PRESENT_SRC → TRANSFER_SRC`, copies it to a host-visible buffer, transitions it back, and after present
waits on that frame's fence and converts BGRA → RGBA with alpha forced to 255. `OnFrameCaptured`
receives a `FrameCapture` (`SavePng`). The swapchain only gets `TRANSFER_SRC` usage when capture is
enabled. The same API drives `--qa-capture` in the Sandbox (`just qa`).

PNG files are written and read by the in-house [`Png`](../../MainframeEngine/Src/Imaging/Png.cs) codec
(`ZLibStream`, adaptive filters; reads 8-bit RGB/RGBA non-interlaced).

### Golden images

Goldens live in `Tests/MainframeEngine.RenderTests/Goldens/<platform-tag>/<scene>_frameNNNN.png`. The
tag comes from the Vulkan driver ID: `lavapipe` (CI), `moltenvk` (local Macs), otherwise the driver
name. Every render test always runs its scene and enforces everything else it checks (validation, allocation,
self-checks, its own pixel assertions). Only the golden comparison depends on a golden existing for the current
tag: without one, `Gates.AssertMatchesGolden` skips just that comparison, reports an xUnit **warning** (CI also lists
the frame as a `No golden` annotation and in the job summary) and copies the frame to
`artifacts/render-tests/new-goldens/<tag>/`, ready to review and commit. The test itself passes or fails on its other
checks.

A pixel differs when any channel differs by more than **4**; a frame matches when at most **0.5 %** of
pixels differ. On mismatch the test writes `<name>.expected.png` and `<name>.diff.png` (differences in
red over a dimmed greyscale copy) next to the actual frame, and CI uploads the folder.

**Updating goldens:** run `just golden-update` (`UPDATE_GOLDENS=1`), look at every changed PNG, and
commit them with the change that caused them. PNGs are stored in Git LFS.

#### Recording goldens

`moltenvk`: `just golden-update` on a Mac. `lavapipe` (any change to rendered output needs both sets): locally with
Docker ([Linux tests in Docker](#linux-tests-in-docker)) — `git rm` the `lavapipe` goldens whose output changed on
purpose (a new scene has none), then from the repository root:

```sh
just render-tests-linux        # optionally: just render-tests-linux --filter "FullyQualifiedName~EditorRenderTests"
cp artifacts/linux/render-tests/new-goldens/lavapipe/*.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
just render-tests-linux        # every test passes and "Every compared frame has a golden."
```

Without Docker, use CI: on a branch (never `main` or `feature/*`), commit the removals and push, then

```sh
run=$(gh workflow run ci.yml --ref "$(git branch --show-current)" | grep -oE '[0-9]+$') # prints the run's URL
gh run watch "$run" --exit-status
rm -rf /tmp/rt && gh run download "$run" -n render-tests -D /tmp/rt
cp /tmp/rt/artifacts/render-tests/new-goldens/lavapipe/*.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
```

Then **look at every copied PNG** next to its `moltenvk` golden: the only allowed differences are the ones listed
below; anything else is an engine bug to fix, not a golden to commit. Commit the PNGs and run the render tests on lavapipe
twice more (Docker or CI): both runs must pass with no frame left without a golden (`CapturesAreDeterministicAcrossRuns` and the other determinism
tests cover run-to-run stability).

Expected `lavapipe` vs `moltenvk` differences (rasterizer, resolution and CPU, not bugs). Rendered at the same
resolution (`--scale 1` locally), the scenes with a grid differ in about 1.2 % of pixels (`shadow-lights`
1.5 %), all on grid lines; sky, floor, lit and shadowed surfaces match. `csm`, `shadow-pcf`, `shadow-cutout`,
`shadow-shimmer`, `gltf`, `instances` and `physics` frame 30 match to within ±4 on ≥ 99.99 % of pixels. The UI scenes
(`ui-*`), `picking` and `color-pipeline` lay out ImGui/RmlUi in points, so compare them at their own sizes (the `moltenvk`
set is at scale 2): layout, colours and effects must match, glyph rasterization differs.

- **Resolution.** Frames are 320×240 (scale 1), not the 640×480 of the `moltenvk` set (scale 2), so the distant grid
  aliases more.
- **Line rasterization.** Distant 1-pixel grid lines step differently (scattered single pixels).
- **Lines on pixel boundaries.** The camera looks straight down −Z, so the Y (yellow) and Z (blue) axis
  lines project exactly onto the boundary between two pixel columns. Lavapipe rasterizes such lines as
  nothing or as dashes; Metal fills one column.
- **Coplanar lines.** Grid lines on the floor quad z-fight, because line and triangle depths are
  interpolated differently. Lavapipe shows the receding ones dashed; MoltenVK hides them.
- **Texture LOD.** Lavapipe picks mip levels slightly differently, so the high-frequency normal map on the
  `materials` sphere shades differently on ~0.7 % of the pixels (the bumps look the same).
- **Physics after contact.** `physics` frame 150 and `physics-debug` (2–3 %): CI runs on x64/glibc, Macs on
  arm64/macOS, and the float results of the two (math library, SIMD code generation) differ in the last bits, which a
  stack of colliding crates amplifies into slightly different resting poses. Frame 30 (before contact) matches;
  each platform is deterministic run to run (`PhysicsCapturesAreDeterministicAcrossRuns`).

Before the grid clipped its lines in the vertex shader ([Scene grid](scene-grid.md#clipping-in-the-vertex-shader)),
lavapipe also dropped grid lines whose endpoints projected far off-screen and drew stray fragments that
covered the ground below the horizon with a grey haze; that was a real difference, not one of the above.

Lavapipe output changes with the Mesa/LLVM version in the runner image (recorded on Ubuntu 24.04:
`llvmpipe (LLVM 20.1.2, 256 bits)`, Mesa 25.2.8). If an image update breaks the goldens, re-record them as above
([Recording goldens](#recording-goldens)).

### Gates

| Gate | Rule | Where |
|---|---|---|
| Validation | 0 warnings and 0 errors from `VK_LAYER_KHRONOS_validation`, including teardown (leaks, in-use destruction) | every scene |
| Allocation | 0 managed bytes (`GC.GetAllocatedBytesForCurrentThread`) over 300 frames after 120 warm-up frames, host run with `DOTNET_TieredCompilation=0` | `sandbox` scene |

The debug messenger listens to WARNING and ERROR only, with a static `[UnmanagedCallersOnly]` callback,
and records into `IVulkanContext.Validation` (`VulkanValidationLog`: counters plus the first 64
messages). Without the layers installed the validation gate skips locally and fails on CI (`CI=true`).

The allocation gate's host runs without tiered compilation: tier-0 code of some generic BCL methods allocates where
the optimized code does not (the interpolated-string handlers' `AppendFormatted<T>` boxes each value until the
background JIT promotes it), and when that promotion lands depends on timing, so with tiering about one run in three
measured a few dozen frames of tier-0 ImGui text formatting. The gate checks the optimized code the steady state runs.

Per-frame code must not allocate: use static lambdas with state (`ShadowSystem.RenderShadows<TState>`),
cached arrays, and stack-formatted ImGui text (`Span<char>.TryWrite` + `ImGui.TextUnformatted`). Unit-test
allocation gates run in parallel with other test classes, so per-frame code must not rely on process-wide pools that
other threads share (`ArrayPool<T>.Shared` partitions): the in-process transports use a private `PacketPool` for that
reason (a loopback test once failed ~1 in 15 runs when another test thread drained the shared pool). The unit gates measure
through `AllocationGate.SmallestWindow`, which runs the steady-state window up to three times and keeps the smallest
count: with many test threads running, a window occasionally reported a few KB (always under one 8 KB allocation
quantum) that instrumentation placed in code that cannot allocate, and that never appears when the test runs alone. A
real per-frame allocation repeats in every window, so it still fails the gate.

Localization (M9) has its suites under [`Localization/`](../../Tests/MainframeEngine.Tests/Localization/) (collection
`LocalizationState`, since `Tr` is process-wide), with a fixture project (RML, scenes, a C# file) copied to the test
output: `TrTests` (catalogs, fallback chain, contexts, Polish/Russian plurals, format safety, interpolation, 0-byte
lookups), `RmlLocalizationTests`, `NodeLocalizationTests` (re-translation immediate/deferred/cross-thread),
`GettextFormatTests` (`.po`/`.mo`, byte equality with GNU `msgfmt` when installed — CI installs gettext on Linux),
`ExtractionTests` (runs the pinned `GetText.Extractor` local tool: `dotnet tool restore`) and `L10nCliTests`
(end-to-end `mf-l10n`); the game UI's translation (`UI/UiLocalizationTests`, in the `SerialRmlUi` collection, restoring
`Tr` through the fixture) includes a 0-byte gate over translated HUD frames. The render tests' `sandbox` allocation gate
runs in Spanish with the RmlUi HUD. See [Localization](localization.md#testing).

### Linux tests in Docker

`just test-linux` and `just render-tests-linux` run CI's Linux jobs locally ([`build/linux/run.sh`](../../build/linux/run.sh)):
an `ubuntu:24.04` container for **linux/amd64** ([`build/linux/Dockerfile`](../../build/linux/Dockerfile): the apt
packages of `ci.yml` — `mesa-vulkan-drivers`, `vulkan-validationlayers`, Xvfb, the X libraries, gettext — and the .NET
SDK from `global.json` via `dotnet-install.sh`), with CI's environment (`VK_DRIVER_FILES`/`VK_ICD_FILENAMES` = lavapipe,
`SDL_VIDEODRIVER=x11`, `SDL_AUDIODRIVER=dummy`, `CI=true`). x86_64 matters: the `lavapipe` goldens are CI's x86_64
llvmpipe output, and physics after contact differs between x64 and arm64 (see below).

- The script sends the working tree — tracked and untracked-but-not-ignored files (so new goldens count), the Spine
  C# sources, and `.git` without LFS objects (SourceLink stamps the commit) — into a cached volume
  (`mainframe-linux-work`; NuGet packages in `mainframe-linux-nuget`), builds the solution Release with
  `-warnaserror -p:CompileShaders=false`, and runs the suite: `unit` (engine tests, `mf-l10n check --msgfmt`, editor
  tests), `render` (`xvfb-run … dotnet test Tests/MainframeEngine.RenderTests`, extra arguments passed through) or
  `all` (`build/linux/run.sh all`).
- Results land in `artifacts/linux/`: `TestResults/*.trx` and `render-tests/` (failed comparisons' actual/expected/diff
  PNGs; frames without a golden in `render-tests/new-goldens/lavapipe/`, also listed at the end of the run).
- Docker Desktop on Apple Silicon runs the container under x86_64 emulation (Rosetta): the first run builds the image
  and restores packages; later runs rebuild incrementally. Docker needs about 8 GB of memory. Emulated test processes can stall
  with every thread idle (seen ~190 tests into the parallel unit suite, while tests start processes, SDL and sockets;
  never on native x64): the image sets `DOTNET_EnableWriteXorExecute=0`, which makes it rare, every suite runs with
  `--blame-hang-timeout 5m`, and a run aborted that way (not one with failed tests) is retried once.
- The image's Mesa must match CI's for the goldens to match: both are Ubuntu 24.04's `mesa-vulkan-drivers`
  (recorded with `llvmpipe (LLVM 20.1.2, 256 bits)`, Mesa 25.2.8). An image update that changes the output needs the
  goldens re-recorded.

## Benchmarks

`just bench` runs every benchmark (`NetBuffer` write/read, replication capture/encode/decode at 100 and 1000
nodes, camera and model matrices, lights UBO packing, the mesh draw list for 10k instances
(`MeshDrawListBenchmarks`: build + sort opaque/transparent, instance writes), the scene tree: `ProcessTick10k`,
`TransformPropagationDirtySubtree10k`, `GetNodePathLookup`, `SceneSaveLoadRoundTrip1k`; GPU allocator
alloc/free and churn; audio: `CommandBatchEnqueueAndDrain64`, `AttenuationCurvesAllModels`,
`SpatialProjection32Emitters`, `ServerFrame32PositionalVoices`, `MixBlock480Frames32Voices`; physics:
`Step1kRigidBodies3D`, `Step1kRigidBodies2D`, `Raycast10k3D`; the game UI: `Update500Idle`,
`Update500DirtyBindings`, `Relayout500`, `Render500Callbacks`; `LocalizationBenchmarks`: `Tr` hit/miss/context
lookups, formatting, `TranslateMarkup`, locale switch; `ShadowSetupBenchmarks`: `PlanSunCascades`, `PlanEveryLightType`,
`PackAtlasElevenTiles`) and compares with
[`baseline.json`](../../Tests/MainframeEngine.Benchmarks/baseline.json): a benchmark
fails when its mean is **more than 10 % slower** or it allocates more per operation. A run fails too when a benchmark produces no result
or nothing runs at all (a filter that matches nothing, a build failure), and `just bench-baseline` then writes
nothing. BenchmarkDotNet builds the benchmarks from the project file recorded at build time (`BenchmarkProjectFile`
assembly metadata) instead of searching the repository, whose search also finds the copies in git worktrees under
`.claude/worktrees/` and then builds nothing. Results also land
in `artifacts/bench`. Baselines are machine-specific (the file records machine and runtime), so
compare on the machine that recorded them; refresh with `just bench-baseline` when a change is
intentionally slower or a new benchmark is added, and commit the file.

## Related docs

[Build & platforms](build-and-platforms.md) · [Engine lifecycle](engine-lifecycle.md) ·
[Vulkan renderer](vulkan-renderer.md) · [Coordinate conventions](coordinate-conventions.md)

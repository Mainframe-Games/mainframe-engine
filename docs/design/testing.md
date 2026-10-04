# Testing

## Purpose

How the engine is tested: fast unit tests, headless Vulkan render tests with golden images and two
gates (validation, allocation), and benchmarks with a stored baseline. Every recipe below is in the
[`justfile`](../../justfile); CI runs the same commands (see [Build & platforms](build-and-platforms.md#ci)).

## Projects

| Project | Kind | Runs on |
|---|---|---|
| [`Tests/MainframeEngine.Tests`](../../Tests/MainframeEngine.Tests/) | xUnit v3 unit tests, no window/GPU | every OS in CI (`just test`) |
| [`Tests/MainframeEngine.RenderTests`](../../Tests/MainframeEngine.RenderTests/) | xUnit v3 render tests | lavapipe in CI, MoltenVK locally (`just test-render`) |
| [`Tests/MainframeEngine.RenderTests.Host`](../../Tests/MainframeEngine.RenderTests.Host/) | Console app that runs one scene | launched by the render tests |
| [`Tests/MainframeEngine.Benchmarks`](../../Tests/MainframeEngine.Benchmarks/) | BenchmarkDotNet, `MemoryDiagnoser` | locally (`just bench`) |

The test projects run through VSTest (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`, with
`UseMicrosoftTestingPlatformRunner=false`) so `coverlet.collector` works; `just coverage` writes
Cobertura XML to `artifacts/coverage`. The engine exposes internals to the test assemblies with
`InternalsVisibleTo`.

Unit tests cover pure logic: `GameTime`/`FPSCounter`, `EngineOptions` defaults (validation on in
Debug, off in Release), the macOS Vulkan loader probe order, the shadow light-VP ring offsets and pass
indices, `ChooseUp`, the shadow-fallback matrix, `SpineNode` (scale setter, `SetAnimation` replace vs
`QueueAnimation`, Spine's update order, vertex growth past the initial capacity, atlas pixel release —
SpineBoy is linked into the test output and a non-Vulkan `IRenderer` skips GPU work), `NetBuffer` round trips
and the pool, `PeerId`/`NodeId`, `Log` filtering, camera matrices against
[Coordinate conventions](coordinate-conventions.md), the 1200-byte std140 lights UBO (linear colours), the
PNG codec, `VulkanValidationLog`, and since M3: the GPU allocator through a fake device (`FreeListBlock`
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
  no `ShadowSystem`: the fallback set 2), `sandbox` (adds the Sandbox's ImGui windows — including
  `RendererDebugWindow` — gizmos and the audio bus mixer, a streamed ambience and an orbiting doppler voice, and
  self-checks that both play; not used for goldens because it shows timings), `color-pipeline` (a solid-colour
  sRGB panorama fills the frame; ImGui rectangles; exposure changes on frame 8). Every host scene runs audio on
  the silent null device.
- **Host hooks** (command line): `--resize WxH@frame`, `--toggle-vsync frame` (swapchain recreation
  mid-run), `--quit-error frame` (`Quit(ExitCode.Error)`; the host exits with `Run()`'s code and
  `result.json` records it), `--pipeline-cache dir`. Scene self-check failures are reported in
  `SceneCheckFailures`; `result.json` also records GPU allocator totals, shader-module count and the
  pipeline-cache bytes loaded. The runner points `MAINFRAME_PIPELINE_CACHE_DIR` at
  `artifacts/render-tests/pipeline-cache`.
- **Tests:** `LitShapesRenderCleanlyAndMatchGoldens`, `MultipleShadowCastingLightsEachUseTheirOwnMatrix`,
  `SpineRendersCleanlyAndMatchesGolden`, `SpineRendersWithoutAShadowSystem`,
  `SwapchainRecreationOnResizeAndVSyncToggleIsClean`, `QuitWithErrorReturnsErrorExitCode`,
  `SandboxSteadyStateAllocatesNothing`, `CapturesAreDeterministicAcrossRuns`,
  `PipelineCacheIsPersistedAndReloaded` (cold run writes, warm run loads),
  `HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath` (scene pixels = sRGB decode × exposure → ACES →
  encode within ±2 at two exposures; ImGui colour exact and blended in sRGB space). The multi-light test
  also asserts sub-allocation (≤ 16 `VkDeviceMemory`).
- **Window.** 320×240, hidden (`--hidden`); the swapchain still presents on MoltenVK and Xvfb. On a
  Retina Mac the framebuffer, and so the capture, is 640×480.

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
name. A golden is **only enforced when one exists for the current tag**; otherwise the test skips and
leaves the frame in `artifacts/render-tests/<scene>/`.

A pixel differs when any channel differs by more than **4**; a frame matches when at most **0.5 %** of
pixels differ. On mismatch the test writes `<name>.expected.png` and `<name>.diff.png` (differences in
red over a dimmed greyscale copy) next to the actual frame, and CI uploads the folder.

**Updating goldens:** run `just golden-update` (`UPDATE_GOLDENS=1`), look at every changed PNG, and
commit them with the change that caused them. PNGs are stored in Git LFS.

**Updating the `lavapipe` goldens** (CI only; any change to rendered output needs both sets):

1. Delete the stale files from `Tests/MainframeEngine.RenderTests/Goldens/lavapipe/` (a missing golden
   skips instead of failing, so the run records fresh frames), commit, push the branch and run CI on
   it: `gh workflow run ci.yml --ref <branch>`, then `gh run watch <run-id> --exit-status`.
2. Download the frames: `gh run download <run-id> -n render-tests -D /tmp/rt`. The golden-backed
   frames are `lit-shapes/lit-shapes_frame0001.png`, `lit-shapes/lit-shapes_frame0060.png`,
   `multi-light/multi-light_frame0030.png`, `spine/spine_frame0060.png` and
   `spine-no-shadows/spine-no-shadows_frame0060.png` under `/tmp/rt/artifacts/render-tests/`.
3. Look at every PNG next to its `moltenvk` golden. Check `result.json` too: `PlatformTag` must be
   `lavapipe` and the validation counts 0.
4. Copy the frames into `Goldens/lavapipe/`, commit, and re-run CI. It passes only if the frames match
   (`CapturesAreDeterministicAcrossRuns` covers run-to-run stability).

Expected `lavapipe` vs `moltenvk` differences (rasterizer and resolution, not bugs):

- **Resolution.** Frames are 320×240, not the 640×480 of a Retina Mac. The distant grid aliases more,
  showing bright horizontal streaks near the horizon.
- **Lines on pixel boundaries.** The camera looks straight down −Z, so the Y (yellow) and Z (blue) axis
  lines project exactly onto the boundary between two pixel columns. Lavapipe rasterizes such lines as
  nothing or as dashes; Metal fills one column.
- **Coplanar lines.** Grid lines on the floor quad z-fight, because line and triangle depths are
  interpolated differently. Lavapipe shows them dashed or hidden.

Lavapipe output changes with the Mesa/LLVM version in the runner image (recorded on Ubuntu 24.04:
`llvmpipe (LLVM 20.1.2, 256 bits)`). If an image update breaks the goldens, re-record them as above.

### Gates

| Gate | Rule | Where |
|---|---|---|
| Validation | 0 warnings and 0 errors from `VK_LAYER_KHRONOS_validation`, including teardown (leaks, in-use destruction) | every scene |
| Allocation | 0 managed bytes (`GC.GetAllocatedBytesForCurrentThread`) over 300 frames after 120 warm-up frames | `sandbox` scene |

The debug messenger listens to WARNING and ERROR only, with a static `[UnmanagedCallersOnly]` callback,
and records into `IVulkanContext.Validation` (`VulkanValidationLog`: counters plus the first 64
messages). Without the layers installed the validation gate skips locally and fails on CI (`CI=true`).

Per-frame code must not allocate: use static lambdas with state (`ShadowSystem.RenderShadows<TState>`),
cached arrays, and stack-formatted ImGui text (`Span<char>.TryWrite` + `ImGui.TextUnformatted`).

## Benchmarks

`just bench` runs every benchmark (`NetBuffer` write/read, replication capture/encode/decode at 100 and 1000
nodes, camera and model matrices, lights UBO packing, the scene tree: `ProcessTick10k`,
`TransformPropagationDirtySubtree10k`, `GetNodePathLookup`, `SceneSaveLoadRoundTrip1k`; GPU allocator
alloc/free and churn; audio: `CommandBatchEnqueueAndDrain64`, `AttenuationCurvesAllModels`,
`SpatialProjection32Emitters`, `ServerFrame32PositionalVoices`, `MixBlock480Frames32Voices`) and compares with [`baseline.json`](../../Tests/MainframeEngine.Benchmarks/baseline.json): a benchmark
fails when its mean is **more than 10 % slower** or it allocates more per operation. Results also land
in `artifacts/bench`. Baselines are machine-specific (the file records machine and runtime), so
compare on the machine that recorded them; refresh with `just bench-baseline` when a change is
intentionally slower or a new benchmark is added, and commit the file.

## Related docs

[Build & platforms](build-and-platforms.md) · [Engine lifecycle](engine-lifecycle.md) ·
[Vulkan renderer](vulkan-renderer.md) · [Coordinate conventions](coordinate-conventions.md)

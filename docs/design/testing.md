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
[Coordinate conventions](coordinate-conventions.md), the 1200-byte std140 lights UBO, the PNG codec and
`VulkanValidationLog`. Tests that touch process-wide state (`NetBufferPool`, `Console`, `Log.LogLevel`)
share a non-parallel collection.

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
- **Scenes** (in the host, built from engine nodes): `lit-shapes` (procedural sky, grid, floor quad,
  two boxes, one shadow-casting directional light), `multi-light` (same geometry, directional + spot +
  point shadow casters; self-checks that every shadow sub-pass's ring slot holds its own matrix),
  `spine` (adds SpineBoy, content linked from the Sandbox), `spine-no-shadows` (Spine and shapes with
  no `ShadowSystem`: the fallback set 2), `sandbox` (adds the Sandbox's ImGui window and gizmos; not
  used for goldens because it shows timings).
- **Host hooks** (command line): `--resize WxH@frame`, `--toggle-vsync frame` (swapchain recreation
  mid-run), `--quit-error frame` (`Quit(ExitCode.Error)`; the host exits with `Run()`'s code and
  `result.json` records it). Scene self-check failures are reported in `SceneCheckFailures`.
- **Tests:** `LitShapesRenderCleanlyAndMatchGoldens`, `MultipleShadowCastingLightsEachUseTheirOwnMatrix`,
  `SpineRendersCleanlyAndMatchesGolden`, `SpineRendersWithoutAShadowSystem`,
  `SwapchainRecreationOnResizeAndVSyncToggleIsClean`, `QuitWithErrorReturnsErrorExitCode`,
  `SandboxSteadyStateAllocatesNothing`, `CapturesAreDeterministicAcrossRuns`.
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
commit them with the change that caused them. For `lavapipe`, download the `render-tests` artifact
from the CI run and copy the frames into `Goldens/lavapipe/`. PNGs are stored in Git LFS.

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

`just bench` runs every benchmark (`NetBuffer` write/read, camera and model matrices, lights UBO packing)
and compares with [`baseline.json`](../../Tests/MainframeEngine.Benchmarks/baseline.json): a benchmark
fails when its mean is **more than 10 % slower** or it allocates more per operation. Results also land
in `artifacts/bench`. Baselines are machine-specific (the file records machine and runtime), so
compare on the machine that recorded them; refresh with `just bench-baseline` when a change is
intentionally slower or a new benchmark is added, and commit the file.

## Related docs

[Build & platforms](build-and-platforms.md) · [Engine lifecycle](engine-lifecycle.md) ·
[Vulkan renderer](vulkan-renderer.md) · [Coordinate conventions](coordinate-conventions.md)

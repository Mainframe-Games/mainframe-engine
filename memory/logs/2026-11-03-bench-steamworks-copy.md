---
summary: >-
  `just bench` works again: every BenchmarkDotNet job failed to build (MSB3030 on both Steamworks shim copies) because
  the job's global OutDir sent the shims away from where the engine csproj looks. The shim references now undefine
  OutDir/OutputPath. No in-process runs.
---

# 2026-11-03: `just bench` — the Steamworks shim copy in BenchmarkDotNet jobs

Branch `fix/bench-steamworks-copy` (made in a session that shared the G9.10b checkout, then moved to its own branch
from `main`). Small fix, no plan.

## Cause

BenchmarkDotNet builds a generated project per job with global properties `ArtifactsPath=<job>/` and
`OutDir`/`OutputPath=<job>/bin/Release/net10.0/`. Global properties flow to project references, so:

- `ArtifactsPath` turns on the artifacts layout, and `MainframeEngine.csproj` then expects the shims at
  `<job>/bin/MainframeEngine.Steamworks.{Posix,Win64}/release/`;
- the global `OutDir` overrides that layout, so both shims built into the job's one output folder instead — the same
  `MainframeEngine.Steamworks.dll` twice, the Win64 and Posix builds overwriting each other.

The `None` items pointed at paths that never existed: MSB3030, NA for every benchmark. `dotnet build -o <dir>` on a
project would have hit the same.

## Fix

`MainframeEngine/MainframeEngine.csproj`: both shim `ProjectReference`s carry `UndefineProperties="OutDir;OutputPath"`,
so the shims build into their own `bin/<cfg>/<tfm>/` or the artifacts folder whatever the caller passes, which is where
the copy items look. `ArtifactsPath` still flows, so the export path (ADR 0229: `--artifacts-path` + `publish -o`, which
sets `PublishDir`, not `OutDir`) is unchanged. `docs/design/steamworks.md` (How the right build loads) says so.

Not chosen: forcing `--inProcess` in the `bench` recipes. It works, but runs every benchmark in the host process
(no per-job isolation, e.g. `ProfilerBenchmarks.ScopeOff`'s own process with its `AppContext` switch), and leaves
`dotnet build -o` broken.

## Verified

- `just bench '*JointBenchmarks*'`: StepChain100Pins 56.07 µs, StepChain100SixDof 81.69 µs, Step50Ragdolls 604.78 µs,
  all 0 B; "No regressions" against the baseline. Numbers from a busy dev machine, not for `baseline.json` (untouched).
- `just build`, Release `-warnaserror -p:CompileShaders=false`: 0 warnings. `just test`: 3192 + 1809 + 460 pass
  (the Slow editor tests include a real export, the artifacts-layout publish).
- Editor `bin/{Debug,Release}/net10.0/steamworks/{win64,posix}/` and the Demo's: both shim builds (different sizes) with
  their Facepunch assembly. `dotnet build Examples/Demo/Demo.Desktop -o <dir>` now gives the same two folders and no
  shim next to the app.
- Not run: `just test-render` (no rendering change).

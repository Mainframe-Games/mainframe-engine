# ADR 0001 — ENet natives built from source in CI

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W1 lane C)

## Context

The engine uses the NuGet package `ENet-CSharp` 2.4.8 (nxrighthere). The package's native assets have three problems:

- `runtimes/osx/native/libenet.dylib` is **x86_64 only**, so networking fails on Apple Silicon.
- There are no RID-specific assets, so `osx-arm64` falls back to the x86_64 `osx` asset.
- The package's `build/ENet-CSharp.targets` also copies the libraries to the output folder.

The user approved building ENet natives in CI. Adding a different (maintained) ENet package was out of scope; that
would need a dependency discussion.

## Decision

- Build the **exact upstream native source** of 2.4.8 ourselves:
  - `Native/ENet/upstream` is a git submodule of `nxrighthere/ENet-CSharp`, pinned to tag `2.4.8` (`4b7a0ff`, 2022-03-26).
  - The packaged binaries are dated 2022-03-27.
  - The managed assembly is unchanged; we keep the NuGet package for it.
- `Native/ENet/CMakeLists.txt` builds `Source/Native/enet.c` into a shared library named for `DllImport("enet")`:
  - Windows: `enet.dll`, static CRT.
  - Linux: `libenet.so`.
  - macOS: `libenet.dylib`, universal arm64 + x86_64, minimum macOS 11.0, install name `@rpath`.
- Exports are limited to `enet_*`:
  - macOS/Linux: linker export lists. Upstream has no visibility macro, so `-fvisibility=hidden` would hide the API.
  - Windows: upstream's `ENET_DLL` dllexport.
- Tests (CTest):
  - `enet_exports` resolves all 65 P/Invoke entry points. The list was extracted from `ENet-CSharp.dll` metadata and
    is stored in `Native/ENet/pinvoke-exports.txt`. It is identical for net6.0 and netstandard2.1.
  - `enet_smoke` does a loopback reliable round trip and checks that `enet_linked_version` is 2.4.8.
- `.github/workflows/natives.yml` builds win-x64, linux-x64 (ubuntu-22.04) and osx universal.
- Binaries are committed under `MainframeEngine/runtimes/<rid>/native/`. The universal dylib goes under both
  `osx-arm64` and `osx-x64`.
- `Native/natives.lock` records each binary's sha256 and the build-input hash and submodule commits it was built from.
- CI verifies the lock; it does not try bit-for-bit rebuilds, which are not reproducible across toolchains.

## Consequences

- The local universal dylib exports the same 101 symbols as the package's x86_64 dylib, and passes both tests.
- The win-x64 and linux-x64 binaries come from the first `natives.yml` run (download `natives-all` and commit).
- **Managed side (other lane):** the project must make the runtime pick our binaries over the package's.
  - Today the package copies its own `enet.dylib`, `libenet.so` and `enet.dll` next to the app via
    `build/ENet-CSharp.targets`. It also contributes `runtimes/{osx,linux,win}/native` assets to `deps.json`.
  - Options: reference the package with `ExcludeAssets="build;native"` (or `runtime;native` as appropriate) and add
    our `runtimes/**` as `None`/`Content` items with `CopyToOutputDirectory`, or as RID-specific native assets.
  - Verify with `dotnet run` on osx-arm64 that `NativeLibrary` resolves our universal dylib.
- Upgrading ENet means bumping the submodule tag, regenerating `pinvoke-exports.txt`, re-running `natives.yml` and
  committing the artifacts.

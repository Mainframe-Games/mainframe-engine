# Native libraries

## Purpose

How the engine's own native libraries are laid out, built, tested, shipped and refreshed.

| Library | Built from | P/Invoke name | Files |
|---|---|---|---|
| ENet | `Native/ENet/upstream`: nxrighthere/ENet-CSharp submodule, tag `2.4.8` | `enet` (ENet-CSharp 2.4.8) | `enet.dll`, `libenet.so`, `libenet.dylib` |
| RmlUi shim | `Native/RmlUi`: in-house C ABI over RmlUi 6.3 + FreeType 2.14.3 | `mfrmlui` | `mfrmlui.dll`, `libmfrmlui.so`, `libmfrmlui.dylib` |

Decisions: [ADR 0001](../../memory/decisions/0001-enet-natives-built-in-ci.md) (ENet) and
[ADR 0002](../../memory/decisions/0002-rmlui-native-shim.md) (RmlUi shim, ABI rules, licence finding).

## Layout

```
Native/
  CMakeLists.txt            top level: options MF_NATIVES_ENET, MF_NATIVES_RMLUI (both ON)
  build.sh                  local build + test (+ --stage into runtimes/) for macOS / Linux
  natives-lock.sh           update / verify Native/natives.lock
  natives.lock              provenance of the committed binaries (generated)
  ENet/
    CMakeLists.txt          shared library `enet`, exports limited to enet_*
    exports.macos.txt       ld64 export list
    exports.linux.map       GNU ld version script
    pinvoke-exports.txt     the 65 entry points ENet-CSharp 2.4.8 P/Invokes
    tests/                  enet_exports, enet_smoke
    upstream/               submodule (nxrighthere/ENet-CSharp @ 2.4.8)
  RmlUi/
    CMakeLists.txt          shared library `mfrmlui` (RmlUi Core + Debugger + FreeType, all static)
    include/mfrmlui.h       public C header: the ABI contract
    shim/                   C++ implementation, export lists, NOTICE.md
    tests/                  mfrmlui_exports, mfrmlui_smoke
    external/RmlUi/         submodule (mikke89/RmlUi @ 6.3)
    external/freetype/      submodule (freetype/freetype @ VER-2-14-3)
MainframeEngine/runtimes/
  osx-arm64/native/         libenet.dylib, libmfrmlui.dylib   (universal arm64 + x86_64)
  osx-x64/native/           the same universal files (NuGet does not pick osx-x64 assets for arm64, so both RIDs)
  win-x64/native/           enet.dll, mfrmlui.dll             (from natives.yml)
  linux-x64/native/         libenet.so, libmfrmlui.so         (from natives.yml)
```

## Build settings

| | macOS | Windows | Linux |
|---|---|---|---|
| Toolchain | Apple clang, Ninja | MSVC, newest Visual Studio generator, `-A x64` | GCC on ubuntu-22.04, Ninja |
| Architecture | universal `arm64;x86_64` | x64 | x86_64 |
| Minimum OS / runtime | macOS 11.0 | static CRT (`/MT`) | glibc ≤ 2.35; libstdc++/libgcc linked statically into `mfrmlui` |
| Exports | `-exported_symbols_list` (`_enet_*`, `_mfrmlui_*`) | `__declspec(dllexport)` | `--version-script` (+ `--exclude-libs,ALL`) |
| Install name / SONAME | `@rpath/lib<name>.dylib` | n/a | `lib<name>.so` (no version suffix) |

- **Visibility.** Everything linked into `mfrmlui` is compiled with `-fvisibility=hidden`. ENet relies on its export
  list only: upstream has no visibility macro, so hiding everything would hide its API too.
- **Warnings.**
  - Our code is built with `-Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion` on clang/gcc and
    `/W4 /permissive-` on MSVC, as errors. `-DMF_NATIVES_WERROR=OFF` turns off warnings-as-errors.
  - Third-party code builds with its own settings.
  - For upstream ENet, three reviewed, benign warning categories are silenced (see `Native/ENet/CMakeLists.txt`).
  - A clean macOS build currently prints zero warnings.
- **No system dependencies.** FreeType is configured without zlib/bzip2/png/harfbuzz/brotli; it uses its bundled
  zlib. Homebrew libraries are single-architecture and would break the universal build.
- **Determinism.** On the same toolchain, rebuilding produces byte-identical binaries.

## Building locally

Requirements: CMake ≥ 3.25, Ninja (optional; `brew install cmake ninja`), a C/C++17 compiler, and the submodules:

```bash
git submodule update --init --recursive Native
```

macOS / Linux, using the helper (universal on macOS):

```bash
Native/build.sh                    # configure + build + ctest into Native/build (git-ignored)
Native/build.sh --stage            # ...and copy the libraries into MainframeEngine/runtimes/<rid>/native + update the lock
Native/build.sh -DMF_NATIVES_RMLUI=OFF   # extra CMake arguments are passed through
```

Or with plain CMake:

```bash
cmake -S Native -B Native/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0   # macOS only
cmake --build Native/build
ctest --test-dir Native/build --output-on-failure
```

You can also build one library on its own, e.g. `cmake -S Native/RmlUi -B Native/build/rmlui -G Ninja
-DCMAKE_BUILD_TYPE=Release`.

Windows (Developer PowerShell not required):

```powershell
cmake -S Native -B Native/build -A x64
cmake --build Native/build --config Release --parallel
ctest --test-dir Native/build -C Release --output-on-failure
```

Cross-compiling is not supported. Windows and Linux binaries come from CI.

## Tests (CTest)

| Test | What it proves |
|---|---|
| `enet_exports` | Loads the built library by path, the way .NET does, and resolves all 65 ENet-CSharp P/Invoke names |
| `enet_smoke` | Linked version is 2.4.8; a reliable packet round trip over loopback (connect, send, receive, disconnect) |
| `mfrmlui_exports` | Resolves every function declared in `mfrmlui.h`; the list is parsed from the header at configure time, currently 121 |
| `mfrmlui_smoke` | A C11 client of the header (details below) |

What `mfrmlui_smoke` exercises:

- **Setup.** A no-op counting render interface and an in-memory file interface. It loads a font and a data-bound
  document (scalar getter, dynamic array of structs, event callback), then updates and renders it.
- **Checks.**
  - Geometry and the FreeType atlas callbacks.
  - `TranslateString`.
  - Element and attribute APIs.
  - Input routing: consumed vs propagate, click, text input.
  - Listener lifetime.
  - Data-model dirtying.
  - The debugger.
  - Hot reload.
  - Handle validation.
  - On shutdown: every compiled geometry and texture is released, and no unexpected RmlUi errors or warnings were
    logged.

## CI: `.github/workflows/natives.yml`

- **Triggers:** `workflow_dispatch`, and pull requests touching `Native/**`, `MainframeEngine/runtimes/**`,
  `.gitmodules` or the workflow itself.
- **Jobs:**
  - **`build`** is a matrix:
    - `macos-14`: universal build, uploaded for both osx RIDs.
    - `windows-latest`: win-x64.
    - `ubuntu-22.04`: linux-x64.
    Each leg checks out with recursive submodules, builds, runs CTest, uploads `natives-<name>` (laid out as
    `MainframeEngine/runtimes/<rid>/native/*`) and writes the exports, dependencies and glibc level to the step summary.
  - **`collect`** merges all RIDs into **`natives-all`**: `MainframeEngine/runtimes/**` plus a `Native/natives.lock`
    stamped for exactly these binaries.
  - **`verify-lock`** runs `Native/natives-lock.sh verify` against the committed tree.
- **Actions:** only official ones (`actions/checkout`, `actions/upload-artifact`, `actions/download-artifact`).

## `Native/natives.lock`

Native builds are not bit-reproducible across toolchains, so CI cannot rebuild a binary and compare hashes. The lock
records provenance instead:

```
component <enet|mfrmlui> inputs <sha256 over the component's build inputs + submodule commits>
submodule <component> <path> <commit>
binary <sha256> <component> <inputs hash it was built from> <path>
```

- **Build inputs.**
  - ENet: `Native/CMakeLists.txt`, `Native/ENet/CMakeLists.txt` and the export lists.
  - mfrmlui: `Native/CMakeLists.txt`, `Native/RmlUi/CMakeLists.txt`, `include/` and `shim/`.
  - Tests and docs are not inputs.
- **What `verify` fails on:**
  - A runtime binary that is not in the lock.
  - A missing locked binary, or one whose sha256 has changed.
  - A binary built from inputs that no longer match the tree, i.e. the sources changed but the binaries were not
    refreshed.

## Refreshing the committed binaries

After changing anything under `Native/` (or bumping a submodule):

1. Push the branch. A PR runs `natives.yml` automatically; otherwise run it from the Actions tab with
   `workflow_dispatch`.
2. When it is green, download **`natives-all`** from the run:
   `gh run download <run-id> -n natives-all -D natives-all`.
3. Copy it over the repository root:
   `cp -R natives-all/MainframeEngine natives-all/Native .`.
4. Run `Native/natives-lock.sh verify`, then commit `MainframeEngine/runtimes` and `Native/natives.lock`.

The CI binaries are the canonical set, including the macOS ones. To iterate on macOS locally, use
`Native/build.sh --stage`. It restamps only the binaries it rebuilt, so `verify-lock` keeps flagging the other RIDs
until CI artifacts are committed.

## Consuming the binaries from .NET

> This is the managed lane's job (csproj changes); the native layer only provides the files.

- **`mfrmlui`.** M8 adds `[LibraryImport("mfrmlui")]` and checks `mfrmlui_abi_version()` at start-up. The `runtimes/`
  files must be packed or copied as RID-specific native assets.
- **`enet`.** The `ENet-CSharp` package ships its own natives:
  - an x86_64-only `runtimes/osx/native/libenet.dylib`, and
  - `build/ENet-CSharp.targets`, which copies `enet.dll`, `enet.dylib` and `libenet.so` next to the app.

  Exclude those package assets (e.g. `ExcludeAssets="build;native"` on the `PackageReference`) and ship ours from
  `MainframeEngine/runtimes/`, or the package's x86_64 dylib may win on Apple Silicon. Verify with
  `NativeLibrary.TryLoad` / a Sandbox run on osx-arm64.

## Licences

See [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md):

- ENet and ENet-CSharp: MIT.
- RmlUi: MIT. Its bundled robin_hood/itlib are MIT, and the Courier Prime font in the Debugger is OFL.
- FreeType: FTL. Credit required: "Portions of this software are copyright © 2026 The FreeType Project
  (https://freetype.org). All rights reserved." It must appear on the credits screen (M8 task list).
- FreeType's bundled zlib: zlib licence.

The RmlUi.Net attribution is in [`Native/RmlUi/shim/NOTICE.md`](../../Native/RmlUi/shim/NOTICE.md).

## Status

| RID | enet | mfrmlui |
|---|---|---|
| osx-arm64 / osx-x64 | ✅ committed, built locally (universal, tests pass) | ✅ committed, built locally (universal, tests pass) |
| win-x64 | ⏳ from the first `natives.yml` run | ⏳ from the first `natives.yml` run |
| linux-x64 | ⏳ from the first `natives.yml` run | ⏳ from the first `natives.yml` run |

## Related

[Build & platforms](build-and-platforms.md) · [Networking](networking.md) ·
[Game UI (RmlUi)](future/game-ui.md) · [Networking & replication](future/networking-replication.md)

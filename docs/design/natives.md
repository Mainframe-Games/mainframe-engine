# Native libraries

## Purpose

How the engine's own native libraries are laid out, built, tested, shipped and refreshed.

| Library | Built from | P/Invoke name | Files |
|---|---|---|---|
| ENet | `Native/ENet/upstream`: nxrighthere/ENet-CSharp submodule, tag `2.4.8` | `enet` (ENet-CSharp 2.4.8) | `enet.dll`, `libenet.so`, `libenet.dylib` |
| RmlUi shim | `Native/RmlUi`: in-house C ABI over RmlUi 6.3 + FreeType 2.14.3 | `mfrmlui` | `mfrmlui.dll`, `libmfrmlui.so`, `libmfrmlui.dylib` |
| SVG rasteriser | `Native/Svg`: in-house C ABI over ThorVG 1.0.3 (Godot 4.7.2's vendored copy, PNG loader off; ADR 0112) | `mfsvg` | `mfsvg.dll`, `libmfsvg.so`, `libmfsvg.dylib` |
| Music editor helper (editor only) | `Native/PluginHost`: C++20 executable over libogg 1.3.6 + libvorbis 1.3.7 + the VST 3 SDK 3.8.1 hosting classes + RtMidi 6.0.0 (ADR 0146, 0147, 0148) | none: a process (`PluginHostClient`) | `mfplughost.exe`, `mfplughost` |

Decisions: [ADR 0001](../../memory/decisions/0001-enet-natives-built-in-ci.md) (ENet),
[ADR 0002](../../memory/decisions/0002-rmlui-native-shim.md) (RmlUi shim, ABI rules, licence finding) and
[ADR 0146](../../memory/decisions/0146-plugin-host-helper-process.md) (the `mfplughost` helper process and protocol).

## Layout

```
Native/
  CMakeLists.txt            top level: options MF_NATIVES_ENET, MF_NATIVES_RMLUI, MF_NATIVES_SVG, MF_NATIVES_PLUGINHOST (all ON)
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
  PluginHost/
    CMakeLists.txt          executable `mfplughost` (+ static ogg/vorbis); MF_PLUGINHOST_VST3 (ON) / _MIDI (ON: RtMidi)
    vst3.cmake              our source lists for the VST 3 SDK's base, pluginterfaces and hosting (external/vst3sdk)
    src/                    main, WAV reader, Vorbis encoder, protocol framing, socket transport, server
    tests/                  mfplughost_tests; plugins/ = mf_test_plugins.vst3 (gain, sine synth, crasher; never shipped)
    external/ogg/           submodule (xiph/ogg @ v1.3.6)
    external/vorbis/        submodule (xiph/vorbis @ v1.3.7)
MainframeEngine/runtimes/
  osx-arm64/native/         libenet.dylib, libmfrmlui.dylib   (universal arm64 + x86_64)
  osx-x64/native/           the same universal files (NuGet does not pick osx-x64 assets for arm64, so both RIDs)
  win-x64/native/           enet.dll, mfrmlui.dll             (from natives.yml)
  linux-x64/native/         libenet.so, libmfrmlui.so         (from natives.yml)
MainframeEngine.Editor/runtimes/   editor-only natives (never copied into games)
  osx-arm64/native/, osx-x64/native/   mfplughost (universal)
  win-x64/native/           mfplughost.exe   (from natives.yml; Git LFS like every .exe)
  linux-x64/native/         mfplughost       (from natives.yml)
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

Requirements: CMake ≥ 3.25, Ninja (optional; `brew install cmake ninja`), a C/C++20 compiler, and the submodules:

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
| `mfplughost_tests` | VST3 against `mf_test_plugins.vst3`: scan (and a missing bundle), load, a synth note chained into the gain, state round trip, latency, offline mode with the crasher's delay, no editor, unload; and WAV reading (float + 16-bit), Vorbis encoding decoded back through libvorbisfile (exact length, level), a failed encode leaves the old file, the `--encode` command line, and a socket round trip: hello (and a wrong version), ping, unknown message, encode, shutdown, exit on disconnect; MIDI: the event and port-list encodings, the clock request and (CoreMIDI/ALSA) a virtual output in the test process seen as a helper input — listed, opened, a note arrives as a timestamped midiEvent, closing it reports the device offline |

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

## The music editor helper: `mfplughost`

An executable, not a library: the editor runs it as a child process (music editor proposal, ADR 0146), so a crashing
VST3 plugin (later phases) cannot take the editor down. It is editor-only: the binaries live in
`MainframeEngine.Editor/runtimes/<rid>/native/`, the editor csproj copies the host OS's one next to the editor (a
RID-specific publish copies that RID's), and `PluginHostClient.Locate()` finds it there (or via `MAINFRAME_PLUGIN_HOST`).
Games never reference it.

- **Modes:** `--midi-test-source <name> <seconds>` (tests only: a virtual MIDI keyboard playing C4 every 200 ms;
  macOS/Linux), `--encode <in.wav> <out.ogg> [--quality 0..10]` (16-bit PCM or 32-bit float WAV, mono/stereo → Vorbis VBR
  at q/10; written to `<out>.encoding.tmp`, then renamed; exit 0/1 with one error line on stderr), `--serve <socket>`
  (the editor's helper), `--version`.
- **Protocol** (`src/protocol.hpp`, C# `PluginHostProtocol`): a Unix domain socket on every OS (Windows 10 1803+ has
  `AF_UNIX`; one code path instead of a named pipe). Frames are `u32 payloadLength | u16 type | u16 flags | u32 id |
  payload`, little-endian, ≤ 64 MiB; a reply is `type | 0x8000` with the request's id, or `0xFFFF` error (`u32 code,
  str message`). Core messages: `0x0001` hello (protocol version, capabilities, pid), `0x0002` ping, `0x0003` shutdown,
  `0x0004` sleep (timeout tests), `0x0005` clock (the helper's steady clock in ns), `0x0010` encode; `0x0100–0x010B`
  VST3 (below); `0x0200–0x0204` MIDI (below).
- **MIDI input** (ADR 0148; `src/midi.cpp`, RtMidi 6.0.0 static: CoreMIDI on macOS, WinMM on Windows, ALSA on Linux):
  `0x0200` listInputs → `[u32 id, str name, u16 online]` (ids are stable per port name for the helper's life; a second
  port with the same name is "name #2"), `0x0201` open / `0x0202` close (idempotent; open fails while offline), and two
  notifications with request id 0: `0x0203` midiEvent (`u32 device, u64 timestamp ns, u16 length, bytes`: channel
  messages only — sysex, clock and active sensing are ignored; stamped with `std::chrono::steady_clock` on RtMidi's
  thread) and `0x0204` devicesChanged (the listInputs payload). A poll thread re-reads the port list every second; an
  open port that disappears is closed and reported offline (the editor reopens it when it is back). RtMidi objects are
  created on the main thread (CoreMIDI delivers port changes to that thread's run loop). `hello` sets capability bit
  `4` (MIDI) only when an API initialised: a Linux build without the ALSA headers (`libasound2-dev`) still builds,
  with RtMidi's dummy API and no MIDI capability; so does a machine without `/dev/snd/seq`.
- **VST3** (ADR 0147; `src/plugins.cpp`, `src/shm.hpp`): `--scan <bundle>` prints one bundle's audio classes as JSON
  (the editor runs one process per bundle) and the same as the `0x0101` message; `0x0100` maps the editor's
  file-backed shared memory (header + one slot per instance: planar stereo in/out, note events); `load` (bundle, class
  ID, slot, rate, block → id, latency, channels, editor flag), `unload`, `getState`/`setState` (component + controller
  streams, each `u32`-length-prefixed), `latency`, `setOffline` (kOffline for every instance), `openEditor`/
  `closeEditor` and the `0x010B` editorClosed notification (request id 0); `0x010A` process runs a block for every
  listed instance in order (an entry can take another slot's output as its input) — one round trip per block. The
  helper writes the instance it is calling into to the shared memory header, so the editor can blame a crash.
  Control calls run on the main thread — on macOS an `NSApplication` loop (accessory, no Dock icon) that also hosts
  editor windows (`NSWindow` + the plugin's `IPlugView` NSView, titled "Plugin — Track — Song") — and `process` on the
  protocol thread. Windows/Linux answer `openEditor` with "not supported on this platform yet".
- **Lifetime:** the helper listens, accepts one connection (30 s limit), removes the socket file, serves requests one at
  a time, and exits on shutdown or when the connection closes (the editor quit or crashed).
- **Build:** static libogg/libvorbis compiled from the submodules' sources by our CMake (their own build scripts are not
  used; `ogg/config_types.h` is generated from `<stdint.h>`), static CRT on Windows, static libstdc++/libgcc on Linux,
  universal on macOS. The VST 3 SDK's own CMake is not used (it pulls VSTGUI, the validator and global settings):
  `vst3.cmake` compiles `base`, `pluginterfaces` and the hosting sources as two static libraries (warnings off). Only the
  SDK's `base`, `pluginterfaces`, `public.sdk` and `cmake` submodules are needed (a recursive checkout also fetches
  `vstgui4`, `doc` and `tutorials`; harmless). RtMidi's `RtMidi.cpp` is compiled by our CMake too (`mfph_rtmidi`,
  one API define per OS, warnings off); Linux links `libasound` dynamically when ALSA was found.
- **Packaging:** `build/package-editor.sh` keeps the exec bit; on macOS the helper sits in `Contents/MacOS/`. Releases
  are not signed yet; the commented `codesign` step and `build/macos/mfplughost.entitlements`
  (`com.apple.security.cs.disable-library-validation`, needed to load vendor-signed plugins under the hardened runtime)
  are ready for when they are.

## CI: `.github/workflows/natives.yml`

- **Triggers:** `workflow_dispatch`, and pull requests touching `Native/**`, `MainframeEngine/runtimes/**`,
  `.gitmodules` or the workflow itself.
- **Jobs:**
  - **`build`** is a matrix:
    - `macos-14`: universal build, uploaded for both osx RIDs.
    - `windows-latest`: win-x64.
    - `ubuntu-22.04`: linux-x64.
    Each leg checks out with recursive submodules, builds, runs CTest, uploads `natives-<name>` (laid out as
    `MainframeEngine/runtimes/<rid>/native/*` plus the helper under `MainframeEngine.Editor/runtimes/<rid>/native/`)
    and writes the exports, dependencies and glibc level to the step summary.
  - **`collect`** merges all RIDs into **`natives-all`**: `MainframeEngine/runtimes/**`,
    `MainframeEngine.Editor/runtimes/**` plus a `Native/natives.lock` stamped for exactly these binaries.
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
  - mfplughost: `Native/CMakeLists.txt`, `Native/PluginHost/CMakeLists.txt`, `vst3.cmake`, `src/` and the ogg, vorbis,
    vst3sdk and rtmidi submodule commits.
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
   `cp -R natives-all/MainframeEngine natives-all/MainframeEngine.Editor natives-all/Native .`.
4. Run `Native/natives-lock.sh verify`, then commit `MainframeEngine/runtimes`, `MainframeEngine.Editor/runtimes` and
   `Native/natives.lock`.

The CI binaries are the canonical set, including the macOS ones. To iterate on macOS locally, use
`Native/build.sh --stage`. It restamps only the binaries it rebuilt, so `verify-lock` keeps flagging the other RIDs
until CI artifacts are committed.

## Consuming the binaries from .NET

> This is the managed lane's job (csproj changes); the native layer only provides the files.

- **`mfrmlui`.** The managed binding (`MainframeEngine/Src/UI/Rml/`, M8) uses `[LibraryImport("mfrmlui")]` and checks
  `mfrmlui_abi_version()` when the UI server starts (`RmlCore.EnsureLibrary`: a missing or incompatible library fails
  with a clear `RmlException`). The `runtimes/` files are copied flat next to the app at build time. See
  [Game UI](game-ui.md).
- **`enet`.** The `ENet-CSharp` package ships its own natives:
  - an x86_64-only `runtimes/osx/native/libenet.dylib`, and
  - `build/ENet-CSharp.targets`, which copies `enet.dll`, `enet.dylib` and `libenet.so` next to the app.

  Exclude those package assets (e.g. `ExcludeAssets="build;native"` on the `PackageReference`) and ship ours from
  `MainframeEngine/runtimes/`, or the package's x86_64 dylib may win on Apple Silicon. Verify with
  `NativeLibrary.TryLoad` / a run of any game (e.g. the Demo) on osx-arm64.

## Licences

See [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md):

- ENet and ENet-CSharp: MIT.
- RmlUi: MIT. Its bundled robin_hood/itlib are MIT, and the Courier Prime font in the Debugger is OFL.
- FreeType: FTL. Credit required: "Portions of this software are copyright © 2026 The FreeType Project
  (https://freetype.org). All rights reserved." It must appear on the credits screen (M8 task list).
- FreeType's bundled zlib: zlib licence.
- libogg and libvorbis (in the editor-only `mfplughost`): BSD-3-Clause, notice text reproduced.
- RtMidi (in the editor-only `mfplughost`): MIT, notice text reproduced.

The RmlUi.Net attribution is in [`Native/RmlUi/shim/NOTICE.md`](../../Native/RmlUi/shim/NOTICE.md).

## Status

| RID | enet | mfrmlui | mfsvg | mfplughost |
|---|---|---|---|---|
| osx-arm64 / osx-x64 | ✅ committed (CI) | ✅ committed (CI) | ✅ committed (CI) | ✅ committed with VST3 and MIDI, built locally (universal, tests pass, virtual-port test included) |
| win-x64 | ✅ committed (CI) | ✅ committed (CI) | ✅ committed (CI) | ⏳ TODO: from the next `natives.yml` run (the Windows `AF_UNIX` path, VST3 hosting and WinMM MIDI are built there, not yet run) |
| linux-x64 | ✅ committed (CI) | ✅ committed (CI) | ✅ committed (CI) | ⏳ TODO: from the next `natives.yml` run (VST3 hosting and ALSA MIDI included; `libasound2-dev` installed there) |

Adding `MF_NATIVES_PLUGINHOST` to `Native/CMakeLists.txt` changed every component's build inputs, so
`natives-lock.sh verify` reports the committed enet/mfrmlui/mfsvg binaries as stale until that `natives.yml` run's
`natives-all` is committed (the binaries themselves are unaffected).

## Related

[Build & platforms](build-and-platforms.md) · [Networking](networking.md) ·
[Game UI (RmlUi)](game-ui.md) · [Networking](networking.md)

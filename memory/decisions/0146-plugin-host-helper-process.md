# ADR 0146 — Music editor helper process (`mfplughost`): socket protocol, Vorbis encoding, editor-only natives

- **Date:** 2026-10-08
- **Status:** accepted (proposal [music-editor.md](../../docs/design/future/music-editor.md), delivery 3 and the
  process/protocol foundation of delivery 2; VST3 and MIDI come later)

## Context

The music editor needs native code the engine must not carry: VST3 hosting (plugins crash; they must not take the
editor down), MIDI input, and Vorbis encoding for renders (NVorbis only decodes). ADR 0145 left `.ogg` renders to "the
plugin helper". The approved native dependencies are the VST3 SDK, RtMidi and libogg/libvorbis; this phase needs only
the last two, but its process, protocol and shipping must be what the plugin phase builds on.

## Decision

1. **One executable, out of process.** `Native/PluginHost` builds `mfplughost` (C++20, static libogg 1.3.6 +
   libvorbis 1.3.7 compiled by our CMake from submodules; `MF_NATIVES_PLUGINHOST` ON, `MF_PLUGINHOST_VST3`/`_MIDI` OFF
   until vendored). Modes: `--encode in.wav out.ogg --quality q` (CLI, also for scripts), `--serve <socket>` (the
   editor's helper) and `--version`. Not a P/Invoke library: a crash in the helper is a lost connection, not a dead editor.
2. **Transport: a Unix domain socket on every OS**, Windows included (`AF_UNIX`, Windows 10 1803+; .NET's
   `UnixDomainSocketEndPoint`), instead of the proposal's named pipe on Windows: one code path on both sides. The helper
   listens at a path the editor chooses (`$TMPDIR/mfph-<pid>-<n>.sock`, short for `sun_path`), accepts one connection
   within 30 s, removes the file, and exits on `shutdown` or when the connection closes — an editor crash never leaves
   a helper behind.
3. **Framing:** `u32 payloadLength | u16 type | u16 flags | u32 requestId | payload`, little-endian, payload ≤ 64 MiB;
   strings are `u32` length + UTF-8. A reply echoes the id with `type | 0x8000`, or is `0xFFFF` (`u32 code, str
   message`; the helper stays up). Core types `0x0001` hello (protocol version — exact match required — helper version,
   capability bits, pid), `0x0002` ping, `0x0003` shutdown, `0x0004` sleep (tests), `0x0010` encode; `0x0100–0x01FF`
   reserved for plugins, `0x0200–0x02FF` for MIDI. Audio blocks will cross through a memory map (proposal), not frames.
4. **Client semantics** (`IPluginHost`, `PluginHostClient`): synchronous, serialised requests, each with a timeout
   (10 s default, 10 min for encode) and cancellation. Any transport failure, timeout, cancellation or out-of-step reply
   stops the helper (the stream can no longer be trusted); unexpected stops raise `Stopped`, and the next request
   starts a new helper and raises `Restarted` (the hook for reloading plugin state), at most 3 restarts a minute. An
   error reply only fails that request. Tests use a `FakePluginHost` with the same semantics.
5. **Editor-only natives location:** `MainframeEngine.Editor/runtimes/<rid>/native/`, pinned in `Native/natives.lock`
   (component `mfplughost`) and built by `natives.yml` like the engine natives, but copied only into the editor's
   output (the host OS's RID for RID-agnostic builds, since macOS and Linux share the file name). Games never get it.
6. **Render to `.ogg`:** new songs render to `Content/Music/<Song>.ogg`: a temporary 32-bit float WAV encoded by the
   helper (VBR, the song's `quality`, default 6), replaced atomically, then the `.meta`. Without a helper binary for the
   platform the output is written as `.wav` with a warning (proposal error table). One helper (the song view's) serves
   renders for now; per-song plugin helpers arrive with VST3.

## Consequences

- The VST3 phase adds message types in the reserved range and methods on `IPluginHost`; framing, lifetime, restart and
  shipping stay.
- Windows/Linux helper binaries come from the next `natives.yml` run; until then those platforms render `.wav`. Adding
  the option to `Native/CMakeLists.txt` changed every component's lock inputs, so `natives-lock.sh verify` flags the
  committed engine binaries as stale until that run's artifacts are committed.
- Releases are unsigned; `build/macos/mfplughost.entitlements` (`disable-library-validation`) and the commented
  `codesign` step in `build/package-editor.sh` are ready for signing.
- The Demo's song is a 290 KB `.ogg` instead of a 2.4 MB `.wav`.

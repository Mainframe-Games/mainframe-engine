# ADR 0147 — VST3 hosting in `mfplughost`: shared-memory block processing, one round trip per block

- **Date:** 2026-10-08
- **Status:** accepted (proposal [music-editor.md](../../docs/design/future/music-editor.md), delivery 2; builds on
  ADR 0145 and 0146)

## Context

The song engine (ADR 0145) renders 256-frame blocks on one render thread; the helper (ADR 0146) is a separate process
behind a framed socket protocol. Plugins must run in the helper, yet a song can have dozens of instruments and inserts,
and a block is ~5 ms at 48 kHz. Plugins crash, hang, report latency, keep state the song must save, and have their own
editor windows that expect the platform's main thread.

## Decision

1. **VST 3 SDK 3.8.1 (MIT) as a submodule**, compiled by our `vst3.cmake` (base, pluginterfaces, public.sdk hosting:
   `Module`, `PlugProvider`, `HostApplication`, `EventList`, `ParameterChanges`) — not the SDK's CMake, which pulls in
   VSTGUI and global settings. `MF_PLUGINHOST_VST3` defaults ON; a test bundle (`mf_test_plugins.vst3`: gain, sine
   synth, crasher) written against the SDK is a CTest fixture and is never shipped.
2. **Audio and note events go through a file-backed shared memory region** the editor creates in the temp folder and
   names in `PluginSetupShm` (a file, not a named section: one code path; `MemoryMappedFile` / `mmap` /
   `CreateFileMapping`). One slot per instance: planar stereo in/out (`maxBlock` floats each) and up to 256 note events.
   The header's `currentInstance` is written by the helper before every call into a plugin, so after a crash the editor
   knows which plugin to blame.
3. **One `PluginProcess` round trip per block** for every track's chain, in order; an entry may take another slot's
   output as its input, so a chain (instrument → inserts) runs in the helper without extra trips. Built-in instruments
   and audio clips render first in the editor and are copied into the chain head's input. Master inserts need the
   track sum and take a second round trip. The editor's process path is allocation-free (preallocated payload and
   reply buffers, `Lock.TryEnter` with the deadline).
4. **Deadline 4 × block duration** in real time: a late or busy reply is an xrun (silence, counted); the late reply
   is consumed by the next block so the stream stays in step; a reply later than 10 s stops the helper. Offline
   renders use a separate helper in `kOffline` mode with a 10 s watchdog per block (the render fails past it).
5. **Plugin delay compensation:** each track is delayed by (slowest chain latency − its own) in the engine.
6. **Crash recovery:** on `Stopped` every instance is marked unloaded; the UI thread restarts the helper (≤ 3 per
   minute, ADR 0146) and reloads every instance from its last captured state; a plugin blamed for 2 crashes within a
   minute is disabled for the session and its track shows it.
7. **The song model is the truth for state.** States (`u32` length + component stream, `u32` length + controller
   stream, base64 in `.msong`) are captured on save, on editor-window close (helper notification `0x010B`, request id
   0) and before a render, as undoable `SongDocument.SetPluginState` edits; when the model's state differs from the
   plugin's (undo/redo) the rack pushes it back.
8. **Threads in the helper:** control calls run on the main thread (macOS: an accessory `NSApplication` loop that also
   hosts the editor windows, `NSWindow` + `IPlugView`), `process` runs on the protocol thread. The editor serialises
   requests, so the two do not overlap. Windows/Linux editor windows are not implemented yet (clear error).

## Consequences

- One extra copy per chain head and a socket round trip per block (~30–900 µs measured with real plugins on macOS)
  instead of per-instance messages.
- A hung plugin stalls every chain of the song until the deadline (silence) and, past 10 s, the helper is restarted.
- Plugins see a fixed 4/4 time signature and no parameter automation yet; latency changes reported at run time are
  only picked up on load and state changes (no `IComponentHandler::restartComponent`).
- Windows/Linux `mfplughost` binaries with VST3 come from the next `natives.yml` run (`natives-lock.sh verify` reports
  them stale until then).

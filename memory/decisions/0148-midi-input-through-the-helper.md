# ADR 0148 — MIDI input through `mfplughost`: RtMidi in the helper, helper timestamps, heard-position recording

- **Date:** 2026-10-08
- **Status:** accepted (proposal [music-editor.md](../../docs/design/future/music-editor.md), delivery 4; builds on
  ADR 0145, 0146 and 0147)

## Context

The song editor needs MIDI keyboards: play the armed or selected track live and record takes into clips. .NET has no
cross-platform MIDI API and the engine takes no new NuGet dependencies lightly; the editor already runs a native helper
(ADR 0146). The editor reads helper notifications only when it talks to the helper, so a message can wait up to a UI
frame before the editor sees it. Recording must still put notes where the player heard them, not where the editor
happened to read them, and the audible position lags the engine by the generator queue and the device buffer.

## Decision

1. **RtMidi 6.0.0 (MIT) in the helper**, a submodule compiled by our CMake (`mfph_rtmidi`, static; CoreMIDI, WinMM,
   ALSA). `MF_PLUGINHOST_MIDI` defaults ON. A Linux build without the ALSA headers still succeeds with RtMidi's dummy
   API; `hello` sets the MIDI capability only when an API initialised, so the editor shows why MIDI is unavailable.
2. **Protocol `0x0200–0x0204`:** listInputs (ids stable per port name for the helper's life), open/close
   (idempotent), and notifications with request id 0: midiEvent (device, ns timestamp, bytes; channel messages only)
   and devicesChanged (polled once a second). The helper closes ports that vanish; the editor reopens enabled devices
   when they come back. RtMidi objects live on the helper's main thread (CoreMIDI port changes arrive on that run loop).
3. **Helper timestamps on the helper's steady clock**, plus a core `Clock` request (`0x0005`). The client measures the
   offset to `Stopwatch` at connect and on every ping (round-trip midpoint; same machine, so only origins differ) and
   hands events to the editor already on its clock.
4. **A dedicated helper for MIDI** (`MidiInputService`, owned by the workspace), separate from each song's plugin
   helper, so device state does not depend on which songs are open and the block round trips are not interleaved with
   a stream of notifications. Events are drained on the UI thread and enter the render thread through the engine's
   existing command ring (single consumer, lock-free for the render thread, no allocation per event).
5. **Recording maps each event to the heard position:** `SongPlayer.PositionTicks` (already minus the generator queue)
   minus the time since the event minus a device-buffer estimate (10 ms; the audio layer does not report it). A take is
   one clip — the recorded range rounded to bars, or the loop region when looping — committed at each loop wrap with a
   shared merge key, so overdub passes are heard and the whole take is one "Record" undo entry. The sustain pedal is
   applied by the editor (held note-offs, recorded lengths); other controllers are dropped (no controller path yet).

## Consequences

- MIDI works wherever the helper does, with no managed dependency; Windows/Linux binaries come from `natives.yml`
  (`libasound2-dev` added there).
- Live monitoring can lag by up to one editor frame; recorded timing does not (helper timestamps). Moving the drain to a
  dedicated thread is possible later without protocol changes.
- A preview released in the block it started in is pushed one frame later in the engine, so fast taps never stick.
- Tests: a native virtual-port round trip (CoreMIDI/ALSA), `FakePluginHost` MIDI simulation for the editor logic, and a
  real-helper test against `mfplughost --midi-test-source` (a virtual keyboard; macOS/Linux).

# ADR 0139 — Silk's SDL event pump without its per-frame allocation

- **Date:** 2026-10-06
- **Status:** accepted (Driving Range port: the stray 88 B frame of V5)
- **Amends:** the window event path of [ADR 0003](0003-sdl2-via-silk-net-2x.md) (SDL2 through Silk.NET 2.x)

## Context

Driving Range's `--measure` showed an occasional 88 B frame with no game code involved. Probes between frames and an
SDL event watch put it on every frame that has at least one SDL event: a window event, a key, and every frame of mouse
motion, so every frame of mouse look. The bytes are inside Silk.NET 2.23, not the engine's input path (`PushInput`
measured 0):

- `SdlPlatform.DoEvents` polls the frame's events into a `List<Event>` and raises `EventReceived(list)`.
- `SdlView.OnEventReceived(IEnumerable<Event> events)` walks them with `foreach` through the interface, which boxes
  `List<Event>.Enumerator`: 16 B header + list, index, version and a 56 B `Event` = 88 B. With no events, .NET returns
  a shared empty enumerator, so quiet frames cost nothing, which is why it looked like a stray.

Both classes are internal to Silk; Silk 3 replaces this code, and the 2.x line will not change. The engine pins Silk.

## Decision

1. `SdlEventBatch` (engine, `Src/Core`) re-subscribes Silk's own `SdlView.OnEventReceived` behind a reusable
   `IEnumerable<Event>` that is its own enumerator. It is found by reflection once, in `Engine.OnLoad`, after Silk has
   subscribed it. Silk's routing (window ids, window events, the `Events` list the SDL input context reads) runs
   unchanged on the same events, without the box.
2. It checks Silk's handler is really gone before adding its own, so events are never delivered twice. If Silk's
   internals are not where it expects (a Silk upgrade), it logs a warning and leaves Silk as it is. Events still flow,
   with the 88 B.
3. `Engine.OnClose` and `Engine.Dispose` put Silk's handler back before the window resets.
4. The `gamehost` render-test run pushes real SDL events every frame of its measured window (mouse motion, an unbound
   key, an `Exposed` window event). A node counts that they all reach the tree.

## Consequences

- Frames with SDL events allocate nothing in the pump; mouse look is 0 B per frame.
- The engine depends on three private Silk names (`SdlView.OnEventReceived`, `SdlView._platform`,
  `SdlPlatform.EventReceived`). A Silk upgrade that moves them is caught by `GameHostSteadyStateAllocatesNothing`
  (88 B/frame), not by a crash. On Silk 3 this class goes.
- Tests: `GameHostSteadyStateAllocatesNothing` fails with the swap disabled (26,400 B over 300 frames, 88 B/frame) and
  fails on its input count when the batch drops events. The input, minimise and resize render tests still pass.

# ADR 0040 — `[Replicated]` via the source generator, changed-since-ack delta snapshots

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W3 lane m5-repl)

## Context

Open question from the networking proposal: should `[Replicated]` use a source generator or reflection? The plan's
default was "via the M2 source generator". Requirements: zero managed allocations per steady-state network tick with
100 replicated nodes, no reflection in hot paths, resilience to loss/reorder/duplication on the unreliable channel.

## Decision

- **Generator.** `TypeModel` gains `Replicated` and `Rpcs` collections (filled in the same member walk as `[Export]`)
  and a separate `ReplicationEmitter` writes `MainframeEngine.Replication.g.cs` (only for assemblies with networked
  members). Per declaring type it emits a sealed `ReplicatedState` subclass with **typed shadow fields** (not boxed
  delegates): `Capture` compares each member with `EqualityComparer<T>.Default` and stamps changes with the tick,
  `Write`/`Read` serialize the masked members through compile-time-selected `NetCodec` overloads, interpolated
  members get an `InterpolationBuffer<T>`. RPCs get typed dispatch lambdas and `Rpc<Method>`/`Rpc<Method>To`
  extension senders. No `partial` requirement; members must be public/internal (MFG007–MFG009).
- **Delta encoding.** Per member "last changed tick" + per client "last acknowledged tick". Each unreliable snapshot
  carries every member changed after the client's ack (a superset of anything lost), entries are length-prefixed so
  unknown ids can be skipped, and a client acknowledges only snapshots it fully applied (an id above every spawn
  received means its reliable spawn is still in flight). Stale/duplicate ticks are discarded. Spawns carry full
  state captured with tick 0.
- **Custom structs** must implement `IEquatable<T>` to be replicated (otherwise change detection would box).

## Consequences

- Measured: 0 B per tick (server + client, 100 interpolated nodes, RPCs both ways); 1000 changed nodes encode in
  ~13 µs and decode in ~25 µs on Apple M5.
- Snapshot size is bounded by the full state (when a client never acks), and is a single message (ENet fragments
  large ones). Quantization, compression and per-client interest management are future work.
- Inheritance: one state per declaring type; RPC wire ids are base-first.

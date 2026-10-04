# ADR 0041 — Server-authoritative replication with per-node authority for RPCs

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W3 lane m5-repl)

## Context

Open question: server-authoritative only, or client authority per node? The plan's default: server-authoritative.
Games still need "this client controls that player".

## Decision

- **All replicated state flows server → clients.** Clients never send member values; client-side changes to
  replicated members are overwritten by the next change from the server.
- **`Node.NetworkAuthority`** (a `PeerId`; `MultiplayerApi.ServerPeerId` = 0 by default) is set by the server
  (`Spawn(…, authority)`, `SetAuthority`) and replicated. It gates RPCs:
  - `RpcMode.Server`: server → clients only.
  - `RpcMode.Authority` (default): the node's authority — a client calls it on the server; the server may always call.
  - `RpcMode.AnyPeer`: anyone; handlers check `RemoteSender`.
  The caller refuses locally (and counts it), and the server re-checks every client call (forged calls are dropped,
  `RpcRejected`). Clients only talk to the server (no client-to-client relaying).
- `IsNetworkAuthority` is true on the owning peer, and for nodes that are not networked (single-player code works
  unchanged).
- When a client leaves, nodes it owned are freed (despawned) by default, or handed back to the server.

## Consequences

- Simple, cheat-resistant model; input → server → snapshot costs one round trip plus the interpolation delay.
- Client-side prediction and lag compensation remain out of scope (they would build on authority + RPCs).
